using System.Net;
using Chronicle.Plugin.MusicBrainz;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>
/// Unit tests for <see cref="MusicBrainzMetadataProvider.SearchAsync"/> —
/// validates the four-attempt Stage 1+2 cascade (exact/fuzzy × year/no-year),
/// angle-bracket escaping, and scoring logic without making real HTTP calls.
/// </summary>
public class MusicBrainzProviderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string EmptyRecordings = """{"count":0,"recordings":[]}""";
    private const string EmptyArtists    = """{"count":0,"artists":[]}""";
    private const string EmptyReleases   = """{"count":0,"release-groups":[]}""";

    private static string OneRecording(string id, string title) =>
        $$"""{"count":1,"recordings":[{"id":"{{id}}","title":"{{title}}","first-release-date":"2002"}]}""";

    private static string OneArtist(string id, string name) =>
        $$"""{"count":1,"artists":[{"id":"{{id}}","name":"{{name}}"}]}""";

    private static string OneReleaseGroup(string id, string title) =>
        $$"""{"count":1,"release-groups":[{"id":"{{id}}","title":"{{title}}","first-release-date":"2002"}]}""";

    /// <summary>Creates a provider backed by a stub handler that delegates per-URL to the factory.</summary>
    private static MusicBrainzMetadataProvider BuildProvider(Func<string, HttpResponseMessage> factory)
    {
        var handler = new StubRouterHandler(factory);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://mb.test/") };
        var client  = new MusicBrainzClient(http, minInterval: TimeSpan.Zero);
        return new MusicBrainzMetadataProvider(client);
    }

    // ── Stage 1 succeeds on first call ────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Track_Stage1Succeeds_ReturnsWithoutFallback()
    {
        // Stage 1 query matches → provider must NOT proceed to Stage 2.
        int callCount = 0;
        var provider = BuildProvider(url =>
        {
            callCount++;
            return callCount == 1
                ? Ok(OneRecording("rec-1", "Duck and Run"))
                : Ok(EmptyRecordings);
        });

        var ctx = new MediaSearchContext(
            Name:           "Duck and Run (LP version)",
            HierarchyLevel: 2,
            GrandparentName: "3 Doors Down",
            ParentName:     "Away From the Sun",
            FilenameStem:   "Duck and Run");

        var results = await provider.SearchAsync(ctx);

        Assert.Single(results);
        Assert.Equal("recording:rec-1", results[0].Metadata.ExternalId);
        Assert.Equal(1, callCount); // only Stage 1b (no year) called
    }

    // ── All stages empty ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Track_ReturnsEmptyList_WhenAllStagesEmpty()
    {
        var provider = BuildProvider(_ => Ok(EmptyRecordings));

        var ctx = new MediaSearchContext(
            Name:           "Nonexistent Track",
            HierarchyLevel: 2,
            GrandparentName: "Unknown Artist");

        var results = await provider.SearchAsync(ctx);

        Assert.Empty(results);
    }

    // ── Angle-bracket escaping (Lucene operator stripping) ───────────────────

    [Fact]
    public async Task SearchAsync_Artist_AngleBracketsStrippedFromQuery()
    {
        // "<shutdown.exe>" contains Lucene range operators; they must be stripped
        // before the query is sent to MusicBrainz.
        string? capturedQuery = null;
        var provider = BuildProvider(url =>
        {
            capturedQuery = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
            return Ok(OneArtist("art-1", "3TEETH"));
        });

        var ctx = new MediaSearchContext(Name: "<shutdown.exe>", HierarchyLevel: 0);
        await provider.SearchAsync(ctx);

        Assert.NotNull(capturedQuery);
        Assert.DoesNotContain("<", capturedQuery);
        Assert.DoesNotContain(">", capturedQuery);
        // The stripped name should still be present
        Assert.Contains("shutdown.exe", capturedQuery);
    }

    // ── Scoring: FilenameStem exact match preferred ───────────────────────────

    [Fact]
    public async Task SearchAsync_Track_FilenameStemExactMatchScoresHigher_ThanTagContains()
    {
        // When FilenameStem gives an exact match but the tag title only "contains" the
        // candidate, the exact-match score (60) must win over the contains score (30).
        var provider = BuildProvider(url =>
        {
            var qs = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
            if (qs.Contains("Duck and Run (LP version)"))
                return Ok(OneRecording("rec-lp", "Duck and Run"));
            return Ok(EmptyRecordings);
        });

        var ctx = new MediaSearchContext(
            Name:           "Duck and Run (LP version)",
            HierarchyLevel: 2,
            FilenameStem:   "Duck and Run");

        var results = await provider.SearchAsync(ctx);

        Assert.Single(results);
        // Score must reflect exact match via FilenameStem (≥60), not just contains (30)
        Assert.True(results[0].Score >= 60,
            $"Expected score ≥60 (exact match via FilenameStem), got {results[0].Score}");
    }

    // ── Artist search at root level ───────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Artist_ReturnsResultsForLevel0()
    {
        var provider = BuildProvider(_ => Ok(OneArtist("art-3dd", "3 Doors Down")));

        var ctx = new MediaSearchContext(Name: "3 Doors Down", HierarchyLevel: 0);
        var results = await provider.SearchAsync(ctx);

        Assert.Single(results);
        Assert.Equal("artist:art-3dd", results[0].Metadata.ExternalId);
        Assert.Equal("3 Doors Down", results[0].Metadata.Title);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static HttpResponseMessage Ok(string json) =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

    // ── New cascade tests: AltTitles ─────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_UsesAltTitles_InOrder()
    {
        // Stage 1b with first AltTitle "(2000) The Better Life" returns empty;
        // Stage 1b with second AltTitle "The Better Life" returns a result.
        var queriesReceived = new List<string>();
        var provider = BuildProvider(url =>
        {
            var qs = Uri.UnescapeDataString(url);
            queriesReceived.Add(qs);
            // Return result only when query contains "The Better Life" without the year prefix
            return qs.Contains("The Better Life") && !qs.Contains("2000") && !qs.Contains("firstreleasedate")
                ? Ok(OneReleaseGroup("rg-1", "The Better Life"))
                : Ok(EmptyReleases);
        });

        var ctx = new MediaSearchContext(
            Name:           "(2000) The Better Life",
            HierarchyLevel: 1,
            ParentName:     "3 Doors Down",
            Year:           null,   // no year so only Stage 1b fires
            AltTitles:      ["(2000) The Better Life", "The Better Life"]);

        var results = await provider.SearchAsync(ctx);
        Assert.NotEmpty(results);
        Assert.Equal("release-group:rg-1", results[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task SearchAsync_DropsYear_WhenStage1aWithYearEmpty()
    {
        // Stage 1a (exact + year) returns nothing; Stage 1b (exact, no year) returns a result.
        int callCount = 0;
        var provider = BuildProvider(url =>
        {
            callCount++;
            var qs = Uri.UnescapeDataString(url);
            // First call has firstreleasedate constraint — return empty
            if (qs.Contains("firstreleasedate")) return Ok(EmptyReleases);
            // Second call has no year — return result
            return Ok(OneReleaseGroup("rg-1", "Remixed"));
        });

        var ctx = new MediaSearchContext(
            Name:           "Remixed",
            HierarchyLevel: 1,
            ParentName:     "3TEETH",
            Year:           2014,
            AltTitles:      ["Remixed"]);

        var results = await provider.SearchAsync(ctx);
        Assert.NotEmpty(results);
        Assert.Equal(2, callCount); // Stage 1a with year, then Stage 1b without year
    }

    [Fact]
    public async Task SearchAsync_Track_UsesDateField_ForYear()
    {
        // Track searches use "date:{year}" not "firstreleasedate:{year}"
        string? capturedQuery = null;
        var provider = BuildProvider(url =>
        {
            capturedQuery = Uri.UnescapeDataString(url);
            return Ok(OneRecording("rec-1", "Kryptonite"));
        });

        var ctx = new MediaSearchContext(
            Name:           "Kryptonite",
            HierarchyLevel: 2,
            GrandparentName: "3 Doors Down",
            Year:           2000,
            AltTitles:      ["Kryptonite"]);

        await provider.SearchAsync(ctx);
        Assert.Contains("date:2000", capturedQuery);
        Assert.DoesNotContain("firstreleasedate", capturedQuery);
    }

    [Fact]
    public async Task SearchAsync_Album_UsesFirstReleaseDateField_ForYear()
    {
        string? capturedQuery = null;
        var provider = BuildProvider(url =>
        {
            capturedQuery = Uri.UnescapeDataString(url);
            return Ok(OneReleaseGroup("rg-1", "The Better Life"));
        });

        var ctx = new MediaSearchContext(
            Name:           "The Better Life",
            HierarchyLevel: 1,
            ParentName:     "3 Doors Down",
            Year:           2000,
            AltTitles:      ["The Better Life"]);

        await provider.SearchAsync(ctx);
        // Release-group queries must use "firstreleasedate", NOT the recording-level "date" field.
        Assert.Contains("firstreleasedate:2000", capturedQuery);
        // Must not contain a bare "date:YYYY" field (which is the recording/track field).
        // Note: "firstreleasedate" contains "date" as a substring, so we check for word-boundary form.
        Assert.DoesNotContain("&date:", capturedQuery);
        Assert.DoesNotContain("AND date:", capturedQuery);
    }

    [Fact]
    public async Task SearchAsync_FallsThrough_ToFuzzyStage_WhenExactFails()
    {
        // Stages 1a and 1b (exact with and without year) return empty;
        // Stage 2a (fuzzy with year) returns a result.
        // Use a multi-word title so MbQuote wraps it in phrase quotes ("...") while
        // MbSanitize leaves it unquoted — this makes exact vs fuzzy distinguishable.
        int callCount = 0;
        string? firstFuzzyQuery = null;
        var provider = BuildProvider(url =>
        {
            callCount++;
            var qs = Uri.UnescapeDataString(url);
            // Exact queries: MbQuote wraps multi-word title in phrase quotes → query= starts with "
            // Fuzzy queries: MbSanitize leaves the title unquoted → query= starts with a letter
            var queryPart = qs.Contains("query=") ? qs.Split("query=")[1] : qs;
            bool isFuzzy = !queryPart.TrimStart().StartsWith('"');
            if (isFuzzy)
            {
                firstFuzzyQuery ??= qs;
                return Ok(OneReleaseGroup("rg-1", "Phantom Remixed"));
            }
            return Ok(EmptyReleases);
        });

        var ctx = new MediaSearchContext(
            Name:           "Phantom Remixed",
            HierarchyLevel: 1,
            ParentName:     "3TEETH",
            Year:           2014,
            AltTitles:      ["Phantom Remixed"]);

        var results = await provider.SearchAsync(ctx);
        Assert.NotEmpty(results);
        Assert.True(callCount >= 3, $"Expected ≥3 calls (1a, 1b, 2a) but got {callCount}");
        // The first fuzzy call should include the year constraint
        Assert.NotNull(firstFuzzyQuery);
        Assert.Contains("firstreleasedate:2014", firstFuzzyQuery);
    }

    [Fact]
    public async Task SearchAsync_NoAltTitles_FallsBackToContextName()
    {
        // When AltTitles is null, should still search using context.Name
        string? capturedQuery = null;
        var provider = BuildProvider(url =>
        {
            capturedQuery = Uri.UnescapeDataString(url);
            return Ok(OneReleaseGroup("rg-1", "Lateralus"));
        });

        var ctx = new MediaSearchContext(
            Name:           "Lateralus",
            HierarchyLevel: 1,
            ParentName:     "Tool",
            Year:           null,
            AltTitles:      null);   // no AltTitles

        var results = await provider.SearchAsync(ctx);
        Assert.NotEmpty(results);
        Assert.Contains("Lateralus", capturedQuery);
    }

    // ── Stage 3: sub-item name/count comparison ───────────────────────────────

    [Fact]
    public async Task SearchAsync_Stage3_BoostsAlbum_WhenTrackNamesMatch()
    {
        // Stages 1+2 (first 4 release-group searches) return empty.
        // Stage 3 (5th/6th release-group searches) returns a candidate.
        // Sub-item fetch returns 3 tracks whose names match context.ChildNames.
        const string releasesJson = "{\"count\":1,\"releases\":[{\"id\":\"rel-123\"}]}";
        const string tracksJson   = "{\"media\":[{\"position\":1,\"track-count\":3,\"tracks\":[" +
                                    "{\"position\":1,\"title\":\"Kryptonite\",\"length\":239000}," +
                                    "{\"position\":2,\"title\":\"Loser\",\"length\":214000}," +
                                    "{\"position\":3,\"title\":\"Duck and Run\",\"length\":246000}" +
                                    "]}]}";
        int rgSearchCount = 0;
        var provider = BuildProvider(url =>
        {
            if (url.Contains("release-group?query="))
            {
                rgSearchCount++;
                // First 4 calls are Stages 1a, 1b, 2a, 2b — return empty
                // 5th and 6th calls are Stage 3 (with year, then without) — return a candidate
                return rgSearchCount <= 4
                    ? Ok(EmptyReleases)
                    : Ok(OneReleaseGroup("rg-abc", "The Better Life"));
            }
            if (url.Contains("release?release-group="))
                return Ok(releasesJson);
            if (url.Contains("/release/rel-123"))
                return Ok(tracksJson);
            return Ok(EmptyReleases);
        });

        var ctx = new MediaSearchContext(
            Name:           "The Better Life",
            HierarchyLevel: 1,
            ParentName:     "3 Doors Down",
            Year:           2000,
            AltTitles:      ["The Better Life"],
            ChildNames:     ["Kryptonite", "Loser", "Duck and Run"]);

        var results = await provider.SearchAsync(ctx);

        Assert.NotEmpty(results);
        // Base score: title exact (60) + year exact (20) = 80; plus count boost (15) + 3×5 names (15) = 110 → capped naturally
        Assert.True(results[0].Score > 50,
            $"Expected score >50 (base + sub-item boost), got {results[0].Score}");
        Assert.Equal("release-group:rg-abc", results[0].Metadata.ExternalId);
    }

    // ── Stage 4: sub-item metadata (track number + duration) comparison ───────

    [Fact]
    public async Task SearchAsync_Stage4_BoostsScore_WhenTrackNumberAndDurationMatch()
    {
        // Same setup as Stage 3, but we provide SubItemMetadata with track numbers and durations.
        const string releasesJson = "{\"count\":1,\"releases\":[{\"id\":\"rel-456\"}]}";
        const string tracksJson   = "{\"media\":[{\"position\":1,\"track-count\":2,\"tracks\":[" +
                                    "{\"position\":1,\"title\":\"Kryptonite\",\"length\":239000}," +
                                    "{\"position\":2,\"title\":\"Loser\",\"length\":214000}" +
                                    "]}]}";
        int rgSearchCount = 0;
        var provider = BuildProvider(url =>
        {
            if (url.Contains("release-group?query="))
            {
                rgSearchCount++;
                return rgSearchCount <= 4
                    ? Ok(EmptyReleases)
                    : Ok(OneReleaseGroup("rg-abc", "The Better Life"));
            }
            if (url.Contains("release?release-group="))
                return Ok(releasesJson);
            if (url.Contains("/release/rel-456"))
                return Ok(tracksJson);
            return Ok(EmptyReleases);
        });

        var ctx = new MediaSearchContext(
            Name:           "The Better Life",
            HierarchyLevel: 1,
            ParentName:     "3 Doors Down",
            Year:           2000,
            AltTitles:      ["The Better Life"],
            SubItemMetadata: new List<SiblingInfo>
            {
                new("Kryptonite", ItemNumber: 1, DurationSeconds: 239),  // exact match
                new("Loser",      ItemNumber: 2, DurationSeconds: 214),  // exact match
            }.AsReadOnly());

        var results = await provider.SearchAsync(ctx);

        Assert.NotEmpty(results);
        // Base: exact title (60) + year (20) = 80
        // Stage 4: 2 × track-number matches (+10 each) + 2 × duration matches (+10 each) = +40
        // Total: 120 (but scores can exceed 100 in this implementation)
        Assert.True(results[0].Score > 80,
            $"Expected score >80 (base + track+duration boosts), got {results[0].Score}");
        Assert.Equal("release-group:rg-abc", results[0].Metadata.ExternalId);
    }

    // ── Stage 3+4 not triggered for non-album levels ─────────────────────────

    [Fact]
    public async Task SearchAsync_SubItemStages_NotTriggered_ForArtistLevel()
    {
        // Stage 3+4 are only for HierarchyLevel 1 (albums).
        // For an artist (level 0), RunSubItemStagesAsync should return empty immediately.
        int callCount = 0;
        var provider = BuildProvider(url =>
        {
            callCount++;
            return Ok(EmptyArtists);
        });

        var ctx = new MediaSearchContext(
            Name:           "3 Doors Down",
            HierarchyLevel: 0,
            ChildNames:     ["The Better Life", "Away From the Sun"]);

        var results = await provider.SearchAsync(ctx);

        Assert.Empty(results);
        // Only 2 calls: Stage 1 exact (no year at level 0), Stage 2 fuzzy (no year at level 0)
        Assert.Equal(2, callCount);
    }
}

/// <summary>
/// Stub handler that routes to a caller-supplied factory based on the request URL path+query.
/// </summary>
internal sealed class StubRouterHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _factory;

    public StubRouterHandler(Func<string, HttpResponseMessage> factory)
        => _factory = factory;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Pass the relative path+query (after the base URL) to the factory.
        var relativeUrl = request.RequestUri?.PathAndQuery ?? string.Empty;
        return Task.FromResult(_factory(relativeUrl));
    }
}

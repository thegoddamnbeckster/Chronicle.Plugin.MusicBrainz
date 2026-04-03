using System.Net;
using Chronicle.Plugin.MusicBrainz;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>
/// Unit tests for <see cref="MusicBrainzMetadataProvider.SearchAsync"/> —
/// validates the three-stage track cascade, angle-bracket escaping, and scoring logic
/// without making real HTTP calls.
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

    // ── Track cascade: Stage 1 succeeds ───────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Track_Stage1Succeeds_ReturnsWithoutFallback()
    {
        // Stage 1 query matches → provider must NOT call Stage 2 or 3.
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
        Assert.Equal(1, callCount); // only Stage 1 called
    }

    // ── Track cascade: Stage 2 (FilenameStem fallback) ───────────────────────

    [Fact]
    public async Task SearchAsync_Track_FilenameStemFallback_WhenStage1Empty()
    {
        // Stage 1 (tag title) returns nothing; Stage 2 (filename stem) should return a hit.
        var queriesReceived = new List<string>();
        var provider = BuildProvider(url =>
        {
            var qs = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
            queriesReceived.Add(qs);
            // Stage 2 query contains "Duck and Run" without "(LP version)"
            return qs.Contains("Duck and Run (LP version)")
                ? Ok(EmptyRecordings)
                : Ok(OneRecording("rec-stem", "Duck and Run"));
        });

        var ctx = new MediaSearchContext(
            Name:           "Duck and Run (LP version)",
            HierarchyLevel: 2,
            GrandparentName: "3 Doors Down",
            ParentName:     "Away From the Sun",
            FilenameStem:   "Duck and Run");

        var results = await provider.SearchAsync(ctx);

        Assert.Single(results);
        Assert.Equal("recording:rec-stem", results[0].Metadata.ExternalId);
        Assert.Equal(2, queriesReceived.Count); // Stage 1 + Stage 2
    }

    // ── Track cascade: Stage 3 (no release constraint) ───────────────────────

    [Fact]
    public async Task SearchAsync_Track_NoReleaseConstraint_WhenStage1And2Empty()
    {
        // Stage 1 and 2 both return empty; Stage 3 drops the release clause.
        var queriesReceived = new List<string>();
        var provider = BuildProvider(url =>
        {
            var qs = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
            queriesReceived.Add(qs);
            // Stage 3 has no "release:" in query
            return qs.Contains("release:")
                ? Ok(EmptyRecordings)
                : Ok(OneRecording("rec-bside", "It's Not Me"));
        });

        var ctx = new MediaSearchContext(
            Name:           "It's Not Me",
            HierarchyLevel: 2,
            GrandparentName: "3 Doors Down",
            ParentName:     "Away From the Sun");  // no FilenameStem

        var results = await provider.SearchAsync(ctx);

        Assert.Single(results);
        Assert.Equal("recording:rec-bside", results[0].Metadata.ExternalId);
        Assert.Equal(2, queriesReceived.Count); // Stage 1 (no FilenameStem → Stage 2 skipped) + Stage 3
    }

    // ── Track cascade: all stages empty ──────────────────────────────────────

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

    // ── Album search ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Album_StripYearPrefixBeforeQuery()
    {
        // Albums stored as "(2000) The Better Life" should strip the year prefix
        // before querying, so the Lucene query uses "The Better Life" not "(2000) The Better Life".
        string? capturedQuery = null;
        var provider = BuildProvider(url =>
        {
            capturedQuery = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
            return Ok(OneReleaseGroup("rg-1", "The Better Life"));
        });

        var ctx = new MediaSearchContext(
            Name:           "(2000) The Better Life",
            HierarchyLevel: 1,
            ParentName:     "3 Doors Down");

        await provider.SearchAsync(ctx);

        Assert.NotNull(capturedQuery);
        Assert.DoesNotContain("(2000)", capturedQuery);
        Assert.Contains("The Better Life", capturedQuery);
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

    // ── Track cascade: Stage 4 (strip version qualifier) ─────────────────────

    [Fact]
    public async Task SearchAsync_Track_VersionQualifierStripped_WhenAllEarlierStagesEmpty()
    {
        // Stages 1–3 all return empty because MusicBrainz phrase-search fails to match
        // "Kryptonite (LP version)" (parentheses confuse the Lucene parser).
        // Stage 4 strips the trailing parenthetical and retries with the bare title.
        var queriesReceived = new List<string>();
        var provider = BuildProvider(url =>
        {
            var qs = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
            queriesReceived.Add(qs);
            // Only the bare-title query (no "(LP version)") returns a hit
            return qs.Contains("LP version")
                ? Ok(EmptyRecordings)
                : Ok(OneRecording("rec-kryptonite", "Kryptonite (LP version)"));
        });

        var ctx = new MediaSearchContext(
            Name:           "Kryptonite (LP version)",
            HierarchyLevel: 2,
            GrandparentName: "3 Doors Down",
            ParentName:     "Kryptonite"); // no FilenameStem — stem == name

        var results = await provider.SearchAsync(ctx);

        Assert.Single(results);
        Assert.Equal("recording:rec-kryptonite", results[0].Metadata.ExternalId);
        // Stage 1 (with release) + Stage 3 (no release, still has LP version) + Stage 4 (bare title)
        Assert.Equal(3, queriesReceived.Count);
        // Stage 4 query must NOT contain "LP version"
        Assert.DoesNotContain("LP version", queriesReceived[2]);
        // And must still contain the bare title
        Assert.Contains("Kryptonite", queriesReceived[2]);
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

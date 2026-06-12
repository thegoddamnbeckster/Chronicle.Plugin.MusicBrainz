using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Chronicle metadata provider for MusicBrainz.
/// Supports "music" (Artist → Album → Track hierarchy) and "audiobooks" media types.
/// </summary>
public sealed class MusicBrainzMetadataProvider : IMetadataProvider
{
    // ── IMetadataProvider identity ────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.musicbrainz";
    public string Name     => "MusicBrainz";
    public string Version  => "1.0.4";
    public string Author   => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyUserAgent   = "UserAgent";
    private const string KeyUsername    = "Username";
    private const string KeyPassword    = "Password";
    private const string KeyMaxRetries  = "MaxRetries";
    private const string KeyFetchCovers = "FetchCoverArt";

    // ── Live state ────────────────────────────────────────────────────────────

    private MusicBrainzClient? _client;

    /// <summary>Production: Chronicle instantiates plugins via <c>Activator.CreateInstance</c>, so a
    /// public parameterless constructor is required. Configuration is applied via <see cref="Configure"/>.</summary>
    public MusicBrainzMetadataProvider() { }

    /// <summary>Test-only: inject a pre-configured client instead of going through Configure().</summary>
    internal MusicBrainzMetadataProvider(MusicBrainzClient client) => _client = client;

    // ── IMetadataProvider: static declarations ────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName    = "music",
            DisplayName      = "Music",
            HierarchyLevels  = 3,
            HierarchyLabels  = ["Artist", "Album", "Track"],
            InteractionVerb  = "listened",
            DefaultPriority  = 10,
            SupportedFields  = ["title", "overview", "poster_url", "genres", "rating", "tags"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url", "genres", "rating", "tags"],
                [2] = ["title", "year", "runtime_minutes", "tags"],
            },
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "audiobooks",
            DisplayName     = "Audiobooks",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            InteractionVerb = "listened",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "genres", "cast", "rating", "runtime_minutes", "tags"],
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key          = KeyUserAgent,
                Label        = "User-Agent",
                Description  = "MusicBrainz requires a descriptive User-Agent string. " +
                               "Format: AppName/Version (contact@example.com).",
                Type         = SettingType.Text,
                Required     = true,
                DefaultValue = "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)",
            },
            new SettingDefinition
            {
                Key          = KeyUsername,
                Label        = "MusicBrainz Username",
                Description  = "Optional. Enables authenticated access for higher rate limits.",
                Type         = SettingType.Text,
                Required     = false,
            },
            new SettingDefinition
            {
                Key      = KeyPassword,
                Label    = "MusicBrainz Password",
                Type     = SettingType.Password,
                Required = false,
            },
            new SettingDefinition
            {
                Key          = KeyMaxRetries,
                Label        = "Max Retries",
                Type         = SettingType.Number,
                Required     = false,
                DefaultValue = "3",
            },
            new SettingDefinition
            {
                Key          = KeyFetchCovers,
                Label        = "Fetch Cover Art",
                Description  = "Download album art from the Cover Art Archive. " +
                               "Disable if you want metadata only.",
                Type         = SettingType.Boolean,
                Required     = false,
                DefaultValue = "true",
            },
        ],
    };

    // ── IMetadataProvider: configuration ─────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var userAgent = settings.GetValueOrDefault(KeyUserAgent,
            "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)");
        var username  = settings.GetValueOrDefault(KeyUsername);
        var password  = settings.GetValueOrDefault(KeyPassword);

        _client?.Dispose();
        _client = new MusicBrainzClient(userAgent, username, password);
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    // Matches a trailing " (YYYY)" year suffix — used when stripping release year from names.
    private static readonly System.Text.RegularExpressions.Regex YearSuffixRe =
        new(@"\s*\((\d{4})\)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches a leading "(YYYY) " year prefix — file scanners often store names this way.
    private static readonly System.Text.RegularExpressions.Regex YearPrefixRe =
        new(@"^\s*\(\d{4}\)\s*",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Use AltTitles when provided (populated by Chronicle from tag, stem, version-stripped
        // forms); fall back to Name so the plugin works even with older Chronicle versions.
        var titles = context.AltTitles?.Count > 0
            ? context.AltTitles
            : (IReadOnlyList<string>)[context.Name];

        var year = context.Year;

        // For an open Add Media search (music, level 0, no parent context), run artist and
        // album (release-group) searches in parallel so the user gets results with cover art
        // and rich metadata alongside the artist stubs.
        bool isMusicOpenSearch = !string.Equals(
                context.MediaTypeName, "audiobooks", StringComparison.OrdinalIgnoreCase)
            && context.HierarchyLevel == 0
            && context.ParentName is null;

        IReadOnlyList<MediaMetadata> allContainers;
        if (isMusicOpenSearch)
        {
            var artistTask = RunCascadeAsync(context, titles, year, ct);
            var albumQuery = string.Join(" ", titles.Take(1));
            var albumTask  = MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, albumQuery, ct);
            await Task.WhenAll(artistTask, albumTask);
            var albumMeta = albumTask.Result;
            allContainers = [artistTask.Result, albumMeta];
        }
        else
        {
            allContainers = [await RunCascadeAsync(context, titles, year, ct)];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = allContainers
            .SelectMany(c => c.Results ?? [])
            .Select(r => ScoreCandidate(context, r))
            .Where(c => !string.IsNullOrEmpty(c.Metadata.ExternalId) && seen.Add(c.Metadata.ExternalId!))
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();

        // Stages 3+4: if earlier stages found nothing, try sub-item comparison
        if (candidates.Count == 0)
        {
            var subItemCandidates = await RunSubItemStagesAsync(context, titles, year, ct);
            if (subItemCandidates.Count > 0) return subItemCandidates;
        }

        return candidates;
    }

    /// <summary>
    /// Runs the four-attempt Stages 1+2 cascade:
    ///   1a. Exact + year  (all alt-titles)
    ///   1b. Exact, no year (all alt-titles)
    ///   2a. Fuzzy + year  (all alt-titles)
    ///   2b. Fuzzy, no year (all alt-titles)
    /// Stages 3 and 4 (sub-item comparison) are handled by <see cref="RunSubItemStagesAsync"/>,
    /// called from <see cref="SearchAsync"/> when this cascade returns no results.
    /// </summary>
    private async Task<MediaMetadata> RunCascadeAsync(
        MediaSearchContext context,
        IReadOnlyList<string> titles,
        int? year,
        CancellationToken ct)
    {
        bool isAudiobook = string.Equals(
            context.MediaTypeName, "audiobooks", StringComparison.OrdinalIgnoreCase);

        // ── 3-level audiobook routing ─────────────────────────────────────────
        // Level 0 = Author  → search as artist
        // Level 1 = Series  → search MusicBrainz series (nice-to-have; returns empty if not found)
        // Level 2 = Book    → search release-group with secondarytype:Audiobook
        if (isAudiobook)
        {
            return context.HierarchyLevel switch
            {
                0 => await RunAudiobookAuthorSearchAsync(titles, year, ct),
                1 => await RunAudiobookSeriesSearchAsync(titles, ct),
                _ => await RunAudiobookBookSearchAsync(context, titles, year, ct),
            };
        }

        string? artist = context.HierarchyLevel switch
        {
            0 => null,                    // searching FOR an artist — no artist constraint
            1 => context.ParentName,      // album: artist is the parent
            _ => context.GrandparentName  // track: artist is the grandparent
        };

        // Artist searches do not support a year constraint on MusicBrainz.
        int? effectiveYear = context.HierarchyLevel == 0 ? null : year;

        // Stage 1: exact title
        if (effectiveYear.HasValue)
        {
            var r = await TryEachTitleAsync(titles, artist, effectiveYear, exact: true, context, isAudiobook, ct);
            if (r.Results?.Count > 0) return r;
        }
        {
            var r = await TryEachTitleAsync(titles, artist, null, exact: true, context, isAudiobook, ct);
            if (r.Results?.Count > 0) return r;
        }

        // Stage 2: fuzzy title
        if (effectiveYear.HasValue)
        {
            var r = await TryEachTitleAsync(titles, artist, effectiveYear, exact: false, context, isAudiobook, ct);
            if (r.Results?.Count > 0) return r;
        }
        {
            var r = await TryEachTitleAsync(titles, artist, null, exact: false, context, isAudiobook, ct);
            if (r.Results?.Count > 0) return r;
        }

        return new MediaMetadata();
    }

    /// <summary>
    /// Stage 3: fuzzy search + sub-item name/count comparison.
    /// Stage 4: fuzzy search + sub-item metadata (track number, duration) comparison.
    /// Only implemented for HierarchyLevel 1 (albums → track list).
    /// </summary>
    private async Task<IReadOnlyList<ScoredCandidate>> RunSubItemStagesAsync(
        MediaSearchContext context,
        IReadOnlyList<string> titles,
        int? year,
        CancellationToken ct)
    {
        // Only implemented for albums (level 1) — tracks and artists handled by earlier stages
        if (context.HierarchyLevel != 1) return [];

        var localNames = context.ChildNames ?? context.SiblingNames;
        var hasSubItemData = (localNames?.Count > 0) || (context.SubItemMetadata?.Count > 0);
        if (!hasSubItemData) return [];

        string? artist = context.ParentName;

        // Get candidates via fuzzy search across all titles (with year, then without year)
        var seen = new HashSet<string>();
        var allCandidates = new List<MediaMetadata>();

        void AddCandidates(IReadOnlyList<MediaMetadata> results)
        {
            foreach (var c in results)
                if (!string.IsNullOrEmpty(c.ExternalId) && seen.Add(c.ExternalId))
                    allCandidates.Add(c);
        }

        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (year.HasValue)
            {
                var r = await MusicBrainzSearcher.SearchReleaseGroupsAsync(
                    _client!, BuildReleaseGroupQuery(title, artist, year, exact: false), ct);
                AddCandidates(r.Results ?? []);
            }
            {
                var r = await MusicBrainzSearcher.SearchReleaseGroupsAsync(
                    _client!, BuildReleaseGroupQuery(title, artist, null, exact: false), ct);
                AddCandidates(r.Results ?? []);
            }
        }

        if (allCandidates.Count == 0) return [];

        // For each candidate, fetch its track listing and compute sub-item score
        var scored = new List<ScoredCandidate>();
        foreach (var candidate in allCandidates)
        {
            var baseScore = ScoreCandidate(context, candidate);

            // ExternalId format: "release-group:{mbid}"
            var rgMbid = candidate.ExternalId?.Replace("release-group:", "");
            if (string.IsNullOrEmpty(rgMbid)) { scored.Add(baseScore); continue; }

            try
            {
                var releaseIds = await MusicBrainzSearcher.FetchReleaseGroupReleasesAsync(
                    _client!, rgMbid, ct);
                if (releaseIds.Count == 0) { scored.Add(baseScore); continue; }

                // Use the first release in the list
                var tracks = await MusicBrainzSearcher.FetchReleaseTracksAsync(
                    _client!, releaseIds[0], ct);

                // Stage 3: name + count boost
                int subItemBoost = ScoreSubItemNames(tracks, localNames);

                // Stage 4: metadata boost (track numbers + durations)
                subItemBoost += ScoreSubItemMetadata(tracks, context.SubItemMetadata);

                scored.Add(baseScore with { Score = baseScore.Score + subItemBoost });
            }
            catch (Exception)
            {
                // If sub-item fetch fails, fall back to base score
                scored.Add(baseScore);
            }
        }

        return scored
            .Where(c => !string.IsNullOrEmpty(c.Metadata.ExternalId))
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Scores how well a list of MusicBrainz track titles matches Chronicle's
    /// known child/sibling names. Returns a boost score (0..55): +15 if track count matches exactly,
    /// +5 per name match (name portion capped at 40, combined max 55).
    /// </summary>
    private static int ScoreSubItemNames(
        IReadOnlyList<(int TrackNumber, string Title, int? DurationMs)> mbTracks,
        IReadOnlyList<string>? localNames)
    {
        if (localNames is null || localNames.Count == 0) return 0;
        int score = 0;

        if (mbTracks.Count == localNames.Count) score += 15;

        var mbNormalized = mbTracks.Select(t => Normalize(t.Title)).ToHashSet();
        int nameMatches = localNames.Count(n => mbNormalized.Contains(Normalize(n)));
        score += Math.Min(nameMatches * 5, 40);

        return score;
    }

    /// <summary>Duration tolerance in seconds for sub-item metadata comparison (Stage 4).</summary>
    private const int DurationToleranceSeconds = 10;

    /// <summary>
    /// Scores how well MusicBrainz track metadata matches Chronicle's SiblingInfo list.
    /// Returns a boost score (0..60).
    /// +10 per track with matching track number; +10 per track with duration within tolerance.
    /// </summary>
    private static int ScoreSubItemMetadata(
        IReadOnlyList<(int TrackNumber, string Title, int? DurationMs)> mbTracks,
        IReadOnlyList<SiblingInfo>? localItems)
    {
        if (localItems is null || localItems.Count == 0) return 0;
        int score = 0;

        // Build a lookup: MusicBrainz track number → duration in seconds
        var mbByNumber = mbTracks.ToDictionary(
            t => t.TrackNumber,
            t => t.DurationMs.HasValue ? t.DurationMs.Value / 1000 : (int?)null);

        foreach (var local in localItems)
        {
            if (local.ItemNumber.HasValue && mbByNumber.ContainsKey(local.ItemNumber.Value))
                score += 10;

            if (local.DurationSeconds.HasValue && local.ItemNumber.HasValue &&
                mbByNumber.TryGetValue(local.ItemNumber.Value, out var mbDurSec) &&
                mbDurSec.HasValue &&
                Math.Abs(local.DurationSeconds.Value - mbDurSec.Value) <= DurationToleranceSeconds)
                score += 10;
        }

        // Cap at 60 to avoid overwhelming earlier-stage title match scores
        return Math.Min(score, 60);
    }

    // ── Audiobook level helpers ───────────────────────────────────────────────

    /// <summary>Level 0 audiobook — searches for the author as a MusicBrainz artist (person only).</summary>
    private async Task<MediaMetadata> RunAudiobookAuthorSearchAsync(
        IReadOnlyList<string> titles, int? year, CancellationToken ct)
    {
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            // Year is irrelevant for authors; just search by name.
            // personOnly: true — band/group/orchestra names are never valid author matches.
            var result = await MusicBrainzSearcher.SearchArtistsAsync(_client!, MbQuote(title), ct, personOnly: true);
            if (result.Results?.Count > 0) return result;
            result = await MusicBrainzSearcher.SearchArtistsAsync(_client!, MbSanitize(title), ct, personOnly: true);
            if (result.Results?.Count > 0) return result;
        }
        return new MediaMetadata();
    }

    /// <summary>
    /// Level 1 audiobook — tries to find a MusicBrainz series for the audiobook series name.
    /// Gracefully returns empty (no results) if the series is not found, because MusicBrainz
    /// audiobook series coverage is incomplete — the calling enrichment engine will leave the
    /// series at NotFound rather than erroring out.
    /// </summary>
    private async Task<MediaMetadata> RunAudiobookSeriesSearchAsync(
        IReadOnlyList<string> titles, CancellationToken ct)
    {
        try
        {
            foreach (var title in titles)
            {
                if (string.IsNullOrWhiteSpace(title)) continue;
                var result = await MusicBrainzSearcher.SearchAudiobookSeriesAsync(_client!, title, ct);
                if (result.Results?.Count > 0) return result;
            }
        }
        catch
        {
            // Series search is nice-to-have — silently swallow all errors.
        }
        return new MediaMetadata();
    }

    /// <summary>
    /// Level 2 audiobook — searches for a book as a release-group with secondarytype:Audiobook.
    /// Uses ParentName (series name) or GrandparentName (author name) as the artist constraint.
    /// </summary>
    private async Task<MediaMetadata> RunAudiobookBookSearchAsync(
        MediaSearchContext context,
        IReadOnlyList<string> titles,
        int? year,
        CancellationToken ct)
    {
        // Author is GrandparentName (Author → Series → Book); ParentName is the series.
        // Fall back to ParentName if GrandparentName is absent (Author → Book path).
        var author = context.GrandparentName ?? context.ParentName;

        if (author is null)
            return new MediaMetadata(); // Without author, MusicBrainz book search is unreliable.

        // Stage 1a: exact + year
        if (year.HasValue)
        {
            var r = await TryBookTitlesAsync(titles, author, year, exact: true, ct);
            if (r.Results?.Count > 0) return r;
        }
        // Stage 1b: exact, no year
        {
            var r = await TryBookTitlesAsync(titles, author, null, exact: true, ct);
            if (r.Results?.Count > 0) return r;
        }
        // Stage 2a: fuzzy + year
        if (year.HasValue)
        {
            var r = await TryBookTitlesAsync(titles, author, year, exact: false, ct);
            if (r.Results?.Count > 0) return r;
        }
        // Stage 2b: fuzzy, no year
        {
            var r = await TryBookTitlesAsync(titles, author, null, exact: false, ct);
            if (r.Results?.Count > 0) return r;
        }
        return new MediaMetadata();
    }

    private async Task<MediaMetadata> TryBookTitlesAsync(
        IReadOnlyList<string> titles, string author, int? year, bool exact, CancellationToken ct)
    {
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            var query = BuildReleaseGroupQuery(title, author, year, exact, audiobookOnly: true);
            var result = await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query, ct);
            if (result.Results?.Count > 0) return result;
        }
        return new MediaMetadata();
    }

    private async Task<MediaMetadata> TryEachTitleAsync(
        IReadOnlyList<string> titles,
        string? artist,
        int? year,
        bool exact,
        MediaSearchContext context,
        bool isAudiobook,
        CancellationToken ct)
    {
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;

            var query = (isAudiobook && context.HierarchyLevel == 0)
                ? BuildReleaseGroupQuery(title, artist, year, exact, audiobookOnly: true)
                : context.HierarchyLevel switch
                {
                    0 => exact ? MbQuote(title) : MbSanitize(title),  // artist search
                    1 => BuildReleaseGroupQuery(title, artist, year, exact),
                    _ => BuildRecordingQuery(title, artist, year, exact)
                };

            var result = (isAudiobook && context.HierarchyLevel == 0)
                ? await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query, ct)
                : context.HierarchyLevel switch
                {
                    0 => await MusicBrainzSearcher.SearchArtistsAsync(_client!, query, ct),
                    1 => await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query, ct),
                    _ => await MusicBrainzSearcher.SearchRecordingsAsync(_client!, query, ct)
                };

            if (result.Results?.Count > 0) return result;
        }
        return new MediaMetadata();
    }

    /// <summary>
    /// Builds a MusicBrainz recording (track) Lucene query.
    /// For exact search the title is phrase-quoted via <see cref="MbQuote"/>;
    /// for fuzzy search it is sanitised but unquoted via <see cref="MbSanitize"/>.
    /// Year maps to the <c>date</c> field on recordings.
    /// </summary>
    private static string BuildRecordingQuery(
        string title, string? artist, int? year, bool exact)
    {
        var titlePart = exact ? MbQuote(title) : MbSanitize(title);
        var parts = new List<string> { titlePart };
        if (artist is not null)  parts.Add($"artist:{MbQuote(artist)}");
        if (year   is not null)  parts.Add($"date:{year}");
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// Builds a MusicBrainz release-group (album) Lucene query.
    /// Year maps to the <c>firstreleasedate</c> field on release-groups.
    /// Pass <paramref name="audiobookOnly"/> to add <c>secondarytype:Audiobook</c>,
    /// which prevents music albums from matching audiobook title searches.
    /// </summary>
    private static string BuildReleaseGroupQuery(
        string title, string? artist, int? year, bool exact, bool audiobookOnly = false)
    {
        var titlePart = exact ? MbQuote(title) : MbSanitize(title);
        var parts = new List<string> { titlePart };
        if (artist is not null)  parts.Add($"artist:{MbQuote(artist)}");
        if (year   is not null)  parts.Add($"firstreleasedate:{year}");
        if (audiobookOnly)       parts.Add("secondarytype:Audiobook");
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// Strips Lucene range/special operators then quotes as a phrase whenever the term
    /// contains whitespace or non-alphanumeric punctuation.
    /// Examples: "shutdown.exe" → <c>"shutdown.exe"</c> (dot forces phrase quote so SOLR
    /// doesn't tokenise on it); "Duck and Run (LP version)" → <c>"Duck and Run (LP version)"</c>
    /// (parentheses are legal inside phrase quotes and preserve the full title).
    /// Chars stripped entirely: &lt;&gt;{}[]^~ — these are invalid even inside phrases.
    /// </summary>
    private static string MbQuote(string s)
    {
        // Strip chars that are always invalid even inside Lucene phrase quotes.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[<>{}[\]^~]", "").Trim();
        // Quote as a phrase whenever the term contains a space OR any punctuation that
        // Lucene would mis-parse as an operator (parentheses, dots, colons, etc.).
        // Parentheses are NOT stripped — inside a quoted phrase they are literals and
        // preserve the full title (e.g. "Duck and Run (LP version)").
        // Hyphens are NOT excluded — Lucene parses "a-ha" as "a AND NOT ha" without quotes.
        var needsQuotes = s.Any(c => !char.IsLetterOrDigit(c));
        return needsQuotes ? $"\"{s.Replace("\"", "\\\"")}\"" : s;
    }

    /// <summary>
    /// Strips Lucene special operators and returns the term unquoted, suitable for
    /// fuzzy/tokenised matching (Stage 2). Unlike <see cref="MbQuote"/>, this does not
    /// phrase-quote the result — Lucene will tokenise on spaces and apply standard
    /// analysis, giving broader (fuzzier) matching than a phrase search.
    /// </summary>
    private static string MbSanitize(string s)
    {
        // Strip chars invalid in Lucene query strings
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[<>{}[\]^~""\\]", "").Trim();
        // Escape remaining Lucene special chars that must be escaped when unquoted
        s = System.Text.RegularExpressions.Regex.Replace(s, @"([+\-!():|\?*])", "\\$1");
        return s;
    }

    private static string StripYearSuffix(string name)
    {
        // Strip trailing "(YYYY)" — e.g. "The Better Life (2000)" → "The Better Life"
        var m = YearSuffixRe.Match(name);
        if (m.Success) name = name[..m.Index].Trim();
        // Strip leading "(YYYY) " — e.g. "(2001) Duck and Run" → "Duck and Run"
        name = YearPrefixRe.Replace(name, string.Empty).Trim();
        return name;
    }

    private static ScoredCandidate ScoreCandidate(MediaSearchContext ctx, MediaMetadata candidate)
    {
        int score = 0;
        var reasons = new List<string>();

        var cn = Normalize(candidate.Title ?? string.Empty);
        // Strip year prefix/suffix before comparing: "(1958) The Blues" → "The Blues"
        // so folder-named albums with year prefixes get exact-match scores, not "contains".
        var qn = Normalize(StripYearSuffix(ctx.Name));
        // If a filename stem is available and is an exact match where the tag title isn't,
        // prefer it — ensures Stage 2 (filename-fallback) results still score as exact matches.
        if (!string.IsNullOrEmpty(ctx.FilenameStem))
        {
            var qnStem = Normalize(ctx.FilenameStem);
            if (string.Equals(cn, qnStem, StringComparison.Ordinal)
                && !string.Equals(cn, qn, StringComparison.Ordinal))
                qn = qnStem;
        }

        if (string.Equals(cn, qn, StringComparison.Ordinal))
        {
            score += 60;
            reasons.Add("title exact");
        }
        else if (cn.Contains(qn, StringComparison.Ordinal) || qn.Contains(cn, StringComparison.Ordinal))
        {
            score += 30;
            reasons.Add("title contains");
        }

        if (ctx.Year.HasValue && candidate.Year.HasValue)
        {
            if (ctx.Year.Value == candidate.Year.Value)
            {
                score += 20;
                reasons.Add("year exact");
            }
            else if (Math.Abs(ctx.Year.Value - candidate.Year.Value) == 1)
            {
                score += 10;
                reasons.Add("year ±1");
            }
        }

        // ── Context-aware author/musician disambiguation ──────────────────────
        // When enriching an author-level item (audiobooks/books, hierarchy 0),
        // boost candidates that look like authors and penalise those that look
        // like musicians. Uses the disambiguation string and MusicBrainz tags
        // that were stored in candidate.Tags by the searcher.
        if (ctx.MediaTypeName is "audiobooks" or "books" && ctx.HierarchyLevel == 0
            && candidate.Tags is { Count: > 0 })
        {
            var tagText = string.Join(" ", candidate.Tags).ToLowerInvariant();

            bool looksLikeAuthor = tagText.Contains("author") || tagText.Contains("writer")
                                || tagText.Contains("novelist") || tagText.Contains("poet");
            bool looksLikeMusician = tagText.Contains("vocalist") || tagText.Contains("singer")
                                  || tagText.Contains("musician") || tagText.Contains("rapper")
                                  || tagText.Contains("guitarist") || tagText.Contains("drummer")
                                  || tagText.Contains("bassist")   || tagText.Contains("pianist")
                                  || tagText.Contains("dj ")       || tagText.Contains("band")
                                  || tagText.Contains("orchestra") || tagText.Contains("choir");

            if (looksLikeAuthor)    { score += 30; reasons.Add("author context boost"); }
            if (looksLikeMusician)  { score -= 40; reasons.Add("musician context penalty"); }
        }

        return new ScoredCandidate(candidate, score,
            reasons.Count > 0 ? string.Join(", ", reasons) : "no signals");
    }

    private static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"[:\-,\.']", " ")
            .Replace("  ", " ").Trim().ToLowerInvariant();

    // ── IMetadataProvider: get by ID ──────────────────────────────────────────

    /// <summary>
    /// External ID format: "{type}:{mbid}"
    /// e.g. "release-group:f4179994-7621-4a46-b272-c62d8b3b9b1b"
    ///      "artist:5b11f4ce-a62d-471e-81fc-a69a8278c7da"
    ///      "recording:e14a55f4-ef25-4e35-bd5e-920f7af42d5c"
    ///
    /// Also accepts full MusicBrainz URLs:
    ///      "https://musicbrainz.org/artist/5b11f4ce-a62d-471e-81fc-a69a8278c7da"
    /// </summary>
    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Normalise: accept full MusicBrainz URLs by extracting type and MBID from the path.
        // e.g. https://musicbrainz.org/artist/5b11f4ce-... → artist:5b11f4ce-...
        if (externalId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            externalId = NormalizeUrl(externalId);

        var sep = externalId.IndexOf(':');
        if (sep < 0)
            throw new ArgumentException(
                $"Invalid MusicBrainz ID format: '{externalId}'. " +
                "Expected 'type:mbid' (e.g. artist:5b11f4ce-...) or a MusicBrainz URL.");

        var type = externalId[..sep];
        var mbid = externalId[(sep + 1)..];
        return type switch
        {
            "artist"        => await MusicBrainzEntityFetcher.FetchArtistAsync(_client!, mbid, ct),
            "release-group" => await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(_client!, mbid, ct),
            "recording"     => await MusicBrainzEntityFetcher.FetchRecordingAsync(_client!, mbid, ct),
            // A specific release URL was stored (e.g. from Fix Match). Look up its parent
            // release-group and delegate there — release-groups are the canonical identifier.
            "release"       => await FetchReleaseAsReleaseGroupAsync(mbid, ct),
            // Audiobook series (nice-to-have; may return minimal metadata)
            "series"        => await MusicBrainzEntityFetcher.FetchSeriesAsync(_client!, mbid, ct),
            _ => throw new ArgumentException(
                $"Unknown MusicBrainz entity type: '{type}'. " +
                "Supported types: artist, release-group, recording, series.")
        };
    }

    /// <summary>
    /// Resolves a specific MusicBrainz release MBID to its parent release-group and
    /// delegates to <see cref="MusicBrainzEntityFetcher.FetchReleaseGroupAsync"/>.
    /// This normalises Fix Match URLs that point to a release instead of a release-group.
    /// </summary>
    private async Task<MediaMetadata> FetchReleaseAsReleaseGroupAsync(
        string releaseMbid, CancellationToken ct)
    {
        var json = await _client!.GetAsync(
            $"release/{releaseMbid}?inc=release-groups&fmt=json", ct).ConfigureAwait(false);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("release-group", out var rgEl)
            || !rgEl.TryGetProperty("id", out var rgIdEl))
            throw new InvalidOperationException(
                $"MusicBrainz release/{releaseMbid} response contained no release-group ID.");

        var rgMbid = rgIdEl.GetString()
            ?? throw new InvalidOperationException(
                $"MusicBrainz release/{releaseMbid} release-group ID was null.");

        return await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(_client!, rgMbid, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a MusicBrainz browser URL to the internal "type:mbid" format.
    /// Handles both /artist/ and /release-group/ path segments.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Cannot parse MusicBrainz URL: '{url}'");

        if (!uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"URL is not a musicbrainz.org address: '{url}'");

        // AbsolutePath: /artist/5b11f4ce-a62d-471e-81fc-a69a8278c7da
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || string.IsNullOrWhiteSpace(segments[1]))
            throw new ArgumentException($"Cannot extract entity type and MBID from URL: '{url}'");

        return $"{segments[0]}:{segments[1]}";
    }

    // ── IMetadataProvider: image ──────────────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        return await _client!.GetBytesAsync(url, ct).ConfigureAwait(false);
    }

    // ── IMetadataProvider: health ─────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var json = await _client!.GetAsync(
                "artist/a74b1b7f-71a5-4011-9441-d0b5e4122711?fmt=json", ct).ConfigureAwait(false);
            return json.Contains("Radiohead");
        }
        catch { return false; }
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "MusicBrainzMetadataProvider has not been configured. Call Configure() first.");
    }
}

using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Chronicle metadata provider for MusicBrainz.
/// Supports "music", "album", and "artist" media types. No API key required.
/// </summary>
public sealed class MusicBrainzMetadataProvider : IMetadataProvider
{
    // ── IMetadataProvider identity ────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.musicbrainz";
    public string Name     => "MusicBrainz";
    public string Version  => "1.0.3";
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
            MediaTypeName   = "music",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "genres", "cast", "directors", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "album",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "genres", "cast", "directors", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "artist",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "genres"],
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

    // Matches a trailing version/edition qualifier in parentheses, e.g. "(LP version)",
    // "(radio edit)", "(acoustic)", "(remastered)". Used to strip the qualifier before
    // Stage 4 search so that MusicBrainz can find recordings whose phrases with parens
    // fail to match in Lucene.
    private static readonly System.Text.RegularExpressions.Regex VersionQualifierRe =
        new(@"\s*\([^)]+\)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        MediaMetadata container;

        if (context.HierarchyLevel == 2)
        {
            // Tracks: five-stage cascade so that problematic tag values and ambiguous titles
            // don't permanently block a match.
            //
            // Stage 1 — tag title + artist + release (most precise, honors the tag as-is)
            container = await MusicBrainzSearcher.SearchRecordingsAsync(
                _client!, BuildTrackQuery(context.Name, context, includeRelease: true), ct);

            // Stage 2 — filename stem + artist + release
            // Handles cases where the tag title has a qualifier MusicBrainz doesn't store
            // (e.g. tag="Duck and Run (LP version)", filename stem="Duck and Run").
            if (!(container.Results?.Count > 0) && !string.IsNullOrEmpty(context.FilenameStem))
                container = await MusicBrainzSearcher.SearchRecordingsAsync(
                    _client!, BuildTrackQuery(context.FilenameStem, context, includeRelease: true), ct);

            // Stage 3 — best available name, no release constraint
            // Handles B-sides whose parent folder name doesn't match the MusicBrainz release
            // they actually appear on.
            if (!(container.Results?.Count > 0))
            {
                var bestName = !string.IsNullOrEmpty(context.FilenameStem)
                    ? context.FilenameStem : context.Name;
                container = await MusicBrainzSearcher.SearchRecordingsAsync(
                    _client!, BuildTrackQuery(bestName, context, includeRelease: false), ct);
            }

            // Stage 4 — sibling-based release pinning (reid: constraint)
            // When all previous stages fail and sibling names are available, search for a sibling
            // track using inc=releases to discover the specific MusicBrainz release MBID, then
            // search the current track with that MBID as a reid: constraint. This is the most
            // precise possible search: it identifies the exact release (not just the title) and
            // avoids false matches from other releases with similar track titles.
            if (!(container.Results?.Count > 0)
                && context.SiblingNames?.Count > 0
                && context.GrandparentName is not null)
            {
                var releaseName = context.ParentName is not null
                    ? StripYearSuffix(context.ParentName) : null;
                var stage3Name = !string.IsNullOrEmpty(context.FilenameStem)
                    ? context.FilenameStem : context.Name;
                var currentBare = VersionQualifierRe.Replace(stage3Name, string.Empty).Trim();
                if (string.IsNullOrEmpty(currentBare)) currentBare = context.Name;

                foreach (var sibling in context.SiblingNames.Take(2))
                {
                    var siblingBare = VersionQualifierRe.Replace(sibling, string.Empty).Trim();
                    if (string.IsNullOrEmpty(siblingBare)) continue;

                    var siblingQuery = BuildTrackQuery(siblingBare, context, includeRelease: false);
                    var releases = await MusicBrainzSearcher.FindReleasesForSiblingAsync(
                        _client!, siblingQuery, ct);

                    // Prefer a release whose title matches our parent (folder/album) name
                    string? matchedReleaseId = null;
                    if (releaseName is not null)
                        matchedReleaseId = releases
                            .FirstOrDefault(r => string.Equals(
                                Normalize(r.ReleaseTitle), Normalize(releaseName),
                                StringComparison.Ordinal))
                            .ReleaseId;
                    // Fall back to the first release returned
                    matchedReleaseId ??= releases.FirstOrDefault().ReleaseId;

                    if (matchedReleaseId is null) continue;

                    var reidQuery =
                        $"{MbQuote(currentBare)} AND reid:{matchedReleaseId}" +
                        $" AND artist:{MbQuote(context.GrandparentName)}";
                    container = await MusicBrainzSearcher.SearchRecordingsAsync(
                        _client!, reidQuery, ct);
                    if (container.Results?.Count > 0) break;
                }
            }

            // Stage 5 — strip trailing version qualifier, no release constraint
            // Last resort: if even sibling-based pinning failed (or no siblings exist), strip the
            // version qualifier (e.g. "Kryptonite (LP version)" → "Kryptonite") and do a broad
            // artist search. Scoring still picks the correct recording because Normalize() strips
            // punctuation so "(LP version)" in the candidate title still compares equal to the
            // ctx.Name "(LP version)".
            if (!(container.Results?.Count > 0))
            {
                var stage3Name = !string.IsNullOrEmpty(context.FilenameStem)
                    ? context.FilenameStem : context.Name;
                var strippedName = VersionQualifierRe.Replace(stage3Name, string.Empty).Trim();
                if (!string.IsNullOrEmpty(strippedName) &&
                    !string.Equals(strippedName, stage3Name, StringComparison.OrdinalIgnoreCase))
                {
                    container = await MusicBrainzSearcher.SearchRecordingsAsync(
                        _client!, BuildTrackQuery(strippedName, context, includeRelease: false), ct);
                }
            }
        }
        else if (context.HierarchyLevel == 0)
        {
            // Artist: single query — name only.
            container = await MusicBrainzSearcher.SearchArtistsAsync(
                _client!, MbQuote(context.Name), ct);
        }
        else if (context.HierarchyLevel == 1)
        {
            // Album: three-stage cascade so special names (e.g. "<shutdown.exe>") and
            // folder-prefixed names (e.g. "(2000) The Better Life") are still found.
            //
            // Stage 1 — year-stripped name + artist (most precise)
            container = await MusicBrainzSearcher.SearchReleaseGroupsAsync(
                _client!, BuildAlbumQuery(context), ct);

            // Stage 2 — year-stripped name only, no artist constraint
            // Handles mismatched artist names (e.g. capitalisation variants, features).
            if (!(container.Results?.Count > 0))
                container = await MusicBrainzSearcher.SearchReleaseGroupsAsync(
                    _client!, MbQuote(StripYearSuffix(context.Name)), ct);

            // Stage 3 — raw name (no year strip, no normalisation) + artist
            // Last resort: use the exact stored name as a phrase in case StripYearSuffix
            // changed a meaningful part of the title.
            if (!(container.Results?.Count > 0))
            {
                var rawQuoted = MbQuote(context.Name);
                var stage1Quoted = MbQuote(StripYearSuffix(context.Name));
                // Only bother if the raw form is actually different from Stage 1/2
                if (!string.Equals(rawQuoted, stage1Quoted, StringComparison.Ordinal))
                {
                    var artistClause = context.ParentName is not null
                        ? $" AND artist:{MbQuote(context.ParentName)}" : string.Empty;
                    container = await MusicBrainzSearcher.SearchReleaseGroupsAsync(
                        _client!, $"{rawQuoted}{artistClause}", ct);
                }
            }
        }
        else
        {
            container = await MusicBrainzSearcher.SearchArtistsAsync(
                _client!, MbQuote(context.Name), ct);
        }

        return (container.Results ?? [])
            .Select(r => ScoreCandidate(context, r))
            .Where(c => !string.IsNullOrEmpty(c.Metadata.ExternalId))
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
    }

    private static string BuildAlbumQuery(MediaSearchContext ctx)
    {
        var name = StripYearSuffix(ctx.Name);
        var artistClause = ctx.ParentName is not null
            ? $" AND artist:{MbQuote(ctx.ParentName)}" : string.Empty;
        return $"{MbQuote(name)}{artistClause}";
    }

    /// <summary>
    /// Builds a recording Lucene query with an explicit <paramref name="trackName"/>.
    /// Keeping the name separate from context lets the caller cascade through
    /// tag title → filename stem without duplicating the artist/release clause logic.
    /// </summary>
    private static string BuildTrackQuery(string trackName, MediaSearchContext ctx, bool includeRelease)
    {
        var artistClause  = ctx.GrandparentName is not null
            ? $" AND artist:{MbQuote(ctx.GrandparentName)}" : string.Empty;
        var releaseClause = includeRelease && ctx.ParentName is not null
            ? $" AND release:{MbQuote(StripYearSuffix(ctx.ParentName))}" : string.Empty;
        return $"{MbQuote(trackName)}{artistClause}{releaseClause}";
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
        var needsQuotes = s.Any(c => c == ' ' || (!char.IsLetterOrDigit(c) && c != '-'));
        return needsQuotes ? $"\"{s.Replace("\"", "\\\"")}\"" : s;
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
            _ => throw new ArgumentException(
                $"Unknown MusicBrainz entity type: '{type}'. " +
                "Supported types: artist, release-group, recording.")
        };
    }

    /// <summary>
    /// Converts a MusicBrainz browser URL to the internal "type:mbid" format.
    /// Handles both /artist/ and /release-group/ path segments.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        Uri uri;
        try { uri = new Uri(url); }
        catch (UriFormatException)
        {
            throw new ArgumentException($"Cannot parse MusicBrainz URL: '{url}'");
        }

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

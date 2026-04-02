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

    // Matches trailing version qualifiers common in file tags but absent from MusicBrainz recording
    // titles — e.g. "(LP version)", "(Radio Edit)", "(Remastered)", "(Live)", "(Acoustic)".
    private static readonly System.Text.RegularExpressions.Regex VersionQualifierRe =
        new(@"\s*\((?:[^)]*\b(?:version|edit|mix|remix|lp|live|acoustic|demo|instrumental|reprise|remaster(?:ed)?|stereo|mono|explicit|clean|extended|alternate|original)\b[^)]*)\)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        MediaMetadata container;

        if (context.HierarchyLevel == 2)
        {
            // Tracks: try with release constraint first (more precise); if MusicBrainz returns
            // nothing — common for B-sides stored in a different release than their parent folder —
            // retry without the release clause so we can still find the recording.
            container = await MusicBrainzSearcher.SearchRecordingsAsync(
                _client!, BuildTrackQuery(context, includeRelease: true), ct);

            if (!(container.Results?.Count > 0))
                container = await MusicBrainzSearcher.SearchRecordingsAsync(
                    _client!, BuildTrackQuery(context, includeRelease: false), ct);
        }
        else
        {
            container = context.HierarchyLevel switch
            {
                0 => await MusicBrainzSearcher.SearchArtistsAsync(
                         _client!, MbQuote(context.Name), ct),
                1 => await MusicBrainzSearcher.SearchReleaseGroupsAsync(
                         _client!, BuildAlbumQuery(context), ct),
                _ => await MusicBrainzSearcher.SearchArtistsAsync(
                         _client!, MbQuote(context.Name), ct),
            };
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

    private static string BuildTrackQuery(MediaSearchContext ctx, bool includeRelease = true)
    {
        // Strip version qualifiers that appear in file tags but not in MusicBrainz recording
        // titles: "(LP version)" → "", "(Radio Edit)" → "", etc.
        var trackName     = StripVersionQualifiers(StripYearSuffix(ctx.Name));
        var artistClause  = ctx.GrandparentName is not null
            ? $" AND artist:{MbQuote(ctx.GrandparentName)}" : string.Empty;
        var releaseClause = includeRelease && ctx.ParentName is not null
            ? $" AND release:{MbQuote(StripYearSuffix(ctx.ParentName))}" : string.Empty;
        return $"{MbQuote(trackName)}{artistClause}{releaseClause}";
    }

    private static string StripVersionQualifiers(string name)
        => VersionQualifierRe.Replace(name, string.Empty).Trim();

    /// <summary>
    /// Strips Lucene range/special operators then quotes if the term contains whitespace.
    /// Operators like &lt;&gt;{}[]^~ break MusicBrainz SOLR if unescaped in the query string.
    /// </summary>
    private static string MbQuote(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[<>{}[\]^~]", "").Trim();
        return s.Contains(' ') ? $"\"{s.Replace("\"", "\\\"")}\"" : s;
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
        // Strip year prefix/suffix and version qualifiers before comparing so that
        // "(1958) The Blues" → "The Blues" and "Duck and Run (LP version)" → "Duck and Run"
        // both get exact-match scores rather than falling through to "contains".
        var qn = Normalize(StripVersionQualifiers(StripYearSuffix(ctx.Name)));

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

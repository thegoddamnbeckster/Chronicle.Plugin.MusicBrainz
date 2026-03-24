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

    public async Task<MediaMetadata> SearchAsync(string query, string mediaType,
        CancellationToken ct = default)
    {
        EnsureConfigured();

        // Explicit prefix in query takes priority over mediaType parameter.
        // EnrichOneAsync prefixes music queries with "artist:", "album:", or "track:"
        // based on the item's position in the hierarchy so the right entity is searched.
        MediaMetadata container;
        if (query.StartsWith("artist:", StringComparison.OrdinalIgnoreCase))
            container = await MusicBrainzSearcher.SearchArtistsAsync(_client!, query[7..].Trim(), ct);
        else if (query.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
            container = await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query[6..].Trim(), ct);
        else if (query.StartsWith("track:", StringComparison.OrdinalIgnoreCase))
            container = await MusicBrainzSearcher.SearchRecordingsAsync(_client!, query[6..].Trim(), ct);
        else
            // No prefix: route by mediaType parameter
            container = mediaType switch
            {
                "artist" => await MusicBrainzSearcher.SearchArtistsAsync(_client!, query, ct),
                "album"  => await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query, ct),
                "music"  => await MusicBrainzSearcher.SearchRecordingsAsync(_client!, query, ct),
                _        => await MusicBrainzSearcher.SearchArtistsAsync(_client!, query, ct),
            };

        // EnrichOneAsync checks result.ExternalId — pick the best match (first/highest-scored)
        // and return it directly so the enrichment service can record it.
        // Preserve Results list so UI search previews still work.
        var best = container.Results?.FirstOrDefault();
        if (best is null) return container; // no results → ExternalId stays null → NotFound
        best.Results     = container.Results;
        best.TotalResults = container.TotalResults;
        return best;
    }

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

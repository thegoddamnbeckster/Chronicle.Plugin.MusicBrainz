using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Plugin.MusicBrainz.Models;

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
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyUserAgent   = "UserAgent";
    private const string KeyUsername    = "Username";
    private const string KeyPassword    = "Password";
    private const string KeyMaxRetries  = "MaxRetries";
    private const string KeyFetchCovers = "FetchCoverArt";

    // ── Live state ────────────────────────────────────────────────────────────

    private MusicBrainzClient? _client;
    private bool _fetchCovers = true;
    private int _maxRetries = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

        _maxRetries  = int.TryParse(settings.GetValueOrDefault(KeyMaxRetries), out var r) ? r : 3;
        _fetchCovers = !bool.TryParse(settings.GetValueOrDefault(KeyFetchCovers), out var fc) || fc;

        _client?.Dispose();
        _client = new MusicBrainzClient(userAgent, username, password);
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    public async Task<MediaMetadata> SearchAsync(string query, string mediaType,
        CancellationToken ct = default)
    {
        EnsureConfigured();

        return mediaType.ToLowerInvariant() switch
        {
            "artist" => await SearchArtistsAsync(query, ct).ConfigureAwait(false),
            _        => await SearchAlbumsAsync(query, ct).ConfigureAwait(false),
        };
    }

    private async Task<MediaMetadata> SearchAlbumsAsync(string query, CancellationToken ct)
    {
        var path = $"release?query={Uri.EscapeDataString(query)}&fmt=json&limit=20&inc=artist-credits+release-groups+genres";
        var json = await _client!.GetAsync(path, ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbRelease>>(json, JsonOpts);

        var releases = result?.Releases ?? [];
        return new MediaMetadata
        {
            Source       = "musicbrainz",
            TotalResults = result?.Count ?? releases.Count,
            Results      = releases.Select(r => MapRelease(r, null)).ToList(),
        };
    }

    private async Task<MediaMetadata> SearchArtistsAsync(string query, CancellationToken ct)
    {
        var path = $"artist?query={Uri.EscapeDataString(query)}&fmt=json&limit=20&inc=genres";
        var json = await _client!.GetAsync(path, ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbArtist>>(json, JsonOpts);

        var artists = result?.Artists ?? [];
        return new MediaMetadata
        {
            Source       = "musicbrainz",
            TotalResults = result?.Count ?? artists.Count,
            Results      = artists.Select(MapArtist).ToList(),
        };
    }

    // ── IMetadataProvider: get by ID ──────────────────────────────────────────

    /// <summary>
    /// External ID format: "{type}:{mbid}"
    /// e.g. "album:f4179994-7621-4a46-b272-c62d8b3b9b1b"
    ///      "artist:5b11f4ce-a62d-471e-81fc-a69a8278c7da"
    /// </summary>
    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        var parts = externalId.Split(':', 2);
        if (parts.Length != 2)
            throw new ArgumentException(
                $"Invalid MusicBrainz external ID: '{externalId}'. Expected 'type:mbid'.");

        var (type, mbid) = (parts[0].ToLowerInvariant(), parts[1]);

        if (type == "artist")
        {
            var artistJson = await _client!.GetAsync(
                $"artist/{mbid}?fmt=json&inc=genres+releases", ct).ConfigureAwait(false);
            var artist = JsonSerializer.Deserialize<MbArtist>(artistJson, JsonOpts)!;
            return MapArtist(artist);
        }

        var releaseJson = await _client!.GetAsync(
            $"release/{mbid}?fmt=json&inc=artist-credits+release-groups+recordings+genres+label-infos",
            ct).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<MbRelease>(releaseJson, JsonOpts)!;

        string? coverUrl = null;
        if (_fetchCovers)
            coverUrl = $"https://coverartarchive.org/release/{mbid}/front-500";

        return MapRelease(release, coverUrl);
    }

    // ── IMetadataProvider: image ──────────────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (!_fetchCovers) return [];
        return await _client!.GetBytesAsync(url, ct).ConfigureAwait(false);
    }

    // ── IMetadataProvider: health ─────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _client!.GetAsync(
                "artist/4a4ee089-93b9-4a56-a4f0-9f234f0cb04f?fmt=json", ct).ConfigureAwait(false);
            return json.Contains("Radiohead");
        }
        catch { return false; }
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static MediaMetadata MapRelease(MbRelease r, string? coverUrl)
    {
        var artists = r.ArtistCredit?
            .Select(ac => ac.Name ?? ac.Artist?.Name ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList() ?? [];

        var genres = r.Genres?
            .OrderByDescending(g => g.Count)
            .Select(g => g.Name)
            .OfType<string>()
            .ToList() ?? [];

        var totalRuntime = r.Media?
            .SelectMany(m => m.Tracks ?? [])
            .Sum(t => t.Length ?? 0) / 60_000;  // ms → minutes

        return new MediaMetadata
        {
            ExternalId     = $"album:{r.Id}",
            Source         = "musicbrainz",
            Title          = r.Title ?? string.Empty,
            Year           = ParseYear(r.Date),
            PosterUrl      = coverUrl,
            RuntimeMinutes = totalRuntime > 0 ? totalRuntime : null,
            Genres         = genres,
            Cast           = artists,
            Directors      = [],
        };
    }

    private static MediaMetadata MapArtist(MbArtist a) => new()
    {
        ExternalId = $"artist:{a.Id}",
        Source     = "musicbrainz",
        Title      = a.Name ?? string.Empty,
        Year       = ParseYear(a.LifeSpan?.Begin),
        Genres     = a.Genres?
            .OrderByDescending(g => g.Count)
            .Select(g => g.Name)
            .OfType<string>()
            .ToList() ?? [],
        Overview   = FormatArtistOverview(a),
    };

    private static string? FormatArtistOverview(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type))    parts.Add(a.Type);
        if (!string.IsNullOrEmpty(a.Country)) parts.Add($"Country: {a.Country}");
        if (a.LifeSpan?.Begin is { } begin)
        {
            var span = (a.LifeSpan.Ended == true) && a.LifeSpan.End is { } end
                ? $"Active: {begin}–{end}"
                : $"Active from: {begin}";
            parts.Add(span);
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static int? ParseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "MusicBrainzMetadataProvider has not been configured. Call Configure() first.");
    }
}

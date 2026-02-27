using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Chronicle metadata provider for MusicBrainz.
/// Supports "album" and "artist" media types. No API key required.
/// </summary>
public sealed class MusicBrainzMetadataProvider : IMetadataProvider
{
    // ── IMetadataProvider identity ────────────────────────────────────────────

    public string PluginId => "musicbrainz";
    public string Name     => "MusicBrainz";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyUserAgent   = "user_agent";
    private const string KeyFetchCovers = "fetch_cover_art";

    // ── Live state ────────────────────────────────────────────────────────────

    private MusicBrainzClient? _client;
    private bool _fetchCovers = true;

    // ── IMetadataProvider: static declarations ────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
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
                Key         = KeyUserAgent,
                Label       = "Application User-Agent",
                Description = "MusicBrainz requires a descriptive User-Agent string. " +
                              "Format: AppName/Version (contact@example.com). " +
                              "Defaults to Chronicle/1.0 if left blank.",
                Type        = SettingType.Text,
                Required    = false,
                DefaultValue = "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)",
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
        settings.TryGetValue(KeyUserAgent,   out var userAgent);
        settings.TryGetValue(KeyFetchCovers, out var fetchCoversStr);

        var ua = string.IsNullOrWhiteSpace(userAgent)
            ? "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)"
            : userAgent;

        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        _client = new MusicBrainzClient(http);

        _fetchCovers = !bool.TryParse(fetchCoversStr, out var fc) || fc;
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    public async Task<MediaMetadata> SearchAsync(string query, string mediaType,
        CancellationToken ct = default)
    {
        EnsureConfigured();

        return mediaType.ToLowerInvariant() switch
        {
            "artist" => await SearchArtistsAsync(query, ct).ConfigureAwait(false),
            _        => await SearchAlbumsAsync(query, ct).ConfigureAwait(false),   // default → album
        };
    }

    private async Task<MediaMetadata> SearchAlbumsAsync(string query, CancellationToken ct)
    {
        var resp = await _client!.SearchReleasesAsync(query, ct).ConfigureAwait(false);
        return new MediaMetadata
        {
            Source       = "musicbrainz",
            TotalResults = resp.ReleaseCount,
            Results      = resp.Releases.Select(r => MapRelease(r, null)).ToList(),
        };
    }

    private async Task<MediaMetadata> SearchArtistsAsync(string query, CancellationToken ct)
    {
        var resp = await _client!.SearchArtistsAsync(query, ct).ConfigureAwait(false);
        return new MediaMetadata
        {
            Source       = "musicbrainz",
            TotalResults = resp.ArtistCount,
            Results      = resp.Artists.Select(MapArtist).ToList(),
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
            throw new ArgumentException($"Invalid MusicBrainz external ID: '{externalId}'. Expected 'type:mbid'.");

        var (type, mbid) = (parts[0].ToLowerInvariant(), parts[1]);

        if (type == "artist")
            return MapArtist(await _client!.GetArtistAsync(mbid, ct).ConfigureAwait(false));

        var release = await _client!.GetReleaseAsync(mbid, ct).ConfigureAwait(false);
        string? coverUrl = _fetchCovers ? MusicBrainzClient.CoverArtUrl(mbid) : null;
        return MapRelease(release, coverUrl);
    }

    // ── IMetadataProvider: image ──────────────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();

        // The URL encodes the release MBID for cover art
        if (!_fetchCovers) return [];

        // Extract MBID from URL pattern "https://coverartarchive.org/release/{mbid}/front-500"
        var parts = url.Split('/');
        var mbidIndex = Array.IndexOf(parts, "release") + 1;
        if (mbidIndex > 0 && mbidIndex < parts.Length)
        {
            var mbid = parts[mbidIndex];
            return await _client!.GetCoverArtAsync(mbid, ct).ConfigureAwait(false);
        }

        return [];
    }

    // ── IMetadataProvider: health ─────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        return await _client.PingAsync(ct).ConfigureAwait(false);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static MediaMetadata MapRelease(MbRelease r, string? coverUrl)
    {
        var artists = r.ArtistCredit?
            .Select(ac => ac.Name ?? ac.Artist?.Name ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList() ?? [];

        var genres = r.Genres?
            .OrderByDescending(g => g.Count ?? 0)
            .Select(g => g.Name)
            .ToList() ?? [];

        var totalRuntime = r.Media?
            .SelectMany(m => m.Tracks ?? [])
            .Sum(t => t.Length ?? 0) / 60_000;  // ms → minutes

        return new MediaMetadata
        {
            ExternalId     = $"album:{r.Id}",
            Source         = "musicbrainz",
            Title          = r.Title,
            Year           = ParseYear(r.Date),
            PosterUrl      = coverUrl,
            RuntimeMinutes = totalRuntime > 0 ? totalRuntime : null,
            Genres         = genres,
            Cast           = artists,          // "cast" = performers for music
            Directors      = [],               // not applicable for albums
        };
    }

    private static MediaMetadata MapArtist(MbArtist a) => new()
    {
        ExternalId = $"artist:{a.Id}",
        Source     = "musicbrainz",
        Title      = a.Name,
        Year       = ParseYear(a.LifeSpan?.Begin),
        Genres     = a.Genres?.OrderByDescending(g => g.Count ?? 0).Select(g => g.Name).ToList() ?? [],
        Overview   = FormatArtistOverview(a),
    };

    private static string? FormatArtistOverview(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type))    parts.Add(a.Type);
        if (!string.IsNullOrEmpty(a.Country)) parts.Add($"Country: {a.Country}");
        if (a.LifeSpan?.Begin is { } begin)
        {
            var span = a.LifeSpan.Ended && a.LifeSpan.End is { } end
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

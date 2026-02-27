using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Thin wrapper around the MusicBrainz JSON Web Service v2.
/// <para>
/// MusicBrainz requires a descriptive User-Agent for all requests:
/// https://wiki.musicbrainz.org/MusicBrainz_API/Rate_Limiting
/// </para>
/// </summary>
internal sealed class MusicBrainzClient
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";

    // MusicBrainz cover art is served by the Cover Art Archive
    private const string CoverArtBase = "https://coverartarchive.org/release/";

    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MusicBrainzClient(HttpClient http)
    {
        _http = http;
    }

    // ── Releases ──────────────────────────────────────────────────────────────

    public Task<MbReleaseSearch> SearchReleasesAsync(string query, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/release?query={Uri.EscapeDataString(query)}&fmt=json&limit=20&inc=artist-credits+release-groups+genres";
        return GetAsync<MbReleaseSearch>(url, ct);
    }

    public Task<MbRelease> GetReleaseAsync(string mbid, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/release/{mbid}?fmt=json&inc=artist-credits+release-groups+recordings+genres+label-infos";
        return GetAsync<MbRelease>(url, ct);
    }

    // ── Artists ───────────────────────────────────────────────────────────────

    public Task<MbArtistSearch> SearchArtistsAsync(string query, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/artist?query={Uri.EscapeDataString(query)}&fmt=json&limit=20&inc=genres";
        return GetAsync<MbArtistSearch>(url, ct);
    }

    public Task<MbArtist> GetArtistAsync(string mbid, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/artist/{mbid}?fmt=json&inc=genres+releases";
        return GetAsync<MbArtist>(url, ct);
    }

    // ── Cover art (Cover Art Archive) ─────────────────────────────────────────

    /// <summary>
    /// Downloads the front cover art for a release MBID.
    /// Returns an empty array if no cover art exists (HTTP 404).
    /// </summary>
    public async Task<byte[]> GetCoverArtAsync(string releaseMbid, CancellationToken ct = default)
    {
        var url = $"{CoverArtBase}{releaseMbid}/front-500";
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the URL for a release's front cover art (500 px), or null.</summary>
    public static string? CoverArtUrl(string releaseMbid) =>
        $"{CoverArtBase}{releaseMbid}/front-500";

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            // Lightweight lookup — any valid entity
            var url = $"{BaseUrl}/release?query=title:a&limit=1&fmt=json";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("MusicBrainz returned null response.");
    }
}

using System.Net;

namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Thread-safe MusicBrainz API client with built-in rate limiting.
///
/// Root-caused live (2026-09-28): this used to pace authenticated requests at 240ms (~4.2/s),
/// on the assumption that logging in raised the allowed rate the way it does for some other
/// APIs. MusicBrainz's own published rate-limiting docs
/// (musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting) say otherwise: the enforced limit is "on
/// average 1 request per second" PER IP ADDRESS, all-or-nothing ("we decline 100% of [requests],
/// until the rate drops to 1 per second or lower") -- with no mention of authentication raising
/// that ceiling for a generic client. The only higher-throughput carve-out named in that doc is
/// for a short list of specific, individually-recognized applications ("Headphones, beets"),
/// not "any authenticated request." So both modes now pace the same, conservative interval.
/// </summary>
internal sealed class MusicBrainzClient : IDisposable
{
    private const string BaseUrl      = "https://musicbrainz.org/ws/2";
    private const string CoverArtBase = "https://coverartarchive.org";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private readonly TimeSpan _minInterval;

    public MusicBrainzClient(string userAgent, string? username, string? password)
    {
        // ~0.91 req/s -- under MusicBrainz's documented 1 req/s-per-IP limit regardless of
        // whether this client authenticates (see this class's own doc for why authentication
        // does not raise the limit here).
        _minInterval = TimeSpan.FromMilliseconds(1100);

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            handler.Credentials = new NetworkCredential(username, password);
            handler.PreAuthenticate = true;
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    /// <summary>Test-only constructor that accepts a pre-built HttpClient and throttle interval.</summary>
    internal MusicBrainzClient(HttpClient http, TimeSpan minInterval)
    {
        _http        = http;
        _minInterval = minInterval;
    }

    /// <summary>Test-only visibility into the pacing interval the production constructor chose.</summary>
    internal TimeSpan MinInterval => _minInterval;

    /// <summary>GET MusicBrainz API path (auto-throttled, retries on 503 and 200+error body).</summary>
    public async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        const int maxRetries = 4;
        var delay = TimeSpan.FromSeconds(2);

        for (int attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            var url = _http.BaseAddress is not null
                ? new Uri(_http.BaseAddress, path.TrimStart('/')).ToString()
                : $"{BaseUrl}/{path.TrimStart('/')}";
            var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && attempt < maxRetries)
            {
                // MusicBrainz returns 503 when we exceed the rate limit.
                // Back off and retry; the backoff also resets _lastRequest so the
                // next ThrottleAsync interval starts fresh.
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                continue;
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);

            // MusicBrainz sometimes returns HTTP 200 with a JSON error body instead of 503
            // when rate-limiting. Detect this and treat it identically to a 503 so that the
            // retry logic fires rather than silently treating the error as "no results".
            if (content.Contains("\"error\"", StringComparison.Ordinal) && attempt < maxRetries)
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                continue;
            }

            if (content.Contains("\"error\"", StringComparison.Ordinal))
                throw new HttpRequestException(
                    $"MusicBrainz returned an error response after {attempt + 1} attempt(s): {content[..Math.Min(content.Length, 200)]}");

            return content;
        }
    }

    /// <summary>
    /// GET Cover Art Archive (auto-throttled).
    /// Returns "{}" for 404 (no images) and for any other non-success status.
    /// CAA can return 401/403/500 for releases with no CAA entry or during outages.
    /// Cover art is supplemental — callers must not fail enrichment because of it.
    /// </summary>
    public async Task<string> GetCoverArtAsync(string path, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        var url = $"{CoverArtBase}/{path.TrimStart('/')}";
        var response = await _http.GetAsync(url, ct);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync(ct);
        // Any non-success (404 = no art, 401/403/500 = CAA unavailable for this release)
        // is treated as "no cover art available" — never throw from a supplemental fetch.
        return "{}";
    }

    /// <summary>Download raw image bytes (auto-throttled).</summary>
    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        return await _http.GetByteArrayAsync(url, ct);
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < _minInterval)
                await Task.Delay(_minInterval - elapsed, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    public void Dispose()
    {
        _throttle.Dispose();
        _http.Dispose();
    }
}

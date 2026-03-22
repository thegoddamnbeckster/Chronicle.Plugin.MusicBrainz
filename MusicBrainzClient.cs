using System.Net;

namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Thread-safe MusicBrainz API client with built-in rate limiting.
/// Anonymous: 1 req/sec. Authenticated (HTTP Digest): 5 req/sec.
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
        _minInterval = string.IsNullOrEmpty(username)
            ? TimeSpan.FromMilliseconds(1200)   // 1 req/sec anonymous  (+20% over limit)
            : TimeSpan.FromMilliseconds(240);   // 5 req/sec authenticated (+20% over limit)

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            handler.Credentials = new NetworkCredential(username, password);
            handler.PreAuthenticate = true;
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>GET MusicBrainz API path (auto-throttled, retries on 503).</summary>
    public async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        const int maxRetries = 4;
        var delay = TimeSpan.FromSeconds(2);

        for (int attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            var url = $"{BaseUrl}/{path.TrimStart('/')}";
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
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>GET Cover Art Archive (auto-throttled). Returns "{}" on 404.</summary>
    public async Task<string> GetCoverArtAsync(string path, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        var url = $"{CoverArtBase}/{path.TrimStart('/')}";
        var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return "{}";
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
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

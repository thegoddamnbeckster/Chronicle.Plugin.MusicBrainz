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
            ? TimeSpan.FromMilliseconds(1100)   // 1 req/sec anonymous
            : TimeSpan.FromMilliseconds(220);   // 5 req/sec authenticated

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

    /// <summary>GET MusicBrainz API path (auto-throttled).</summary>
    public async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        var url = $"{BaseUrl}/{path.TrimStart('/')}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
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

using System.Net;
using System.Text.Json;

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

    /// <summary>How many times a rate-limited or error-bodied answer is retried before giving up (from the Max Retries setting).</summary>
    public int MaxRetries { get; set; } = 4;

    /// <param name="inner">Test seam: a stand-in for the network. When given, the account is still passed in but the stand-in answers, so a test can prove the account never reaches an error. Production passes nothing.</param>
    public MusicBrainzClient(string userAgent, string? username, string? password, HttpMessageHandler? inner = null)
    {
        // ~0.91 req/s -- under MusicBrainz's documented 1 req/s-per-IP limit regardless of
        // whether this client authenticates (see this class's own doc for why authentication
        // does not raise the limit here).
        _minInterval = TimeSpan.FromMilliseconds(1100);

        HttpMessageHandler handler;
        if (inner is not null)
            handler = inner;
        else
        {
            var real = new HttpClientHandler();
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                real.Credentials = new NetworkCredential(username, password);
                real.PreAuthenticate = true;
            }
            handler = real;
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        // One REQUEST may take this long. It used to be 25 s, because the host gave a whole call 25 s; the manifest now declares a call
        // deadline of hours (call_timeout_seconds), so a single slow answer (a browse page of 100 recordings, a busy Cover Art Archive)
        // must not end a fetch that has already made hundreds of requests -- it did: three artists failed 76 to 212 s in, on one request
        // that took more than 25 s (2026-11-07). A request that still times out is asked again (see GetOrRetryOnTimeoutAsync).
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    /// <summary>
    /// One GET, asked again when the HttpClient's own timeout cancels it (not when the caller cancels). A service that is merely slow
    /// for one request must not fail the whole fetch around it; after <see cref="MaxRetries"/> further tries the failure is reported
    /// as the timeout it is, so the host retries the item later.
    /// </summary>
    private async Task<HttpResponseMessage> GetOrRetryOnTimeoutAsync(string url, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _http.GetAsync(url, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxRetries)
                    throw new HttpRequestException(
                        $"No answer within {_http.Timeout.TotalSeconds:0} s after {attempt + 1} attempt(s): {new Uri(url).AbsolutePath}", ex);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                await ThrottleAsync(ct);
            }
        }
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
        var maxRetries = MaxRetries;
        var delay = TimeSpan.FromSeconds(2);

        for (int attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            var url = _http.BaseAddress is not null
                ? new Uri(_http.BaseAddress, path.TrimStart('/')).ToString()
                : $"{BaseUrl}/{path.TrimStart('/')}";
            var response = await GetOrRetryOnTimeoutAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new Chronicle.Plugins.PluginAuthException("chronicle.plugin.musicbrainz",
                    "MusicBrainz refused the request as unauthorised. If you entered a username and password in the plugin settings, check them, or clear both to browse anonymously.");

            if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                // Honour the server's own Retry-After when it sends one (capped), otherwise the doubling back-off below.
                if (response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero) delay = ra > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : ra;
                // MusicBrainz returns 503 when we exceed the rate limit.
                // Back off and retry; the backoff also resets _lastRequest so the
                // next ThrottleAsync interval starts fresh.
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                continue;
            }

            // Out of retries while MusicBrainz is still saying "slow down": report it as a rate limit so the host pauses the pass
            // instead of counting it as this item's failure.
            if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("MusicBrainz is rate limiting requests", null, System.Net.HttpStatusCode.TooManyRequests);

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
        var url = $"{CoverArtBase}/{path.TrimStart('/')}";
        if (_resume.TryGet(url) is { } known) return known;   // answered earlier in a fetch that did not finish: not asked again

        var delay = TimeSpan.FromSeconds(2);
        for (int attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            var response = await GetOrRetryOnTimeoutAsync(url, ct);
            if (response.IsSuccessStatusCode)
                return _resume.Put(url, await response.Content.ReadAsStringAsync(ct));

            // A busy or failing archive is NOT "no cover art": that answer is kept as the item's complete data and never asked again.
            // Wait and ask again; if it stays down, fail the fetch so the host tries the item later (what was already fetched is kept).
            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt >= MaxRetries)
                    throw new HttpRequestException(
                        $"The Cover Art Archive kept answering {(int)response.StatusCode} for {path}", null, response.StatusCode);
                if (response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero) delay = ra > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : ra;
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                continue;
            }

            // 404 (no images for this release or group) and the other client errors (400/401/403) are the archive's real answer: no art.
            return _resume.Put(url, "{}");
        }
    }

    /// <summary>
    /// Every entity of one kind linked to another, all of them: a lookup returns at most 25 of any list (an artist with 1,363 release
    /// groups showed 25), so the rest is read by browsing, a hundred at a time, until the count MusicBrainz reports is reached.
    /// <paramref name="entity"/> is the singular name ("release-group", "release", "recording", "work"); <paramref name="linkParam"/> names
    /// what they are linked to ("artist", "release-group", "recording"). Each page is remembered, so a fetch that fails near its end
    /// resumes from there when the item is tried again instead of starting over. Items come back exactly as MusicBrainz sent them.
    /// </summary>
    public async Task<List<JsonElement>> BrowseAllAsync(string entity, string linkParam, string mbid, string? inc, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var all = new List<JsonElement>();
        var incPart = string.IsNullOrEmpty(inc) ? "" : $"&inc={inc}";
        var offset = 0;
        while (true)
        {
            var path = $"{entity}?{linkParam}={mbid}{incPart}&limit={pageSize}&offset={offset}&fmt=json";
            var body = _resume.TryGet(path);
            if (body is null) body = _resume.Put(path, await GetAsync(path, ct));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var n = 0;
            if (root.TryGetProperty($"{entity}s", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray()) { all.Add(item.Clone()); n++; }
            var total = root.TryGetProperty($"{entity}-count", out var c) && c.TryGetInt32(out var tc) ? tc : all.Count;
            offset += n;
            if (n == 0 || offset >= total) return all;   // an empty page ends it too: a count that never arrives cannot loop for ever
        }
    }

    /// <summary>GET that remembers its answer (see <see cref="BrowseAllAsync"/>): for the many lookups inside one long fetch.</summary>
    public async Task<string> GetResumableAsync(string path, CancellationToken ct = default) =>
        _resume.TryGet(path) ?? _resume.Put(path, await GetAsync(path, ct));

    // What this client has already been told in fetches that have not finished: a long fetch that fails near its end resumes from there.
    private readonly ResumeCache _resume = new();

    private sealed class ResumeCache
    {
        private const long MaxBytes = 192L * 1024 * 1024;   // memory only; when full it starts over (it is a convenience, never the data)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, string Body)> _byUrl = new();
        private long _bytes;
        private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);

        public string? TryGet(string key) =>
            _byUrl.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < Fresh ? e.Body : null;

        public string Put(string key, string body)
        {
            if (Interlocked.Add(ref _bytes, body.Length * 2L) > MaxBytes) { _byUrl.Clear(); Interlocked.Exchange(ref _bytes, body.Length * 2L); }
            _byUrl[key] = (DateTime.UtcNow, body);
            return body;
        }
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

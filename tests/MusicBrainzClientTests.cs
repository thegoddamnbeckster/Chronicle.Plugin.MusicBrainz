using System.Net;
using Chronicle.Plugin.MusicBrainz;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>
/// Tests for MusicBrainzClient rate-limit and error handling.
/// </summary>
public class MusicBrainzClientTests
{
    // ── 200 + JSON error body handling ────────────────────────────────────────

    /// <summary>
    /// MusicBrainz sometimes returns HTTP 200 with {"error":"..."} instead of a 503
    /// when a client is rate-limited. The client must treat this like a transient failure
    /// and retry rather than silently returning the error JSON as if it were valid data.
    /// When all retries are exhausted it must throw, not return garbage JSON.
    /// </summary>
    [Fact]
    public async Task GetAsync_200WithErrorBody_AlwaysThrowsRatherThanReturningErrorJson()
    {
        // Arrange: every response is HTTP 200 with a JSON error body
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"error\": \"Your requests are exceeding the allowable rate limit.\"}"),
            });

        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.mb.org/") },
            minInterval: TimeSpan.FromMilliseconds(1));

        // Act + Assert: must NOT return the error JSON; must throw
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("artist?query=test&fmt=json"));

        // The error JSON must not appear in the return value — verified by the throw above.
        Assert.True(handler.CallCount > 1, "Expected at least one retry before throwing");
    }

    [Fact]
    public async Task GetAsync_200WithErrorBody_ThenSuccess_ReturnsSuccessContent()
    {
        // Arrange: first call is rate-limited (200+error), second call succeeds
        int call = 0;
        var handler = new StubHandler(_ =>
        {
            call++;
            if (call == 1)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"error\": \"rate limited\"}"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"artists\":[],\"count\":0}"),
            };
        });

        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.mb.org/") },
            minInterval: TimeSpan.FromMilliseconds(1));

        // Act
        var result = await client.GetAsync("artist?query=test&fmt=json");

        // Assert: should return the valid JSON, not the error JSON
        Assert.Contains("artists", result);
        Assert.DoesNotContain("error", result);
    }

    [Fact]
    public async Task GetAsync_503Response_RetriesAndEventuallyThrows()
    {
        // Existing 503 retry behaviour must be preserved
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.mb.org/") },
            minInterval: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("artist?query=test&fmt=json"));

        Assert.True(handler.CallCount > 1, "Expected retries on 503");
    }

    [Fact]
    public async Task GetAsync_200ValidJson_ReturnsContent()
    {
        const string json = "{\"artists\":[{\"id\":\"abc\",\"name\":\"Metallica\"}],\"count\":1}";
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });

        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.mb.org/") },
            minInterval: TimeSpan.FromMilliseconds(1));

        var result = await client.GetAsync("artist?query=test&fmt=json");

        Assert.Equal(json, result);
        Assert.Equal(1, handler.CallCount); // no retries needed
    }

    // ── Rate-limit pacing ──────────────────────────────────────────────────────

    /// <summary>
    /// Root-caused live (2026-09-28): the production constructor used to pace authenticated
    /// requests at 240ms (~4.2 req/s), on the mistaken assumption that logging in raises
    /// MusicBrainz's per-IP limit. MusicBrainz's own published rate-limiting docs say the
    /// enforced limit is 1 req/s PER IP, all-or-nothing, regardless of authentication -- so both
    /// modes must pace at (or under) that same rate.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("someuser", "somepass")]
    public void ProductionConstructor_PacesAtOrUnderOneRequestPerSecond_RegardlessOfAuthentication(
        string? username, string? password)
    {
        var client = new MusicBrainzClient("Chronicle/1.0 (test)", username, password);

        Assert.True(client.MinInterval >= TimeSpan.FromSeconds(1),
            $"Expected at least a 1s interval (MusicBrainz's own 1 req/s-per-IP limit), got {client.MinInterval}.");
    }

    [Fact]
    public void ProductionConstructor_PacesTheSame_AuthenticatedOrNot()
    {
        var anonymous     = new MusicBrainzClient("Chronicle/1.0 (test)", null, null);
        var authenticated = new MusicBrainzClient("Chronicle/1.0 (test)", "someuser", "somepass");

        Assert.Equal(anonymous.MinInterval, authenticated.MinInterval);
    }
}

/// <summary>Minimal HttpMessageHandler stub for unit tests.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
    public int CallCount { get; private set; }

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        => _factory = factory;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_factory(request));
    }
}

using System.Net;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>The checklist's C4, K7 and K10: a rejected account is one call and an auth error; a failing call never echoes the account;
/// endless slow-downs end as a rate limit. (The production constructor takes an optional stand-in handler for exactly this.)</summary>
public class SecretProofTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task A_rejected_account_is_one_call_and_an_auth_error()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new MusicBrainzClient(new HttpClient(h), TimeSpan.Zero);
        var ex = await Assert.ThrowsAsync<Chronicle.Plugins.PluginAuthException>(() => client.GetAsync("artist/x", default));
        Assert.Equal(1, h.Calls);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_failing_call_leaves_the_account_out_of_the_error(HttpStatusCode code)
    {
        const string User = "SECRET-USER-1234567890";
        const string Pass = "SECRET-PASS-0987654321";
        var h = new Handler(_ =>
        {
            var r = new HttpResponseMessage(code);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return r;
        });
        var client = new MusicBrainzClient("Chronicle-Test/1.0", User, Pass, h) { MaxRetries = 1 };
        var ex = await Record.ExceptionAsync(() => client.GetAsync("artist/x", default));
        Assert.NotNull(ex);
        Assert.DoesNotContain(User, ex!.ToString());
        Assert.DoesNotContain(Pass, ex.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Being_told_to_slow_down_for_ever_is_reported_as_a_rate_limit_after_the_retries(HttpStatusCode code)
    {
        var h = new Handler(_ =>
        {
            var r = new HttpResponseMessage(code);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return r;
        });
        var client = new MusicBrainzClient(new HttpClient(h), TimeSpan.Zero) { MaxRetries = 2 };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("artist/x", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, h.Calls);
    }
}

using System.Net;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>
/// One slow request must not end a long fetch. Three artists failed 76 to 212 seconds into their fetch on a single request that took
/// longer than the client's 25 s per-request limit (2026-11-07). A request the HttpClient's own timeout cancels is asked again; only
/// after the retries are used is it reported, as a timeout; and the CALLER's cancellation is never retried.
/// </summary>
public class SlowRequestTests
{
    private sealed class Handler(Func<int, HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(n, request));
        }
    }

    // A handler that throws the cancellation the HttpClient's own timeout produces (not tied to the caller's token).
    private static HttpResponseMessage TimedOut() => throw new TaskCanceledException("the request timed out", new TimeoutException());

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static MusicBrainzClient Client(Handler h, int retries) => new(new HttpClient(h), TimeSpan.Zero) { MaxRetries = retries };

    [Fact]
    public async Task A_request_that_times_out_once_is_asked_again_and_the_answer_is_used()
    {
        var h = new Handler((n, _) => n == 1 ? TimedOut() : Json("{\"id\":\"a\"}"));

        var body = await Client(h, retries: 2).GetAsync("artist/a?fmt=json");

        Assert.Equal("{\"id\":\"a\"}", body);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task A_request_that_keeps_timing_out_is_reported_as_a_timeout_after_the_retries_and_names_what_it_was_asking_for()
    {
        var h = new Handler((_, _) => TimedOut());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client(h, retries: 1).GetAsync("recording?artist=a-1&limit=100&offset=0&fmt=json"));

        Assert.Equal(2, h.Calls);   // the first try and one retry
        Assert.Contains("No answer within", ex.Message);
        Assert.Contains("/recording", ex.Message);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_retried()
    {
        var h = new Handler((_, _) => Json("{}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h, retries: 3).GetAsync("artist/a?fmt=json", cts.Token));

        Assert.True(h.Calls <= 1, "a cancelled caller is not asked again");
    }

    [Fact]
    public async Task The_cover_art_archive_is_retried_on_a_timeout_the_same_way()
    {
        var h = new Handler((n, _) => n == 1 ? TimedOut() : new HttpResponseMessage(HttpStatusCode.NotFound));

        var images = await CoverArtArchiveClient.GetImagesAsync(Client(h, retries: 2), "release", "r-1", default);

        Assert.Empty(images);   // after the retry the archive says 404: no art, which is the real answer
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task A_long_fetch_survives_one_slow_request_in_the_middle()
    {
        var slowOnce = true;
        var h = new Handler((_, req) =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("/release/rel-2") && slowOnce) { slowOnce = false; return TimedOut(); }
            if (u.Contains("/release-group/g-1")) return Json("{\"id\":\"g-1\",\"title\":\"An Album\"}");
            if (u.Contains("/release?release-group=g-1"))
                return Json("{\"release-count\":3,\"releases\":[{\"id\":\"rel-1\"},{\"id\":\"rel-2\"},{\"id\":\"rel-3\"}]}");
            if (u.Contains("/release/rel-")) return Json("{\"id\":\"x\",\"title\":\"Edition\"}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);   // no cover art
        });

        var meta = await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(Client(h, retries: 2), "g-1", default);

        Assert.Equal(3, meta.ExtendedData!.Value.GetProperty("releases").GetArrayLength());   // every release, despite the slow one
    }
}

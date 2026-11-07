using System.Net;
using System.Text.Json;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>
/// Everything MusicBrainz lists for a recording is kept, however long the list: every community tag (it used to be the top 20), every release
/// (the top 10) and every linked work (the first 3).
/// </summary>
public class EverythingIsKeptTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static string Recording(int tags, int releases, int works)
    {
        var t = string.Join(",", Enumerable.Range(1, tags).Select(i => "{\"name\":\"tag " + i + "\",\"count\":" + (1000 - i) + "}"));
        var r = string.Join(",", Enumerable.Range(1, releases).Select(i => "{\"id\":\"rel-" + i + "\",\"title\":\"Release " + i + "\",\"status\":\"Official\"}"));
        var w = string.Join(",", Enumerable.Range(1, works).Select(i => "{\"type\":\"performance\",\"target-type\":\"work\",\"work\":{\"id\":\"work-" + i + "\",\"title\":\"Work " + i + "\"}}"));
        return "{\"id\":\"rec-1\",\"title\":\"A Song\",\"tags\":[" + t + "],\"releases\":[" + r + "],\"relations\":[" + w + "]}";
    }

    [Fact]
    public async Task A_recording_keeps_every_tag_every_release_and_every_linked_work()
    {
        var h = new Handler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("/recording/")) return Json(Recording(tags: 45, releases: 0, works: 7));
            if (u.Contains("/release?recording=")) return Json("{\"release-count\":23,\"releases\":[" + string.Join(",", Enumerable.Range(1, 23).Select(i => "{\"id\":\"rel-" + i + "\",\"title\":\"Release " + i + "\"}")) + "]}");
            if (u.Contains("/work/")) return Json("{\"id\":\"w\",\"title\":\"A Work\",\"relations\":[]}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);   // cover art: none
        });
        var client = new MusicBrainzClient(new HttpClient(h), TimeSpan.Zero) { MaxRetries = 0 };   // no pacing in a test

        var meta = await MusicBrainzEntityFetcher.FetchRecordingAsync(client, "rec-1", default);

        Assert.Equal(45, meta.Tags.Count);
        Assert.Equal("tag 1", meta.Tags[0]);        // still ordered by votes
        Assert.Equal("tag 45", meta.Tags[44]);
        var ext = meta.ExtendedData!.Value;
        Assert.Equal(23, ext.GetProperty("releases").GetArrayLength());
        Assert.Equal(7, ext.GetProperty("works").GetArrayLength());
        Assert.Equal(7, h.Urls.Count(u => u.Contains("/work/")));
    }
}

using System.Net;
using System.Text.Json;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>
/// Everything MusicBrainz lists is fetched, however long the lists are. A lookup shows only 25 of any list (Elvis Presley: 1,363 release
/// groups behind a lookup that showed 25), so the lists are browsed in full; every release of a group and every release group of an
/// artist gets its cover art; a busy or failing Cover Art Archive is retried and never recorded as "no cover art"; and what was fetched
/// is remembered, so a long fetch that fails near its end resumes instead of starting over.
/// </summary>
public class EveryThingIsFetchedTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
        public int Count(string part) { lock (Urls) return Urls.Count(u => u.Contains(part, StringComparison.Ordinal)); }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    // No pacing in a test (the real client waits 1.1 s between requests, so a test of a large artist would take minutes).
    private static MusicBrainzClient Client(Handler h, int retries = 0) =>
        new(new HttpClient(h), TimeSpan.Zero) { MaxRetries = retries };

    private static string Page(string entity, int total, int offset, int limit, Func<int, string> item)
    {
        var n = Math.Max(0, Math.Min(limit, total - offset));
        return "{\"" + entity + "-count\":" + total + ",\"" + entity + "-offset\":" + offset + ",\"" + entity + "s\":["
               + string.Join(",", Enumerable.Range(offset, n).Select(item)) + "]}";
    }

    private static int Q(HttpRequestMessage r, string key)
    {
        var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
        return int.Parse(q[key] ?? "0");
    }

    // ── browsing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Browsing_reads_every_page_until_the_count_is_reached()
    {
        var h = new Handler(r => Json(Page("release-group", 250, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"rg-" + i + "\",\"title\":\"T" + i + "\"}")));
        var all = await Client(h).BrowseAllAsync("release-group", "artist", "a-1", null);

        Assert.Equal(250, all.Count);
        Assert.Equal("rg-0", all[0].GetProperty("id").GetString());
        Assert.Equal("rg-249", all[249].GetProperty("id").GetString());
        Assert.Equal(3, h.Count("release-group?artist=a-1"));   // 100 + 100 + 50
    }

    [Fact]
    public async Task Browsing_an_empty_list_is_one_request_and_an_empty_result()
    {
        var h = new Handler(r => Json(Page("work", 0, 0, 100, i => "{}")));
        Assert.Empty(await Client(h).BrowseAllAsync("work", "artist", "a-1", null));
        Assert.Equal(1, h.Count("work?artist="));
    }

    [Fact]
    public async Task A_page_that_never_arrives_ends_the_browse_instead_of_looping_for_ever()
    {
        // the service says there are 500 but answers every page empty
        var h = new Handler(r => Json("{\"recording-count\":500,\"recordings\":[]}"));
        Assert.Empty(await Client(h).BrowseAllAsync("recording", "artist", "a-1", null));
        Assert.Equal(1, h.Count("recording?artist="));
    }

    // ── a long fetch resumes ─────────────────────────────────────────────────

    [Fact]
    public async Task A_fetch_that_fails_near_its_end_asks_again_only_for_what_it_had_not_got()
    {
        var failSecondPage = true;
        var h = new Handler(r =>
        {
            var offset = Q(r, "offset");
            if (offset == 100 && failSecondPage) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Json(Page("release", 150, offset, Q(r, "limit"), i => "{\"id\":\"r-" + i + "\"}"));
        });
        var client = Client(h);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.BrowseAllAsync("release", "release-group", "g-1", null));
        failSecondPage = false;
        var all = await client.BrowseAllAsync("release", "release-group", "g-1", null);

        Assert.Equal(150, all.Count);
        Assert.Equal(1, h.Count("offset=0"));     // the first page was not fetched a second time
        Assert.Equal(2, h.Count("offset=100"));   // the failed one was
    }

    // ── cover art ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_cover_art_is_a_404_and_is_remembered()
    {
        var h = new Handler(r => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(h);
        Assert.Empty(await CoverArtArchiveClient.GetImagesAsync(client, "release", "r-1", default));
        Assert.Empty(await CoverArtArchiveClient.GetImagesAsync(client, "release", "r-1", default));
        Assert.Equal(1, h.Count("coverartarchive.org/release/r-1"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_failing_archive_is_never_recorded_as_no_cover_art(HttpStatusCode status)
    {
        var h = new Handler(r => new HttpResponseMessage(status));
        var client = Client(h, retries: 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => CoverArtArchiveClient.GetImagesAsync(client, "release", "r-1", default));
    }

    [Fact]
    public async Task A_busy_archive_is_asked_again_and_the_art_it_then_returns_is_kept()
    {
        var calls = 0;
        var h = new Handler(r =>
        {
            if (++calls == 1) { var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)); return busy; }
            return Json("{\"images\":[{\"id\":7,\"front\":true,\"back\":false,\"types\":[\"Front\"],\"image\":\"http://x/7.jpg\",\"thumbnails\":{}}]}");
        });
        var images = await CoverArtArchiveClient.GetImagesAsync(Client(h, retries: 2), "release", "r-1", default);
        Assert.Single(images);
        Assert.Equal(2, calls);
    }

    // ── the fetchers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_release_group_gets_every_release_in_full_and_every_releases_cover_art()
    {
        const int Releases = 31;   // a lookup shows 25; the old code looked at 3
        var h = new Handler(r =>
        {
            var u = r.RequestUri!.ToString();
            if (u.Contains("/release-group/g-1")) return Json("{\"id\":\"g-1\",\"title\":\"An Album\",\"primary-type\":\"Album\"}");
            if (u.Contains("/release?release-group=g-1")) return Json(Page("release", Releases, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"rel-" + i + "\",\"title\":\"Edition " + i + "\"}"));
            if (u.Contains("/release/rel-")) return Json("{\"id\":\"x\",\"title\":\"Edition\",\"label-info\":[{\"label\":{\"name\":\"A Label\"}}]}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);   // no cover art anywhere
        });

        var meta = await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(Client(h), "g-1", default);

        Assert.Equal(Releases, meta.ExtendedData!.Value.GetProperty("releases").GetArrayLength());
        Assert.Equal(Releases, h.Count("/ws/2/release/rel-"));                        // each release looked up in full
        Assert.Equal(Releases, h.Count("coverartarchive.org/release/rel-"));          // each release's art looked for
        Assert.Equal(1, h.Count("coverartarchive.org/release-group/g-1"));
    }

    [Fact]
    public async Task An_artist_keeps_every_release_group_release_work_and_recording_and_every_release_groups_cover_art()
    {
        var h = new Handler(r =>
        {
            var u = r.RequestUri!.ToString();
            if (u.Contains("/artist/a-1")) return Json("{\"id\":\"a-1\",\"name\":\"Prolific\",\"type\":\"Group\"}");
            if (u.Contains("/release-group?artist=a-1")) return Json(Page("release-group", 130, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"rg-" + i + "\",\"title\":\"RG " + i + "\"}"));
            if (u.Contains("/release?artist=a-1")) return Json(Page("release", 210, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"r-" + i + "\"}"));
            if (u.Contains("/work?artist=a-1")) return Json(Page("work", 40, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"w-" + i + "\"}"));
            if (u.Contains("/recording?artist=a-1")) return Json(Page("recording", 350, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"rec-" + i + "\"}"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);   // no cover art
        });

        var meta = await MusicBrainzEntityFetcher.FetchArtistAsync(Client(h), "a-1", default);
        var ext = meta.ExtendedData!.Value;

        Assert.Equal(130, ext.GetProperty("allReleaseGroups").GetArrayLength());
        Assert.Equal(210, ext.GetProperty("allReleases").GetArrayLength());
        Assert.Equal(40, ext.GetProperty("allWorks").GetArrayLength());
        Assert.Equal(350, ext.GetProperty("allRecordings").GetArrayLength());
        Assert.Equal(130, h.Count("coverartarchive.org/release-group/rg-"));   // the old code looked at the first 5
    }

    [Fact]
    public async Task A_recording_gets_every_release_it_appears_on_and_looks_for_every_ones_cover_art()
    {
        const int Releases = 60;
        var h = new Handler(r =>
        {
            var u = r.RequestUri!.ToString();
            if (u.Contains("/recording/rec-1")) return Json("{\"id\":\"rec-1\",\"title\":\"A Song\"}");
            if (u.Contains("/release?recording=rec-1")) return Json(Page("release", Releases, Q(r, "offset"), Q(r, "limit"), i => "{\"id\":\"rel-" + i + "\",\"title\":\"R" + i + "\"}"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var meta = await MusicBrainzEntityFetcher.FetchRecordingAsync(Client(h), "rec-1", default);

        Assert.Equal(Releases, meta.ExtendedData!.Value.GetProperty("releases").GetArrayLength());
        Assert.Equal(Releases, h.Count("coverartarchive.org/release/rel-"));   // the old code looked at the first 5
    }

    [Fact]
    public void The_manifest_declares_a_call_deadline_long_enough_for_a_large_artist()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement;
        Assert.True(manifest.GetProperty("call_timeout_seconds").GetInt32() >= 3600);
    }
}

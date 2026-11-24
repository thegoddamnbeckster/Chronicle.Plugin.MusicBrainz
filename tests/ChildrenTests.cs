using System.Net;
using Chronicle.Plugin.MusicBrainz;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>What is beneath an artist (its albums and EPs) and beneath an album (its tracks), as Chronicle asks when the user adds an artist by hand.</summary>
public class ChildrenTests
{
    private static MusicBrainzMetadataProvider Provider(Func<string, string> answer)
    {
        var handler = new StubRouterHandler(url => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(url)) });
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://mb.test/") };
        return new MusicBrainzMetadataProvider(new MusicBrainzClient(http, minInterval: TimeSpan.Zero));
    }

    [Fact]
    public async Task AnArtist_ListsItsAlbumsAndEps_OldestFirst_AndLeavesOutSingles()
    {
        var provider = Provider(url => """
            {"release-group-count":4,"release-groups":[
              {"id":"rg-late","title":"Late Album","primary-type":"Album","first-release-date":"2010-05-01"},
              {"id":"rg-single","title":"A Single","primary-type":"Single","first-release-date":"2001-01-01"},
              {"id":"rg-early","title":"Early Album","primary-type":"Album","first-release-date":"1999"},
              {"id":"rg-ep","title":"An EP","primary-type":"EP","first-release-date":"2005-03-03"}]}
            """);

        var children = await provider.GetChildrenAsync("artist:abc", 0);

        Assert.Equal(["Early Album", "An EP", "Late Album"], children.Select(c => c.Title));
        Assert.Equal([1, 2, 3], children.Select(c => c.Number));
        Assert.Equal("release-group:rg-early", children[0].ExternalId);
        Assert.Equal(1999, children[0].Year);
    }

    [Fact]
    public async Task AnAlbum_ListsTheTracksOfItsEarliestOfficialRelease_NumberedAcrossDiscs()
    {
        var provider = Provider(url =>
        {
            if (url.StartsWith("/release?release-group=rg"))
                return """
                    {"release-count":2,"releases":[
                      {"id":"rel-bootleg","status":"Bootleg","date":"1990"},
                      {"id":"rel-official","status":"Official","date":"2000"}]}
                    """;
            Assert.Contains("release/rel-official", url);
            return """
                {"id":"rel-official","media":[
                  {"tracks":[{"position":1,"title":"One","length":180000},{"position":2,"title":"Two","length":61000}]},
                  {"tracks":[{"position":1,"title":"Three","length":null}]}]}
                """;
        });

        var children = await provider.GetChildrenAsync("release-group:rg", 1);

        Assert.Equal(["One", "Two", "Three"], children.Select(c => c.Title));
        Assert.Equal([1, 2, 3], children.Select(c => c.Number));
        Assert.Equal(3, children[0].RuntimeMinutes);
        Assert.Null(children[2].RuntimeMinutes);
        Assert.All(children, c => Assert.Null(c.ExternalId));
    }

    [Fact]
    public async Task AnythingElse_HasNothingBeneathIt()
    {
        var provider = Provider(_ => "{}");
        Assert.Empty(await provider.GetChildrenAsync("recording:abc", 2));
    }
}

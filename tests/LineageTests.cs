using System.Net;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

/// <summary>Where an album or track sits, so Chronicle files one added by hand under its artist (and album) and never at the top with nothing above it.</summary>
public class LineageTests
{
    private sealed class Handler(Func<string, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer(request.RequestUri!.ToString()), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static MusicBrainzClient Client(Func<string, string> answer) => new(new HttpClient(new Handler(answer)), TimeSpan.Zero) { MaxRetries = 0 };

    [Fact]
    public async Task AnAlbum_SitsUnderItsCreditedArtist()
    {
        var client = Client(u =>
            u.Contains("/release-group/g-1") ? "{\"id\":\"g-1\",\"title\":\"An Album\",\"primary-type\":\"Album\",\"artist-credit\":[{\"name\":\"The Band\",\"artist\":{\"id\":\"a-1\",\"name\":\"The Band\"}}]}"
            : u.Contains("release?release-group=") ? "{\"release-count\":0,\"releases\":[]}"
            : "{}");

        var meta = await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(client, "g-1", default);

        var artist = Assert.Single(meta.Lineage);
        Assert.Equal("The Band", artist.Name);
        Assert.Equal("artist:a-1", artist.ExternalId);
    }

    [Fact]
    public async Task AnAlbumWithNoCreditedArtist_HasNoLineage()
    {
        var client = Client(u =>
            u.Contains("/release-group/g-1") ? "{\"id\":\"g-1\",\"title\":\"An Album\"}"
            : u.Contains("release?release-group=") ? "{\"release-count\":0,\"releases\":[]}"
            : "{}");

        Assert.Empty((await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(client, "g-1", default)).Lineage);
    }

    [Fact]
    public async Task ATrack_SitsUnderItsArtistAndTheStudioAlbumItFirstAppearedOn()
    {
        var client = Client(u =>
            u.Contains("/recording/r-1") ? "{\"id\":\"r-1\",\"title\":\"A Song\",\"artist-credit\":[{\"name\":\"The Band\",\"artist\":{\"id\":\"a-1\",\"name\":\"The Band\"}}]}"
            : u.Contains("release?recording=r-1") ? "{\"release-count\":1,\"releases\":[{\"id\":\"rel-1\",\"title\":\"An Album\",\"status\":\"Official\",\"release-group\":{\"id\":\"g-1\",\"title\":\"An Album\",\"primary-type\":\"Album\"}}]}"
            : "{}");

        var meta = await MusicBrainzEntityFetcher.FetchRecordingAsync(client, "r-1", default);

        Assert.Equal(["The Band", "An Album"], meta.Lineage.Select(a => a.Name));
        Assert.Equal(["artist:a-1", "release-group:g-1"], meta.Lineage.Select(a => a.ExternalId));
    }

    [Fact]
    public async Task ATrackWithNoKnownAlbum_HasNoLineage_BecauseTheArtistAloneWouldFileItTooHigh()
    {
        var client = Client(u =>
            u.Contains("/recording/r-1") ? "{\"id\":\"r-1\",\"title\":\"A Song\",\"artist-credit\":[{\"name\":\"The Band\",\"artist\":{\"id\":\"a-1\",\"name\":\"The Band\"}}]}"
            : u.Contains("release?recording=r-1") ? "{\"release-count\":0,\"releases\":[]}"
            : "{}");

        Assert.Empty((await MusicBrainzEntityFetcher.FetchRecordingAsync(client, "r-1", default)).Lineage);
    }
}

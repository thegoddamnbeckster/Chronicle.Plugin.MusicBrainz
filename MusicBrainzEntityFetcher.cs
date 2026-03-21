using System.Text.Json;
using Chronicle.Plugin.MusicBrainz.Models;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzEntityFetcher
{
    private const string ArtistIncludes =
        "recordings+releases+release-groups+works+aliases+tags+genres+ratings+url-rels+artist-rels";
    private const string ReleaseGroupIncludes =
        "artists+releases+tags+genres+ratings+url-rels";
    private const string ReleaseIncludes =
        "artists+recordings+release-groups+labels+media+tags+genres+url-rels+artist-credits+isrcs";
    private const string RecordingIncludes =
        "artists+releases+tags+genres+isrcs+url-rels+artist-rels+work-rels";
    private const string WorkIncludes =
        "artist-rels+url-rels";

    // ── Task 14: FetchArtistAsync ─────────────────────────────────────────────

    public static async Task<MediaMetadata> FetchArtistAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"artist/{mbid}?inc={ArtistIncludes}&fmt=json", ct);
        var artist = JsonSerializer.Deserialize<MbArtist>(json, MusicBrainzJsonOptions.Opts)
            ?? throw new InvalidOperationException($"Empty response for artist {mbid}");

        var artistImageUrl = ExtractWikimediaImageUrl(artist.Relations);

        // Fetch cover art for first 5 release groups
        var allImages = new List<object>();
        var allRawImages = new List<CaaImage>();
        foreach (var rg in (artist.ReleaseGroups ?? []).Take(5))
        {
            if (rg.Id is null) continue;
            var images = await CoverArtArchiveClient.GetImagesAsync(client, "release-group", rg.Id, ct);
            allRawImages.AddRange(images);
            allImages.AddRange(CoverArtArchiveClient.ToStorageFormat(images));
        }

        var posterUrl = artistImageUrl
            ?? (allRawImages.Count > 0
                ? (allRawImages.FirstOrDefault(i => i.Front)?.Image ?? allRawImages[0].Image)
                : null);

        return new MediaMetadata
        {
            ExternalId     = $"artist:{artist.Id}",
            Source         = "MusicBrainz",
            Title          = artist.Name ?? string.Empty,
            Overview       = BuildBio(artist),
            Year           = MusicBrainzSearcher.ParseYear(artist.LifeSpan?.Begin),
            PosterUrl      = posterUrl,
            Genres         = artist.Genres?.Select(g => g.Name ?? "").Where(g => g != "").ToList() ?? [],
            Rating         = artist.Rating?.Value,
        };
    }

    // ── Task 15: FetchReleaseGroupAsync ───────────────────────────────────────

    public static async Task<MediaMetadata> FetchReleaseGroupAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"release-group/{mbid}?inc={ReleaseGroupIncludes}&fmt=json", ct);
        var rg = JsonSerializer.Deserialize<MbReleaseGroup>(json, MusicBrainzJsonOptions.Opts)
            ?? throw new InvalidOperationException($"Empty response for release-group {mbid}");

        // Full detail for up to 20 releases in this group
        var releases = new List<object>();
        foreach (var release in (rg.Releases ?? []).Take(20))
        {
            if (release.Id is null) continue;
            var releaseJson = await client.GetAsync($"release/{release.Id}?inc={ReleaseIncludes}&fmt=json", ct);
            var full = JsonSerializer.Deserialize<MbRelease>(releaseJson, MusicBrainzJsonOptions.Opts);
            if (full is not null) releases.Add(MapRelease(full));
        }

        var images = await CoverArtArchiveClient.GetImagesAsync(client, "release-group", mbid, ct);
        var frontImage = images.FirstOrDefault(i => i.Front)?.Image ?? images.FirstOrDefault()?.Image;

        var creditedArtist = rg.ArtistCredit?.FirstOrDefault()?.Artist?.Name ?? string.Empty;

        return new MediaMetadata
        {
            ExternalId = $"release-group:{rg.Id}",
            Source     = "MusicBrainz",
            Title      = rg.Title ?? string.Empty,
            Overview   = string.IsNullOrEmpty(creditedArtist) ? rg.PrimaryType : $"{rg.PrimaryType} by {creditedArtist}",
            Year       = MusicBrainzSearcher.ParseYear(rg.FirstReleaseDate),
            PosterUrl  = frontImage,
            Genres     = rg.Genres?.Select(g => g.Name ?? "").Where(g => g != "").ToList() ?? [],
            Rating     = rg.Rating?.Value,
        };
    }

    // ── Task 16: FetchRecordingAsync ──────────────────────────────────────────

    public static async Task<MediaMetadata> FetchRecordingAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"recording/{mbid}?inc={RecordingIncludes}&fmt=json", ct);
        var rec = JsonSerializer.Deserialize<MbRecording>(json, MusicBrainzJsonOptions.Opts)
            ?? throw new InvalidOperationException($"Empty response for recording {mbid}");

        // Fetch linked works (compositions)
        var works = new List<object>();
        foreach (var rel in (rec.Relations ?? []).Where(r => r.Work is not null).Take(3))
        {
            if (rel.Work?.Id is null) continue;
            var workJson = await client.GetAsync($"work/{rel.Work.Id}?inc={WorkIncludes}&fmt=json", ct);
            var work = JsonSerializer.Deserialize<MbWork>(workJson, MusicBrainzJsonOptions.Opts);
            if (work is not null) works.Add(MapWork(work));
        }

        // Cover art from first release that has it
        string? coverUrl = null;
        foreach (var release in (rec.Releases ?? []).Take(5))
        {
            if (release.Id is null) continue;
            var releaseImages = await CoverArtArchiveClient.GetImagesAsync(client, "release", release.Id, ct);
            var front = releaseImages.FirstOrDefault(i => i.Front);
            if (front?.Image is not null) { coverUrl = front.Image; break; }
        }

        return new MediaMetadata
        {
            ExternalId     = $"recording:{rec.Id}",
            Source         = "MusicBrainz",
            Title          = rec.Title ?? string.Empty,
            Year           = MusicBrainzSearcher.ParseYear(rec.FirstReleaseDate),
            PosterUrl      = coverUrl,
            RuntimeMinutes = rec.Length.HasValue ? (int)Math.Round(rec.Length.Value / 60000.0) : null,
            Genres         = rec.Genres?.Select(g => g.Name ?? "").Where(g => g != "").ToList() ?? [],
            Rating         = rec.Rating?.Value,
        };
    }

    // ── Private mapping helpers ───────────────────────────────────────────────

    private static string? ExtractWikimediaImageUrl(List<MbRelation>? relations) =>
        relations?
            .Where(r => r.Type == "image" && r.Url?.Resource?.Contains("wikimedia") == true)
            .Select(r => r.Url!.Resource)
            .FirstOrDefault();

    private static string? BuildBio(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type)) parts.Add(a.Type);
        if (a.Area?.Name is { } area) parts.Add(area);
        if (a.LifeSpan?.Begin is { } begin) parts.Add($"Active from {begin}");
        if (a.LifeSpan?.Ended == true && a.LifeSpan.End is { } end) parts.Add($"Ended {end}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static object MapRelease(MbRelease r) => new
    {
        mbid           = r.Id,
        title          = r.Title,
        date           = r.Date,
        country        = r.Country,
        status         = r.Status,
        barcode        = r.Barcode,
        disambiguation = r.Disambiguation,
        packaging      = r.Packaging,
        quality        = r.Quality,
        language       = r.TextRepresentation?.Language,
        script         = r.TextRepresentation?.Script,
        label_info     = r.LabelInfo?.Select(li => new { catalog_number = li.CatalogNumber, label_name = li.Label?.Name, label_mbid = li.Label?.Id }),
        media          = r.Media?.Select(m => new
        {
            position    = m.Position,
            format      = m.Format,
            title       = m.Title,
            track_count = m.TrackCount,
            tracks      = m.Tracks?.Select(t => new
            {
                position       = t.Position,
                number         = t.Number,
                title          = t.Title,
                length_ms      = t.Length,
                recording_mbid = t.Recording?.Id,
                isrcs          = t.Recording?.Isrcs
            })
        }),
        artist_credit = r.ArtistCredit?.Select(ac => new { ac.Name, ac.JoinPhrase, artist_mbid = ac.Artist?.Id }),
        tags          = r.Tags?.Select(t => new { t.Name, t.Count }),
        genres        = r.Genres?.Select(g => g.Name)
    };

    private static object MapWork(MbWork w) => new
    {
        mbid      = w.Id,
        title     = w.Title,
        type      = w.Type,
        iswcs     = w.Iswcs,
        language  = w.Language,
        composers = (w.Relations ?? []).Where(r => r.Type == "composer" && r.Artist is not null).Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name }).ToList(),
        lyricists = (w.Relations ?? []).Where(r => r.Type == "lyricist" && r.Artist is not null).Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name }).ToList(),
        arrangers = (w.Relations ?? []).Where(r => r.Type == "arranger" && r.Artist is not null).Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name }).ToList(),
        urls      = (w.Relations ?? []).Where(r => r.Url is not null).Select(r => new { type = r.Type, url = r.Url!.Resource }).ToList()
    };

    internal static int? ParseYear(string? date) => MusicBrainzSearcher.ParseYear(date);
}

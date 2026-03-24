using System.Text.Json;
using Chronicle.Plugin.MusicBrainz.Models;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzEntityFetcher
{
    private const string ArtistIncludes =
        "recordings+releases+release-groups+works+aliases+tags+genres+ratings+url-rels+artist-rels";
    private const string ReleaseGroupIncludes =
        "artists+releases+tags+genres+ratings+url-rels+artist-credits";
    private const string ReleaseIncludes =
        "artists+recordings+release-groups+labels+media+tags+genres+url-rels+artist-credits+isrcs";
    private const string RecordingIncludes =
        "artists+releases+release-groups+tags+genres+isrcs+url-rels+artist-rels+work-rels+artist-credits";
    private const string WorkIncludes =
        "artist-rels+url-rels";

    // ── FetchArtistAsync ──────────────────────────────────────────────────────

    public static async Task<MediaMetadata> FetchArtistAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"artist/{mbid}?inc={ArtistIncludes}&fmt=json", ct);
        var artist = JsonSerializer.Deserialize<MbArtist>(json, MusicBrainzJsonOptions.Opts)
            ?? throw new InvalidOperationException($"Empty response for artist {mbid}");

        // Artist image: Wikimedia Commons direct URL (converted from the page URL stored in MB)
        var artistImageUrl = ExtractWikimediaDirectImageUrl(artist.Relations);

        // Fetch cover art from first 5 release groups — used as fallback poster and for the
        // additional images gallery (shows the artist's discography thumbnails).
        var allRawImages   = new List<CaaImage>();
        var releaseGroupArt = new List<object>();
        foreach (var rg in (artist.ReleaseGroups ?? []).Take(5))
        {
            if (rg.Id is null) continue;
            var images = await CoverArtArchiveClient.GetImagesAsync(client, "release-group", rg.Id, ct);
            if (images.Count > 0)
            {
                allRawImages.AddRange(images);
                releaseGroupArt.Add(new
                {
                    mbid   = rg.Id,
                    title  = rg.Title,
                    type   = rg.PrimaryType,
                    year   = ParseYear(rg.FirstReleaseDate),
                    images = CoverArtArchiveClient.ToStorageFormat(images),
                });
            }
        }

        var posterUrl = artistImageUrl
            ?? (allRawImages.Count > 0
                ? (allRawImages.FirstOrDefault(i => i.Front)?.Image ?? allRawImages[0].Image)
                : null);

        // BackdropUrl: a second distinct front image from a different release group
        var backdropUrl = allRawImages
            .Where(i => i.Front && i.Image != posterUrl)
            .Select(i => i.Image)
            .FirstOrDefault();

        // Cast: band members (from artist-to-artist relations)
        var members = (artist.Relations ?? [])
            .Where(r => r.Type == "member of band" && r.Artist?.Name is not null)
            .Select(r => r.Artist!.Name!)
            .Distinct()
            .ToList();

        // Tags: community folksonomy tags, ordered by vote count
        var tags = (artist.Tags ?? [])
            .OrderByDescending(t => t.Count)
            .Select(t => t.Name ?? "")
            .Where(n => n != "")
            .Take(20)
            .ToList();

        // All cover art as AdditionalImages
        var additionalImages = BuildAdditionalImages(allRawImages);

        // External URLs (Wikipedia, Wikidata, Discogs, social media, etc.)
        var externalUrls = ExtractExternalUrls(artist.Relations);

        // ExtendedData: everything that doesn't fit the generic MediaMetadata fields
        var extendedData = JsonSerializer.SerializeToElement(new
        {
            artistType     = artist.Type,
            country        = artist.Country,
            area           = artist.Area?.Name,
            beginArea      = artist.BeginArea?.Name,
            endArea        = artist.EndArea?.Name,
            lifeSpan       = artist.LifeSpan is null ? null : new
            {
                begin  = artist.LifeSpan.Begin,
                end    = artist.LifeSpan.End,
                ended  = artist.LifeSpan.Ended,
            },
            disambiguation = artist.Disambiguation,
            sortName       = artist.SortName,
            aliases        = (artist.Aliases ?? [])
                .Select(a => new { name = a.Name, type = a.Type, locale = a.Locale, primary = a.Primary })
                .ToList(),
            members        = (artist.Relations ?? [])
                .Where(r => r.Type == "member of band" && r.Artist is not null)
                .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name, begin = r.Begin, end = r.End })
                .ToList(),
            externalUrls   = externalUrls,
            releaseGroupArt = releaseGroupArt,
        });

        return new MediaMetadata
        {
            ExternalId       = $"artist:{artist.Id}",
            Source           = "MusicBrainz",
            Title            = artist.Name ?? string.Empty,
            Overview         = BuildBio(artist),
            Year             = ParseYear(artist.LifeSpan?.Begin),
            PosterUrl        = posterUrl,
            BackdropUrl      = backdropUrl,
            Genres           = (artist.Genres ?? []).Select(g => g.Name ?? "").Where(g => g != "").ToList(),
            Cast             = members,
            Tags             = tags,
            Rating           = artist.Rating?.Value * 2,   // MB is 0–5; Chronicle uses 0–10
            AdditionalImages = additionalImages,
            ExtendedData     = extendedData,
        };
    }

    // ── FetchReleaseGroupAsync ────────────────────────────────────────────────

    public static async Task<MediaMetadata> FetchReleaseGroupAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"release-group/{mbid}?inc={ReleaseGroupIncludes}&fmt=json", ct);
        var rg = JsonSerializer.Deserialize<MbReleaseGroup>(json, MusicBrainzJsonOptions.Opts)
            ?? throw new InvalidOperationException($"Empty response for release-group {mbid}");

        // Full release details (track listings, labels, barcodes, media) for up to 20 releases
        var releases = new List<object>();
        foreach (var release in (rg.Releases ?? []).Take(20))
        {
            if (release.Id is null) continue;
            var releaseJson = await client.GetAsync($"release/{release.Id}?inc={ReleaseIncludes}&fmt=json", ct);
            var full = JsonSerializer.Deserialize<MbRelease>(releaseJson, MusicBrainzJsonOptions.Opts);
            if (full is not null) releases.Add(MapRelease(full));
        }

        // All cover art images from the Cover Art Archive
        var images         = await CoverArtArchiveClient.GetImagesAsync(client, "release-group", mbid, ct);
        var frontImage     = images.FirstOrDefault(i => i.Front)?.Image ?? images.FirstOrDefault()?.Image;
        var backImage      = images.FirstOrDefault(i => i.Back && !i.Front)?.Image;
        var additionalImages = BuildAdditionalImages(images);

        // Cast: credited artist names
        var creditedArtists = (rg.ArtistCredit ?? [])
            .Select(ac => ac.Name ?? ac.Artist?.Name ?? "")
            .Where(n => n != "")
            .ToList();

        // Tags
        var tags = (rg.Tags ?? [])
            .OrderByDescending(t => t.Count)
            .Select(t => t.Name ?? "")
            .Where(n => n != "")
            .Take(20)
            .ToList();

        // Richer overview: "Album · Live · by Metallica (Deluxe edition)"
        var overviewParts = new List<string>();
        if (!string.IsNullOrEmpty(rg.PrimaryType))    overviewParts.Add(rg.PrimaryType);
        if (rg.SecondaryTypes is { Count: > 0 })       overviewParts.AddRange(rg.SecondaryTypes);
        if (creditedArtists.Count > 0)                 overviewParts.Add($"by {string.Join(", ", creditedArtists)}");
        if (!string.IsNullOrEmpty(rg.Disambiguation))  overviewParts.Add($"({rg.Disambiguation})");
        var overview = overviewParts.Count > 0 ? string.Join(" · ", overviewParts) : null;

        var externalUrls = ExtractExternalUrls(rg.Relations);

        var extendedData = JsonSerializer.SerializeToElement(new
        {
            primaryType    = rg.PrimaryType,
            secondaryTypes = rg.SecondaryTypes ?? [],
            disambiguation = rg.Disambiguation,
            artistCredit   = MapArtistCredit(rg.ArtistCredit),
            externalUrls   = externalUrls,
            releases       = releases,
            coverArt       = CoverArtArchiveClient.ToStorageFormat(images),
        });

        return new MediaMetadata
        {
            ExternalId       = $"release-group:{rg.Id}",
            Source           = "MusicBrainz",
            Title            = rg.Title ?? string.Empty,
            Overview         = overview,
            Year             = ParseYear(rg.FirstReleaseDate),
            PosterUrl        = frontImage,
            BackdropUrl      = backImage,
            Genres           = (rg.Genres ?? []).Select(g => g.Name ?? "").Where(g => g != "").ToList(),
            Cast             = creditedArtists,
            Tags             = tags,
            Rating           = rg.Rating?.Value * 2,   // MB is 0–5; Chronicle uses 0–10
            AdditionalImages = additionalImages,
            ExtendedData     = extendedData,
        };
    }

    // ── FetchRecordingAsync ───────────────────────────────────────────────────

    public static async Task<MediaMetadata> FetchRecordingAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"recording/{mbid}?inc={RecordingIncludes}&fmt=json", ct);
        var rec = JsonSerializer.Deserialize<MbRecording>(json, MusicBrainzJsonOptions.Opts)
            ?? throw new InvalidOperationException($"Empty response for recording {mbid}");

        // Fetch linked works (compositions) and extract composer/lyricist/arranger credits
        var works      = new List<object>();
        var composers  = new List<string>();
        var lyricists  = new List<string>();
        var arrangers  = new List<string>();
        foreach (var rel in (rec.Relations ?? []).Where(r => r.Work is not null).Take(3))
        {
            if (rel.Work?.Id is null) continue;
            var workJson = await client.GetAsync($"work/{rel.Work.Id}?inc={WorkIncludes}&fmt=json", ct);
            var work = JsonSerializer.Deserialize<MbWork>(workJson, MusicBrainzJsonOptions.Opts);
            if (work is not null)
            {
                works.Add(MapWork(work));
                composers.AddRange((work.Relations ?? [])
                    .Where(r => r.Type == "composer"  && r.Artist?.Name is not null)
                    .Select(r => r.Artist!.Name!));
                lyricists.AddRange((work.Relations ?? [])
                    .Where(r => r.Type == "lyricist"  && r.Artist?.Name is not null)
                    .Select(r => r.Artist!.Name!));
                arrangers.AddRange((work.Relations ?? [])
                    .Where(r => r.Type == "arranger"  && r.Artist?.Name is not null)
                    .Select(r => r.Artist!.Name!));
            }
        }

        // Cover art from up to 5 releases — prefer studio album releases over compilations/singles.
        // MusicBrainz returns releases in arbitrary order; a track may appear on dozens of
        // compilations whose cover art has nothing to do with the original album.
        var sortedReleases = (rec.Releases ?? [])
            .OrderBy(r => IsStudioAlbum(r) ? 0 : 1)   // studio albums first
            .Take(5)
            .ToList();

        string? coverUrl    = null;
        string? backdropUrl = null;
        var allCoverImages  = new List<CaaImage>();
        foreach (var release in sortedReleases)
        {
            if (release.Id is null) continue;
            var releaseImages = await CoverArtArchiveClient.GetImagesAsync(client, "release", release.Id, ct);
            allCoverImages.AddRange(releaseImages);
            coverUrl    ??= releaseImages.FirstOrDefault(i => i.Front)?.Image;
            backdropUrl ??= releaseImages.FirstOrDefault(i => i.Back && !i.Front)?.Image;
        }

        var additionalImages = BuildAdditionalImages(allCoverImages);

        // Cast: credited artist names on the recording
        var creditedArtists = (rec.ArtistCredit ?? [])
            .Select(ac => ac.Name ?? ac.Artist?.Name ?? "")
            .Where(n => n != "")
            .ToList();

        // Directors: composers and lyricists (music equivalent of directors/writers)
        var directors = composers.Concat(lyricists).Distinct().ToList();

        // Tags
        var tags = (rec.Tags ?? [])
            .OrderByDescending(t => t.Count)
            .Select(t => t.Name ?? "")
            .Where(n => n != "")
            .Take(20)
            .ToList();

        var externalUrls = ExtractExternalUrls(rec.Relations);

        var extendedData = JsonSerializer.SerializeToElement(new
        {
            disambiguation = rec.Disambiguation,
            video          = rec.Video,
            isrcs          = rec.Isrcs ?? [],
            artistCredit   = MapArtistCredit(rec.ArtistCredit),
            composers      = composers.Distinct().ToList(),
            lyricists      = lyricists.Distinct().ToList(),
            arrangers      = arrangers.Distinct().ToList(),
            externalUrls   = externalUrls,
            works          = works,
            releases       = (rec.Releases ?? []).Take(10).Select(r => new
            {
                mbid    = r.Id,
                title   = r.Title,
                date    = r.Date,
                country = r.Country,
                status  = r.Status,
            }).ToList<object>(),
            coverArt       = CoverArtArchiveClient.ToStorageFormat(allCoverImages),
        });

        return new MediaMetadata
        {
            ExternalId       = $"recording:{rec.Id}",
            Source           = "MusicBrainz",
            Title            = rec.Title ?? string.Empty,
            Year             = ParseYear(rec.FirstReleaseDate),
            PosterUrl        = coverUrl,
            BackdropUrl      = backdropUrl,
            RuntimeMinutes   = rec.Length.HasValue ? (int)Math.Round(rec.Length.Value / 60000.0) : null,
            Genres           = (rec.Genres ?? []).Select(g => g.Name ?? "").Where(g => g != "").ToList(),
            Cast             = creditedArtists,
            Directors        = directors,
            Tags             = tags,
            Rating           = rec.Rating?.Value * 2,   // MB is 0–5; Chronicle uses 0–10
            AdditionalImages = additionalImages,
            ExtendedData     = extendedData,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the release is a studio album (PrimaryType=Album,
    /// no Compilation/Live/Soundtrack/DJ-mix/Interview secondary type).
    /// Used to prefer original album art over compilation cover art for tracks.
    /// </summary>
    private static bool IsStudioAlbum(MbRelease release)
    {
        var rg = release.ReleaseGroup;
        if (rg is null) return false;
        if (!string.Equals(rg.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase))
            return false;
        var nonAlbumSecondary = new[] { "Compilation", "Live", "Soundtrack", "DJ-mix", "Interview", "Mixtape/Street", "Demo", "Spokenword" };
        return !(rg.SecondaryTypes ?? []).Any(s => nonAlbumSecondary.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts a direct Wikimedia Commons image URL from artist relations.
    /// MusicBrainz stores the Commons page URL (e.g. ".../wiki/File:Foo.jpg");
    /// we derive the actual image URL using the standard MD5-based shard algorithm.
    /// </summary>
    private static string? ExtractWikimediaDirectImageUrl(List<MbRelation>? relations)
    {
        var pageUrl = relations?
            .Where(r => r.Type == "image" && r.Url?.Resource?.Contains("wikimedia") == true)
            .Select(r => r.Url!.Resource!)
            .FirstOrDefault();

        return pageUrl is null ? null : WikimediaPageToDirectUrl(pageUrl);
    }

    /// <summary>
    /// Converts a Wikimedia Commons file-page URL to the direct image URL.
    /// Algorithm: extract filename → compute MD5 → build upload.wikimedia.org path.
    /// No additional HTTP call is required; the shard path is fully deterministic.
    /// </summary>
    private static string? WikimediaPageToDirectUrl(string pageUrl)
    {
        try
        {
            const string filePrefix = "File:";
            var idx = pageUrl.IndexOf(filePrefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            // Decode percent-encoding, normalise spaces to underscores
            var filename = Uri.UnescapeDataString(pageUrl[(idx + filePrefix.Length)..])
                .Replace(' ', '_');

            // MD5 of the filename (UTF-8 bytes) gives the two-level shard directory
            var hash = System.Security.Cryptography.MD5
                .HashData(System.Text.Encoding.UTF8.GetBytes(filename));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            // Re-encode the filename for the URL path segment
            var encodedFilename = string.Join("/",
                filename.Split('/').Select(Uri.EscapeDataString));

            return $"https://upload.wikimedia.org/wikipedia/commons/{hex[0]}/{hex[..2]}/{encodedFilename}";
        }
        catch { return null; }
    }

    private static string? BuildBio(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type))               parts.Add(a.Type);
        if (a.Country is { } country)                    parts.Add(country);
        if (a.Area?.Name is { } area)                    parts.Add(area);
        if (a.LifeSpan?.Begin is { } begin)              parts.Add($"Active from {begin}");
        if (a.LifeSpan?.Ended == true &&
            a.LifeSpan.End is { } end)                   parts.Add($"Ended {end}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static List<AdditionalImage> BuildAdditionalImages(List<CaaImage> images) =>
        images
            .Select(img => new AdditionalImage
            {
                Url          = img.Image ?? "",
                Type         = img.Types?.FirstOrDefault()
                               ?? (img.Front ? "Front" : img.Back ? "Back" : null),
                ThumbnailUrl = img.Thumbnails?.Medium
                               ?? img.Thumbnails?.Large
                               ?? img.Thumbnails?.LargeAlt,
            })
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .ToList();

    private static List<object> ExtractExternalUrls(List<MbRelation>? relations) =>
        (relations ?? [])
            .Where(r => r.Url?.Resource is not null && r.Type is
                "wikipedia" or "wikidata" or "allmusic" or "discogs" or
                "streaming" or "free streaming" or "purchase for download" or
                "bandcamp" or "soundcloud" or "youtube" or
                "social network" or "official homepage" or "last.fm" or
                "lyrics" or "secondhandsongs" or "setlist.fm")
            .Select(r => (object)new { type = r.Type, url = r.Url!.Resource })
            .ToList();

    private static List<object> MapArtistCredit(List<MbArtistCredit>? credits) =>
        (credits ?? [])
            .Select(ac => (object)new
            {
                name       = ac.Name ?? ac.Artist?.Name,
                joinPhrase = ac.JoinPhrase,
                artistMbid = ac.Artist?.Id,
                artistName = ac.Artist?.Name,
            })
            .ToList();

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
        labelInfo      = r.LabelInfo?.Select(li => new
        {
            catalogNumber = li.CatalogNumber,
            labelName     = li.Label?.Name,
            labelMbid     = li.Label?.Id,
        }),
        artistCredit   = MapArtistCredit(r.ArtistCredit),
        media          = r.Media?.Select(m => new
        {
            position   = m.Position,
            format     = m.Format,
            title      = m.Title,
            trackCount = m.TrackCount,
            tracks     = m.Tracks?.Select(t => new
            {
                position      = t.Position,
                number        = t.Number,
                title         = t.Title,
                lengthMs      = t.Length,
                recordingMbid = t.Recording?.Id,
                isrcs         = t.Recording?.Isrcs ?? [],
            }),
        }),
        tags    = r.Tags?.Select(t => new { t.Name, t.Count }),
        genres  = r.Genres?.Select(g => g.Name),
    };

    private static object MapWork(MbWork w) => new
    {
        mbid      = w.Id,
        title     = w.Title,
        type      = w.Type,
        iswcs     = w.Iswcs ?? [],
        language  = w.Language,
        composers = (w.Relations ?? [])
            .Where(r => r.Type == "composer" && r.Artist is not null)
            .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name })
            .ToList(),
        lyricists = (w.Relations ?? [])
            .Where(r => r.Type == "lyricist" && r.Artist is not null)
            .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name })
            .ToList(),
        arrangers = (w.Relations ?? [])
            .Where(r => r.Type == "arranger" && r.Artist is not null)
            .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name })
            .ToList(),
        urls      = (w.Relations ?? [])
            .Where(r => r.Url is not null)
            .Select(r => new { type = r.Type, url = r.Url!.Resource })
            .ToList(),
    };

    internal static int? ParseYear(string? date) => MusicBrainzSearcher.ParseYear(date);
}

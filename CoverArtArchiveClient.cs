using System.Text.Json;
using Chronicle.Plugin.MusicBrainz.Models;

namespace Chronicle.Plugin.MusicBrainz;

internal static class CoverArtArchiveClient
{
    /// <summary>Fetch all images for a release or release-group from the Cover Art Archive.</summary>
    public static async Task<List<CaaImage>> GetImagesAsync(
        MusicBrainzClient client, string entityType, string mbid, CancellationToken ct)
    {
        var json = await client.GetCoverArtAsync($"{entityType}/{mbid}", ct);
        if (json == "{}") return [];
        var response = JsonSerializer.Deserialize<CaaResponse>(json, MusicBrainzJsonOptions.Opts);
        return response?.Images ?? [];
    }

    /// <summary>Convert CaaImage list to a storage-friendly anonymous object list.</summary>
    public static List<object> ToStorageFormat(List<CaaImage> images) =>
        images.Select(img => (object)new
        {
            id        = img.Id,
            types     = img.Types ?? [],
            front     = img.Front,
            back      = img.Back,
            comment   = img.Comment,
            url       = img.Image,
            thumbnails = new
            {
                small  = img.Thumbnails?.Small  ?? img.Thumbnails?.SmallAlt,
                medium = img.Thumbnails?.Medium,
                large  = img.Thumbnails?.Large  ?? img.Thumbnails?.LargeAlt
            },
            approved  = img.Approved
        }).ToList();
}

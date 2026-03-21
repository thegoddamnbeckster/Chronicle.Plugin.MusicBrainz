using System.Text.Json;
using Chronicle.Plugin.MusicBrainz.Models;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzSearcher
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<MediaMetadata> SearchArtistsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"artist?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbArtist>>(json, Opts);
        var items = (result?.Artists ?? []).Select(a => new MediaMetadata
        {
            ExternalId = $"artist:{a.Id}",
            Source     = "musicbrainz",
            Title      = a.Name ?? string.Empty,
            Overview   = BuildArtistSummary(a),
            Year       = ParseYear(a.LifeSpan?.Begin)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    public static async Task<MediaMetadata> SearchReleaseGroupsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"release-group?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbReleaseGroup>>(json, Opts);
        var items = (result?.ReleaseGroups ?? []).Select(rg => new MediaMetadata
        {
            ExternalId = $"release-group:{rg.Id}",
            Source     = "musicbrainz",
            Title      = rg.Title ?? string.Empty,
            Overview   = rg.PrimaryType is not null
                ? $"{rg.PrimaryType}{(rg.SecondaryTypes?.Count > 0 ? " (" + string.Join(", ", rg.SecondaryTypes) + ")" : "")}"
                : null,
            Year = ParseYear(rg.FirstReleaseDate)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    public static async Task<MediaMetadata> SearchRecordingsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"recording?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbRecording>>(json, Opts);
        var items = (result?.Recordings ?? []).Select(r => new MediaMetadata
        {
            ExternalId     = $"recording:{r.Id}",
            Source         = "musicbrainz",
            Title          = r.Title ?? string.Empty,
            RuntimeMinutes = r.Length.HasValue ? r.Length.Value / 60000 : null,
            Year           = ParseYear(r.FirstReleaseDate)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    private static string? BuildArtistSummary(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type)) parts.Add(a.Type);
        if (a.Area?.Name is { } area) parts.Add(area);
        if (!string.IsNullOrEmpty(a.Disambiguation)) parts.Add($"({a.Disambiguation})");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    internal static int? ParseYear(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        return int.TryParse(date[..Math.Min(4, date.Length)], out var y) ? y : null;
    }
}

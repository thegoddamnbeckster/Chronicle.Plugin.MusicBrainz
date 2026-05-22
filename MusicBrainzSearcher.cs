using System.Text.Json;
using Chronicle.Plugin.MusicBrainz.Models;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzSearcher
{
    public static async Task<MediaMetadata> SearchArtistsAsync(
        MusicBrainzClient client, string query, CancellationToken ct,
        bool personOnly = false)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"artist?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbArtist>>(json, MusicBrainzJsonOptions.Opts);
        var items = (result?.Artists ?? [])
            .Where(a => !personOnly || IsPersonArtistType(a.Type))
            .Select(a => new MediaMetadata
            {
                ExternalId = $"artist:{a.Id}",
                Source     = "MusicBrainz",
                Title      = a.Name ?? string.Empty,
                Overview   = BuildArtistSummary(a),
                Year       = ParseYear(a.LifeSpan?.Begin)
            }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    /// <summary>
    /// Returns true for artist types that can be an audiobook author (Person or unknown).
    /// Rejects Group, Orchestra, Choir, Character, and Other — none of these can be an author.
    /// Null/empty type is allowed because some legitimate MusicBrainz author entries predate
    /// the type cleanup effort and may not yet have a type set.
    /// </summary>
    private static bool IsPersonArtistType(string? type) =>
        string.IsNullOrEmpty(type) ||
        string.Equals(type, "Person", StringComparison.OrdinalIgnoreCase);

    public static async Task<MediaMetadata> SearchReleaseGroupsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"release-group?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbReleaseGroup>>(json, MusicBrainzJsonOptions.Opts);
        var items = (result?.ReleaseGroups ?? []).Select(rg => new MediaMetadata
        {
            ExternalId = $"release-group:{rg.Id}",
            Source     = "MusicBrainz",
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
        var result = JsonSerializer.Deserialize<MbSearchResult<MbRecording>>(json, MusicBrainzJsonOptions.Opts);
        var items = (result?.Recordings ?? []).Select(r => new MediaMetadata
        {
            ExternalId     = $"recording:{r.Id}",
            Source         = "MusicBrainz",
            Title          = r.Title ?? string.Empty,
            RuntimeMinutes = r.Length.HasValue ? (int)Math.Round(r.Length.Value / 60000.0) : null,
            Year           = ParseYear(r.FirstReleaseDate)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    /// <summary>
    /// Searches recordings with <c>inc=releases</c> and returns the (releaseId, releaseTitle)
    /// pairs found in the results. Used by Stage 5 to discover the specific release MBID
    /// for a sibling track, which is then used as a <c>reid:</c> constraint.
    /// </summary>
    public static async Task<IReadOnlyList<(string ReleaseId, string ReleaseTitle)>>
        FindReleasesForSiblingAsync(MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync(
            $"recording?query={encoded}&limit=5&inc=releases&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbRecording>>(json, MusicBrainzJsonOptions.Opts);
        var pairs = new List<(string, string)>();
        foreach (var rec in result?.Recordings ?? [])
            foreach (var rel in rec.Releases ?? [])
                if (rel.Id is not null && rel.Title is not null)
                    pairs.Add((rel.Id, rel.Title));
        return pairs;
    }

    private static string? BuildArtistSummary(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type)) parts.Add(a.Type);
        if (a.Area?.Name is { } area) parts.Add(area);
        if (!string.IsNullOrEmpty(a.Disambiguation)) parts.Add($"({a.Disambiguation})");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>
    /// Returns the release MBIDs for a release-group, ordered by the list returned
    /// by the API (typically earliest first).
    /// </summary>
    public static async Task<IReadOnlyList<string>>
        FetchReleaseGroupReleasesAsync(MusicBrainzClient client, string releaseGroupMbid, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(releaseGroupMbid);
        var json = await client.GetAsync(
            $"release?release-group={encoded}&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbRelease>>(json, MusicBrainzJsonOptions.Opts);
        return (result?.Releases ?? [])
            .Select(r => r.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>
    /// Fetches the track listing for a specific release by its MBID.
    /// Returns a list of (TrackNumber, Title, DurationMs) tuples.
    /// </summary>
    public static async Task<IReadOnlyList<(int TrackNumber, string Title, int? DurationMs)>>
        FetchReleaseTracksAsync(MusicBrainzClient client, string releaseMbid, CancellationToken ct)
    {
        var json = await client.GetAsync(
            $"release/{releaseMbid}?inc=recordings&fmt=json", ct);
        var release = JsonSerializer.Deserialize<MbRelease>(json, MusicBrainzJsonOptions.Opts);
        var tracks = new List<(int, string, int?)>();
        foreach (var medium in release?.Media ?? [])
        {
            foreach (var track in medium.Tracks ?? [])
            {
                var title = track.Title ?? track.Recording?.Title ?? string.Empty;
                tracks.Add((track.Position, title, track.Length));
            }
        }
        return tracks;
    }

    internal static int? ParseYear(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        return int.TryParse(date[..Math.Min(4, date.Length)], out var y) ? y : null;
    }

    /// <summary>
    /// Searches MusicBrainz for audiobook series by name.
    /// Returns an empty-results <see cref="MediaMetadata"/> (not an exception) when nothing is found
    /// — the caller gracefully falls back so the book lands at level 1 under its author.
    /// </summary>
    public static async Task<MediaMetadata> SearchAudiobookSeriesAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var json = await client.GetAsync($"series?query={encoded}&limit=10&fmt=json", ct);
            var result = JsonSerializer.Deserialize<MbSeriesSearchResult>(json, MusicBrainzJsonOptions.Opts);
            var items = (result?.Series ?? [])
                .Select(s => new MediaMetadata
                {
                    ExternalId = $"series:{s.Id}",
                    Source     = "MusicBrainz",
                    Title      = s.Name ?? string.Empty,
                })
                .ToList();
            return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
        }
        catch
        {
            // MusicBrainz series coverage for audiobooks is incomplete.
            // Silently return empty rather than propagating the error.
            return new MediaMetadata();
        }
    }
}

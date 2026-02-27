using System.Text.Json.Serialization;

namespace Chronicle.Plugin.MusicBrainz;

// ── Search responses ──────────────────────────────────────────────────────────

internal record MbReleaseSearch(
    [property: JsonPropertyName("releases")]       List<MbRelease> Releases,
    [property: JsonPropertyName("release-count")] int ReleaseCount
);

internal record MbArtistSearch(
    [property: JsonPropertyName("artists")]       List<MbArtist> Artists,
    [property: JsonPropertyName("artist-count")] int ArtistCount
);

// ── Release (album / single / EP) ─────────────────────────────────────────────

internal record MbRelease(
    [property: JsonPropertyName("id")]            string  Id,
    [property: JsonPropertyName("title")]         string  Title,
    [property: JsonPropertyName("date")]          string? Date,
    [property: JsonPropertyName("status")]        string? Status,
    [property: JsonPropertyName("country")]       string? Country,
    [property: JsonPropertyName("artist-credit")] List<MbArtistCredit>? ArtistCredit,
    [property: JsonPropertyName("release-group")] MbReleaseGroup? ReleaseGroup,
    [property: JsonPropertyName("media")]         List<MbMedium>? Media,
    [property: JsonPropertyName("genres")]        List<MbGenre>? Genres,
    [property: JsonPropertyName("label-info")]    List<MbLabelInfo>? LabelInfo
);

internal record MbReleaseGroup(
    [property: JsonPropertyName("id")]                  string  Id,
    [property: JsonPropertyName("primary-type")]        string? PrimaryType,
    [property: JsonPropertyName("secondary-types")]     List<string>? SecondaryTypes
);

internal record MbMedium(
    [property: JsonPropertyName("track-count")] int TrackCount,
    [property: JsonPropertyName("tracks")]      List<MbTrack>? Tracks
);

internal record MbTrack(
    [property: JsonPropertyName("id")]       string  Id,
    [property: JsonPropertyName("title")]    string  Title,
    [property: JsonPropertyName("number")]   string? Number,
    [property: JsonPropertyName("length")]   int?    Length   // milliseconds
);

internal record MbLabelInfo(
    [property: JsonPropertyName("catalog-number")] string? CatalogNumber,
    [property: JsonPropertyName("label")]          MbLabel? Label
);

internal record MbLabel(
    [property: JsonPropertyName("name")] string Name
);

// ── Artist ────────────────────────────────────────────────────────────────────

internal record MbArtist(
    [property: JsonPropertyName("id")]            string  Id,
    [property: JsonPropertyName("name")]          string  Name,
    [property: JsonPropertyName("sort-name")]     string? SortName,
    [property: JsonPropertyName("type")]          string? Type,
    [property: JsonPropertyName("country")]       string? Country,
    [property: JsonPropertyName("life-span")]     MbLifeSpan? LifeSpan,
    [property: JsonPropertyName("genres")]        List<MbGenre>? Genres
);

internal record MbLifeSpan(
    [property: JsonPropertyName("begin")] string? Begin,
    [property: JsonPropertyName("end")]   string? End,
    [property: JsonPropertyName("ended")] bool    Ended
);

// ── Shared ────────────────────────────────────────────────────────────────────

internal record MbArtistCredit(
    [property: JsonPropertyName("name")]   string? Name,
    [property: JsonPropertyName("artist")] MbArtist? Artist
);

internal record MbGenre(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("count")] int?   Count
);

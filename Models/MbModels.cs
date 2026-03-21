using System.Text.Json.Serialization;

namespace Chronicle.Plugin.MusicBrainz.Models;

public class MbArtist
{
    [JsonPropertyName("id")]             public string? Id { get; set; }
    [JsonPropertyName("name")]           public string? Name { get; set; }
    [JsonPropertyName("sort-name")]      public string? SortName { get; set; }
    [JsonPropertyName("type")]           public string? Type { get; set; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
    [JsonPropertyName("country")]        public string? Country { get; set; }
    [JsonPropertyName("life-span")]      public MbLifeSpan? LifeSpan { get; set; }
    [JsonPropertyName("area")]           public MbArea? Area { get; set; }
    [JsonPropertyName("begin-area")]     public MbArea? BeginArea { get; set; }
    [JsonPropertyName("end-area")]       public MbArea? EndArea { get; set; }
    [JsonPropertyName("aliases")]        public List<MbAlias>? Aliases { get; set; }
    [JsonPropertyName("tags")]           public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]         public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("rating")]         public MbRating? Rating { get; set; }
    [JsonPropertyName("relations")]      public List<MbRelation>? Relations { get; set; }
    [JsonPropertyName("release-groups")] public List<MbReleaseGroup>? ReleaseGroups { get; set; }
}

public class MbReleaseGroup
{
    [JsonPropertyName("id")]                  public string? Id { get; set; }
    [JsonPropertyName("title")]               public string? Title { get; set; }
    [JsonPropertyName("primary-type")]        public string? PrimaryType { get; set; }
    [JsonPropertyName("secondary-types")]     public List<string>? SecondaryTypes { get; set; }
    [JsonPropertyName("first-release-date")]  public string? FirstReleaseDate { get; set; }
    [JsonPropertyName("disambiguation")]      public string? Disambiguation { get; set; }
    [JsonPropertyName("artist-credit")]       public List<MbArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("releases")]            public List<MbRelease>? Releases { get; set; }
    [JsonPropertyName("tags")]                public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]              public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("rating")]              public MbRating? Rating { get; set; }
    [JsonPropertyName("relations")]           public List<MbRelation>? Relations { get; set; }
}

public class MbRelease
{
    [JsonPropertyName("id")]                  public string? Id { get; set; }
    [JsonPropertyName("title")]               public string? Title { get; set; }
    [JsonPropertyName("date")]                public string? Date { get; set; }
    [JsonPropertyName("country")]             public string? Country { get; set; }
    [JsonPropertyName("status")]              public string? Status { get; set; }
    [JsonPropertyName("barcode")]             public string? Barcode { get; set; }
    [JsonPropertyName("disambiguation")]      public string? Disambiguation { get; set; }
    [JsonPropertyName("label-info")]          public List<MbLabelInfo>? LabelInfo { get; set; }
    [JsonPropertyName("media")]               public List<MbMedium>? Media { get; set; }
    [JsonPropertyName("artist-credit")]       public List<MbArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("release-group")]       public MbReleaseGroup? ReleaseGroup { get; set; }
    [JsonPropertyName("text-representation")] public MbTextRepresentation? TextRepresentation { get; set; }
    [JsonPropertyName("quality")]             public string? Quality { get; set; }
    [JsonPropertyName("packaging")]           public string? Packaging { get; set; }
    [JsonPropertyName("tags")]                public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]              public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("relations")]           public List<MbRelation>? Relations { get; set; }
}

public class MbRecording
{
    [JsonPropertyName("id")]                  public string? Id { get; set; }
    [JsonPropertyName("title")]               public string? Title { get; set; }
    [JsonPropertyName("length")]              public int? Length { get; set; }
    [JsonPropertyName("disambiguation")]      public string? Disambiguation { get; set; }
    [JsonPropertyName("first-release-date")]  public string? FirstReleaseDate { get; set; }
    [JsonPropertyName("video")]               public bool? Video { get; set; }
    [JsonPropertyName("isrcs")]               public List<string>? Isrcs { get; set; }
    [JsonPropertyName("artist-credit")]       public List<MbArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("releases")]            public List<MbRelease>? Releases { get; set; }
    [JsonPropertyName("tags")]                public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]              public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("rating")]              public MbRating? Rating { get; set; }
    [JsonPropertyName("relations")]           public List<MbRelation>? Relations { get; set; }
}

public class MbWork
{
    [JsonPropertyName("id")]        public string? Id { get; set; }
    [JsonPropertyName("title")]     public string? Title { get; set; }
    [JsonPropertyName("type")]      public string? Type { get; set; }
    [JsonPropertyName("iswcs")]     public List<string>? Iswcs { get; set; }
    [JsonPropertyName("language")]  public string? Language { get; set; }
    [JsonPropertyName("relations")] public List<MbRelation>? Relations { get; set; }
}

public class MbLifeSpan
{
    [JsonPropertyName("begin")] public string? Begin { get; set; }
    [JsonPropertyName("end")]   public string? End { get; set; }
    [JsonPropertyName("ended")] public bool? Ended { get; set; }
}

public class MbArea
{
    [JsonPropertyName("id")]   public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public class MbAlias
{
    [JsonPropertyName("name")]    public string? Name { get; set; }
    [JsonPropertyName("type")]    public string? Type { get; set; }
    [JsonPropertyName("locale")]  public string? Locale { get; set; }
    [JsonPropertyName("primary")] public bool? Primary { get; set; }
}

public class MbTag
{
    [JsonPropertyName("name")]  public string? Name { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public class MbRating
{
    [JsonPropertyName("value")]       public double? Value { get; set; }
    [JsonPropertyName("votes-count")] public int VotesCount { get; set; }
}

public class MbRelation
{
    [JsonPropertyName("type")]       public string? Type { get; set; }
    [JsonPropertyName("type-id")]    public string? TypeId { get; set; }
    [JsonPropertyName("direction")]  public string? Direction { get; set; }
    [JsonPropertyName("artist")]     public MbArtist? Artist { get; set; }
    [JsonPropertyName("work")]       public MbWork? Work { get; set; }
    [JsonPropertyName("url")]        public MbUrl? Url { get; set; }
    [JsonPropertyName("attributes")] public List<string>? Attributes { get; set; }
    [JsonPropertyName("begin")]      public string? Begin { get; set; }
    [JsonPropertyName("end")]        public string? End { get; set; }
}

public class MbUrl
{
    [JsonPropertyName("id")]       public string? Id { get; set; }
    [JsonPropertyName("resource")] public string? Resource { get; set; }
}

public class MbArtistCredit
{
    [JsonPropertyName("name")]       public string? Name { get; set; }
    [JsonPropertyName("joinphrase")] public string? JoinPhrase { get; set; }
    [JsonPropertyName("artist")]     public MbArtist? Artist { get; set; }
}

public class MbMedium
{
    [JsonPropertyName("position")]    public int Position { get; set; }
    [JsonPropertyName("format")]      public string? Format { get; set; }
    [JsonPropertyName("title")]       public string? Title { get; set; }
    [JsonPropertyName("track-count")] public int TrackCount { get; set; }
    [JsonPropertyName("tracks")]      public List<MbTrack>? Tracks { get; set; }
}

public class MbTrack
{
    [JsonPropertyName("id")]        public string? Id { get; set; }
    [JsonPropertyName("number")]    public string? Number { get; set; }
    [JsonPropertyName("title")]     public string? Title { get; set; }
    [JsonPropertyName("length")]    public int? Length { get; set; }
    [JsonPropertyName("position")]  public int Position { get; set; }
    [JsonPropertyName("recording")] public MbRecording? Recording { get; set; }
}

public class MbLabelInfo
{
    [JsonPropertyName("catalog-number")] public string? CatalogNumber { get; set; }
    [JsonPropertyName("label")]          public MbLabel? Label { get; set; }
}

public class MbLabel
{
    [JsonPropertyName("id")]        public string? Id { get; set; }
    [JsonPropertyName("name")]      public string? Name { get; set; }
    [JsonPropertyName("sort-name")] public string? SortName { get; set; }
}

public class MbTextRepresentation
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("script")]   public string? Script { get; set; }
}

// Search result wrappers
public class MbSearchResult<T>
{
    [JsonPropertyName("count")]          public int Count { get; set; }
    [JsonPropertyName("offset")]         public int Offset { get; set; }
    [JsonPropertyName("artists")]        public List<T>? Artists { get; set; }
    [JsonPropertyName("release-groups")] public List<T>? ReleaseGroups { get; set; }
    [JsonPropertyName("releases")]       public List<T>? Releases { get; set; }
    [JsonPropertyName("recordings")]     public List<T>? Recordings { get; set; }
}

// Cover Art Archive models
public class CaaResponse
{
    [JsonPropertyName("images")]  public List<CaaImage>? Images { get; set; }
    [JsonPropertyName("release")] public string? Release { get; set; }
}

public class CaaImage
{
    [JsonPropertyName("id")]         public long Id { get; set; }
    [JsonPropertyName("types")]      public List<string>? Types { get; set; }
    [JsonPropertyName("front")]      public bool Front { get; set; }
    [JsonPropertyName("back")]       public bool Back { get; set; }
    [JsonPropertyName("comment")]    public string? Comment { get; set; }
    [JsonPropertyName("image")]      public string? Image { get; set; }
    [JsonPropertyName("thumbnails")] public CaaThumbnails? Thumbnails { get; set; }
    [JsonPropertyName("approved")]   public bool Approved { get; set; }
}

public class CaaThumbnails
{
    [JsonPropertyName("250")]   public string? Small { get; set; }
    [JsonPropertyName("500")]   public string? Medium { get; set; }
    [JsonPropertyName("1200")]  public string? Large { get; set; }
    [JsonPropertyName("large")] public string? LargeAlt { get; set; }
    [JsonPropertyName("small")] public string? SmallAlt { get; set; }
}

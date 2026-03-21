using System.Text.Json;

namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzJsonOptions
{
    internal static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
}

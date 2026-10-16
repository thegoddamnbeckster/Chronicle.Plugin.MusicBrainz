using Chronicle.Plugin.MusicBrainz;
using Xunit;

namespace Chronicle.Plugin.MusicBrainz.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new MusicBrainzMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("musicbrainz", p.Source);
        Assert.Contains(p.IdShapes, s => System.Text.RegularExpressions.Regex.IsMatch("artist:5b11f4ce", s.Pattern) && s.Depths.Contains(0) && s.ItemFamilies.Contains("music"));
        Assert.Contains(p.IdShapes, s => System.Text.RegularExpressions.Regex.IsMatch("release-group:f4179994", s.Pattern) && s.Depths.Contains(1));
        Assert.Contains(p.IdShapes, s => System.Text.RegularExpressions.Regex.IsMatch("recording:e14a55f4", s.Pattern) && s.Depths.Contains(2));
        Assert.Contains(p.IdShapes, s => System.Text.RegularExpressions.Regex.IsMatch("release:abc", s.Pattern) && s.Depths.Length == 0);
    }
}

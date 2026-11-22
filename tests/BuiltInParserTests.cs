using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;
using Xunit;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Tmdb;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   Without the Offline Importer, the built-in parser reads paths, and the
///   common shapes still match.
/// </summary>
public class BuiltInParserTests
{
    [Theory]
    [InlineData("Frieren (2023)/Season 1/Frieren - S01E05.mkv", "Frieren", null, 1, 5, 5)]
    [InlineData("Sample Show (2024)/Season 01/S01E03.mkv", "", null, 1, 3, 3)]
    [InlineData("Sample Show (2024)/Season 01/Sample Show - S01E01-E02 [1080p].mkv", "Sample Show", null, 1, 1, 2)]
    [InlineData("One Piece/One Piece - 070.mkv", "One Piece", null, null, 70, 70)]
    [InlineData("[SubsPlease] Mushoku Tensei S2 - 13 (1080p).mkv", "Mushoku Tensei S2", null, null, 13, 13)]
    [InlineData("TANK.CHAIR.S01E05.1080p.AMZN.WEB-DL.DUAL.DDP5.1.H.264-VARYG.mkv", "TANK CHAIR", null, 1, 5, 5)]
    [InlineData("Your Name (2016)/Your Name (2016).mkv", "Your Name", 2016, null, 0, 0)]
    public void ThePath_IsRead(string path, string name, int? year, int? season, float start, float end)
    {
        var parsed = PathParser.ParseBuiltIn(path);

        Assert.True(parsed.Success);
        Assert.Equal(PathParser.BuiltInRuleName, parsed.RuleName);
        Assert.Equal((name, year, season, start, end), (parsed.SeriesName, parsed.SeriesYear, parsed.SeasonNumber, parsed.EpisodeStart, parsed.EpisodeEnd));
    }

    [Fact]
    public void TmdbTags_AreReadWithTheirKindAndDepth()
    {
        var parsed = PathParser.ParseBuiltIn("Show [tmdbid-tv12345]/Season 01/Show - S01E03 [tmdb-e1234501003] {tmdb-372058}.mkv");

        Assert.Equal(
            [("1234501003", MetadataEntityType.Episode, 0), ("372058", null, 0), ("12345", MetadataEntityType.Series, 2)],
            parsed.IDs.Select(id => (id.ID, id.Type, id.Depth)).ToArray()
        );
        Assert.All(parsed.IDs, id => Assert.Equal(MetadataSource.TMDB, id.Source));
    }

    [Fact]
    public void AGroupInFront_IsTheReleaseGroup()
        => Assert.Equal("SubsPlease", PathParser.ParseBuiltIn("[SubsPlease] Mushoku Tensei S2 - 13 (1080p).mkv").ReleaseGroup);

    [Fact]
    public void WithoutTheImporter_TheBuiltInParserIsUsed()
        => Assert.False(new PathParser(null).UsesImporter);

    [Theory]
    [InlineData("Frieren (2023)/Season 1/Frieren - S01E05.mkv", "tmdb://episode/20986701005+tmdb://series/209867")]
    [InlineData("Sample Show (2024)/Season 01/S01E03.mkv", "tmdb://episode/1234501003+tmdb://series/12345")]
    [InlineData("One Piece/One Piece - 070.mkv", "tmdb://episode/3785402009+tmdb://series/37854")]
    [InlineData("[SubsPlease] Mushoku Tensei S2 - 13 (1080p).mkv", "tmdb://episode/9466402013+tmdb://series/94664")]
    [InlineData("Your Name (2016)/Your Name (2016).mkv", "tmdb://movie/372058")]
    [InlineData("Sample Show (2024) [tmdbid-tv12345]/Season 01/Sample Show - S01E03.mkv", "tmdb://episode/1234501003+tmdb://series/12345")]
    [InlineData("Unrecognized/Auto Scan/Series/TANK.CHAIR.S01E05.1080p.AMZN.WEB-DL.DUAL.DDP5.1.H.264-VARYG.mkv", "tmdb://episode/32037701005+tmdb://series/320377")]
    public async Task WithoutTheImporter_CommonPathsStillMatch(string path, string reference)
    {
        var outcome = await FilenameCorpusTests.Pipeline(Fixtures(), new PathParser(null)).Match([new FileLocation(1, path)], new(), TestContext.Current.CancellationToken);

        Assert.True(outcome.Kind is MatchKind.Accepted, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal([reference], FilenameCorpusTests.References(outcome));
    }
}

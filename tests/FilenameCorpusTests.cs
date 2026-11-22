using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;
using Shoko.Plugin.TmdbFileMatcher.Search;
using Xunit;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Parsed;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Tmdb;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   Paths, what the Offline Importer makes of them, and what they are
///   linked to; the scratch cases are real file names from the test server.
/// </summary>
public class FilenameCorpusTests
{
    internal sealed record Case(string Path, ParsedFileResult Parsed, string[] References, MatchKind Kind, string? Taken, Action<FakeCatalog>? Arrange = null);

    private const string Scratch = "Unrecognized/Auto Scan/Series/";

    private static string Ref(params MetadataGuid[] ids)
        => string.Join('+', ids.Select(id => id.ToString()));

    private static string Ep(string seriesID, int season, int number)
        => Ref(Episode(seriesID, season, number), Series(seriesID));

    private static Case Make(string path, Func<string, ParsedFileResult> parse, string[] references, MatchKind kind, string? taken, Action<FakeCatalog>? arrange = null)
        => new(path, parse(path), references, kind, taken, arrange);

    private static readonly Dictionary<string, Case> _cases = new()
    {
        ["tagged episode"] = Make(
            "Sample Show (2024) [tmdbid-tv12345]/Season 01/Sample Show - S01E03 [tmdb-e1234501003].mkv",
            path => File(path, "Sample Show", 1, 3, ids: [Tag("1234501003", MetadataEntityType.Episode, 0), Tag(SampleShowID, MetadataEntityType.Series, 2)]),
            [Ep(SampleShowID, 1, 3)], MatchKind.Accepted, "tag:tmdb://episode/1234501003"),
        ["tagged show with SxxEyy"] = Make(
            "Sample Show (2024) [tmdbid-tv12345]/Season 01/[Group] Sample Show - S01E03 [1080p].mkv",
            path => File(path, "Sample Show", 1, 3, group: "Group", ids: [Tag(SampleShowID, MetadataEntityType.Series, 2)]),
            [Ep(SampleShowID, 1, 3)], MatchKind.Accepted, "tag:tmdb://episode/1234501003"),
        ["bare TMDb ID as a show"] = Make(
            "Sample Show (2024) [tmdbid-12345]/Season 01/Sample Show - S01E03.mkv",
            path => File(path, "Sample Show", 1, 3, ids: [Tag(SampleShowID, null, 2)]),
            [Ep(SampleShowID, 1, 3)], MatchKind.Accepted, "tag:tmdb://episode/1234501003"),
        ["bare TMDb ID as a movie"] = Make(
            "Your Name (2016) [tmdbid-372058]/Your Name (2016).mkv",
            path => File(path, "Your Name (2016)", episode: 1, year: 2016, rule: "fallback", ids: [Tag(YourNameID, null, 1)]),
            [Ref(Movie(YourNameID))], MatchKind.Accepted, "tag:tmdb://movie/372058",
            catalog => catalog.Add(Show(YourNameID, "A Show Sharing The Number", 2010, false, seasons: [3]))),
        ["movie by tag"] = Make(
            "Your Name (2016) [tmdbid-m372058]/Your Name (2016).mkv",
            path => File(path, "Your Name (2016)", episode: 1, year: 2016, rule: "fallback", ids: [Tag(YourNameID, MetadataEntityType.Movie, 1)]),
            [Ref(Movie(YourNameID))], MatchKind.Accepted, "tag:tmdb://movie/372058"),
        ["movie by the fallback shape"] = Make(
            "Your Name (2016)/Your Name (2016).mkv",
            path => File(path, "Your Name (2016)", episode: 1, year: 2016, rule: "fallback"),
            [Ref(Movie(YourNameID))], MatchKind.Accepted, "search:Your Name"),
        ["the title's main part"] = Make(
            "Frieren (2023)/Season 1/Frieren - S01E05.mkv",
            path => File(path, "Frieren", 1, 5),
            [Ep(FrierenID, 1, 5)], MatchKind.Accepted, "search:Frieren"),
        ["a year off by one"] = Make(
            "Frieren (2024)/Season 1/Frieren - S01E05.mkv",
            path => File(path, "Frieren", 1, 5),
            [Ep(FrierenID, 1, 5)], MatchKind.Accepted, "search:Frieren"),
        ["a sequel suffix"] = Make(
            "Mushoku Tensei II/Mushoku Tensei II - 05.mkv",
            path => File(path, "Mushoku Tensei II", episode: 5),
            [Ep(MushokuID, 2, 5)], MatchKind.Accepted, "search:Mushoku Tensei II"),
        ["absolute numbering"] = Make(
            "One Piece/One Piece - 070.mkv",
            path => File(path, "One Piece", episode: 70),
            [Ep(OnePieceID, 2, 9)], MatchKind.Accepted, "search:One Piece"),
        ["S00"] = Make(
            "One Piece/Specials/One Piece - S00E02.mkv",
            path => File(path, "One Piece", 0, 2, type: EpisodeType.Special),
            [Ep(OnePieceID, 0, 2)], MatchKind.Accepted, "search:One Piece"),
        ["two episodes in one file"] = Make(
            "Sample Show (2024)/Season 01/Sample Show - S01E01-E02.mkv",
            path => File(path, "Sample Show", 1, 1, 2),
            [Ep(SampleShowID, 1, 1), Ep(SampleShowID, 1, 2)], MatchKind.Accepted, "search:Sample Show"),
        ["a percentage tag"] = Make(
            "Sample Show (2024)/Season 01/Sample Show - S01E04 [tmdb-e1234501004'0-50].mkv",
            path => File(path, "Sample Show", 1, 4, ids: [Tag("1234501004", MetadataEntityType.Episode, 0, 0, 50)]),
            [Ep(SampleShowID, 1, 4) + "@0-50"], MatchKind.Accepted, "tag:tmdb://episode/1234501004",
            catalog => catalog.Shows[Series(SampleShowID)].Stored = true),
        ["a bad tag stops the match"] = Make(
            "Sample Show (2024)/Season 01/Sample Show - S01E04 [tmdb-e999].mkv",
            path => File(path, "Sample Show", 1, 4, ids: [Tag("999", MetadataEntityType.Episode, 0)]),
            [], MatchKind.None, null),
        ["a show sharing its title with a movie"] = Make(
            "Dororo (2019)/Season 1/Dororo - S01E03.mkv",
            path => File(path, "Dororo", 1, 3),
            [Ep(DororoShowID, 1, 3)], MatchKind.Accepted, "search:Dororo"),
        ["a movie sharing its title with a show"] = Make(
            "Dororo (2007)/Dororo (2007).mkv",
            path => File(path, "Dororo (2007)", episode: 1, year: 2007, rule: "fallback"),
            [Ref(Movie(DororoMovieID))], MatchKind.Accepted, "search:Dororo"),
        ["two shows of one name are provisional"] = Make(
            "Hunter x Hunter/Hunter x Hunter - 05.mkv",
            path => File(path, "Hunter x Hunter", episode: 5),
            [Ep(HunterNewID, 1, 5)], MatchKind.Provisional, "search:Hunter x Hunter"),
        ["a fractional episode"] = Make(
            "Sample Show (2024)/Season 01/Sample Show - S01E05.5.mkv",
            path => File(path, "Sample Show", 1, 5.5f),
            [], MatchKind.None, null),
        ["credits"] = Make(
            "Sample Show (2024)/Extras/Sample Show - NCOP1.mkv",
            path => File(path, "Sample Show", episode: 1, type: EpisodeType.Credits),
            [], MatchKind.None, null),
        ["scratch: a dotted name in a generic folder"] = Make(
            Scratch + "TANK.CHAIR.S01E05.1080p.AMZN.WEB-DL.DUAL.DDP5.1.H.264-VARYG.mkv",
            path => File(path, "TANK CHAIR", 1, 5),
            [Ep(TankChairID, 1, 5)], MatchKind.Accepted, "search:TANK CHAIR"),
        ["scratch: a subtitle after a dash"] = Make(
            Scratch + "[SubsPlus+] Nia Liston - The Merciless Maiden - S01E05 (ADN WEB-DL 1080p AVC AAC) [038D5F82].mkv",
            path => File(path, "Nia Liston - The Merciless Maiden", 1, 5, group: "SubsPlus+"),
            [Ep(NiaListonID, 1, 5)], MatchKind.Accepted, "search:Nia Liston - The Merciless Maiden"),
        ["scratch: a season TMDb does not have"] = Make(
            Scratch + "Tokyo.Revengers.S04E02.Be.Left.Behind.the.Times.1080p.DSNP.WEB-DL.AAC2.0.H.264-NTb.mkv",
            path => File(path, "Tokyo Revers", 4, 2),
            [], MatchKind.None, null),
        ["scratch: a show TMDb does not have"] = Make(
            Scratch + "Bleach.S17E50.Thousand-Year.Blood.War.DEATH.and.STRAWBERRY.1080p.DSNP.WEB-DL.AAC2.0.H.264-NTb.mkv",
            path => File(path, "Bleach", 17, 50),
            [], MatchKind.None, null),
    };

    public static TheoryData<string> Cases => [.. _cases.Keys];

    internal static MatchPipeline Pipeline(FakeCatalog catalog, PathParser parser)
        => new(parser, catalog);

    internal static string[] References(MatchOutcome outcome)
        => outcome.IsMatched
            ? [.. TmdbReleaseInfoProvider.Build(outcome, 1, []).CrossReferences.Select(reference => string.Join('+', reference.IDs.Select(id => id.ToString())) + (reference.PercentageStart is { } start ? $"@{start}-{reference.PercentageEnd}" : string.Empty))]
            : [];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheCorpus_MatchesAsExpected(string name)
    {
        var expected = _cases[name];
        var catalog = Fixtures();
        expected.Arrange?.Invoke(catalog);
        var parser = new PathParser(Fake.Importer(new Dictionary<string, ParsedFileResult> { [expected.Path] = expected.Parsed }));

        var outcome = await Pipeline(catalog, parser).Match([new FileLocation(1, expected.Path)], new(), TestContext.Current.CancellationToken);

        Assert.True(expected.Kind == outcome.Kind, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal(expected.Taken, outcome.Path);
        Assert.Equal(expected.References, References(outcome));
    }

    [Fact]
    public async Task AProvisionalMatch_DefersToTheNextProvider()
    {
        const string path = "Hunter x Hunter/Hunter x Hunter - 05.mkv";
        var parser = new PathParser(Fake.Importer(new Dictionary<string, ParsedFileResult> { [path] = File(path, "Hunter x Hunter", episode: 5) }));

        var outcome = await Pipeline(Fixtures(), parser).Match([new FileLocation(1, path)], new(), TestContext.Current.CancellationToken);

        Assert.True(TmdbReleaseInfoProvider.Build(outcome, 1, []).DeferToNext);
        Assert.False(TmdbReleaseInfoProvider.Build(outcome with { Kind = MatchKind.Accepted }, 1, []).DeferToNext);
    }

    [Fact]
    public async Task AnImporterMisreading_IsSearchedAsTheFileNameReadsIt()
    {
        var path = Scratch + "Tokyo.Revengers.S04E02.Be.Left.Behind.the.Times.1080p.DSNP.WEB-DL.AAC2.0.H.264-NTb.mkv";
        var catalog = Fixtures();
        var parser = new PathParser(Fake.Importer(new Dictionary<string, ParsedFileResult> { [path] = File(path, "Tokyo Revers", 4, 2) }));

        var outcome = await Pipeline(catalog, parser).Match([new FileLocation(1, path)], new(), TestContext.Current.CancellationToken);

        Assert.Contains("series:Tokyo Revengers:", catalog.Searches);
        Assert.Equal(MatchKind.None, outcome.Kind);
    }

    [Fact]
    public async Task AnUnreachableTmdb_GivesUnavailable()
    {
        const string path = "Frieren (2023)/Season 1/Frieren - S01E05.mkv";
        var catalog = Fixtures();
        catalog.Unavailable = true;

        var outcome = await Pipeline(catalog, new PathParser(null)).Match([new FileLocation(1, path)], new(), TestContext.Current.CancellationToken);

        Assert.Equal(MatchKind.Unavailable, outcome.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), outcome.RetryAfter);
    }

    [Theory]
    [InlineData("Tokyo.Revengers.S04E02.Be.Left.Behind.the.Times.1080p.DSNP.WEB-DL.AAC2.0.H.264-NTb.mkv", "Tokyo Revengers")]
    [InlineData("Bleach.S17E50.Thousand-Year.Blood.War.DEATH.and.STRAWBERRY.1080p.DSNP.WEB-DL.AAC2.0.H.264-NTb.mkv", "Bleach")]
    [InlineData("[SubsPlus+] Nia Liston - The Merciless Maiden - S01E05 (ADN WEB-DL 1080p AVC AAC) [038D5F82].mkv", "Nia Liston - The Merciless Maiden")]
    [InlineData("[Some-Stuffs] Steel Ball Run - JoJo's Bizarre Adventure 05 (1080p) [A96A9DB2].mkv", null)]
    [InlineData("[SubsPlease] Mushoku Tensei S2 - 13 (1080p).mkv", "Mushoku Tensei")]
    public void TheFileName_GivesItsSeries(string fileName, string? expected)
        => Assert.Equal(expected, EvidenceReader.FileNameSeries(fileName));

    [Fact]
    public void AGenericFolder_IsNoSeriesName()
    {
        var path = Scratch + "TANK.CHAIR.S01E05.1080p.AMZN.WEB-DL.DUAL.DDP5.1.H.264-VARYG.mkv";
        var evidence = EvidenceReader.Read(new FileLocation(1, path), File(path, "TANK CHAIR", 1, 5));

        Assert.Null(evidence.FolderSeriesName);
        Assert.DoesNotContain(QueryPlan.Build(evidence), query => query.Origin == "folder name");
    }

    [Fact]
    public void ARealFolder_AboveAGenericOne_StillGivesItsYear()
    {
        var evidence = EvidenceReader.Read(new FileLocation(1, "Shows/Frieren (2023)/Series/Frieren - S01E05.mkv"), File("Frieren - S01E05.mkv", "Frieren", 1, 5));

        Assert.Equal(2023, evidence.Year);
    }
}

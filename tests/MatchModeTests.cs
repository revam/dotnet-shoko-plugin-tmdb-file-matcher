using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Hashing;
using Shoko.Plugin.OfflineImporter.Abstractions.Configuration;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;
using Xunit;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Parsed;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Tmdb;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   The three match modes, each path read both by the Offline Importer and
///   by the built-in parser: lax searches untagged files, and the strict
///   modes act only on tagged ones, the tag alone deciding without extra
///   info.
/// </summary>
public class MatchModeTests
{
    private const string EpisodeInFileName = "Sample Show (2024)/Season 01/Sample Show - S01E03 [tmdb-e1234501003].mkv";
    private const string ShowInFileName = "Sample Show (2024)/Season 01/Sample Show - S01E03 [tmdbid-tv12345].mkv";
    private const string ShowInFolder = "Sample Show (2024) [tmdbid-tv12345]/Season 01/Sample Show - S01E03.mkv";
    private const string ShowWithoutEpisode = "Sample Show [tmdbid-tv12345]/Sample Show.mkv";
    private const string BareShowInFolder = "Sample Show (2024) [tmdbid-12345]/Season 01/Sample Show - S01E03.mkv";
    private const string MovieInFolder = "Your Name (2016) [tmdbid-m372058]/Your Name (2016).mkv";
    private const string BareMovieInFolder = "Your Name (2016) [tmdbid-372058]/Your Name (2016).mkv";
    private const string Untagged = "Frieren (2023)/Season 1/Frieren - S01E05.mkv";

    private const string Episode3 = "tmdb://episode/1234501003+tmdb://series/12345";
    private const string YourName = "tmdb://movie/372058";

    // What the importer makes of each path, as its rules and tag grammar read them.
    private static readonly Dictionary<string, ParsedFileResult> _importer = new()
    {
        [EpisodeInFileName] = File(EpisodeInFileName, "Sample Show", 1, 3, ids: [Tag("1234501003", MetadataEntityType.Episode, 0)]),
        [ShowInFileName] = File(ShowInFileName, "Sample Show", 1, 3, ids: [Tag(SampleShowID, MetadataEntityType.Series, 0)]),
        [ShowInFolder] = File(ShowInFolder, "Sample Show", 1, 3, ids: [Tag(SampleShowID, MetadataEntityType.Series, 2)]),
        [ShowWithoutEpisode] = File(ShowWithoutEpisode, "Sample Show", ids: [Tag(SampleShowID, MetadataEntityType.Series, 1)]),
        [BareShowInFolder] = File(BareShowInFolder, "Sample Show", 1, 3, ids: [Tag(SampleShowID, null, 2)]),
        [MovieInFolder] = File(MovieInFolder, "Your Name (2016)", episode: 1, year: 2016, rule: "fallback", ids: [Tag(YourNameID, MetadataEntityType.Movie, 1)]),
        [BareMovieInFolder] = File(BareMovieInFolder, "Your Name (2016)", episode: 1, year: 2016, rule: "fallback", ids: [Tag(YourNameID, null, 1)]),
        [Untagged] = File(Untagged, "Frieren", 1, 5),
    };

    private static PathParser Parser(bool withImporter)
        => new(withImporter ? Fake.Importer(_importer) : null);

    private static async Task<(MatchOutcome Outcome, FakeCatalog Catalog)> Match(string path, TmdbFileMatcherConfiguration config, bool withImporter, bool stored = false)
    {
        var catalog = Fixtures(stored);
        var outcome = await new MatchPipeline(Parser(withImporter), catalog).Match([new FileLocation(1, path)], config, TestContext.Current.CancellationToken);
        return (outcome, catalog);
    }

    private static TmdbFileMatcherConfiguration Config(MatchMode mode)
        => new() { Mode = mode };

    public static TheoryData<MatchMode, bool> EveryMode => new()
    {
        { MatchMode.Lax, false }, { MatchMode.Lax, true },
        { MatchMode.StrictAndInfo, false }, { MatchMode.StrictAndInfo, true },
        { MatchMode.StrictAndFast, false }, { MatchMode.StrictAndFast, true },
    };

    public static TheoryData<MatchMode, bool> StrictModes => new()
    {
        { MatchMode.StrictAndInfo, false }, { MatchMode.StrictAndInfo, true },
        { MatchMode.StrictAndFast, false }, { MatchMode.StrictAndFast, true },
    };

    [Fact]
    public void TheDefault_IsStrictWithExtraInfo()
        => Assert.Equal(MatchMode.StrictAndInfo, new TmdbFileMatcherConfiguration().Mode);

    [Theory]
    [MemberData(nameof(EveryMode))]
    public async Task AnEpisodeTaggedInTheFileName_MatchesInEveryMode(MatchMode mode, bool withImporter)
    {
        // An episode tag without its show's tag resolves against what the server stores.
        var (outcome, catalog) = await Match(EpisodeInFileName, Config(mode), withImporter, stored: true);

        Assert.True(outcome.Kind is MatchKind.Accepted, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal([Episode3], FilenameCorpusTests.References(outcome));
        Assert.Empty(catalog.Searches);
    }

    [Theory]
    [MemberData(nameof(EveryMode))]
    public async Task AMovieTag_MatchesInEveryMode(MatchMode mode, bool withImporter)
    {
        var (outcome, catalog) = await Match(MovieInFolder, Config(mode), withImporter);

        Assert.True(outcome.Kind is MatchKind.Accepted, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal([YourName], FilenameCorpusTests.References(outcome));
        Assert.Empty(catalog.Searches);
    }

    [Theory]
    [MemberData(nameof(EveryMode))]
    public async Task ABareTagOnAMovie_MatchesInEveryMode(MatchMode mode, bool withImporter)
    {
        var (outcome, _) = await Match(BareMovieInFolder, Config(mode), withImporter);

        Assert.True(outcome.Kind is MatchKind.Accepted, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal([YourName], FilenameCorpusTests.References(outcome));
    }

    [Theory]
    [InlineData(MatchMode.Lax, false)]
    [InlineData(MatchMode.Lax, true)]
    [InlineData(MatchMode.StrictAndInfo, false)]
    [InlineData(MatchMode.StrictAndInfo, true)]
    public async Task AShowTag_TakesTheEpisodeFromTheFileName_WithExtraInfo(MatchMode mode, bool withImporter)
    {
        foreach (var path in new[] { ShowInFileName, ShowInFolder, BareShowInFolder })
        {
            var (outcome, catalog) = await Match(path, Config(mode), withImporter);

            Assert.True(outcome.Kind is MatchKind.Accepted, $"{path}: {outcome.Kind}: {outcome.Reason}");
            Assert.Equal([Episode3], FilenameCorpusTests.References(outcome));
            Assert.Empty(catalog.Searches);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AShowTag_WithoutAnEpisodeInTheTag_IsSkipped_WithoutExtraInfo(bool withImporter)
    {
        foreach (var path in new[] { ShowInFileName, ShowInFolder, BareShowInFolder, ShowWithoutEpisode })
        {
            var (outcome, catalog) = await Match(path, Config(MatchMode.StrictAndFast), withImporter);

            Assert.True(outcome.Kind is MatchKind.Skipped, $"{path}: {outcome.Kind}: {outcome.Reason}");
            Assert.Contains("without extra info", outcome.Reason);
            Assert.Empty(catalog.Searches);
            Assert.Empty(catalog.Refreshes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AShowTag_OnAFileWithNoEpisodeNumber_HasNothingToPlace_WithExtraInfo(bool withImporter)
    {
        var (outcome, catalog) = await Match(ShowWithoutEpisode, Config(MatchMode.StrictAndInfo), withImporter);

        Assert.Equal(MatchKind.None, outcome.Kind);
        Assert.Empty(catalog.Searches);
    }

    [Fact]
    public async Task AShowAndEpisodeTag_DecidesAlone_WithoutExtraInfo()
    {
        // The importer's grammar reads [tmdbid-12345-1234501003] in a file name as the show and its episode.
        const string path = "Sample Show (2024)/Sample Show - 03 [tmdbid-12345-1234501003].mkv";
        var parsed = File(path, "Sample Show", episode: 3, ids: [Tag("1234501003", MetadataEntityType.Episode, 0), Tag(SampleShowID, MetadataEntityType.Series, 0)]);
        var catalog = Fixtures();

        var outcome = await new MatchPipeline(new PathParser(Fake.Importer(new Dictionary<string, ParsedFileResult> { [path] = parsed })), catalog)
            .Match([new FileLocation(1, path)], Config(MatchMode.StrictAndFast), TestContext.Current.CancellationToken);

        Assert.True(outcome.Kind is MatchKind.Accepted, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal([Episode3], FilenameCorpusTests.References(outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUntaggedFile_IsSearchedByName_InLaxMode(bool withImporter)
    {
        var (outcome, catalog) = await Match(Untagged, Config(MatchMode.Lax), withImporter);

        Assert.True(outcome.Kind is MatchKind.Accepted, $"{outcome.Kind}: {outcome.Reason}");
        Assert.Equal(["tmdb://episode/20986701005+tmdb://series/209867"], FilenameCorpusTests.References(outcome));
        Assert.NotEmpty(catalog.Searches);
    }

    [Theory]
    [MemberData(nameof(StrictModes))]
    public async Task AnUntaggedFile_IsLeftAlone_InStrictMode(MatchMode mode, bool withImporter)
    {
        var (outcome, catalog) = await Match(Untagged, Config(mode), withImporter);

        Assert.Equal(MatchKind.Skipped, outcome.Kind);
        Assert.Contains("no TMDb tag", outcome.Reason);
        Assert.False(outcome.IsMatched);
        Assert.Empty(catalog.Searches);
        Assert.Empty(catalog.Refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUntaggedFile_IsLoggedAtDebug_EvenWhenAsked(bool isAutomatic)
    {
        var logger = new ListLogger();
        var provider = new TmdbReleaseInfoProvider(logger, () => new() { AutoMatchRules = [new()] }, new MatchPipeline(new PathParser(null), Fixtures()), Fake.Of<IOfflineAutoMatchService>(), TimeProvider.System);
        var file = Fake.Of<IVideoFile>(("get_ManagedFolderID", _ => 1), ("get_RelativePath", _ => Untagged));
        var video = Fake.Of<IVideo>(("get_Files", _ => new[] { file }), ("get_Hashes", _ => Array.Empty<IHashDigest>()));

        Assert.Null(await provider.GetReleaseInfoForVideo(new() { Video = video, IsAutomatic = isAutomatic }, TestContext.Current.CancellationToken));
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("no TMDb tag", message);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

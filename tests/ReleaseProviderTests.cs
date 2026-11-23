using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Hashing;
using Shoko.Abstractions.Video.Release;
using Shoko.Plugin.OfflineImporter.Abstractions.Configuration;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;
using Xunit;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Tmdb;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   The release provider over the fake catalog: a TMDb that cannot be
///   reached is thrown to core, in every mode, asked for or not, so Shoko
///   retries the search instead of taking it as no match.
/// </summary>
public class ReleaseProviderTests
{
    private const string Tagged = "Sample Show (2024) [tmdbid-tv12345]/Season 01/Sample Show - S01E03 [tmdb-e1234501003].mkv";

    public static TheoryData<MatchMode, bool> EveryMode => new()
    {
        { MatchMode.Lax, false },
        { MatchMode.Lax, true },
        { MatchMode.StrictAndInfo, false },
        { MatchMode.StrictAndInfo, true },
        { MatchMode.StrictAndFast, false },
        { MatchMode.StrictAndFast, true },
    };

    private static Task<ReleaseInfo?> Ask(FakeCatalog catalog, MatchMode mode, bool isAutomatic, TimeSpan? refreshTimeout = null)
    {
        var pipeline = new MatchPipeline(new PathParser(null), catalog) { RefreshTimeout = refreshTimeout ?? TimeSpan.FromSeconds(30) };
        var provider = new TmdbReleaseInfoProvider(NullLogger.Instance, () => new() { Mode = mode, AutoMatchRules = [new AutoMatchRule()] }, pipeline, Fake.Of<IOfflineAutoMatchService>());
        var file = Fake.Of<IVideoFile>(("get_ManagedFolderID", _ => 1), ("get_RelativePath", _ => Tagged));
        var video = Fake.Of<IVideo>(("get_Files", _ => new[] { file }), ("get_Hashes", _ => Array.Empty<IHashDigest>()));
        return provider.GetReleaseInfoForVideo(new() { Video = video, IsAutomatic = isAutomatic }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(EveryMode))]
    public async Task AnUnreachableTmdb_IsThrown_WithItsRetryAfter(MatchMode mode, bool isAutomatic)
    {
        var catalog = Fixtures();
        catalog.Unavailable = true;

        var exception = await Assert.ThrowsAsync<MetadataProviderUnavailableException>(() => Ask(catalog, mode, isAutomatic));

        Assert.Equal(TimeSpan.FromMinutes(5), exception.RetryAfter);
    }

    [Theory]
    [MemberData(nameof(EveryMode))]
    public async Task ARefreshCoreRefuses_IsThrown(MatchMode mode, bool isAutomatic)
    {
        var catalog = Fixtures();
        catalog.RefreshRefused = true;

        var exception = await Assert.ThrowsAsync<MetadataProviderUnavailableException>(() => Ask(catalog, mode, isAutomatic));

        Assert.Contains("paused", exception.Message);
        Assert.Equal(TimeSpan.FromMinutes(10), exception.RetryAfter);
    }

    [Theory]
    [MemberData(nameof(EveryMode))]
    public async Task ARefreshThatRunsOver_IsThrown(MatchMode mode, bool isAutomatic)
    {
        var catalog = Fixtures();
        catalog.RefreshHangs = true;

        var exception = await Assert.ThrowsAsync<MetadataProviderUnavailableException>(() => Ask(catalog, mode, isAutomatic, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("took longer", exception.Message);
        Assert.Equal(TimeSpan.FromMinutes(10), exception.RetryAfter);
    }

    [Fact]
    public async Task AReachableTmdb_StillMatches()
    {
        var release = await Ask(Fixtures(), MatchMode.StrictAndFast, false);

        Assert.NotNull(release);
        Assert.NotEmpty(release.CrossReferences);
    }
}

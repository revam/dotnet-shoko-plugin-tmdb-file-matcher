using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Hashing;
using Shoko.Abstractions.Video.Release;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;
using Xunit;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Tmdb;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   The release provider over the fake catalog: when core is asked to
///   look again after TMDb could not be reached.
/// </summary>
public class ReleaseProviderTests
{
    private static readonly DateTime _outage = new(2026, 11, 23, 10, 0, 0);

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => new(now, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static async Task<TmdbReleaseInfoProvider> AfterAnOutage()
    {
        var catalog = Fixtures();
        catalog.Unavailable = true;
        var provider = new TmdbReleaseInfoProvider(NullLogger.Instance, () => Modes.Lax, new MatchPipeline(new PathParser(null), catalog), null, new FixedTime(_outage));
        var file = Fake.Of<IVideoFile>(("get_ManagedFolderID", _ => 1), ("get_RelativePath", _ => "Frieren (2023)/Season 1/Frieren - S01E05.mkv"));
        var video = Fake.Of<IVideo>(("get_Files", _ => new[] { file }), ("get_Hashes", _ => Array.Empty<IHashDigest>()));

        Assert.Null(await provider.GetReleaseInfoForVideo(new() { Video = video, IsAutomatic = false }, TestContext.Current.CancellationToken));
        return provider;
    }

    private static IReleaseMatchAttempt Attempt(DateTime startedAt, DateTime endedAt, bool isCompleted)
        => Fake.Of<IReleaseMatchAttempt>(
            ("get_AttemptedProviderNames", _ => new[] { TmdbReleaseInfoProvider.ProviderName }),
            ("get_AttemptCount", _ => 1),
            ("get_AttemptStartedAt", _ => startedAt),
            ("get_AttemptEndedAt", _ => endedAt),
            ("get_IsCompleted", _ => isCompleted)
        );

    [Fact]
    public async Task AnAttemptDuringTheOutage_IsScannedAgainAfterItsRetryAfter()
    {
        var provider = await AfterAnOutage();

        Assert.Equal(TimeSpan.FromMinutes(5), provider.GetRescanDelay(Fake.Of<IReleaseInfo>(), Attempt(_outage.AddMinutes(-1), _outage.AddMinutes(1), true)));
        Assert.Equal(TimeSpan.FromMinutes(5), provider.GetRescanDelay(Fake.Of<IReleaseInfo>(), Attempt(_outage.AddMinutes(-1), _outage.AddMinutes(-1), false)));
    }

    [Fact]
    public async Task AnAttemptThatEndedBeforeTheOutage_IsNotScannedAgain()
    {
        var provider = await AfterAnOutage();

        Assert.Null(provider.GetRescanDelay(Fake.Of<IReleaseInfo>(), Attempt(_outage.AddHours(-1), _outage.AddMinutes(-55), true)));
        Assert.Null(provider.GetRescanDelay(Fake.Of<IReleaseInfo>(), Attempt(_outage.AddMinutes(1), _outage.AddMinutes(2), true)));
    }
}

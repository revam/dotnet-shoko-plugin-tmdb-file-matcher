using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Video.Release;
using Xunit;
using static Shoko.Plugin.TmdbFileMatcher.Tests.Tmdb;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   The validator's rules for TMDb IDs, saving and healing.
/// </summary>
public class ValidatorTests
{
    private static readonly MetadataGuid _anidbEpisode = new(MetadataSource.AniDB, MetadataEntityType.Episode, "1");

    private static TmdbCrossReferenceValidator Validator(FakeCatalog? catalog = null)
        => new(NullLogger.Instance, catalog ?? Fixtures(stored: true));

    private static Task<ReleaseCrossReferenceValidationResult?> Validate(TmdbCrossReferenceValidator validator, bool healing, params MetadataGuid[] ids)
    {
        var reference = ReleaseVideoCrossReference.For(ids, null, null);
        var context = new ReleaseCrossReferenceValidationContext { Video = null!, Release = null!, CrossReferences = [reference], Index = 0, IsHealing = healing };
        return validator.Validate(context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheScope_IsTmdbsShowsEpisodesAndMovies()
    {
        var scope = Validator().Scope;

        Assert.Equal([MetadataSource.TMDB], scope.Sources);
        Assert.True(scope.Contains(Series(FrierenID)) && scope.Contains(Episode(FrierenID, 1, 1)) && scope.Contains(Movie(YourNameID)));
        Assert.False(scope.Contains(_anidbEpisode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoTmdbID_IsNotTheValidatorsToJudge(bool healing)
        => Assert.Null(await Validate(Validator(), healing, _anidbEpisode));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEpisodeWithItsShow_IsValid(bool healing)
    {
        var result = await Validate(Validator(), healing, Episode(FrierenID, 1, 5), Series(FrierenID));

        Assert.True(result is { IsValid: true, IsCorrected: false });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEpisodeWithoutItsShow_HasItFilledIn_AfterTheEpisode(bool healing)
    {
        var result = await Validate(Validator(), healing, _anidbEpisode, Episode(FrierenID, 1, 5));

        Assert.True(result!.IsCorrected);
        Assert.Equal([_anidbEpisode, Episode(FrierenID, 1, 5), Series(FrierenID)], result.CorrectedIDs);
    }

    [Fact]
    public async Task AWrongShow_IsRefused_WhenSaving()
    {
        var result = await Validate(Validator(), false, Episode(FrierenID, 1, 5), Series(OnePieceID));

        Assert.False(result!.IsValid);
        Assert.Contains("is not an episode of tmdb://series/37854", result.Reason);
    }

    [Fact]
    public async Task AWrongShow_IsHealed_ByTheEpisodesOwnShow()
    {
        var result = await Validate(Validator(), true, Episode(FrierenID, 1, 5), Series(OnePieceID));

        Assert.True(result!.IsCorrected);
        Assert.Equal([Episode(FrierenID, 1, 5), Series(FrierenID)], result.CorrectedIDs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoShows_AreRefusedWhenSaving_AndLeftWhenHealing(bool healing)
    {
        var result = await Validate(Validator(), healing, Episode(FrierenID, 1, 5), Episode(OnePieceID, 1, 1));

        Assert.Equal(healing, result!.IsValid);
        Assert.False(result.IsCorrected);
    }

    [Fact]
    public async Task AShowTmdbDoesNotHave_IsRefused_WhenSaving()
    {
        var result = await Validate(Validator(), false, Series("999999"));

        Assert.False(result!.IsValid);
        Assert.Contains("tmdb://series/999999 does not exist", result.Reason);
    }

    [Fact]
    public async Task NothingIsLookedUp_WhenHealing()
        => Assert.True((await Validate(Validator(), true, Series("999999")))!.IsValid);

    [Fact]
    public async Task AShowNotStored_IsLookedUp()
        => Assert.True((await Validate(Validator(Fixtures()), false, Movie(YourNameID), Series(FrierenID)))!.IsValid);

    [Fact]
    public async Task AnEpisodeNotStored_IsTakenAsItIs()
    {
        var result = await Validate(Validator(Fixtures()), false, Episode(FrierenID, 1, 5));

        Assert.True(result is { IsValid: true, IsCorrected: false });
    }

    [Fact]
    public async Task AnUnreachableTmdb_CountsAsExisting()
    {
        var catalog = Fixtures();
        catalog.Unavailable = true;

        Assert.True((await Validate(Validator(catalog), false, Series(FrierenID)))!.IsValid);
    }
}

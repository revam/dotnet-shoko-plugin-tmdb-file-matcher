using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;

namespace Shoko.Plugin.TmdbFileMatcher.Catalog;

/// <summary>
///   What the plugin asks core about TMDb: searches, lookups, what the
///   server stores, and refreshes.
/// </summary>
/// <remarks>
///   A narrow port over core's metadata services, so the matcher and the
///   validator can be tested with fakes. A TMDb that cannot be reached
///   throws core's <c>MetadataProviderUnavailableException</c>.
/// </remarks>
internal interface ITmdbCatalog
{
    // Searches TMDb for shows or movies by name, and maybe year.
    Task<IReadOnlyList<SourceEntry>> Search(MetadataEntityType kind, string text, int? year, bool includeRestricted, CancellationToken cancellationToken);

    /// <summary>
    ///   Looks a show or a movie up at TMDb, stored or not; <c>null</c> when
    ///   TMDb has no such entry. Throws <c>NotSupportedException</c> when
    ///   the kind cannot be looked up.
    /// </summary>
    Task<SourceEntry?> Lookup(MetadataGuid id, CancellationToken cancellationToken);

    // An episode the server stores, or <c>null</c>.
    EpisodeEntry? GetEpisode(MetadataGuid episodeID);

    // The episodes of a show the server stores, or <c>null</c> when the show is not stored.
    IReadOnlyList<EpisodeEntry>? GetEpisodes(MetadataGuid seriesID);

    // Whether the server stores a show or a movie.
    bool IsStored(MetadataGuid id);

    // Fetches a show into the store with a quick refresh and no images, waiting for it to finish; false when core did not run it.
    Task<bool> Refresh(MetadataGuid seriesID, CancellationToken cancellationToken);
}

/// <summary>
///   A show or a movie as a search or a lookup gives it.
/// </summary>
/// <param name="ID">The entry.</param>
/// <param name="Titles">Its titles, the main one first.</param>
/// <param name="Year">The year it first aired or was released.</param>
/// <param name="IsRestricted">Whether the entry is restricted.</param>
/// <param name="Seasons">
///   The regular seasons, or <c>null</c> when the answer did not say.
/// </param>
internal sealed record SourceEntry(MetadataGuid ID, IReadOnlyList<string> Titles, int? Year, bool IsRestricted, IReadOnlyList<SeasonEntry>? Seasons = null)
{
    // Whether the entry is a movie.
    public bool IsMovie => ID.EntityType == MetadataEntityType.Movie;
}

/// <summary>
///   A regular season of a show.
/// </summary>
/// <param name="Number">The season's number.</param>
/// <param name="EpisodeCount">How many episodes it has, if known.</param>
/// <param name="Year">The year it first aired, if known.</param>
internal sealed record SeasonEntry(int Number, int? EpisodeCount, int? Year);

/// <summary>
///   An episode the server stores.
/// </summary>
/// <param name="ID">The episode.</param>
/// <param name="SeriesID">The show it belongs to.</param>
/// <param name="SeasonNumber">Its season's number, if it has one.</param>
/// <param name="EpisodeNumber">Its number within the season.</param>
/// <param name="Type">The kind of episode.</param>
/// <param name="AirDate">When it first aired, if known.</param>
internal sealed record EpisodeEntry(MetadataGuid ID, MetadataGuid SeriesID, int? SeasonNumber, int EpisodeNumber, EpisodeType Type, DateOnly? AirDate = null);

/// <summary>
///   The catalog over core's linking service, metadata store and refresh
///   service.
/// </summary>
/// <param name="linkingService">Searches and looks entries up.</param>
/// <param name="metadataService">The entries the server stores.</param>
/// <param name="refreshService">Fetches entries into the store.</param>
internal sealed class TmdbCatalog(IMetadataLinkingService linkingService, IMetadataService metadataService, IMetadataRefreshService refreshService) : ITmdbCatalog
{
    private const int PageSize = 6;

    private static readonly MetadataRefreshOptions _refreshOptions = new()
    {
        QuickRefresh = true,
        DownloadImages = false,
        Reason = MetadataRefreshReason.Requested,
    };

    public async Task<IReadOnlyList<SourceEntry>> Search(MetadataEntityType kind, string text, int? year, bool includeRestricted, CancellationToken cancellationToken)
    {
        var options = new MetadataSearchOptions { Query = text, Year = year, IncludeRestricted = includeRestricted, PageSize = PageSize };
        try
        {
            if (kind == MetadataEntityType.Movie)
            {
                var (movies, _) = await linkingService.SearchMovies(MetadataSource.TMDB, options, cancellationToken).ConfigureAwait(false);
                return [.. movies.Select(FromMovie)];
            }

            var (series, _) = await linkingService.SearchSeries(MetadataSource.TMDB, options, cancellationToken).ConfigureAwait(false);
            return [.. series.Select(FromSeries)];
        }
        catch (NotSupportedException)
        {
            // TMDb's provider has linking of this kind turned off.
            return [];
        }
    }

    public async Task<SourceEntry?> Lookup(MetadataGuid id, CancellationToken cancellationToken)
        => id.EntityType == MetadataEntityType.Movie
            ? await linkingService.LookupMovie(id, cancellationToken).ConfigureAwait(false) is { } movie ? FromMovie(movie) : null
            : await linkingService.LookupSeries(id, cancellationToken).ConfigureAwait(false) is { } series ? FromSeries(series) : null;

    public EpisodeEntry? GetEpisode(MetadataGuid episodeID)
        => metadataService.GetEpisode(episodeID) is { } episode ? FromEpisode(episode) : null;

    public IReadOnlyList<EpisodeEntry>? GetEpisodes(MetadataGuid seriesID)
        => metadataService.GetSeries(seriesID) is { } series ? [.. series.Episodes.Select(FromEpisode)] : null;

    public bool IsStored(MetadataGuid id)
        => id.EntityType == MetadataEntityType.Movie ? metadataService.GetMovie(id) is not null : metadataService.GetSeries(id) is not null;

    public Task<bool> Refresh(MetadataGuid seriesID, CancellationToken cancellationToken)
        => refreshService.RefreshEntry(seriesID, force: false, options: _refreshOptions, immediate: true, cancellationToken: cancellationToken);

    private static SourceEntry FromSeries(MetadataSeriesSearchResult result)
        => new(
            result.ID,
            Titles(result.Title, result.OriginalTitle, result.AlternateTitles),
            result.FirstAiredAt?.Year ?? result.SeasonYear,
            result.IsRestricted,
            result.Seasons?.Select(season => new SeasonEntry(season.SeasonNumber, season.EpisodeCount, season.FirstAiredAt?.Year ?? season.FirstEpisodeAiredAt?.Year)).ToList()
        );

    private static SourceEntry FromMovie(MetadataMovieSearchResult result)
        => new(result.ID, Titles(result.Title, result.OriginalTitle, result.AlternateTitles), result.ReleasedAt?.Year, result.IsRestricted);

    private static EpisodeEntry FromEpisode(IEpisode episode)
        => new(episode.ID, episode.SeriesID, episode.SeasonNumber, episode.EpisodeNumber, episode.Type, episode.AirDate);

    private static IReadOnlyList<string> Titles(string title, string? original, IEnumerable<string> alternates)
        => [.. alternates.Prepend(original).Prepend(title).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct()];
}

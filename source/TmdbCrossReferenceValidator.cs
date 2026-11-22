using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Video.Release;
using Shoko.Plugin.TmdbFileMatcher.Catalog;

namespace Shoko.Plugin.TmdbFileMatcher;

/// <summary>
///   Validates the cross-references of a release naming TMDb shows,
///   episodes and movies, so any release may link to TMDb: an entry must
///   exist, an episode comes with its own show, and a cross-reference names
///   at most one show.
/// </summary>
/// <remarks>
///   Found by core by its type, and identified by its full name, so it is
///   never renamed or moved. Only TMDb IDs are ever added or removed, the
///   range is never touched, and when healing the result is never invalid.
/// </remarks>
public sealed class TmdbCrossReferenceValidator : IReleaseCrossReferenceValidator
{
    private readonly ITmdbCatalog _catalog;
    private readonly ILogger _logger;

    /// <summary>
    ///   Creates the validator over the plugin's services.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="serviceProvider">Gives the plugin's TMDb catalog.</param>
    public TmdbCrossReferenceValidator(ILogger<TmdbCrossReferenceValidator> logger, IServiceProvider serviceProvider)
        : this(logger, serviceProvider.GetRequiredService<ITmdbCatalog>())
    {
    }

    // Creates the validator over a given catalog, for the tests.
    internal TmdbCrossReferenceValidator(ILogger logger, ITmdbCatalog catalog)
    {
        _logger = logger;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public string Name => TmdbReleaseInfoProvider.ProviderName;

    /// <inheritdoc/>
    public MetadataEntityScope Scope { get; } = MetadataEntityScope.ForSource(MetadataSource.TMDB, [MetadataEntityType.Series, MetadataEntityType.Episode, MetadataEntityType.Movie]);

    /// <inheritdoc/>
    public async Task<ReleaseCrossReferenceValidationResult?> Validate(ReleaseCrossReferenceValidationContext context, CancellationToken cancellationToken)
    {
        var ids = context.CrossReference.IDs;
        if (!ids.Any(Scope.Contains))
            return null;

        var corrected = ids.ToList();
        var reasons = new List<string>();
        Place(corrected, context.IsHealing, reasons);
        if (reasons.Count is 0 && !context.IsHealing)
        {
            foreach (var id in ids.Where(id => Scope.Contains(id) && id.EntityType != MetadataEntityType.Episode))
            {
                if (!await Exists(id, cancellationToken).ConfigureAwait(false))
                    reasons.Add($"{id} does not exist at TMDb.");
            }
        }

        if (reasons.Count > 0)
            return ReleaseCrossReferenceValidationResult.Invalid(string.Join(" ", reasons));
        return corrected.SequenceEqual(ids) ? ReleaseCrossReferenceValidationResult.Valid() : ReleaseCrossReferenceValidationResult.Corrected(corrected);
    }

    // Fills in a missing show, refuses a wrong one, or heals it by putting the episode's own show in its place.
    private void Place(List<MetadataGuid> ids, bool isHealing, List<string> reasons)
    {
        var episodes = ids.Where(id => Scope.Contains(id) && id.EntityType == MetadataEntityType.Episode).ToList();
        var series = ids.Where(id => Scope.Contains(id) && id.EntityType == MetadataEntityType.Series).ToList();
        var stored = episodes.Select(_catalog.GetEpisode).OfType<EpisodeEntry>().ToList();
        var owners = stored.Select(episode => episode.SeriesID).Distinct().ToList();

        if (series.Count > 1 || owners.Count > 1)
        {
            var named = string.Join(", ", series.Count > 1 ? series : owners);
            if (isHealing)
                _logger.LogDebug("A cross-reference names more than one TMDb show ({SeriesIDs}); left as it is.", named);
            else
                reasons.Add($"It names more than one TMDb show: {named}.");
            return;
        }

        if (owners.Count is 0)
            return;

        var owner = owners[0];
        if (series.Count is 0)
        {
            ids.Insert(ids.IndexOf(stored[0].ID) + 1, owner);
            _logger.LogDebug("Filled in {SeriesID} for {EpisodeID}.", owner, stored[0].ID);
        }
        else if (series[0] != owner && isHealing)
        {
            ids[ids.IndexOf(series[0])] = owner;
            _logger.LogInformation("Moved {EpisodeID} from {OldSeriesID} to its own show {SeriesID}.", stored[0].ID, series[0], owner);
        }
        else if (series[0] != owner)
        {
            reasons.Add($"{stored[0].ID} is not an episode of {series[0]}.");
        }
    }

    // False only when TMDb says it has no such entry; an unreachable TMDb, or a kind it cannot look up, counts as existing.
    private async Task<bool> Exists(MetadataGuid id, CancellationToken cancellationToken)
    {
        if (_catalog.IsStored(id))
            return true;
        try
        {
            return await _catalog.Lookup(id, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception exception) when (exception is NotSupportedException or MetadataProviderUnavailableException)
        {
            _logger.LogDebug("Could not verify {ID}; taking it as it is. ({Message})", id, exception.Message);
            return true;
        }
    }
}

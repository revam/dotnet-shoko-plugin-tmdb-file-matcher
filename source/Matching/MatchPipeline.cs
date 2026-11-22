using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Video.Release;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;
using Shoko.Plugin.TmdbFileMatcher.Catalog;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Search;

namespace Shoko.Plugin.TmdbFileMatcher.Matching;

/// <summary>
///   How a match ended, or what the scores decided.
/// </summary>
internal enum MatchKind
{
    // Nothing matched, or a tag did not resolve.
    None,
    // A match below the accept score, or without its lead.
    Provisional,
    // A tag resolved, or a search was accepted.
    Accepted,
    // TMDb could not be reached, so nothing can be said yet.
    Unavailable,
}

/// <summary>
///   What the pipeline made of a file.
/// </summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Reason">Why, in words.</param>
/// <param name="Evidence">The evidence used, when the file was read.</param>
/// <param name="References">The cross-references to write.</param>
/// <param name="Path">How: <c>tag:…</c> or <c>search:…</c>.</param>
/// <param name="RetryAfter">When to try again, if TMDb said.</param>
internal sealed record MatchOutcome(MatchKind Kind, string Reason, FileEvidence? Evidence, IReadOnlyList<ReleaseVideoCrossReference> References, string? Path = null, TimeSpan? RetryAfter = null)
{
    // Whether there is something to save.
    public bool IsMatched => Kind is MatchKind.Accepted or MatchKind.Provisional && References.Count > 0;
}

/// <summary>
///   Matches a file to TMDb: evidence from its path, then its tags, then a
///   name and year search, then placement in the show.
/// </summary>
/// <param name="parser">Reads paths.</param>
/// <param name="catalog">What core knows of TMDb.</param>
internal sealed class MatchPipeline(PathParser parser, ITmdbCatalog catalog)
{
    private static readonly TimeSpan _refreshTimeoutRetry = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan _keepFor = TimeSpan.FromHours(1);

    // Search hits and lookups kept for an hour, so the files of one folder search TMDb once.
    private readonly ConcurrentDictionary<string, (DateTime At, object? Value)> _cache = new();

    // How long placement waits for a show to be fetched.
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // Matches a file by its locations; the one that says most is read.
    public async Task<MatchOutcome> Match(IReadOnlyList<FileLocation> locations, TmdbFileMatcherConfiguration config, CancellationToken cancellationToken)
    {
        if (locations.Count is 0)
            return new(MatchKind.None, "The file has no location to read.", null, []);

        var file = locations.Select(location => EvidenceReader.Read(location, parser.Parse(location))).MaxBy(evidence => (evidence.IsParsed, evidence.Tags.Count > 0, evidence.SeriesName is not null, evidence.HasEpisodeNumber))!;
        try
        {
            if (file.Tags.Count > 0)
                return await ByTags(file, cancellationToken).ConfigureAwait(false);
            if (file.SeriesName is null)
                return new(MatchKind.None, "The path gives no series name to search for.", file, []);
            if (!file.IsMovieShaped && (file.HasEpisodeNumber || file.HasFractionalEpisode) && SeasonalPlacement.Unplaceable(file) is { } unplaceable)
                return new(MatchKind.None, unplaceable.How, file, []);
            return await BySearch(file, config, cancellationToken).ConfigureAwait(false);
        }
        catch (MetadataProviderUnavailableException exception)
        {
            return new(MatchKind.Unavailable, exception.Message, file, [], RetryAfter: exception.RetryAfter);
        }
    }

    // The first group of tags taken, from one place in the path; a tag that does not resolve stops the match.
    private async Task<MatchOutcome> ByTags(FileEvidence file, CancellationToken cancellationToken)
    {
        var first = file.Tags[0];
        var group = file.Tags.Where(tag => tag.Depth == first.Depth && tag.IsForced == first.IsForced).ToList();
        var episodes = group.Where(tag => tag.Type == MetadataEntityType.Episode).ToList();
        var series = group.FirstOrDefault(tag => tag.Type == MetadataEntityType.Series) ?? (episodes.Count > 0 ? file.Tags.FirstOrDefault(tag => tag.Type == MetadataEntityType.Series) : null);
        Placement placement;
        if (episodes.Count > 0)
            placement = await ResolveEpisodes(episodes, series is null ? null : Guid(series, MetadataEntityType.Series), cancellationToken).ConfigureAwait(false);
        else if (series is not null)
            placement = await Place(Guid(series, MetadataEntityType.Series), file, null, cancellationToken).ConfigureAwait(false);
        else if (group.FirstOrDefault(tag => tag.Type == MetadataEntityType.Movie) is { } movie)
            placement = await ResolveMovie(movie, cancellationToken).ConfigureAwait(false);
        else
            placement = await ResolveBare(first, file, cancellationToken).ConfigureAwait(false);

        return placement.IsPlaced
            ? new(MatchKind.Accepted, $"The TMDb tag resolved: {placement.How}.", file, placement.References, $"tag:{placement.References[0].IDs[0]}")
            : new(MatchKind.None, $"The TMDb tag does not resolve: {placement.How}", file, []);
    }

    // A bare number is a show or a movie: the show first for a file with an episode number.
    private async Task<Placement> ResolveBare(TaggedMetadataID tag, FileEvidence file, CancellationToken cancellationToken)
    {
        var showFirst = file.HasEpisodeNumber && !file.IsMovieShaped;
        var first = showFirst ? await AsShow(tag, file, cancellationToken).ConfigureAwait(false) : await ResolveMovie(tag, cancellationToken).ConfigureAwait(false);
        if (first.IsPlaced)
            return first;
        var second = showFirst ? await ResolveMovie(tag, cancellationToken).ConfigureAwait(false) : await AsShow(tag, file, cancellationToken).ConfigureAwait(false);
        return second.IsPlaced ? second : Placement.Failed($"{tag.ID} fits neither as a show nor as a movie ({(showFirst ? first : second).How} {(showFirst ? second : first).How})");
    }

    private async Task<Placement> AsShow(TaggedMetadataID tag, FileEvidence file, CancellationToken cancellationToken)
    {
        var seriesID = Guid(tag, MetadataEntityType.Series);
        if (catalog.GetEpisodes(seriesID) is null && await Lookup(seriesID, cancellationToken).ConfigureAwait(false) is null)
            return Placement.Failed($"{seriesID} was not found.");
        return await Place(seriesID, file, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Placement> ResolveMovie(TaggedMetadataID tag, CancellationToken cancellationToken)
    {
        var movieID = Guid(tag, MetadataEntityType.Movie);
        if (!catalog.IsStored(movieID) && await Lookup(movieID, cancellationToken).ConfigureAwait(false) is null)
            return Placement.Failed($"{movieID} was not found.");
        return new([ReleaseVideoCrossReference.For([movieID], tag.PercentageStart, tag.PercentageEnd)], "the tagged movie");
    }

    // Each tagged episode must be stored, after one refresh of the tagged show, and belong to that show.
    private async Task<Placement> ResolveEpisodes(IReadOnlyList<TaggedMetadataID> tags, MetadataGuid? seriesID, CancellationToken cancellationToken)
    {
        var references = new List<ReleaseVideoCrossReference>();
        var refreshed = false;
        foreach (var tag in tags)
        {
            var episodeID = Guid(tag, MetadataEntityType.Episode);
            var episode = catalog.GetEpisode(episodeID);
            if (episode is null && seriesID is not null && !refreshed)
            {
                await Refresh(seriesID, cancellationToken).ConfigureAwait(false);
                refreshed = true;
                episode = catalog.GetEpisode(episodeID);
            }

            if (episode is null)
                return Placement.Failed(seriesID is null ? $"{episodeID} is not stored, and no show is tagged to fetch it with." : $"{episodeID} is not an episode of {seriesID}, or is gone.");
            if (seriesID is not null && episode.SeriesID != seriesID)
                return Placement.Failed($"{episodeID} is not an episode of {seriesID}.");
            references.Add(ReleaseVideoCrossReference.For([episode.ID, episode.SeriesID], tag.PercentageStart, tag.PercentageEnd));
        }

        return new(references, "the tagged episodes");
    }

    // Shows or movies, the likelier first, through the plan, stopping at the first accepted candidate.
    private async Task<MatchOutcome> BySearch(FileEvidence file, TmdbFileMatcherConfiguration config, CancellationToken cancellationToken)
    {
        var plan = QueryPlan.Build(file);
        var seasonHint = QueryPlan.SeasonHint(plan);
        MetadataEntityType[] kinds = file.IsMovieShaped || !file.HasEpisodeNumber ? [MetadataEntityType.Movie, MetadataEntityType.Series] : [MetadataEntityType.Series, MetadataEntityType.Movie];
        var candidates = new List<ScoredCandidate>();
        var decision = CandidateScorer.Decide([], config.AcceptScore, config.ProvisionalScore);
        foreach (var kind in kinds)
        {
            foreach (var query in plan)
            {
                var hits = await Cached($"{kind.Value}|{TitleFolding.Fold(query.Text)}|{query.Year}", () => catalog.Search(kind, query.Text, query.Year, config.IncludeRestricted, cancellationToken)).ConfigureAwait(false);
                var scored = hits.Select(hit => CandidateScorer.Score(hit, file, plan, query, config.IncludeRestricted)).ToList();
                foreach (var candidate in scored.Where(candidate => WorthLookup(candidate, file, config)).OrderByDescending(candidate => candidate.Score).Take(3).ToList())
                    scored[scored.IndexOf(candidate)] = await WithLookup(candidate, file, plan, config, cancellationToken).ConfigureAwait(false);
                candidates.AddRange(scored);
                decision = CandidateScorer.Decide(candidates, config.AcceptScore, config.ProvisionalScore);
                if (decision.Kind is MatchKind.Accepted)
                    break;
            }

            if (decision.Kind is not MatchKind.None)
                break;
        }

        if (decision.Chosen is not { } chosen)
            return new(MatchKind.None, decision.Reason, file, []);

        var path = $"search:{chosen.Query.Text}";
        if (chosen.Entry.IsMovie)
            return new(decision.Kind, decision.Reason, file, [ReleaseVideoCrossReference.For([chosen.Entry.ID], null, null)], path);

        var placement = await Place(chosen.Entry.ID, file, seasonHint, cancellationToken).ConfigureAwait(false);
        return placement.IsPlaced
            ? new(decision.Kind, $"{decision.Reason} Placed {placement.How}.", file, placement.References, path)
            : new(MatchKind.None, $"{decision.Reason} But it cannot be placed: {placement.How}", file, []);
    }

    // A search hit lacks a show's seasons and alternate titles; a lookup adds them for a likely or a title-mismatched hit.
    private static bool WorthLookup(ScoredCandidate candidate, FileEvidence file, TmdbFileMatcherConfiguration config)
        => candidate.Rejection is "title"
            || (candidate.Rejection is null && file.HasEpisodeNumber && !file.IsSpecial && !file.IsMovieShaped && !candidate.Entry.IsMovie && candidate.Entry.Seasons is null
                && candidate.Score >= config.ProvisionalScore - (15 - CandidateScorer.Unknown));

    private async Task<ScoredCandidate> WithLookup(ScoredCandidate candidate, FileEvidence file, IReadOnlyList<SearchQuery> plan, TmdbFileMatcherConfiguration config, CancellationToken cancellationToken)
        => await Cached(candidate.Entry.ID.ToString(), () => Lookup(candidate.Entry.ID, cancellationToken)).ConfigureAwait(false) is { } entry
            ? CandidateScorer.Score(entry, file, plan, candidate.Query, config.IncludeRestricted)
            : candidate;

    // Places a file in a show, refreshing the show first when it is not stored or does not hold the file.
    private async Task<Placement> Place(MetadataGuid seriesID, FileEvidence file, int? seasonHint, CancellationToken cancellationToken)
    {
        if (SeasonalPlacement.Unplaceable(file) is { } unplaceable)
            return unplaceable;

        var episodes = catalog.GetEpisodes(seriesID);
        if (episodes is null || !SeasonalPlacement.Place(seriesID, episodes, file, seasonHint).IsPlaced)
        {
            await Refresh(seriesID, cancellationToken).ConfigureAwait(false);
            episodes = catalog.GetEpisodes(seriesID);
            if (episodes is null)
                return Placement.Failed($"{seriesID} could not be fetched from TMDb.");
        }

        return SeasonalPlacement.Place(seriesID, episodes, file, seasonHint);
    }

    // A refresh bounded by RefreshTimeout; running over is TMDb being unavailable for now.
    private async Task Refresh(MetadataGuid seriesID, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(RefreshTimeout);
        try
        {
            await catalog.Refresh(seriesID, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetadataProviderUnavailableException(MetadataSource.TMDB, $"Fetching {seriesID} took longer than {RefreshTimeout.TotalSeconds:0} s.", _refreshTimeoutRetry);
        }
    }

    private async Task<SourceEntry?> Lookup(MetadataGuid id, CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.Lookup(id, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async Task<T> Cached<T>(string key, Func<Task<T>> fetch)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(key, out var kept) && now - kept.At < _keepFor)
            return (T)kept.Value!;
        var value = await fetch().ConfigureAwait(false);
        _cache[key] = (now, value);
        return value;
    }

    private static MetadataGuid Guid(TaggedMetadataID tag, MetadataEntityType kind)
        => new(MetadataSource.TMDB, kind, tag.ID);
}

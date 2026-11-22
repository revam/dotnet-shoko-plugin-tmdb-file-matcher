using System;
using System.Collections.Generic;
using System.Linq;
using Shoko.Plugin.TmdbFileMatcher.Catalog;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;

namespace Shoko.Plugin.TmdbFileMatcher.Search;

/// <summary>
///   A search hit with its score.
/// </summary>
/// <param name="Entry">The hit.</param>
/// <param name="Score">The score, from 0 to 100.</param>
/// <param name="Rejection">Why it cannot be chosen, if it cannot.</param>
/// <param name="Query">The search that found it.</param>
internal sealed record ScoredCandidate(SourceEntry Entry, int Score, string? Rejection, SearchQuery Query);

/// <summary>
///   The decision over a set of candidates.
/// </summary>
/// <param name="Kind">None, provisional or accepted.</param>
/// <param name="Chosen">The best candidate, if any can be chosen.</param>
/// <param name="Reason">Why, in words.</param>
internal sealed record Decision(MatchKind Kind, ScoredCandidate? Chosen, string Reason);

/// <summary>
///   Scores a candidate from 0 to 100: title up to 65, year up to 20 and
///   fit up to 15, with the rejections that rule it out.
/// </summary>
internal static class CandidateScorer
{
    // How far an accepted candidate must lead the next one.
    public const int Margin = 10;

    // The points for a year, or a fit, that cannot be told.
    public const int Unknown = 8;

    // Scores one candidate.
    public static ScoredCandidate Score(SourceEntry entry, FileEvidence file, IReadOnlyList<SearchQuery> plan, SearchQuery query, bool includeRestricted)
    {
        var season = file.Season ?? QueryPlan.SeasonHint(plan);
        var (title, titleRejection) = Title(entry, plan);
        var (year, yearRejection) = Year(entry, file.Year, season);
        var (fit, fitRejection) = Fit(entry, file, season);
        var rejection = titleRejection ?? yearRejection ?? fitRejection ?? (entry.IsRestricted && !includeRestricted ? "restricted" : null);
        return new(entry, title + year + fit, rejection, query);
    }

    /// <summary>
    ///   Decides over some candidates: accepted at <paramref name="accept"/>
    ///   or more with a lead of <see cref="Margin"/>, provisional at
    ///   <paramref name="provisional"/> or more, none otherwise.
    /// </summary>
    public static Decision Decide(IEnumerable<ScoredCandidate> candidates, int accept, int provisional)
    {
        var ranked = candidates.Where(candidate => candidate.Rejection is null).GroupBy(candidate => candidate.Entry.ID)
            .Select(group => group.MaxBy(candidate => candidate.Score)!).OrderByDescending(candidate => candidate.Score).ToList();
        if (ranked.Count is 0)
            return new(MatchKind.None, null, "No candidate could be chosen.");

        var top = ranked[0];
        var lead = top.Score - (ranked.Count > 1 ? ranked[1].Score : 0);
        if (top.Score >= accept && lead >= Margin)
            return new(MatchKind.Accepted, top, $"{top.Entry.ID} scores {top.Score} and leads by {lead}.");
        if (top.Score >= provisional)
            return new(MatchKind.Provisional, top, $"{top.Entry.ID} scores {top.Score}, but {(top.Score >= accept ? $"leads by only {lead}" : $"is below {accept}")}.");
        return new(MatchKind.None, null, $"The best, {top.Entry.ID}, scores only {top.Score}.");
    }

    // 65 for an exact title, 60 for an exact part before a subtitle, up to 50 for a similar one.
    private static (int, string?) Title(SourceEntry entry, IReadOnlyList<SearchQuery> plan)
    {
        var names = TitleFolding.FoldAll(plan.Select(query => query.Text));
        var titles = TitleFolding.FoldAll(entry.Titles);
        if (names.Full.Intersect(titles.Full).Any())
            return (65, null);

        var allNames = names.Full.Concat(names.Main).ToList();
        var allTitles = titles.Full.Concat(titles.Main).ToList();
        if (allNames.Intersect(allTitles).Any())
            return (60, null);

        var best = allNames.SelectMany(name => allTitles.Select(title => TitleFolding.Similarity(name, title))).DefaultIfEmpty(0).Max();
        return ((int)Math.Round(50 * best), best < 0.5 ? "title" : null);
    }

    // The year against the show's first year, or against the season's for a later season.
    private static (int, string?) Year(SourceEntry entry, int? fileYear, int? season)
    {
        var entryYear = !entry.IsMovie && season is > 1 ? entry.Seasons?.FirstOrDefault(known => known.Number == season)?.Year : entry.Year;
        if (fileYear is null || entryYear is null)
            return (Unknown, null);
        var apart = Math.Abs(fileYear.Value - entryYear.Value);
        return (apart is 0 ? 20 : apart is 1 ? 12 : 0, apart > 2 ? "year" : null);
    }

    // Whether the candidate can hold the file: a movie for a movie, a season with enough episodes for an episode.
    private static (int, string?) Fit(SourceEntry entry, FileEvidence file, int? season)
    {
        if (entry.IsMovie)
        {
            if (file.IsMovieShaped || (file.EpisodeStart is null && season is null))
                return (15, null);
            return season is null && file.EpisodeStart is 1 && file.EpisodeEnd is 1 ? (10, null) : (0, "a movie for an episode");
        }

        if (file.IsMovieShaped)
            return (0, file.IsFirmlyMovie ? "a show for a movie" : null);
        if (file.EpisodeEnd is not { } last || file.IsSpecial || entry.Seasons is not { Count: > 0 } seasons)
            return (Unknown, null);

        if (season is { } number)
        {
            var found = seasons.FirstOrDefault(known => known.Number == number);
            if (found is not null && (found.EpisodeCount is null || found.EpisodeCount >= last))
                return (found.EpisodeCount is null ? Unknown : 15, null);
            return (0, found is null ? $"no season {number}" : $"season {number} has only {found.EpisodeCount} episodes");
        }

        if (seasons.FirstOrDefault(known => known.Number == 1)?.EpisodeCount >= last)
            return (15, null);
        if (seasons.Where(known => known.Number > 0).Sum(known => known.EpisodeCount ?? 0) >= last)
            return (10, null);
        return seasons.Any(known => known.EpisodeCount is null) ? (Unknown, null) : (0, $"episode {last} is past its episodes");
    }
}

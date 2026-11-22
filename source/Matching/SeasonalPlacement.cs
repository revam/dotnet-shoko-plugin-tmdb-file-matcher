using System.Collections.Generic;
using System.Linq;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Video.Release;
using Shoko.Plugin.TmdbFileMatcher.Catalog;
using Shoko.Plugin.TmdbFileMatcher.Evidence;

namespace Shoko.Plugin.TmdbFileMatcher.Matching;

/// <summary>
///   Where a file was placed, or why it could not be.
/// </summary>
/// <param name="References">
///   The cross-references, main entry first; empty on a failure.
/// </param>
/// <param name="How">How it was placed, or why it was not.</param>
internal sealed record Placement(IReadOnlyList<ReleaseVideoCrossReference> References, string How)
{
    // Whether the file was placed.
    public bool IsPlaced => References.Count > 0;

    // A placement that failed.
    public static Placement Failed(string why)
        => new([], why);
}

/// <summary>
///   Places a file among a TMDb show's episodes: SxxEyy as given, no season
///   as season 1 or else absolute across the regular seasons in air order,
///   and specials in season 0.
/// </summary>
internal static class SeasonalPlacement
{
    // Why a file cannot be placed in any show, before its episodes are read.
    public static Placement? Unplaceable(FileEvidence file)
    {
        if (file.EpisodeType is not (EpisodeType.Episode or EpisodeType.Special))
            return Placement.Failed($"{file.EpisodeType} files are not matched; only episodes and specials are.");
        if (file.HasFractionalEpisode)
            return Placement.Failed("The episode number is not a whole number, so it names no TMDb episode.");
        return file.EpisodeStart is null ? Placement.Failed("The path gives no episode number.") : null;
    }

    // Places a file among a show's episodes.
    public static Placement Place(MetadataGuid seriesID, IReadOnlyList<EpisodeEntry> episodes, FileEvidence file, int? seasonHint)
    {
        if (Unplaceable(file) is { } unplaceable)
            return unplaceable;

        var start = file.EpisodeStart!.Value;
        var end = file.EpisodeEnd ?? start;
        var season = file.IsSpecial ? 0 : file.Season ?? seasonHint;
        var label = $"{(season is { } number ? $"S{number:00}" : string.Empty)}E{start:00}{(end != start ? $"-E{end:00}" : string.Empty)}";
        if (season is not null)
        {
            return InSeason(episodes, season.Value, start, end) is { } found
                ? Placed(seriesID, found, $"{label} as given")
                : Placement.Failed($"{seriesID} has no {label}.");
        }

        if (InSeason(episodes, 1, start, end) is { } first)
            return Placed(seriesID, first, $"{label} as season 1, the path giving no season");

        var regular = episodes
            .Where(episode => episode.SeasonNumber is > 0 && episode.Type is EpisodeType.Episode)
            .GroupBy(episode => episode.SeasonNumber!.Value)
            .OrderBy(group => group.Min(episode => episode.AirDate) ?? System.DateOnly.MaxValue)
            .ThenBy(group => group.Key)
            .SelectMany(group => group.OrderBy(episode => episode.EpisodeNumber))
            .ToList();
        if (end > regular.Count)
            return Placement.Failed($"{label} is past the {regular.Count} regular episodes of {seriesID}.");

        var absolute = regular.Skip(start - 1).Take(end - start + 1).ToList();
        return Placed(seriesID, absolute, $"{label} as absolute numbering, {string.Join(", ", absolute.Select(episode => $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}"))}");
    }

    // The episodes of a season covering the file's range, if it holds them all.
    private static List<EpisodeEntry>? InSeason(IReadOnlyList<EpisodeEntry> episodes, int season, int start, int end)
    {
        var found = episodes.Where(episode => episode.SeasonNumber == season && episode.EpisodeNumber >= start && episode.EpisodeNumber <= end).OrderBy(episode => episode.EpisodeNumber).ToList();
        return found.Count == end - start + 1 ? found : null;
    }

    private static Placement Placed(MetadataGuid seriesID, IEnumerable<EpisodeEntry> episodes, string how)
        => new([.. episodes.Select(episode => ReleaseVideoCrossReference.For([episode.ID, seriesID], null, null))], how);
}

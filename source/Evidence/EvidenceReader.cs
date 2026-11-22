using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;
using Shoko.Plugin.TmdbFileMatcher.Search;

namespace Shoko.Plugin.TmdbFileMatcher.Evidence;

/// <summary>
///   Turns the parse of a path into evidence: the year out of the series
///   name, what the folders add, whole episode numbers, the TMDb tags in
///   order, and whether the file looks like a movie.
/// </summary>
internal static partial class EvidenceReader
{
    // Folder names that sort a library rather than name a series.
    private static readonly HashSet<string> _genericFolders = new(new[]
    {
        "series", "shows", "show", "tv", "tv shows", "tv series", "anime", "movies", "movie", "films", "film",
        "videos", "video", "media", "library", "downloads", "download", "complete", "completed", "unsorted",
        "unrecognized", "unrecognised", "auto scan", "import", "imports", "incoming", "new", "misc", "other",
    }.Select(TitleFolding.Fold));

    // Reads one location.
    public static FileEvidence Read(FileLocation location, ParsedFileResult parsed)
    {
        var folder = ReadFolders(location.ParentPath);
        var (name, nameYear) = SplitYear(parsed.Success ? parsed.SeriesName : null);
        if (string.IsNullOrEmpty(name))
            name = folder.SeriesName;

        int? start = null, end = null;
        var fractional = false;
        if (parsed.Success && parsed.EpisodeStart > 0)
        {
            var last = Math.Max(parsed.EpisodeStart, parsed.EpisodeEnd);
            fractional = parsed.EpisodeStart != MathF.Floor(parsed.EpisodeStart) || last != MathF.Floor(last);
            if (!fractional)
                (start, end) = ((int)parsed.EpisodeStart, (int)last);
        }

        var year = parsed.SeriesYear ?? nameYear ?? folder.Year;
        var tags = parsed.IDs.Where(id => id.Source == MetadataSource.TMDB).OrderBy(id => !id.IsForced).ThenBy(id => id.Depth).ToList();
        var firmlyMovie = tags.Any(id => id.Type == MetadataEntityType.Movie) || (parsed.Success && parsed.SeriesType is AnimeType.Movie);
        // The importer's fallback rule reads a lone movie file as episode 1 with a year and no season.
        var fallbackShape = parsed.RuleName == "fallback" && parsed.SeasonNumber is null && start is 1 && end is 1 && year is not null;
        return new(
            location, parsed.Success, string.IsNullOrWhiteSpace(name) ? null : name, Differs(folder.SeriesName, name), Differs(FileNameSeries(location.FileName), name), year,
            (parsed.Success ? parsed.SeasonNumber : null) ?? (start is not null ? folder.Season : null), parsed.Success ? parsed.EpisodeType : EpisodeType.Episode,
            start, end, fractional, firmlyMovie || fallbackShape, firmlyMovie, tags, parsed
        );
    }

    // The series name a file name gives before its episode number, with dots and underscores read as spaces and a leading group dropped.
    public static string? FileNameSeries(string fileName)
    {
        var match = FileNameSeriesRegex().Match(fileName);
        if (!match.Success)
            return null;
        var name = SpaceRegex().Replace(match.Groups["name"].Value.Replace('.', ' ').Replace('_', ' '), " ").Trim(' ', '-');
        return name.Length > 0 && !_genericFolders.Contains(TitleFolding.Fold(name)) ? name : null;
    }

    private static string? Differs(string? candidate, string? name)
        => candidate is not null && TitleFolding.Fold(candidate) != TitleFolding.Fold(name ?? string.Empty) ? candidate : null;

    private static (string? Name, int? Year) SplitYear(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, null);
        var match = TrailingYearRegex().Match(name);
        return match.Success ? (name[..match.Index].Trim(), int.Parse(match.Groups["year"].Value)) : (name.Trim(), null);
    }

    // The nearest season folder, and the nearest other folder that is not a generic one.
    private static (int? Season, string? SeriesName, int? Year) ReadFolders(string parentPath)
    {
        int? season = null;
        foreach (var folder in parentPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (SeasonFolderRegex().Match(folder) is { Success: true } seasonMatch)
            {
                season ??= seasonMatch.Groups["number"].Success ? int.Parse(seasonMatch.Groups["number"].Value) : seasonMatch.Groups["specials"].Success ? 0 : null;
                continue;
            }

            var yearMatch = YearRegex().Match(folder);
            var name = SpaceRegex().Replace(BracketRegex().Replace(folder, " "), " ").Trim(' ', '-', '_', '.');
            if (name.Length is 0 || _genericFolders.Contains(TitleFolding.Fold(name)))
                continue;
            return (season, name, yearMatch.Success ? int.Parse(yearMatch.Groups["year"].Value) : null);
        }

        return (season, null, null);
    }

    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)?(?<name>.+?)[\s._-]+(?:S\d{1,3}[\s._-]*E\d{1,4}|S\d{1,3}\b|-\s*\d{1,4}(?:v\d)?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameSeriesRegex();

    [GeneratedRegex(@"\s*[\(\[](?<year>(?:19|20)\d{2})[\)\]]\s*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"[\(\[](?<year>(?:19|20)\d{2})[\)\]]")]
    internal static partial Regex YearRegex();

    [GeneratedRegex(@"^(?:(?:season|series|s)[\s._-]*(?<number>\d{1,3})|(?<specials>specials?)|extras?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}|\([^)]*\)")]
    internal static partial Regex BracketRegex();

    [GeneratedRegex(@"\s+")]
    internal static partial Regex SpaceRegex();
}

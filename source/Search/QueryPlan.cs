using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Shoko.Plugin.TmdbFileMatcher.Evidence;

namespace Shoko.Plugin.TmdbFileMatcher.Search;

/// <summary>
///   One name search of the plan.
/// </summary>
/// <param name="Text">The name searched for.</param>
/// <param name="Year">The year to narrow by, if any.</param>
/// <param name="SeasonHint">The season a stripped suffix named, if any.</param>
/// <param name="Origin">Which step of the plan made it.</param>
internal sealed record SearchQuery(string Text, int? Year, int? SeasonHint, string Origin);

/// <summary>
///   The name searches tried for a file, in order: the name with its year
///   and without, the name without a season suffix, the name without its
///   subtitle, the file name's own reading, and the folder's name.
/// </summary>
internal static partial class QueryPlan
{
    private static readonly Dictionary<string, int> _romanSeasons = new() { ["II"] = 2, ["III"] = 3, ["IV"] = 4, ["V"] = 5, ["VI"] = 6 };

    // Builds the plan for a file.
    public static IReadOnlyList<SearchQuery> Build(FileEvidence file)
    {
        var queries = new List<SearchQuery>();
        if (file.SeriesName is not { } name)
            return queries;

        if (file.Year is { } year)
            queries.Add(new(name, year, null, "name and year"));
        queries.Add(new(name, null, null, "name"));

        var stripped = StripSuffix(name);
        if (stripped is { } suffix)
            queries.Add(new(suffix.Name, null, suffix.Season, $"name without \"{suffix.Suffix}\""));
        foreach (var main in new[] { name, stripped?.Name }.OfType<string>().Select(TitleFolding.MainPart).OfType<string>())
            queries.Add(new(main, null, stripped?.Season, "name without its subtitle"));

        // A parser can misread a name, as the importer once read "Tokyo.Revengers" as "Tokyo Revers".
        if (file.FileNameSeriesName is { } read)
        {
            if (file.Year is { } readYear)
                queries.Add(new(read, readYear, null, "file name and year"));
            queries.Add(new(read, null, StripSuffix(read)?.Season, "file name"));
        }

        if (file.FolderSeriesName is { } folder)
            queries.Add(new(folder, null, StripSuffix(folder)?.Season, "folder name"));

        return [.. queries.Where(query => TitleFolding.Fold(query.Text).Length > 0).DistinctBy(query => (TitleFolding.Fold(query.Text), query.Year))];
    }

    // The season a plan's stripped suffix names, if any.
    public static int? SeasonHint(IReadOnlyList<SearchQuery> plan)
        => plan.Select(query => query.SeasonHint).FirstOrDefault(hint => hint is not null);

    // Strips a season or sequel suffix off a name: "Season 2", "2nd Season", "S2", "II", "Part 2" or "Cour 2".
    public static (string Name, int? Season, string Suffix)? StripSuffix(string name)
    {
        name = name.Trim();
        if (SeasonSuffixRegex().Match(name) is { Success: true } season)
            return (season.Groups["name"].Value.Trim(), int.Parse(season.Groups["number"].Value), name[season.Groups["suffix"].Index..]);
        if (RomanSuffixRegex().Match(name) is { Success: true } roman)
            return (roman.Groups["name"].Value.Trim(), _romanSeasons[roman.Groups["roman"].Value], roman.Groups["roman"].Value);
        if (PartSuffixRegex().Match(name) is { Success: true } part)
            return (part.Groups["name"].Value.Trim(), null, name[part.Groups["suffix"].Index..]);
        return null;
    }

    [GeneratedRegex(@"^(?<name>.+?)[\s:_-]+(?<suffix>season[\s._-]*(?<number>\d{1,2})|(?<number>\d{1,2})(?:st|nd|rd|th)[\s._-]+season|s(?<number>\d{1,2}))$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonSuffixRegex();

    [GeneratedRegex(@"^(?<name>.+?)[\s:_-]+(?<roman>II|III|IV|V|VI)$")]
    private static partial Regex RomanSuffixRegex();

    [GeneratedRegex(@"^(?<name>.+?)[\s:_-]+(?<suffix>(?:part|cour)[\s._-]*\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex PartSuffixRegex();
}

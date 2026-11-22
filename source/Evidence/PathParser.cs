using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;

namespace Shoko.Plugin.TmdbFileMatcher.Evidence;

/// <summary>
///   Reads a file's path: with the Offline Importer's rules when it is
///   installed, or else with a small built-in parser.
/// </summary>
/// <param name="importer">
///   The importer's matching service, or <c>null</c> when it is absent.
/// </param>
internal sealed partial class PathParser(IOfflineMatchingService? importer)
{
    // The rule name the built-in parser gives its results.
    public const string BuiltInRuleName = "built-in";

    // Whether the importer's rules are used.
    public bool UsesImporter => importer is not null;

    // Parses one location of a file.
    public ParsedFileResult Parse(FileLocation location)
        => importer is null ? ParseBuiltIn(location.NormalizedPath)
            : location.ManagedFolder is { } folder ? importer.Match(folder, location.RelativePath) : importer.Match(location.RelativePath);

    /// <summary>
    ///   The built-in parser: TMDb tags anywhere in the path, then
    ///   <c>Name (Year)/Season N/… SxxEyy</c>, <c>Name - NN</c>, or a bare
    ///   name such as a movie's.
    /// </summary>
    public static ParsedFileResult ParseBuiltIn(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tags = new List<TaggedMetadataID>();
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            foreach (Match tag in TagRegex().Matches(segments[index]))
                tags.Add(new() { Source = MetadataSource.TMDB, ID = tag.Groups["id"].Value, Type = Kind(tag.Groups["kind"].Value), Depth = segments.Length - 1 - index });
        }

        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var group = LeadingGroupRegex().Match(stem) is { Success: true } leading ? leading.Groups["group"].Value : null;
        var text = EvidenceReader.BracketRegex().Replace(LeadingGroupRegex().Replace(stem, string.Empty), " ");
        if (!text.Contains(' '))
            text = text.Replace('.', ' ').Replace('_', ' ');

        // SxxEyy first, then "Name - NN", else the whole name, as a movie's is.
        var match = SeasonEpisodeRegex().Match(text) is { Success: true } sxe ? sxe : DashEpisodeRegex().Match(text);
        var name = match.Success ? match.Groups["name"].Value : text;
        int? season = match.Groups["season"].Success ? int.Parse(match.Groups["season"].Value) : null;
        float start = match.Success ? int.Parse(match.Groups["episode"].Value) : 0;
        float end = match.Groups["end"].Success ? int.Parse(match.Groups["end"].Value) : start;

        var year = EvidenceReader.YearRegex().Match(fileName) is { Success: true } found ? int.Parse(found.Groups["year"].Value) : (int?)null;
        return new()
        {
            RuleName = BuiltInRuleName,
            Success = true,
            FilePath = path,
            FileExtension = Path.GetExtension(fileName),
            SeriesName = EvidenceReader.SpaceRegex().Replace(name, " ").Trim(' ', '-', '.', '_'),
            SeriesYear = year,
            SeasonNumber = season,
            EpisodeStart = start,
            EpisodeEnd = end,
            EpisodeType = season is 0 ? EpisodeType.Special : EpisodeType.Episode,
            ReleaseGroup = group,
            IDs = tags,
        };
    }

    // The importer's TMDb prefixes: a, s and tv for a show, m for a movie, e for an episode.
    private static MetadataEntityType? Kind(string prefix)
        => prefix.ToLowerInvariant() switch
        {
            "a" or "s" or "tv" => MetadataEntityType.Series,
            "m" => MetadataEntityType.Movie,
            "e" => MetadataEntityType.Episode,
            _ => null,
        };

    [GeneratedRegex(@"[\[{]tmdb(?:id)?[-=](?<kind>tv|a|s|m|e)?(?<id>\d+)[\]}]", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\s*\[(?<group>[^\]]+)\]\s*")]
    private static partial Regex LeadingGroupRegex();

    [GeneratedRegex(@"^(?<name>.*?)[\s._-]*\bS(?<season>\d{1,3})[\s._-]*E(?<episode>\d{1,4})(?:(?:[\s._]*-[\s._]*E?|E)(?<end>\d{1,4})\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+-\s+(?<episode>\d{1,4})(?:v\d)?\b")]
    private static partial Regex DashEpisodeRegex();
}

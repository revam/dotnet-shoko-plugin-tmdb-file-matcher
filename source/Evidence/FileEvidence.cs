using System.Collections.Generic;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Video;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;

namespace Shoko.Plugin.TmdbFileMatcher.Evidence;

/// <summary>
///   Where a file sits: a managed folder and the path inside it.
/// </summary>
/// <param name="ManagedFolderID">The managed folder's ID.</param>
/// <param name="RelativePath">The path inside the managed folder.</param>
/// <param name="ManagedFolder">
///   The managed folder, when the server gave one; the importer reads its
///   location rules from it.
/// </param>
internal sealed record FileLocation(int ManagedFolderID, string RelativePath, IManagedFolder? ManagedFolder = null)
{
    // The path with forward slashes and no leading slash.
    public string NormalizedPath => RelativePath.Replace('\\', '/').TrimStart('/');

    // The file's name.
    public string FileName => NormalizedPath[(NormalizedPath.LastIndexOf('/') + 1)..];

    // The folders holding the file, inside the managed folder.
    public string ParentPath => NormalizedPath.LastIndexOf('/') is var slash and >= 0 ? NormalizedPath[..slash] : string.Empty;
}

/// <summary>
///   What one location of a file says about it: the parse of its path, made
///   tidy, with what its folders add.
/// </summary>
/// <param name="Location">The location read.</param>
/// <param name="IsParsed">Whether a parsing rule matched the path.</param>
/// <param name="SeriesName">The series name, from the file or folders.</param>
/// <param name="FolderSeriesName">
///   The nearest folder's series name, when it differs from the file's.
/// </param>
/// <param name="FileNameSeriesName">
///   The series name read straight from the file name, when it differs
///   from the parser's.
/// </param>
/// <param name="Year">The year, from the file, its name or its folders.</param>
/// <param name="Season">The season, from the file or its folders.</param>
/// <param name="EpisodeType">The kind of episode.</param>
/// <param name="EpisodeStart">The first whole episode number, if any.</param>
/// <param name="EpisodeEnd">The last whole episode number, if any.</param>
/// <param name="HasFractionalEpisode">
///   Whether an episode number is not a whole number, such as 12.5.
/// </param>
/// <param name="IsMovieShaped">Whether the file looks like a movie.</param>
/// <param name="IsFirmlyMovie">
///   Whether a tag or the series type says it is a movie.
/// </param>
/// <param name="Tags">
///   The TMDb tags in the order they are taken: forced, then the file's,
///   then the folders', nearest first.
/// </param>
/// <param name="Parsed">
///   The parse itself, for what the release copies: the group, source,
///   version and whether it is creditless or censored.
/// </param>
internal sealed record FileEvidence(
    FileLocation Location,
    bool IsParsed,
    string? SeriesName,
    string? FolderSeriesName,
    string? FileNameSeriesName,
    int? Year,
    int? Season,
    EpisodeType EpisodeType,
    int? EpisodeStart,
    int? EpisodeEnd,
    bool HasFractionalEpisode,
    bool IsMovieShaped,
    bool IsFirmlyMovie,
    IReadOnlyList<TaggedMetadataID> Tags,
    ParsedFileResult Parsed
)
{
    // Whether the file has a whole episode number.
    public bool HasEpisodeNumber => EpisodeStart is not null;

    // Whether the file names a special rather than a regular episode.
    public bool IsSpecial => Season is 0 || EpisodeType is EpisodeType.Special;
}

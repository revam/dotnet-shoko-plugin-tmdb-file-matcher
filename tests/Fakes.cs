using System.Reflection;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.OfflineImporter.Abstractions.Configuration;
using Shoko.Plugin.OfflineImporter.Abstractions.Models;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;
using Shoko.Plugin.TmdbFileMatcher.Catalog;
using Shoko.Plugin.TmdbFileMatcher.Search;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   An interface answered by the given members, by name, and otherwise as
///   an empty service would.
/// </summary>
public class Fake : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[], object?>> _answers = [];

    public static T Of<T>(params (string Member, Func<object?[], object?> Answer)[] answers)
        where T : class
    {
        var proxy = Create<T, Fake>();
        foreach (var (member, answer) in answers)
            ((Fake)(object)proxy)._answers[member] = answer;
        return proxy;
    }

    public static object Of(Type type)
        => typeof(DispatchProxy).GetMethod(nameof(Create), 2, Type.EmptyTypes)!.MakeGenericMethod(type, typeof(Fake)).Invoke(null, null)!;

    /// <summary>
    ///   The importer's matching service, answering hand-written parses by
    ///   path.
    /// </summary>
    public static IOfflineMatchingService Importer(IReadOnlyDictionary<string, ParsedFileResult> results)
        => Of<IOfflineMatchingService>(("Match", args => results.TryGetValue(((string)args[^1]!).Replace('\\', '/'), out var parsed) ? parsed : ParsedFileResult.Empty));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (_answers.TryGetValue(method.Name, out var answer))
            return answer(args ?? []);
        if (method.ReturnType == typeof(Task))
            return Task.CompletedTask;
        return method.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>
///   Importer-shaped parses, as the Offline Importer gives them: episode
///   numbers as floats, and the fallback rule keeping a year in the name.
/// </summary>
internal static class Parsed
{
    public static ParsedFileResult File(string path, string? series, int? season = null, float episode = 0, float? end = null, int? year = null, string rule = "default", EpisodeType type = EpisodeType.Episode, string? group = null, params TaggedMetadataID[] ids)
        => new()
        {
            RuleName = rule,
            Success = true,
            FilePath = path,
            FileExtension = ".mkv",
            SeriesName = series ?? string.Empty,
            SeriesYear = year,
            SeasonNumber = season,
            EpisodeStart = episode,
            EpisodeEnd = end ?? episode,
            EpisodeType = type,
            ReleaseGroup = group,
            IDs = ids,
        };

    public static TaggedMetadataID Tag(string id, MetadataEntityType? type, int depth, int? start = null, int? end = null)
        => new() { Source = MetadataSource.TMDB, ID = id, Type = type, Depth = depth, PercentageStart = start, PercentageEnd = end };
}

/// <summary>
///   A show held by the fake catalog, with its episodes, stored or not.
/// </summary>
internal sealed class FakeShow(SourceEntry entry, IReadOnlyList<EpisodeEntry> episodes, bool stored)
{
    public SourceEntry Entry { get; } = entry;

    public IReadOnlyList<EpisodeEntry> Episodes { get; } = episodes;

    public bool Stored { get; set; } = stored;
}

/// <summary>
///   A catalog over fixtures, searching like TMDb: a hit when the folded
///   query and a title contain one another, narrowed to the year when one is
///   given, with no seasons and only the main titles; lookups add both.
/// </summary>
internal sealed class FakeCatalog : ITmdbCatalog
{
    public Dictionary<MetadataGuid, FakeShow> Shows { get; } = [];

    public Dictionary<MetadataGuid, SourceEntry> Movies { get; } = [];

    public HashSet<MetadataGuid> StoredMovies { get; } = [];

    public List<string> Searches { get; } = [];

    public List<MetadataGuid> Refreshes { get; } = [];

    public bool Unavailable { get; set; }

    // Whether core refuses to run a refresh, as it does while TMDb's provider is paused.
    public bool RefreshRefused { get; set; }

    // A refresh that never ends, whatever its token says, as core's immediate refreshes ignore it.
    public bool RefreshHangs { get; set; }

    public FakeCatalog Add(FakeShow show)
    {
        Shows[show.Entry.ID] = show;
        return this;
    }

    public FakeCatalog Add(SourceEntry movie)
    {
        Movies[movie.ID] = movie;
        return this;
    }

    public Task<IReadOnlyList<SourceEntry>> Search(MetadataEntityType kind, string text, int? year, bool includeRestricted, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        Searches.Add($"{kind.Value}:{text}:{year}");
        var entries = kind == MetadataEntityType.Movie ? Movies.Values : Shows.Values.Select(show => show.Entry);
        var folded = TitleFolding.Fold(text);
        IReadOnlyList<SourceEntry> hits = [.. entries
            .Where(entry => (year is null || entry.Year == year) && (includeRestricted || !entry.IsRestricted))
            .Where(entry => Folded(entry).Any(title => title.Contains(folded) || folded.Contains(title)))
            .Select(entry => entry with { Titles = [.. entry.Titles.Take(2)], Seasons = null })];
        return Task.FromResult(hits);
    }

    public Task<SourceEntry?> Lookup(MetadataGuid id, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (id.EntityType == MetadataEntityType.Movie)
            return Task.FromResult(Movies.GetValueOrDefault(id));
        if (!Shows.TryGetValue(id, out var show))
            return Task.FromResult<SourceEntry?>(null);
        var seasons = show.Episodes.Where(episode => episode.SeasonNumber is > 0).GroupBy(episode => episode.SeasonNumber!.Value).OrderBy(group => group.Key)
            .Select(group => new SeasonEntry(group.Key, group.Count(), group.Min(episode => episode.AirDate)?.Year)).ToList();
        return Task.FromResult<SourceEntry?>(show.Entry with { Seasons = seasons });
    }

    public EpisodeEntry? GetEpisode(MetadataGuid episodeID)
        => Shows.Values.Where(show => show.Stored).SelectMany(show => show.Episodes).FirstOrDefault(episode => episode.ID == episodeID);

    public IReadOnlyList<EpisodeEntry>? GetEpisodes(MetadataGuid seriesID)
        => Shows.TryGetValue(seriesID, out var show) && show.Stored ? show.Episodes : null;

    public bool IsStored(MetadataGuid id)
        => id.EntityType == MetadataEntityType.Movie ? StoredMovies.Contains(id) : GetEpisodes(id) is not null;

    public Task<bool> Refresh(MetadataGuid seriesID, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (RefreshRefused)
            return Task.FromResult(false);
        if (RefreshHangs)
            return new TaskCompletionSource<bool>().Task;
        Refreshes.Add(seriesID);
        if (Shows.TryGetValue(seriesID, out var show))
            show.Stored = true;
        return Task.FromResult(true);
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
            throw new MetadataProviderUnavailableException(MetadataSource.TMDB, "TMDb is paused.", TimeSpan.FromMinutes(5));
    }

    private static IEnumerable<string> Folded(SourceEntry entry)
    {
        var (full, main) = TitleFolding.FoldAll(entry.Titles);
        return full.Concat(main);
    }
}

/// <summary>
///   TMDb-shaped fixtures: titles and years are TMDb's; the IDs of the
///   made-up shows are made up. Episode IDs pack the show, season and
///   number.
/// </summary>
internal static class Tmdb
{
    public const string SampleShowID = "12345";
    public const string FrierenID = "209867";
    public const string OnePieceID = "37854";
    public const string MushokuID = "94664";
    public const string DororoShowID = "83097";
    public const string DororoMovieID = "14164";
    public const string YourNameID = "372058";
    public const string HunterNewID = "46298";
    public const string HunterOldID = "1958";
    public const string TankChairID = "320377";
    public const string NiaListonID = "312850";
    public const string TokyoRevengersID = "105009";

    public static MetadataGuid Series(string id)
        => new(MetadataSource.TMDB, MetadataEntityType.Series, id);

    public static MetadataGuid Movie(string id)
        => new(MetadataSource.TMDB, MetadataEntityType.Movie, id);

    public static MetadataGuid Episode(string seriesID, int season, int number)
        => new(MetadataSource.TMDB, MetadataEntityType.Episode, $"{seriesID}{season:00}{number:000}");

    /// <summary>
    ///   Every fixture in one catalog; nothing stored unless asked.
    /// </summary>
    public static FakeCatalog Fixtures(bool stored = false)
        => new FakeCatalog()
            .Add(Show(SampleShowID, "Sample Show", 2024, stored, seasons: [12]))
            .Add(Show(FrierenID, "Frieren: Beyond Journey's End", 2023, stored, original: "葬送のフリーレン", seasons: [28]))
            .Add(Show(OnePieceID, "One Piece", 1999, stored, original: "ONE PIECE", seasons: [61, 16, 14], specials: 3))
            .Add(Show(MushokuID, "Mushoku Tensei: Jobless Reincarnation", 2021, stored, original: "無職転生 ～異世界行ったら本気だす～", seasons: [23, 25]))
            .Add(Show(DororoShowID, "Dororo", 2019, stored, original: "どろろ", seasons: [24]))
            .Add(Show(HunterNewID, "Hunter x Hunter", 2011, stored, original: "HUNTER×HUNTER", seasons: [148]))
            .Add(Show(HunterOldID, "Hunter x Hunter", 1999, stored, original: "HUNTER×HUNTER", seasons: [62]))
            .Add(Show(TankChairID, "TANK CHAIR", 2025, stored, seasons: [12]))
            .Add(Show(NiaListonID, "Nia Liston: The Merciless Maiden", 2025, stored, alternates: ["Nia Liston - The Merciless Maiden"], seasons: [12]))
            .Add(Show(TokyoRevengersID, "Tokyo Revengers", 2021, stored, original: "東京リベンジャーズ", seasons: [24, 13, 13]))
            .Add(MovieEntry(DororoMovieID, "Dororo", 2007))
            .Add(MovieEntry(YourNameID, "Your Name.", 2016, original: "君の名は。"));

    /// <summary>
    ///   A show with seasons of the given sizes, a season a year from its
    ///   first, and specials in season 0.
    /// </summary>
    public static FakeShow Show(string id, string title, int year, bool stored, string? original = null, string[]? alternates = null, int[]? seasons = null, int specials = 0)
    {
        var seriesID = Series(id);
        var episodes = new List<EpisodeEntry>();
        foreach (var (count, index) in (seasons ?? []).Select((count, index) => (count, index)))
        {
            var aired = new DateOnly(year + index, 1, 7);
            episodes.AddRange(Enumerable.Range(1, count).Select(number => new EpisodeEntry(Episode(id, index + 1, number), seriesID, index + 1, number, EpisodeType.Episode, aired.AddDays(7 * (number - 1)))));
        }

        episodes.AddRange(Enumerable.Range(1, specials).Select(number => new EpisodeEntry(Episode(id, 0, number), seriesID, 0, number, EpisodeType.Special, new DateOnly(year, 12, 24))));
        IReadOnlyList<string> titles = [.. new[] { title, original }.OfType<string>().Concat(alternates ?? [])];
        return new(new(seriesID, titles, year, false), episodes, stored);
    }

    public static SourceEntry MovieEntry(string id, string title, int year, string? original = null)
        => new(Movie(id), [.. new[] { title, original }.OfType<string>()], year, false);
}

/// <summary>
///   The settings in each match mode, the rest left as they come.
/// </summary>
internal static class Modes
{
    public static TmdbFileMatcherConfiguration Lax => new() { Mode = MatchMode.Lax };

    public static TmdbFileMatcherConfiguration StrictAndInfo => new() { Mode = MatchMode.StrictAndInfo };

    public static TmdbFileMatcherConfiguration StrictAndFast => new() { Mode = MatchMode.StrictAndFast };
}

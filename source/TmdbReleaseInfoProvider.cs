using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Hashing;
using Shoko.Abstractions.Video.Release;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;

namespace Shoko.Plugin.TmdbFileMatcher;

/// <summary>
///   Matches files to TMDb shows, episodes and movies, by the TMDb IDs
///   tagged in their paths or, in lax mode, by name and year, and gives
///   core a release whose cross-references name them.
/// </summary>
/// <remarks>
///   Found by core by its type, and identified by its full name, so it is
///   never renamed or moved. An unreachable TMDb throws core's
///   <c>MetadataProviderUnavailableException</c>, so Shoko retries the
///   search later; a bug gives no result.
/// </remarks>
public sealed class TmdbReleaseInfoProvider : IReleaseInfoProvider<TmdbFileMatcherConfiguration>
{
    /// <summary>
    ///   The provider's name, on every release it finds.
    /// </summary>
    public const string ProviderName = "TMDb File Matcher";

    private readonly ILogger _logger;
    private readonly Func<TmdbFileMatcherConfiguration> _configuration;
    private readonly MatchPipeline _pipeline;
    private readonly IOfflineAutoMatchService? _autoMatch;

    /// <summary>
    ///   Creates the provider over the plugin's services; the Offline
    ///   Importer's auto-match service is used when it is installed.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="serviceProvider">Gives the pipeline and importer.</param>
    public TmdbReleaseInfoProvider(ILogger<TmdbReleaseInfoProvider> logger, ConfigurationProvider<TmdbFileMatcherConfiguration> configuration, IServiceProvider serviceProvider)
        : this(logger, () => configuration.Load(), serviceProvider.GetRequiredService<MatchPipeline>(), serviceProvider.GetService<IOfflineAutoMatchService>())
    {
    }

    // Creates the provider over a given pipeline and auto-match service, for the tests.
    internal TmdbReleaseInfoProvider(ILogger logger, Func<TmdbFileMatcherConfiguration> configuration, MatchPipeline pipeline, IOfflineAutoMatchService? autoMatch)
    {
        _logger = logger;
        _configuration = configuration;
        _pipeline = pipeline;
        _autoMatch = autoMatch;
    }

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public string Description => """
        Matches files AniDB does not know to TMDb shows, episodes and movies,
        by the TMDb IDs tagged in their paths. In lax mode it also searches
        untagged files by name and year; the strict modes, the default, leave
        them alone. It matches automatically only where the auto-match rules
        allow, which the Offline Importer judges.
    """;

    /// <inheritdoc/>
    public string? CannotAutoMatchReason
        => _autoMatch is null ? "The Offline Importer is not installed, so files are matched only when asked."
            : _configuration().AutoMatchRules.Count is 0 ? "No auto-match rules are set up, so files are matched only when asked."
            : null;

    /// <inheritdoc/>
    public async Task<ReleaseInfo?> GetReleaseInfoForVideo(ReleaseInfoContext context, CancellationToken cancellationToken)
    {
        var video = context.Video;
        try
        {
            var config = _configuration();
            if (context.IsAutomatic && CannotAutoMatchReason is not null)
                return null;

            var locations = video.Files.Select(file => new FileLocation(file.ManagedFolderID, file.RelativePath, file.ManagedFolder)).ToList();
            var outcome = await _pipeline.Match(locations, config, cancellationToken).ConfigureAwait(false);
            if (!outcome.IsMatched)
            {
                _logger.Log(context.IsAutomatic || outcome.Kind is MatchKind.Skipped ? LogLevel.Debug : LogLevel.Information, "No match for {Path}: {Reason}", outcome.Evidence?.Location.NormalizedPath, outcome.Reason);
                return null;
            }

            var release = Build(outcome, video.Size, [.. video.Hashes.Select(hash => new HashDigest { Type = hash.Type, Value = hash.Value, Metadata = hash.Metadata })]);
            if (context.IsAutomatic && !_autoMatch!.Evaluate(config.AutoMatchRules, video.Files, release).IsAllowed)
            {
                _logger.LogDebug("The auto-match rules do not allow the match for {Path}.", outcome.Evidence?.Location.NormalizedPath);
                return null;
            }

            return release;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not MetadataProviderUnavailableException)
        {
            _logger.LogError(exception, "Matching failed for video {VideoID}.", video?.LocalID);
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<ReleaseInfo?> GetReleaseInfoById(string releaseId, CancellationToken cancellationToken)
        => throw new NotSupportedException("The TMDb file matcher's releases are read from a file's path, and have no ID to look up.");

    // Builds the release for a match; a provisional one defers to the next provider.
    internal static ReleaseInfo Build(MatchOutcome outcome, long size, HashDigest[] hashes)
    {
        var evidence = outcome.Evidence!;
        var parsed = evidence.Parsed;
        var group = string.IsNullOrWhiteSpace(parsed.ReleaseGroup) ? null : parsed.ReleaseGroup.Trim();
        return new()
        {
            ID = null,
            ProviderName = ProviderName,
            OriginalFilename = evidence.Location.FileName,
            FileSize = size,
            Hashes = hashes.Length > 0 ? [.. hashes] : null,
            IsCreditless = parsed.IsCreditless,
            IsCensored = parsed.IsCensored,
            Version = parsed.ReleaseVersion ?? 1,
            Group = group is not null ? new() { ID = group, Name = group, ShortName = group, Source = "tmdb-file-matcher" } : null,
            Source = parsed.ReleaseSource ?? ReleaseSource.Unknown,
            CrossReferences = [.. outcome.References],
            Metadata = JsonSerializer.Serialize(new { kind = outcome.Kind.ToString(), path = outcome.Path, reason = outcome.Reason }),
            IsPublic = false,
            DeferToNext = outcome.Kind is MatchKind.Provisional,
        };
    }
}

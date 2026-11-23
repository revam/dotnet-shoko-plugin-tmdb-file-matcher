using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;
using Shoko.Plugin.OfflineImporter.Abstractions.Configuration;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;
using Xunit;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   Builds the plugin's services over stand-ins for core's, the way core
///   builds the release provider and the validator at start-up, with the
///   Offline Importer absent and present, so a broken registration fails
///   here rather than stopping a server.
/// </summary>
public class CompositionTests
{
    private static ServiceProvider Compose(bool withImporter)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Type[] core = [typeof(IMetadataService), typeof(IMetadataLinkingService), typeof(IMetadataRefreshService), typeof(IPluginManager), typeof(IConfigurationService)];
        Type[] importer = [typeof(IOfflineMatchingService), typeof(IOfflineAutoMatchService)];
        foreach (var type in withImporter ? [.. core, .. importer] : core)
            services.AddSingleton(type, Fake.Of(type));
        services.AddSingleton(typeof(ConfigurationProvider<>));
        Plugin.RegisterServices(services, (IApplicationPaths)Fake.Of(typeof(IApplicationPaths)));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheReleaseProvider_AndTheValidator_AreBuiltAsCoreBuildsThem(bool withImporter)
    {
        using var provider = Compose(withImporter);

        Assert.NotNull(ActivatorUtilities.CreateInstance<TmdbReleaseInfoProvider>(provider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<TmdbCrossReferenceValidator>(provider));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheImporter_ReadsPaths_OnlyWhenInstalled(bool withImporter)
    {
        using var provider = Compose(withImporter);

        Assert.Equal(withImporter, provider.GetRequiredService<PathParser>().UsesImporter);
    }

    [Fact]
    public void WithoutTheImporter_FilesAreMatchedOnlyWhenAsked()
    {
        using var provider = Compose(false);

        var reason = ActivatorUtilities.CreateInstance<TmdbReleaseInfoProvider>(provider).CannotAutoMatchReason;

        Assert.Contains("Offline Importer is not installed", reason);
    }

    [Theory]
    [InlineData(0, "No auto-match rules are set up, so files are matched only when asked.")]
    [InlineData(1, null)]
    public void WithTheImporter_FilesAreMatchedOnlyWhenAsked_UntilThereAreRules(int rules, string? expected)
    {
        using var provider = Compose(true);
        var releaseProvider = new TmdbReleaseInfoProvider(
            NullLogger.Instance,
            () => new() { AutoMatchRules = [.. Enumerable.Range(0, rules).Select(_ => new AutoMatchRule())] },
            provider.GetRequiredService<MatchPipeline>(),
            provider.GetService<IOfflineAutoMatchService>()
        );

        Assert.Equal(expected, releaseProvider.CannotAutoMatchReason);
    }
}

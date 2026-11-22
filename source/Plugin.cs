using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.OfflineImporter.Abstractions.Services;
using Shoko.Plugin.TmdbFileMatcher.Catalog;
using Shoko.Plugin.TmdbFileMatcher.Evidence;
using Shoko.Plugin.TmdbFileMatcher.Matching;

namespace Shoko.Plugin.TmdbFileMatcher;

/// <summary>
///   Matches files to TMDb and validates the releases that link to it.
/// </summary>
/// <remarks>
///   The release provider and the validator are not registered here: core
///   finds them by their types.
/// </remarks>
public partial class Plugin : IPluginServiceRegistration
{
    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton<ITmdbCatalog, TmdbCatalog>();
        // The importer is optional: without it, its service is not registered and the built-in parser reads paths.
        serviceCollection.AddSingleton(provider => new PathParser(provider.GetService<IOfflineMatchingService>()));
        serviceCollection.AddSingleton<MatchPipeline>();
    }
}

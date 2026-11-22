using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.OfflineImporter.Abstractions.Configuration;

namespace Shoko.Plugin.TmdbFileMatcher;

/// <summary>
///   The settings of the TMDb file matcher: the scores a match needs,
///   whether restricted entries may match, and the rules that allow
///   automatic matching.
/// </summary>
/// <remarks>
///   Identified by its full name, so it is never renamed or moved.
/// </remarks>
public sealed class TmdbFileMatcherConfiguration : IReleaseInfoProviderConfiguration, INewtonsoftJsonConfiguration, IConfigurationWithCustomValidation<TmdbFileMatcherConfiguration>
{
    /// <summary>
    ///   The score a candidate needs to be accepted, leading the next by 10.
    /// </summary>
    [Display(Name = "Accept score")]
    [DefaultValue(80)]
    [Range(0, 100)]
    public int AcceptScore { get; set; } = 80;

    /// <summary>
    ///   The score a candidate needs to be saved provisionally, leaving the
    ///   next release provider to carry on.
    /// </summary>
    [Display(Name = "Provisional score")]
    [DefaultValue(60)]
    [Range(0, 100)]
    public int ProvisionalScore { get; set; } = 60;

    /// <summary>
    ///   Whether restricted entries may be matched.
    /// </summary>
    [Display(Name = "Include restricted entries")]
    [DefaultValue(false)]
    public bool IncludeRestricted { get; set; }

    /// <summary>
    ///   The rules that allow automatic matching, judged by the Offline
    ///   Importer. With none, or without the importer, files are matched
    ///   only when asked.
    /// </summary>
    [Display(Name = "Auto-match rules")]
    public List<AutoMatchRule> AutoMatchRules { get; set; } = [];

    /// <inheritdoc/>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(TmdbFileMatcherConfiguration config, IConfigurationService configurationService, IPluginManager pluginManager)
        => config.ProvisionalScore > config.AcceptScore
            ? new Dictionary<string, IReadOnlyList<string>> { [nameof(ProvisionalScore)] = [$"The provisional score ({config.ProvisionalScore}) must not be above the accept score ({config.AcceptScore})."] }
            : new Dictionary<string, IReadOnlyList<string>>();
}

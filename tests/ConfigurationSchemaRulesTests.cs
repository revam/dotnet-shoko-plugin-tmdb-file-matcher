using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.UI.Attributes;
using Shoko.Abstractions.UI.Components;
using Shoko.Abstractions.UI.Enums;
using Xunit;

namespace Shoko.Plugin.TmdbFileMatcher.Tests;

/// <summary>
///   Every configuration of the plugin passes the rules core's schema
///   generator throws on at registration, where a throw stops the whole
///   server's start. Core's generator lives in the server, which the tests do not
///   host, so this mirrors its rules by reflection.
/// </summary>
/// <remarks>
///   Mirrors <c>Shoko.Server/Services/Configuration/ShokoJsonSchemaGenerator.cs</c>
///   in the Shoko server repository: <c>ProcessProperty</c>'s list checks,
///   <c>AssertNoNestedCollection</c>, <c>AssertKeyUsable</c>, the flags enum
///   check and <c>ResolveToggleMember</c>. Re-read it when core changes.
/// </remarks>
public class ConfigurationSchemaRulesTests
{
    /// <summary>
    ///   Every configuration type the plugin declares.
    /// </summary>
    public static IReadOnlyList<Type> ConfigurationTypes { get; } = [.. typeof(TmdbFileMatcherConfiguration).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IConfiguration).IsAssignableFrom(type))
        .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    /// <summary>
    ///   The configuration is found, and no configuration breaks a rule.
    /// </summary>
    [Fact]
    public void EveryConfigurationPassesCoresRules()
    {
        Assert.Contains(typeof(TmdbFileMatcherConfiguration), ConfigurationTypes);
        var problems = ConfigurationTypes.SelectMany(Problems).ToList();
        Assert.True(problems.Count is 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    ///   Every member the plugin's own configuration types write can be set
    ///   back: a get-only one would be written into the file and drawn in
    ///   core's form for nothing. Not core's rule; the Offline Importer's
    ///   types embedded here are the importer's to judge.
    /// </summary>
    [Fact]
    public void EveryWrittenMemberCanBeSetBack()
    {
        var visited = new HashSet<Type>();
        var readOnly = ConfigurationTypes.SelectMany(type => Reached(type, visited))
            .Where(type => type.Assembly == typeof(TmdbFileMatcherConfiguration).Assembly)
            .SelectMany(type => Serialized(type).Where(property => property.SetMethod is not { IsPublic: true }).Select(property => $"{type.Name}.{property.Name}"))
            .ToList();
        Assert.Empty(readOnly);
    }

    /// <summary>
    ///   The check finds what core refuses: an inline list whose items have
    ///   no key, a list of lists, an unusable
    ///   dictionary key, a flags enum with no single bit, and a checkbox
    ///   section with no switch; and passes their fixes.
    /// </summary>
    [Fact]
    public void TheCheckFindsWhatCoreRefuses()
    {
        var problems = Problems(typeof(Broken));
        Assert.Contains("Inline lists must have a primary key set: Broken.Rows has items with no [Key] member.", problems);
        Assert.Contains("Tab lists must have a primary key set: Broken.Tabs has items with no [Key] member.", problems);
        Assert.Contains("Inline lists are not supported for non-class list items.", problems);
        Assert.Contains(problems, problem => problem.StartsWith("Configuration property \"Broken.Nested\" is a ", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("cannot be used as a key", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("which has no member with a single bit set", StringComparison.Ordinal));
        Assert.Contains("Section \"Unswitched\" is drawn as a checkbox but names no ToggleMember, the bool member that turns it on.", problems);
        Assert.Equal(7, problems.Count);
        Assert.Empty(Problems(typeof(Fixed)));
    }

    /// <summary>
    ///   An options provider fits the member it lists, as core checks at
    ///   registration.
    /// </summary>
    [Fact]
    public void TheCheckFindsAnOptionsProviderThatDoesNotFit()
    {
        var problems = Problems(typeof(BrokenOptions));

        Assert.Contains(problems, problem => problem.Contains("BrokenOptions.ListMissing"));
        Assert.Contains(problems, problem => problem.Contains("BrokenOptions.ListWrongType"));
        Assert.Contains(problems, problem => problem.Contains("BrokenOptions.ListKeys"));
        Assert.Empty(Problems(typeof(FittingOptions)));
    }

    /// <summary>
    ///   A settings row with no key, the shape that once stopped a server's
    ///   start.
    /// </summary>
    [Fact]
    public void TheCheckFindsASettingsRowWithNoKey()
        => Assert.Contains("Inline lists must have a primary key set: Unkeyed.Sources has items with no [Key] member.", Problems(typeof(Unkeyed)));

    /// <summary>
    ///   What core's generator would throw for a configuration type and
    ///   everything it reaches.
    /// </summary>
    /// <param name="root">The configuration type.</param>
    /// <returns>Each throw's message.</returns>
    public static List<string> Problems(Type root)
    {
        var problems = new List<string>();
        Walk(root, [], problems);
        return problems;
    }

    private static IEnumerable<Type> Reached(Type type, HashSet<Type> visited)
    {
        if (!IsClassSchema(type) || !visited.Add(type))
            yield break;
        yield return type;
        foreach (var property in Serialized(type))
        {
            var next = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            foreach (var reached in Reached(ElementType(next) ?? next, visited))
                yield return reached;
        }
    }

    private static void Walk(Type type, HashSet<Type> visited, List<string> problems)
    {
        if (!IsClassSchema(type) || !visited.Add(type))
            return;
        CheckSection(type, problems);
        CheckOptionsProviders(type, problems);
        foreach (var property in Serialized(type))
        {
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var name = $"{type.Name}.{property.Name}";
            if (IsFlagsEnum(propertyType))
            {
                if (!Enum.GetValues(propertyType).Cast<object>().Any(value => System.Numerics.BitOperations.PopCount(Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture)) is 1))
                    problems.Add($"Configuration property \"{name}\" holds the flags enum {propertyType.Name}, which has no member with a single bit set.");
                continue;
            }

            var element = ElementType(propertyType);
            if (element is not null && ElementType(element) is not null && !(Dictionary(propertyType) is not null && Dictionary(element) is null))
            {
                problems.Add($"Configuration property \"{name}\" is a {propertyType.Name}. A collection cannot hold another collection directly.");
                continue;
            }

            if (Dictionary(propertyType) is var (key, value))
            {
                if (!KeyUsable(key))
                    problems.Add($"Type \"{key.FullName}\" is not serializable to text and therefore cannot be used as a key in a dictionary inside a configuration.");
                Walk(value, visited, problems);
                continue;
            }

            if (element is not null)
            {
                CheckList(type, property, element, problems);
                Walk(element, visited, problems);
                continue;
            }

            Walk(propertyType, visited, problems);
        }
    }

    // ProcessProperty: an automatic list of class items is inline, and inline, tab and dropdown lists need class items with a key.
    private static void CheckList(Type owner, PropertyInfo property, Type element, List<string> problems)
    {
        var listType = property.GetCustomAttribute<ListAttribute>(false)?.ListType ?? DisplayListType.Auto;
        var isSection = IsClassSchema(element);
        if (listType is DisplayListType.Auto && isSection)
            listType = DisplayListType.ComplexInline;
        var hasKey = property.GetCustomAttribute<KeyAttribute>(false) is not null
            || (isSection && Serialized(element).Any(member => member.GetCustomAttribute<KeyAttribute>(false) is not null || member.PropertyType == typeof(TitleComponent)));
        var plural = listType switch
        {
            DisplayListType.ComplexDropdown => "Dropdown lists",
            DisplayListType.ComplexTab => "Tab lists",
            DisplayListType.ComplexInline => "Inline lists",
            _ => null,
        };
        if (plural is not null)
        {
            if (!isSection)
                problems.Add($"{plural} are not supported for non-class list items.");
            else if (!hasKey)
                problems.Add($"{plural} must have a primary key set: {owner.Name}.{property.Name} has items with no [Key] member.");
        }

        if (listType is DisplayListType.EnumCheckbox && !(Nullable.GetUnderlyingType(element) ?? element).IsEnum)
            problems.Add("Checkbox lists are not supported for non-enum list items.");
    }

    // ResolveToggleMember.
    private static void CheckSection(Type type, List<string> problems)
    {
        var section = type.GetCustomAttribute<SectionAttribute>(false);
        var toggle = string.IsNullOrWhiteSpace(section?.ToggleMember) ? null : section.ToggleMember;
        var isCheckbox = section?.SectionType is DisplaySectionType.Checkbox;
        if (toggle is null)
        {
            if (isCheckbox)
                problems.Add($"Section \"{type.Name}\" is drawn as a checkbox but names no ToggleMember, the bool member that turns it on.");
            return;
        }

        if (!isCheckbox)
            problems.Add($"Section \"{type.Name}\" names the ToggleMember \"{toggle}\", but only a checkbox section has one.");
        else if (Serialized(type).FirstOrDefault(member => member.Name == toggle) is not { } member || member.PropertyType != typeof(bool))
            problems.Add($"Section \"{type.Name}\" names the ToggleMember \"{toggle}\", which is not a serialised bool member of it.");
    }

    // An options provider is public, not generic, names members of its type, and lists what they take.
    private static void CheckOptionsProviders(Type type, List<string> problems)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<OptionsProviderAttribute>() is not { } provider)
                continue;

            var where = $"{type.Name}.{method.Name}";
            if (!method.IsPublic || method.IsGenericMethodDefinition)
                problems.Add($"{where}: an options provider is public and not generic.");
            var listed = ListedType(method.ReturnType);
            foreach (var member in provider.Members)
            {
                if (type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is not { } property)
                {
                    problems.Add($"{where}: {member} is not a property of {type.Name}.");
                    continue;
                }

                var memberType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (provider.Target is OptionsTarget.Keys && Dictionary(memberType) is null)
                {
                    problems.Add($"{where}: only a dictionary's keys can be listed, and {member} is not one.");
                    continue;
                }

                var optionType = Dictionary(memberType) is var (key, value) ? provider.Target is OptionsTarget.Keys ? key : value : ElementType(memberType) ?? memberType;
                if (listed != optionType)
                    problems.Add($"{where}: lists {listed?.Name ?? "nothing"}, but {member} takes {optionType.Name}.");
            }
        }
    }

    private static Type? ListedType(Type returnType)
    {
        if (returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            returnType = returnType.GetGenericArguments()[0];
        if (ElementType(returnType) is not { } item)
            return null;
        item = Nullable.GetUnderlyingType(item) ?? item;
        return item.IsGenericType && item.GetGenericTypeDefinition() == typeof(SelectOption<>) ? item.GetGenericArguments()[0] : item;
    }

    // AssertKeyUsable.
    private static bool KeyUsable(Type key)
        => !IsFlagsEnum(key) && (key == typeof(string) || key == typeof(MetadataSource) || key == typeof(MetadataEntityType) || key == typeof(MetadataGuid) || key.IsEnum
            || key.GetCustomAttribute<SerializableAttribute>() is not null || typeof(ISerializable).IsAssignableFrom(key));

    // The members Newtonsoft writes, as the schema lists them.
    private static IEnumerable<PropertyInfo> Serialized(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length is 0
                && property.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() is null
                && property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null);

    // A class the generator draws as a section; UI components, converted types and the metadata value types are leaves.
    private static bool IsClassSchema(Type type)
        => type is { IsClass: true } && type != typeof(string) && !type.IsArray
            && !(type.FullName?.StartsWith("System.", StringComparison.Ordinal) ?? true)
            && !(type.FullName?.StartsWith("Shoko.Abstractions.UI.Components.", StringComparison.Ordinal) ?? false)
            && type.GetCustomAttribute<TypeConverterAttribute>() is null
            && type.GetCustomAttribute<Newtonsoft.Json.JsonConverterAttribute>() is null
            && type != typeof(MetadataSource) && type != typeof(MetadataGuid)
            && !typeof(IEnumerable).IsAssignableFrom(type)
            && Serialized(type).Any();

    private static bool IsFlagsEnum(Type type)
        => type.IsEnum && type.IsDefined(typeof(FlagsAttribute), false);

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || IsFlagsEnum(type))
            return null;
        if (Dictionary(type) is var (_, value))
            return value;
        if (type.IsArray)
            return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];
        return type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
    }

    private static (Type Key, Type Value)? Dictionary(Type type)
    {
        foreach (var candidate in type.GetInterfaces().Append(type))
        {
            if (candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
                return (candidate.GetGenericArguments()[0], candidate.GetGenericArguments()[1]);
        }

        return null;
    }

    // What the check must refuse, one of each.
    private sealed class Broken
    {
        public List<NoKey> Rows { get; set; } = [];

        [List(ListType = DisplayListType.ComplexTab)]
        public NoKey[] Tabs { get; set; } = [];

        [List(ListType = DisplayListType.ComplexInline)]
        public string[] Words { get; set; } = [];

        public List<List<int>> Nested { get; set; } = [];

        public Dictionary<NoKey, string> ByRow { get; set; } = [];

        public NoBit Bits { get; set; }

        public Unswitched Switch { get; set; } = new();
    }

    private sealed class NoKey
    {
        public string Name { get; set; } = string.Empty;
    }

    [Flags]
    private enum NoBit
    {
        None = 0,
        Both = 3,
    }

    [Section(DisplaySectionType.Checkbox)]
    private sealed class Unswitched
    {
        public bool On { get; set; }
    }

    // The same, as core takes them.
    private sealed class Fixed
    {
        public List<Keyed> Rows { get; set; } = [];

        [List(ListType = DisplayListType.Flat)]
        public NoKey[] FlatRows { get; set; } = [];

        public string[] Words { get; set; } = [];

        public Dictionary<string, List<int>> Grouped { get; set; } = [];

        public Dictionary<string, NoKey> ByName { get; set; } = [];

        public Switched Switch { get; set; } = new();
    }

    private sealed class Keyed
    {
        [Key]
        public string Name { get; set; } = string.Empty;
    }

    [Section(DisplaySectionType.Checkbox, ToggleMember = nameof(On))]
    private sealed class Switched
    {
        public bool On { get; set; }
    }

    // Options providers, as core takes them and as it refuses them.
    private sealed class FittingOptions
    {
        public string Name { get; set; } = string.Empty;

        public List<int> Numbers { get; set; } = [];

        [OptionsProvider(nameof(Name))]
        public Task<IReadOnlyList<SelectOption<string>>> ListNames()
            => Task.FromResult<IReadOnlyList<SelectOption<string>>>([]);

        [OptionsProvider(nameof(Numbers))]
        public int[] ListNumbers()
            => [];
    }

    private sealed class BrokenOptions
    {
        public string Name { get; set; } = string.Empty;

        [OptionsProvider("Missing")]
        public string[] ListMissing()
            => [];

        [OptionsProvider(nameof(Name))]
        public long[] ListWrongType()
            => [];

        [OptionsProvider(nameof(Name), Target = OptionsTarget.Keys)]
        public string[] ListKeys()
            => [];
    }

    // A settings row with no key.
    private sealed class Unkeyed
    {
        public List<NoKey> Sources { get; set; } = [];
    }
}

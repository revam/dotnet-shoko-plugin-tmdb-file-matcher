using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Shoko.Plugin.TmdbFileMatcher.Search;

/// <summary>
///   Folds titles so that spellings of one name compare equal, and measures
///   how alike two folded titles are.
/// </summary>
internal static class TitleFolding
{
    private static readonly string[] _subtitleSeparators = [": ", " - ", " – ", " — ", "："];

    // Folds a title: full width to half, case, accents and apostrophes away, punctuation to spaces, and runs of spaces to one.
    public static string Fold(string title)
    {
        var builder = new StringBuilder(title.Length);
        var space = true;
        foreach (var character in title.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark || character is '\'' or '’' or '`')
                continue;
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    // The part of a title before its subtitle, or <c>null</c> without one.
    public static string? MainPart(string title)
    {
        var cut = _subtitleSeparators.Select(separator => title.IndexOf(separator, StringComparison.Ordinal)).Where(index => index > 0).DefaultIfEmpty(-1).Min();
        return cut > 0 ? title[..cut].Trim() : null;
    }

    // How alike two folded titles are, from 0 to 1: the better of their edit-distance ratio and the overlap of their words.
    public static double Similarity(string a, string b)
    {
        if (a.Length is 0 || b.Length is 0)
            return 0;
        if (a == b)
            return 1;

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        var edit = 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
        var wordsA = a.Split(' ').ToHashSet();
        var wordsB = b.Split(' ').ToHashSet();
        return Math.Max(edit, 2.0 * wordsA.Intersect(wordsB).Count() / (wordsA.Count + wordsB.Count));
    }

    // The folded forms of some titles, and of their main parts, without repeats or empty ones.
    public static (IReadOnlyList<string> Full, IReadOnlyList<string> Main) FoldAll(IEnumerable<string?> titles)
    {
        var list = titles.OfType<string>().Where(title => !string.IsNullOrWhiteSpace(title)).ToList();
        var full = list.Select(Fold).Where(title => title.Length > 0).Distinct().ToList();
        return (full, [.. list.Select(MainPart).OfType<string>().Select(Fold).Where(title => title.Length > 0).Distinct().Except(full)]);
    }
}

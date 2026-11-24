using System.Text.RegularExpressions;

namespace Plicbox.Core;

/// <summary>"1-3, 5" as typed in the print window: which pages, and the same written the way printers take it.</summary>
public static partial class PageRanges
{
    /// <summary>The pages (1-based, in order), or null when the text isn't a list of pages within 1…<paramref name="pageCount"/>.
    /// "4-" runs to the last page.</summary>
    public static IReadOnlyList<int>? Parse(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text) || pageCount < 1) return null;
        var pages = new SortedSet<int>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Range().Match(part);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var from)) return null;
            var to = from;
            if (match.Groups[2].Success)
                to = match.Groups[3].Success ? (int.TryParse(match.Groups[3].Value, out var end) ? end : -1) : pageCount;
            if (from < 1 || from > pageCount || to < from) return null;
            for (var page = from; page <= Math.Min(to, pageCount); page++) pages.Add(page);
        }
        return pages.Count == 0 ? null : [.. pages];
    }

    /// <summary>Pages written as runs: [1,2,3,5] -> "1-3,5".</summary>
    public static string Format(IReadOnlyList<int> pages)
    {
        var runs = new List<string>();
        for (var i = 0; i < pages.Count;)
        {
            var start = i;
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            runs.Add(i == start ? $"{pages[start]}" : $"{pages[start]}-{pages[i]}");
            i++;
        }
        return string.Join(",", runs);
    }

    [GeneratedRegex(@"^(\d{1,5})\s*(?:([-–])\s*(\d{1,5})?)?$")]
    private static partial Regex Range();
}

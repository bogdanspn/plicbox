using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Plicbox.Core;

/// <summary>
/// The app's language. Text is written in English and looked up by that English (<c>L.T("Mark as Read")</c>), so a
/// phrase without a translation simply stays English, and a new language is one more Languages/xx.json file.
/// Dates and numbers keep the Windows regional format (date order, clock, first weekday); only the day and month
/// names follow the language, so words never mix.
/// </summary>
public static class L
{
    /// <summary>The languages on offer, by code, with each one's own name for itself.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("en", "English"), ("de", "Deutsch"), ("ro", "Română"), ("es", "Español")];

    /// <summary>"en", "de", "ro", "es", …: the language in use.</summary>
    public static string Language { get; private set; } = "en";

    private static IReadOnlyDictionary<string, string> _strings = new Dictionary<string, string>();
    private static readonly CultureInfo Windows = CultureInfo.CurrentCulture;
    private static readonly CultureInfo WindowsUi = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Switches the language: "system" follows the Windows display language (English when it isn't one we have),
    /// otherwise a code from <see cref="Languages"/>. Call before any window is built; text doesn't change live.
    /// </summary>
    public static void Apply(string? setting)
    {
        var wanted = setting is null or "system" ? WindowsUi.TwoLetterISOLanguageName : setting;
        Language = Languages.Any(l => l.Code == wanted) ? wanted : "en";
        _strings = Language == "en" ? new Dictionary<string, string>() : Load(Language);

        var culture = Culture(Language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture; // background work formats dates too
        var ui = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = ui;
        CultureInfo.DefaultThreadCurrentUICulture = ui;
    }

    /// <summary>The text in the current language.</summary>
    public static string T(string english) => _strings.TryGetValue(english, out var text) ? text : english;

    /// <summary><see cref="T"/> of a format string ("Block {0}…"), filled in.</summary>
    public static string F(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>The phrases of a language, English → translation (for tests and tools).</summary>
    public static IReadOnlyDictionary<string, string> Load(string language)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Plicbox.Core.Languages.{language}.json");
        return stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    /// <summary>Windows' regional format, with the language's own day and month names.</summary>
    private static CultureInfo Culture(string language)
    {
        var culture = (CultureInfo)Windows.Clone();
        if (culture.TwoLetterISOLanguageName == language) return culture;
        var names = (language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo(language)).DateTimeFormat;
        var format = culture.DateTimeFormat;
        format.DayNames = names.DayNames;
        format.AbbreviatedDayNames = names.AbbreviatedDayNames;
        format.ShortestDayNames = names.ShortestDayNames;
        format.MonthNames = names.MonthNames;
        format.AbbreviatedMonthNames = names.AbbreviatedMonthNames;
        format.MonthGenitiveNames = names.MonthGenitiveNames;
        format.AbbreviatedMonthGenitiveNames = names.AbbreviatedMonthGenitiveNames;
        // A 12-hour Windows clock keeps its AM/PM, in the language's words when it has them.
        if (names.AMDesignator.Length > 0) (format.AMDesignator, format.PMDesignator) = (names.AMDesignator, names.PMDesignator);
        return culture;
    }
}

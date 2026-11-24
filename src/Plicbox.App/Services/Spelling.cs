using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace Plicbox.App.Services;

/// <summary>
/// The spell checker that comes with Windows, in the languages added to Windows. The editor asks it directly: the
/// browser's own spell check follows the Windows display language only, and can't be told another.
/// Everything stays on the PC, and the dictionaries are loaded by Windows as needed.
/// </summary>
public static class Spelling
{
    /// <param name="Tag">"de-DE"</param>
    /// <param name="Name">The language in its own words: "Deutsch (Deutschland)".</param>
    public sealed record SpellingLanguage(string Tag, string Name);

    private static readonly Lock Gate = new();
    private static ISpellCheckerFactory? _factory;
    private static readonly Dictionary<string, ISpellChecker> Checkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The languages added to Windows that it can check, in the user's order. Empty when there's no spell checker.</summary>
    public static Task<IReadOnlyList<SpellingLanguage>> LanguagesAsync() => RunAsync<IReadOnlyList<SpellingLanguage>>(() =>
    {
        var supported = Strings(Factory.GetSupportedLanguages());
        var wanted = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International\User Profile", "Languages", null) as string[]
            ?? [CultureInfo.CurrentUICulture.Name];
        return wanted.Select(w => Match(w, supported)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag => new SpellingLanguage(tag, CultureInfo.GetCultureInfo(tag).NativeName)).ToList();
    }, []);

    /// <summary>Per language, which of the words it doesn't know.</summary>
    public static Task<IReadOnlyDictionary<string, HashSet<string>>> UnknownAsync(IReadOnlyCollection<string> words, IReadOnlyCollection<string> tags) =>
        RunAsync<IReadOnlyDictionary<string, HashSet<string>>>(() =>
        {
            var text = string.Join('\n', words); // one call for all of them
            return tags.ToDictionary(tag => tag, tag =>
            {
                var unknown = new HashSet<string>(StringComparer.Ordinal);
                var errors = Checker(tag).Check(text);
                while (errors.Next(out var error) == 0 && error is not null)
                {
                    var at = (int)error.GetStartIndex();
                    var start = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
                    var end = text.IndexOf('\n', at);
                    unknown.Add(text[start..(end < 0 ? text.Length : end)]);
                }
                return unknown;
            });
        }, new Dictionary<string, HashSet<string>>());

    /// <summary>What the word may have been meant to be: from the first of the languages that has an idea.</summary>
    public static Task<IReadOnlyList<string>> SuggestAsync(string word, IEnumerable<string> tags, int count = 5) =>
        RunAsync<IReadOnlyList<string>>(() =>
        {
            foreach (var tag in tags)
            {
                var suggestions = Strings(Checker(tag).Suggest(word), count);
                if (suggestions.Count > 0) return suggestions;
            }
            return [];
        }, []);

    /// <summary>Adds the word to the user's own Windows dictionary, so other apps know it too.</summary>
    public static Task AddAsync(string word, IEnumerable<string> tags) => RunAsync(() =>
    {
        foreach (var tag in tags) Checker(tag).Add(word);
        return true;
    }, false);

    /// <summary>The exact language, or else another region's (Windows lists plain "de" for some).</summary>
    private static string? Match(string wanted, IReadOnlyList<string> supported) =>
        supported.FirstOrDefault(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase))
        ?? supported.Where(s => Language(s).Equals(Language(wanted), StringComparison.OrdinalIgnoreCase))
            // de-DE before de-AT, es-ES before es-AR: the language's own country, where there is one.
            .OrderByDescending(s => s.Equals(CultureInfo.CreateSpecificCulture(Language(s)).Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    private static string Language(string tag) => tag.Split('-')[0];

    private static ISpellCheckerFactory Factory => _factory ??=
        (ISpellCheckerFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"), throwOnError: true)!)!;

    private static ISpellChecker Checker(string tag) =>
        Checkers.TryGetValue(tag, out var checker) ? checker : Checkers[tag] = Factory.CreateSpellChecker(tag);

    private static List<string> Strings(IEnumString source, int limit = int.MaxValue)
    {
        var list = new List<string>();
        var one = new string[1];
        while (list.Count < limit && source.Next(1, one, IntPtr.Zero) == 0) list.Add(one[0]);
        return list;
    }

    /// <summary>Off the window's thread, one at a time; without a working spell checker nothing is ever wrong.</summary>
    private static Task<T> RunAsync<T>(Func<T> work, T otherwise) => Task.Run(() =>
    {
        lock (Gate)
        {
            try { return work(); }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Spell checking failed");
                return otherwise;
            }
        }
    });

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString GetSupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string tag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string tag);
    }

    // Only the methods up to the last one used: the rest of the interface isn't called.
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetLanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig]
        int Next(out ISpellingError? error);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint GetStartIndex();
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Plicbox.Core;

namespace Plicbox.Tests;

/// <summary>
/// Every phrase the app shows has a translation in every language on offer, and translations keep the English
/// placeholders ({0}, {1:t}…), so string.Format never throws. Phrases are found where they're written: L.T("…") /
/// L.F("…") in C#, {local:T '…'} in XAML, and [[…]] / T('…') in the compose editor's page.
/// </summary>
public sealed partial class LocalizationTests
{
    public static TheoryData<string> Translated => [.. L.Languages.Select(l => l.Code).Where(c => c != "en")];

    [Theory, MemberData(nameof(Translated))]
    public void Every_phrase_has_a_translation(string language)
    {
        var strings = L.Load(language);
        var missing = UsedPhrases().Where(p => !strings.ContainsKey(p.Text)).Select(p => $"{p.File}: {p.Text}").Distinct().ToList();
        Assert.True(missing.Count == 0, $"No {language} for:\n" + string.Join("\n", missing));
    }

    [Theory, MemberData(nameof(Translated))]
    public void Translations_keep_the_placeholders(string language)
    {
        var broken = L.Load(language).Where(kv => !Placeholders(kv.Key).SetEquals(Placeholders(kv.Value))).Select(kv => kv.Key).ToList();
        Assert.True(broken.Count == 0, $"Placeholders differ ({language}):\n" + string.Join("\n", broken));
    }

    [Theory, MemberData(nameof(Translated))]
    public void No_translation_is_left_over_or_empty(string language)
    {
        var strings = L.Load(language);
        var used = UsedPhrases().Select(p => p.Text).ToHashSet();
        Assert.DoesNotContain(strings.Keys, k => !used.Contains(k));
        Assert.Empty(strings.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key));
    }

    [Fact]
    public void Untranslated_text_stays_English()
    {
        // The test process never calls L.Apply, so it runs in English: text comes back as written.
        Assert.Equal("Mark as Read", L.T("Mark as Read"));
        Assert.Equal("Block ana@x.example?", L.F("Block {0}?", "ana@x.example"));
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).ToHashSet();

    private static IEnumerable<(string File, string Text)> UsedPhrases()
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var name = Path.GetFileName(file);
            if (file.EndsWith(".xaml", StringComparison.Ordinal))
            {
                foreach (Match m in XamlPhrase().Matches(File.ReadAllText(file)))
                    yield return (name, WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1")));
            }
            else if (name == "compose.html")
            {
                var page = File.ReadAllText(file);
                foreach (Match m in PageHtmlPhrase().Matches(page)) yield return (name, WebUtility.HtmlDecode(m.Groups[1].Value));
                foreach (Match m in PageScriptPhrase().Matches(page)) yield return (name, m.Groups[1].Value);
            }
            else if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                foreach (Match m in CodePhrase().Matches(File.ReadAllText(file)))
                    yield return (name, m.Groups[1].Success ? m.Groups[2].Value.Replace("\"\"", "\"") : Regex.Unescape(m.Groups[2].Value));
            }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Plicbox.Core"))) return dir.FullName;
        throw new DirectoryNotFoundException("Couldn't find the repository from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{local:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlPhrase();

    [GeneratedRegex(@"\bL\.[TF]\(\s*(@)?""((?:[^""\\]|\\.|"""")*)""")]
    private static partial Regex CodePhrase();

    // The same shapes ComposeWindow.Localize fills in.
    [GeneratedRegex(@"\[\[([^\[\]'""<>]+?)\]\]")]
    private static partial Regex PageHtmlPhrase();

    [GeneratedRegex(@"\bT\('([^'\\]*)'")]
    private static partial Regex PageScriptPhrase();

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();
}

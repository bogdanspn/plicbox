namespace Plicbox.Tests;

/// <summary>
/// House style for anything people read (the app, its translations, the website, the README): no middots (U+00B7), no
/// em dashes (U+2014) and no arrows (U+2190, U+2192). Use a comma, a colon, parentheses or a full stop instead, and "&gt;"
/// for a place in the settings (Settings &gt; Mail). Comments follow it too.
/// </summary>
public sealed class WritingStyleTests
{
    private static readonly string[] Extensions = [".cs", ".xaml", ".json", ".html", ".js", ".css", ".md", ".ps1", ".txt"];

    // Reads incoming signatures, which do use them as separators, so it has to name them.
    private static readonly string[] Allowed = [Path.Combine("src", "Plicbox.Mail", "SignatureReader.cs")];

    [Fact]
    public void No_middots_em_dashes_or_arrows()
    {
        var root = RepoRoot();
        var files = new[] { "src", "site", "tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, "*.md"))
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules" or "icon-work"))
            .Where(f => !Allowed.Any(a => f.EndsWith(a, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.EndsWith("THIRD-PARTY-NOTICES.txt", StringComparison.OrdinalIgnoreCase)); // other people's licence texts

        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].IndexOfAny([(char)0xB7, (char)0x2014, (char)0x2190, (char)0x2192]) >= 0)
                    found.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(found.Count == 0, "Middots, em dashes or arrows (use a comma, colon, parentheses, full stop or >):\n" + string.Join("\n", found));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Plicbox.Core"))) return dir.FullName;
        throw new DirectoryNotFoundException("Couldn't find the repository from " + AppContext.BaseDirectory);
    }
}

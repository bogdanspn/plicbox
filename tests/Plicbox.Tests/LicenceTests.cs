using System.Text.Json;

namespace Plicbox.Tests;

/// <summary>
/// Every package the app ships is credited in THIRD-PARTY-NOTICES.txt at the version shipped (their licences ask for
/// it), and the licence files that travel with the app are there.
/// </summary>
public sealed class LicenceTests
{
    [Fact]
    public void Every_shipped_package_is_in_the_third_party_notices()
    {
        var root = RepoRoot();
        var assets = Path.Combine(root, "src", "Plicbox.App", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), "Build the app first (no project.assets.json).");
        var notices = File.ReadAllText(Path.Combine(root, "src", "Plicbox.App", "THIRD-PARTY-NOTICES.txt"));

        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        var target = json.RootElement.GetProperty("targets").EnumerateObject().First(t => !t.Name.Contains('/'));
        var missing = target.Value.EnumerateObject()
            .Where(p => p.Value.GetProperty("type").GetString() == "package")
            .Select(p => p.Name.Replace('/', ' ')) // "MailKit/4.18.1" -> "MailKit 4.18.1", as the notices list it
            .Where(name => !notices.Contains(name + "  (", StringComparison.Ordinal) && !notices.Contains(name + "\n", StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, "Not in THIRD-PARTY-NOTICES.txt (run licenses\\update-notices.ps1):\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData("LICENSE", "GNU GENERAL PUBLIC LICENSE")]
    [InlineData("NOTICE.md", "section 7")]
    public void The_licence_files_are_in_place(string file, string expected) =>
        Assert.Contains(expected, File.ReadAllText(Path.Combine(RepoRoot(), file)));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Plicbox.Core"))) return dir.FullName;
        throw new DirectoryNotFoundException("Couldn't find the repository from " + AppContext.BaseDirectory);
    }
}

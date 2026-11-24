using Plicbox.Core;
using Plicbox.Mail;
using Plicbox.Storage;
using MimeKit;

namespace Plicbox.Tests;

public sealed class AliasTests
{
    private static readonly Account Me = new()
    {
        Id = 1, DisplayName = "Bogdan", Email = "contact@example.ro", Username = "contact@example.ro", ImapHost = "i", SmtpHost = "s",
        AliasesJson = AccountJson.Aliases([new MailAlias("Bogdan R", "contact@example.com"), new MailAlias("Noreply", "noreply@example.net")]),
    };

    private static MimeMessage SentTo(string address)
    {
        var m = new MimeMessage { Subject = "Hi", MessageId = "x@y" };
        m.From.Add(new MailboxAddress("Ana", "ana@example.org"));
        m.To.Add(MailboxAddress.Parse(address));
        m.Cc.Add(MailboxAddress.Parse("noreply@example.net"));
        m.Body = new TextPart("plain") { Text = "hello" };
        return m;
    }

    [Fact]
    public void Identities_list_own_address_first_then_aliases()
    {
        Assert.Equal(["contact@example.ro", "contact@example.com", "noreply@example.net"], Me.Identities.Select(i => i.Address));
    }

    [Fact]
    public void Reply_comes_from_the_alias_the_mail_was_sent_to_and_never_copies_my_aliases()
    {
        var d = Composing.Build(ComposeMode.ReplyAll, SentTo("contact@example.com"), Me, new MessageRenderer());
        Assert.Equal("contact@example.com", d.FromAddress);
        Assert.DoesNotContain("noreply@example.net", d.Cc);
        Assert.DoesNotContain("contact@example.com", d.To);

        var msg = Composing.ToMimeMessage(Me, d, d.To, d.Cc, "", d.Subject, "<p>ok</p>", []);
        var from = (MailboxAddress)msg.From[0];
        Assert.Equal(("Bogdan R", "contact@example.com"), (from.Name, from.Address));
    }

    [Fact]
    public void New_mail_comes_from_the_starred_alias_and_replies_still_from_the_one_written_to()
    {
        Assert.Equal("contact@example.ro", Composing.New(Me).FromAddress);
        Assert.DoesNotContain("IsDefault", Me.AliasesJson); // nothing changes for accounts without one

        var starred = Me with { AliasesJson = AccountJson.Aliases([new MailAlias("Bogdan R", "contact@example.com", true), new MailAlias("Noreply", "noreply@example.net")]) };
        Assert.Equal("contact@example.com", Composing.New(starred).FromAddress);
        Assert.Equal("contact@example.ro", Composing.Build(ComposeMode.Reply, SentTo("contact@example.ro"), starred, new MessageRenderer()).FromAddress);

        var toAList = SentTo("list@example.org");
        toAList.Cc.Clear();
        Assert.Equal("contact@example.com", Composing.Build(ComposeMode.Reply, toAList, starred, new MessageRenderer()).FromAddress);

        var sent = Composing.ToMimeMessage(starred, new ComposeDraft(), "a@example.org", "", "", "s", "<p>x</p>", []);
        Assert.Equal("contact@example.com", sent.From.Mailboxes.Single().Address);
    }

    [Fact]
    public void Account_bcc_copy_is_added_once()
    {
        var account = Me with { BccAddress = "archive@example.ro" };
        var msg = Composing.ToMimeMessage(account, new ComposeDraft(), "a@example.org", "", "archive@example.ro", "s", "<p>x</p>", []);
        Assert.Single(msg.Bcc.Mailboxes);
    }

    [Fact]
    public void Gmail_send_as_parsing_skips_primary_and_unverified()
    {
        const string json = """
            {"sendAs":[
              {"sendAsEmail":"contact@example.ro","displayName":"","isPrimary":true},
              {"sendAsEmail":"contact@example.com","displayName":"Bogdan Rosu","verificationStatus":"accepted","isDefault":true},
              {"sendAsEmail":"pending@example.com","displayName":"P","verificationStatus":"pending"},
              {"sendAsEmail":"noreply@example.net","displayName":"Matrix Noreply"}
            ]}
            """;
        Assert.Equal(["contact@example.com", "noreply@example.net"], GmailAliases.Parse(json).Select(a => a.Address));
        Assert.Equal([true, false], GmailAliases.Parse(json).Select(a => a.IsDefault));
    }

    [Fact]
    public void Corrupt_account_json_degrades_to_empty()
    {
        var account = Me with { AliasesJson = "{not json", IgnoredPathsJson = "[1,", FolderOverridesJson = "oops" };
        Assert.Empty(account.Aliases);
        Assert.Empty(account.IgnoredPaths);
        Assert.Empty(account.FolderOverrides);
    }

    [Theory]
    [InlineData("Archive/2019", true)]
    [InlineData("Archive", true)]
    [InlineData("Archived", false)]
    [InlineData("Inbox/Archive", false)]
    public void Ignored_paths_cover_subfolders_only(string folder, bool ignored) =>
        Assert.Equal(ignored, FolderRoles.IsIgnored(folder, '/', ["Archive"]));
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid() + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Round_trips_and_raises_changed()
    {
        var store = new JsonSettingsStore(_path);
        Assert.True(store.Current.CloseToTray); // defaults when no file exists
        AppSettings? seen = null;
        store.Changed += s => seen = s;

        store.Save(store.Current with { SyncIntervalMinutes = 15, StartupView = "inbox:2" });

        Assert.Equal(15, seen?.SyncIntervalMinutes);
        Assert.Equal("inbox:2", new JsonSettingsStore(_path).Current.StartupView);
    }

    [Fact]
    public void Folder_preferences_round_trip()
    {
        var store = new JsonSettingsStore(_path);
        var key = FolderPrefs.Key(3, "Projects/2026");
        store.Save(store.Current with
        {
            Folders = new() { [key] = new FolderPrefs { Visibility = FolderVisibility.ShowIfUnread, Favorite = true, Color = "#D13438" } },
        });

        var loaded = new JsonSettingsStore(_path).Current.Folders[key];
        Assert.Equal((FolderVisibility.ShowIfUnread, true, "#D13438"), (loaded.Visibility, loaded.Favorite, loaded.Color));
        Assert.False(loaded.IsDefault);
        Assert.True(new FolderPrefs().IsDefault);
    }

    [Fact]
    public void Unreadable_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ broken");
        Assert.Equal(5, new JsonSettingsStore(_path).Current.SyncIntervalMinutes);
    }
}

using Plicbox.Core;
using Plicbox.Mail;
using Plicbox.Storage;

namespace Plicbox.Tests;

public sealed class SignatureReaderTests
{
    [Fact]
    public void Reads_a_classic_signature_above_the_quote()
    {
        const string body = """
            Hi Bogdan,

            Sounds good, let's do Thursday.

            Best regards,
            Mia Chen
            Senior Product Designer | Contoso
            M: +49 151 2345 6789
            T: +49 (0)30 123 456-0
            Fax: +49 30 123 456-99
            www.contoso.com

            On Tue, 14 Oct 2026 at 10:02, Bogdan Rosu <bogdan@example.com> wrote:
            > Can we meet this week? Call me at +40 721 000 000
            """;
        var facts = SignatureReader.Read(body, "Mia Chen", "mia@contoso.com");
        Assert.Equal("Senior Product Designer", facts.JobTitle);
        Assert.Equal("Contoso", facts.Company);
        Assert.Equal([("+49 151 2345 6789", "mobile"), ("+49 (0)30 123 456-0", "work")], facts.Phones);  // not the fax, not the quote
        Assert.Equal(["www.contoso.com"], facts.Websites);
    }

    [Fact]
    public void Uses_the_dash_dash_separator_and_title_at_company()
    {
        const string body = "Here are the files.\n\n-- \nDiego Ruiz\nHead of Engineering at Acme Robotics\nPhone 030 555 1234 5\nhttps://acme-robotics.example/team";
        var facts = SignatureReader.Read(body, "Diego Ruiz", "diego@acme-robotics.example");
        Assert.Equal("Head of Engineering", facts.JobTitle);
        Assert.Null(facts.Company); // "Acme Robotics" doesn't contain "acme-robotics"
        Assert.Single(facts.Phones);
        Assert.Equal(["https://acme-robotics.example/team"], facts.Websites);
    }

    [Fact]
    public void Free_mail_and_plain_text_give_nothing_made_up()
    {
        var facts = SignatureReader.Read("Thanks for the photos! See you on Sunday.\nAna", "Ana Popescu", "ana@gmail.com");
        Assert.True(facts.IsEmpty);
    }
}

public sealed class AddressesTests
{
    [Fact]
    public void Parses_names_with_commas_and_bare_addresses()
    {
        var list = Addresses.Parse("\"Doe, John\" <j@y.org>, Ana Popescu <ana@x.com>; bob@z.net");
        Assert.Equal([("Doe, John", "j@y.org"), ("Ana Popescu", "ana@x.com"), ("", "bob@z.net")], list);
        Assert.Equal("\"Doe, John\" <j@y.org>", Addresses.Format("Doe, John", "j@y.org"));
    }

    [Theory]
    [InlineData("ana@shop.example", "Ana@Shop.example", true)]
    [InlineData("ana@shop.example", "anna@shop.example", false)]   // exact addresses only
    [InlineData("@shop.example", "news@shop.example", true)]
    [InlineData("@shop.example", "news@mail.shop.example", true)]  // subdomains too
    [InlineData("@shop.example", "news@myshop.example", false)]    // but not look-alike domains
    public void Sender_list_entries(string entry, string address, bool covered) => Assert.Equal(covered, Addresses.Covers(entry, address));

    [Theory]
    [InlineData("  Shop.Example ", "@shop.example")]
    [InlineData("*@shop.example", "@shop.example")]
    [InlineData("<Ana@Shop.example>", "ana@shop.example")]
    [InlineData("nonsense", null)]
    [InlineData("@x", null)]
    public void Sender_list_input_is_tidied(string typed, string? expected) => Assert.Equal(expected, Addresses.NormalizeEntry(typed));
}

public sealed class ContactRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly ContactRepository _contacts;
    private readonly MessageRepository _messages;
    private readonly long _accountId;
    private readonly long _inbox;
    private readonly long _sent;

    public ContactRepositoryTests()
    {
        _db = new Database(Path.Combine(_dir, "c.db"));
        _db.Migrate();
        _contacts = new ContactRepository(_db);
        _messages = new MessageRepository(_db);
        _accountId = new AccountRepository(_db).AddAsync(new Account
        {
            DisplayName = "T", Email = "me@example.com", Username = "t", ImapHost = "i", SmtpHost = "s",
        }).GetAwaiter().GetResult();
        var folders = new FolderRepository(_db);
        _inbox = folders.UpsertAsync(new Folder { AccountId = _accountId, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox }).GetAwaiter().GetResult();
        _sent = folders.UpsertAsync(new Folder { AccountId = _accountId, Path = "Sent", Name = "Sent", Role = FolderRole.Sent }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task People_stats_count_both_directions()
    {
        var now = DateTimeOffset.UtcNow;
        await _messages.UpsertAsync(new MessageSummary { AccountId = _accountId, FolderId = _inbox, Uid = 1, Subject = "Hi", FromName = "Ana", FromAddress = "Ana@X.com", Date = now.AddDays(-10) });
        await _messages.UpsertAsync(new MessageSummary { AccountId = _accountId, FolderId = _inbox, Uid = 2, Subject = "Again", FromName = "Ana", FromAddress = "ana@x.com", Date = now.AddDays(-2) });
        await _messages.UpsertAsync(new MessageSummary
        {
            AccountId = _accountId, FolderId = _sent, Uid = 3, Subject = "Re: Hi", FromAddress = "me@example.com", Date = now.AddDays(-1),
            Recipients = "Ana Popescu <ana@x.com>, \"Doe, John\" <j@y.org>",
        });

        var stats = (await _contacts.GetPeopleStatsAsync()).ToDictionary(p => p.Address);
        Assert.Equal((2, 1, 3), (stats["ana@x.com"].Received, stats["ana@x.com"].Sent, stats["ana@x.com"].Recent));
        Assert.Equal(("Doe, John", 0, 1), (stats["j@y.org"].Name, stats["j@y.org"].Received, stats["j@y.org"].Sent));

        var talks = await _contacts.GetConversationsWithAsync("ana@x.com", 10);
        Assert.Equal(["Re: Hi", "Again", "Hi"], talks.Select(t => t.Subject));
    }

    [Fact]
    public async Task Contacts_round_trip_and_full_sync_replaces()
    {
        var id = await _contacts.UpsertAsync(new Contact
        {
            AccountId = _accountId, RemoteId = "people/c1", Source = ContactSource.Google, DisplayName = "Ana Popescu",
            Emails = [new("ana@x.com", "home")], Phones = [new("+40 721 000 000", "mobile")], Birthday = new DateOnly(Contact.NoYear, 2, 29),
            IsFavorite = true, Updated = DateTimeOffset.UtcNow,
        });
        var ana = await _contacts.GetAsync(id);
        Assert.Equal(("ana@x.com", "mobile", false, true), (ana!.PrimaryEmail, ana.Phones[0].Label, ana.BirthdayHasYear, ana.IsFavorite));

        await _contacts.ReplaceAsync(_accountId, ContactSource.Google, [new Contact { RemoteId = "people/c2", DisplayName = "Bob" }]);
        Assert.Equal(["Bob"], (await _contacts.GetAllAsync()).Select(c => c.DisplayName));
    }
}

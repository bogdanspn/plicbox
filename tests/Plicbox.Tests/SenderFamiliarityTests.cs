using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.Tests;

/// <summary>Senders measured against who you really correspond with: look-alike domains, borrowed names, first messages.</summary>
public sealed class SenderFamiliarityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static readonly KnownCorrespondents Known = new(
        contacts:
        [
            new Contact { DisplayName = "Ana Popescu", Emails = [new LabeledValue("ana@family.example")] },
            new Contact { DisplayName = "Contoso Bank", Emails = [new LabeledValue("service@contosobank.com")] },
        ],
        stats:
        [
            new PersonStats { Address = "mihai@fabrikam.com", Name = "Mihai Ionescu", Sent = 4, Received = 6, First = Now.AddYears(-1) },
            new PersonStats { Address = "news@shop.example", Name = "Shop News", Received = 20, First = Now.AddMonths(-6) },
            new PersonStats { Address = "friend@gmail.com", Name = "Friend", Sent = 1 },
        ],
        own: [new MailAlias("Bogdan Rosu", "me@northwind.com")]);

    private static FamiliarityVerdict Check(string address, string? name = null, PersonStats? _ = null) => Known.Check(address, name, Now);

    [Fact]
    public void People_you_know_are_quiet()
    {
        Assert.Same(FamiliarityVerdict.Nothing, Check("ana@family.example", "Ana Popescu"));
        Assert.Same(FamiliarityVerdict.Nothing, Check("mihai@fabrikam.com", "Mihai Ionescu"));
        Assert.Same(FamiliarityVerdict.Nothing, Check("news@shop.example", "Shop News")); // received before
    }

    [Theory]
    [InlineData("billing@contoso-bank.com", "contosobank.com")]   // a hyphen added
    [InlineData("service@contosobamk.com", "contosobank.com")]    // one letter changed
    [InlineData("service@cotnosobank.com", "contosobank.com")]    // two swapped
    [InlineData("ceo@rnorthwind.com", "northwind.com")]           // your own company, "rn"
    [InlineData("it@n0rthwind.com", "northwind.com")]             // "0" for "o"
    [InlineData("mihai@fabrikam.co", "fabrikam.com")]             // a mistyped ".com"
    public void Look_alikes_of_domains_you_know_are_danger(string from, string imitated)
    {
        var v = Check(from, "Someone");
        Assert.Equal(SenderRisk.Danger, v.Risk);
        Assert.Contains(imitated, v.Reasons[0]);
        Assert.True(v.FirstMessage);
        Assert.Contains("first message", v.Reasons[^1]);
    }

    [Theory]
    [InlineData("hello@fabrikam.de")]       // the same name elsewhere: usually the same company
    [InlineData("someone@mail.com")]        // a free mail provider, not a fake gmail.com
    [InlineData("info@contoso.org")]        // a different name
    [InlineData("team@ibm.com")]            // too short to call a typo
    public void Different_but_honest_domains_are_not_flagged(string from) =>
        Assert.Equal(SenderRisk.None, Check(from).Risk);

    [Fact]
    public void A_known_name_on_a_new_address_is_caution()
    {
        var v = Check("ana.popescu.urgent@gmail.com", "Ana Popescu");
        Assert.Equal(SenderRisk.Caution, v.Risk);
        Assert.Contains("ana@family.example", v.Reasons[0]);

        // Accents and spacing don't hide it.
        Assert.Equal(SenderRisk.Caution, Check("x@y.example", "  ÁNA   popescu ").Risk);
        // Your own name from someone else's address.
        Assert.Contains("your own name", Check("boss@freemail.example", "Bogdan Rosu").Reasons[0]);
        // A colleague's second address on the same domain is fine.
        Assert.Equal(SenderRisk.None, Check("ana.p@family.example", "Ana Popescu").Risk);
        // Generic names and single first names say nothing.
        Assert.Equal(SenderRisk.None, Check("support@other.example", "Customer Support").Risk);
        Assert.Equal(SenderRisk.None, Check("ana@other.example", "Ana").Risk);
    }

    [Fact]
    public void A_first_message_is_noted_quietly()
    {
        var v = Check("hello@newshop.example", "New Shop");
        Assert.Equal(SenderRisk.None, v.Risk);
        Assert.True(v.FirstMessage);
        Assert.Empty(v.Reasons);

        // Already counted in the statistics (this message), or one of several.
        var withThis = new KnownCorrespondents([], [new PersonStats { Address = "a@new.example", Received = 1, First = Now }], []);
        Assert.True(withThis.Check("a@new.example", null, Now).FirstMessage);
        var earlier = new KnownCorrespondents([], [new PersonStats { Address = "a@new.example", Received = 1, First = Now.AddDays(-3) }], []);
        Assert.False(earlier.Check("a@new.example", null, Now).FirstMessage);
    }
}

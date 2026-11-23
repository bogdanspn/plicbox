using Plicbox.Mail;
using MimeKit;

namespace Plicbox.Tests;

public sealed class SenderCheckTests
{
    private static MimeMessage Message(string from, string? auth = null, string? replyTo = null, string? listId = null, string? receivedSpf = null)
    {
        var m = new MimeMessage { Subject = "Hi" };
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse("me@example.com"));
        if (replyTo is not null) m.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        if (listId is not null) m.Headers.Add(HeaderId.ListId, listId);
        if (receivedSpf is not null) m.Headers.Add("Received-SPF", receivedSpf);
        if (auth is not null) m.Headers.Insert(0, new Header(HeaderId.AuthenticationResults, auth));
        m.Body = new TextPart("plain") { Text = "x" };
        return m;
    }

    [Fact]
    public void Authenticated_mail_is_fine()
    {
        var m = Message("Bank <news@bank.example>",
            "mx.google.com; dkim=pass header.i=@bank.example header.s=s1 header.b=abc; spf=pass (google.com: domain of news@bank.example designates 1.2.3.4 as permitted sender) smtp.mailfrom=news@bank.example; dmarc=pass (p=REJECT sp=REJECT dis=NONE) header.from=bank.example");
        Assert.Equal(SenderRisk.None, SenderCheck.Check(m).Risk);
    }

    [Fact]
    public void Dmarc_failure_with_a_reject_policy_is_a_danger()
    {
        var m = Message("PayPal <service@paypal.example>",
            "mx.google.com; dkim=none; spf=fail (google.com: domain of service@paypal.example does not designate 6.6.6.6 as permitted sender) smtp.mailfrom=service@paypal.example; dmarc=fail (p=REJECT sp=REJECT dis=REJECT) header.from=paypal.example");
        var verdict = SenderCheck.Check(m);
        Assert.Equal(SenderRisk.Danger, verdict.Risk);
        Assert.Contains(verdict.Reasons, r => r.Contains("DMARC") && r.Contains("paypal.example"));
    }

    [Fact]
    public void Office365_reject_action_counts_as_strict()
    {
        var m = Message("ceo@corp.example", "spf=fail (sender IP is 6.6.6.6) smtp.mailfrom=corp.example; dkim=none (message not signed) header.d=none;dmarc=fail action=oreject header.from=corp.example;compauth=fail reason=000");
        Assert.Equal(SenderRisk.Danger, SenderCheck.Check(m).Risk);
    }

    [Fact]
    public void Dmarc_failure_without_policy_or_on_a_mailing_list_is_only_a_caution()
    {
        const string auth = "mx.google.com; dkim=fail header.i=@list.example; spf=pass smtp.mailfrom=bounce@lists.example; dmarc=fail (p=NONE sp=NONE dis=NONE) header.from=list.example";
        Assert.Equal(SenderRisk.Caution, SenderCheck.Check(Message("a@list.example", auth)).Risk);
        var strictOnList = auth.Replace("p=NONE sp=NONE dis=NONE", "p=REJECT sp=REJECT dis=NONE");
        Assert.Equal(SenderRisk.Caution, SenderCheck.Check(Message("a@list.example", strictOnList, listId: "<dev.list.example>")).Risk);
    }

    [Fact]
    public void Spf_failure_without_a_signature_is_a_caution()
    {
        var m = Message("shop@store.example", "mx.example.net; spf=softfail smtp.mailfrom=store.example; dkim=none");
        var verdict = SenderCheck.Check(m);
        Assert.Equal(SenderRisk.Caution, verdict.Risk);
        Assert.Contains(verdict.Reasons, r => r.Contains("SPF"));
    }

    [Fact]
    public void Received_spf_is_used_when_there_are_no_authentication_results()
    {
        var m = Message("shop@store.example", receivedSpf: "fail (example.net: domain of shop@store.example does not designate 6.6.6.6 as permitted sender)");
        Assert.Equal(SenderRisk.Caution, SenderCheck.Check(m).Risk);
    }

    [Fact]
    public void Only_the_topmost_result_counts()
    {
        var m = Message("ceo@corp.example", "mx.example.net; spf=fail smtp.mailfrom=corp.example; dkim=none; dmarc=fail (p=REJECT) header.from=corp.example");
        // A forged "pass" further down (written by the sender) changes nothing.
        m.Headers.Add(HeaderId.AuthenticationResults, "fake.example; dmarc=pass header.from=corp.example");
        Assert.Equal(SenderRisk.Danger, SenderCheck.Check(m).Risk);
    }

    [Fact]
    public void Mail_claiming_to_be_from_yourself_is_called_out()
    {
        var m = Message("me@example.com", "mx.example.net; spf=fail smtp.mailfrom=example.com; dkim=none; dmarc=fail (p=NONE) header.from=example.com");
        var verdict = SenderCheck.Check(m, ["me@example.com"]);
        Assert.Equal(SenderRisk.Danger, verdict.Risk);
        Assert.Contains(verdict.Reasons, r => r.Contains("your own address"));
    }

    [Fact]
    public void Own_sent_mail_without_headers_is_fine()
    {
        Assert.Equal(SenderRisk.None, SenderCheck.Check(Message("me@example.com"), ["me@example.com"]).Risk);
    }

    [Fact]
    public void A_display_name_showing_another_address_is_a_danger()
    {
        var verdict = SenderCheck.Check(Message("\"support@bank.example\" <x@elsewhere.example>"));
        Assert.Equal(SenderRisk.Danger, verdict.Risk);
        Assert.Contains(verdict.Reasons, r => r.Contains("support@bank.example") && r.Contains("x@elsewhere.example"));
        Assert.Equal(SenderRisk.None, SenderCheck.Check(Message("\"ana@example.org\" <ana@example.org>")).Risk);
    }

    [Fact]
    public void Look_alike_domains_are_called_out()
    {
        var verdict = SenderCheck.Check(Message("service@xn--pypal-4ve.com"));
        Assert.Equal(SenderRisk.Danger, verdict.Risk);
        Assert.Contains(verdict.Reasons, r => r.Contains("xn--pypal-4ve.com") && r.Contains("pаypal.com"));
        Assert.Equal(SenderRisk.Danger, SenderCheck.Check(Message("x@\u0430\u0440\u0440\u04CF\u0435.com")).Risk); // all-Cyrillic "аррӏе"
        Assert.Equal(SenderRisk.None, SenderCheck.Check(Message("info@müller.de")).Risk);
        Assert.Equal(SenderRisk.None, SenderCheck.Check(Message("info@пример.рф")).Risk);
    }

    [Fact]
    public void Replies_diverted_to_a_free_mailbox_are_a_caution()
    {
        Assert.Equal(SenderRisk.Caution, SenderCheck.Check(Message("ceo@corp.example", replyTo: "ceo.corp@gmail.com")).Risk);
        Assert.Equal(SenderRisk.None, SenderCheck.Check(Message("news@corp.example", replyTo: "replies@mailer.example")).Risk);
        Assert.Equal(SenderRisk.None, SenderCheck.Check(Message("ana@gmail.com", replyTo: "ana@outlook.com")).Risk);
    }
}

public sealed class AttachmentSafetyTests
{
    [Theory]
    [InlineData("report.pdf", "report", ".pdf", AttachmentRisk.None)]
    [InlineData("setup.EXE", "setup", ".EXE", AttachmentRisk.Danger)]
    [InlineData("budget.xlsm", "budget", ".xlsm", AttachmentRisk.Caution)]
    [InlineData("Sign in.html", "Sign in", ".html", AttachmentRisk.Caution)]
    [InlineData("README", "README", "", AttachmentRisk.None)]
    [InlineData(".bashrc", ".bashrc", "", AttachmentRisk.None)]
    [InlineData(null, "attachment", "", AttachmentRisk.None)]
    public void Names_split_into_stem_and_extension(string? name, string stem, string extension, AttachmentRisk risk)
    {
        var a = AttachmentSafety.Assess(name);
        Assert.Equal((stem, extension, risk), (a.Stem, a.Extension, a.Risk));
        Assert.Equal(risk != AttachmentRisk.None, a.Warning is not null);
    }

    [Fact]
    public void Double_extensions_say_what_the_file_really_is()
    {
        var a = AttachmentSafety.Assess("Invoice_2041.pdf.exe");
        Assert.Equal(AttachmentRisk.Danger, a.Risk);
        Assert.Equal(("Invoice_2041.pdf", ".exe"), (a.Stem, a.Extension));
        Assert.Contains("Looks like a .pdf", a.Warning);
    }

    [Fact]
    public void Padding_that_pushes_the_extension_out_of_sight_is_removed()
    {
        var a = AttachmentSafety.Assess("scan.pdf                                        .scr");
        Assert.Equal(("scan.pdf", ".scr", AttachmentRisk.Danger), (a.Stem, a.Extension, a.Risk));
    }

    [Fact]
    public void Right_to_left_override_tricks_are_unmasked()
    {
        var a = AttachmentSafety.Assess("invoice\u202Efdp.txt"); // shows as "invoicetxt.pdf"
        Assert.Equal(AttachmentRisk.Danger, a.Risk);
        Assert.Equal(("invoicefdp", ".txt"), (a.Stem, a.Extension));
        Assert.Contains("hidden characters", a.Warning);
    }
}

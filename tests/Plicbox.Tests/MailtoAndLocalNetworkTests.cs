using System.Net;
using System.Text.RegularExpressions;
using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.Tests;

public sealed class MailtoAndLocalNetworkTests
{
    private static readonly Account Me = new() { DisplayName = "Ana", Email = "ana@example.com", Username = "ana", ImapHost = "i", SmtpHost = "s", Signature = "<p>Ana's signature</p>" };

    [Fact]
    public void A_link_fills_in_the_message()
    {
        var link = MailtoLink.Parse("mailto:bob@example.com,mia@example.org?cc=cc@example.org&bcc=b@example.org&to=third@example.org&subject=Hello%20there&body=One%0D%0A%0D%0ATwo")!;
        Assert.Equal("bob@example.com,mia@example.org, third@example.org", link.To);
        Assert.Equal(("cc@example.org", "b@example.org", "Hello there"), (link.Cc, link.Bcc, link.Subject));
        Assert.Equal("One\n\nTwo", link.Body);

        var draft = Composing.New(Me, link);
        Assert.Equal("Hello there", draft.Subject);
        Assert.StartsWith("<div>One</div><div><br></div><div>Two</div>", draft.BodyHtml);
        Assert.Contains("signature", draft.BodyHtml);
    }

    [Theory]
    [InlineData("mailto:ana+news@example.com", "ana+news@example.com")]
    [InlineData("MAILTO:a%40example.com", "a@example.com")]
    [InlineData("mailto:?to=x@example.com", "x@example.com")]
    [InlineData("mailto:", "")]
    public void Addresses_are_read_as_they_are(string link, string to) => Assert.Equal(to, MailtoLink.Parse(link)!.To);

    [Fact]
    public void A_link_cant_bring_html_headers_or_files()
    {
        var link = MailtoLink.Parse("mailto:a@example.com?subject=x%0D%0ABcc:%20spy@example.com&body=%3Cimg%20src=x%20onerror=alert(1)%3E&attach=C:%5Csecret.txt&attachment=C:%5Csecret.txt")!;
        Assert.DoesNotContain('\n', link.Subject);
        Assert.Equal("", link.Bcc);
        Assert.DoesNotContain("<img", link.BodyHtml);
        Assert.Empty(Composing.New(Me, link).ForwardedAttachments);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("--demo")]
    [InlineData(null)]
    public void Anything_else_isnt_a_mail_link(string? link)
    {
        Assert.False(MailtoLink.Is(link));
        Assert.Null(MailtoLink.Parse(link));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.169.1.1", false)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2606:4700::1111", false)]
    public async Task Local_addresses(string address, bool local)
    {
        Assert.Equal(local, LocalNetwork.IsLocal(IPAddress.Parse(address)));
        Assert.Equal(local, await LocalNetwork.IsLocalAsync(address));
    }

    [Fact]
    public async Task Names_are_looked_up_and_unknown_ones_left_alone()
    {
        Assert.True(await LocalNetwork.IsLocalAsync("localhost"));
        Assert.False(await LocalNetwork.IsLocalAsync("no-such-host.invalid"));
    }

    [Theory]
    [InlineData("0.gravatar.com", false)]
    [InlineData("10.example.com", false)]
    [InlineData("127.example.com", false)]
    [InlineData("local.example.com", false)]
    [InlineData("images.example.com", false)]
    [InlineData("10.200.3.4", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.200.1.1", false)]
    [InlineData("192.168.178.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("printer.local", true)]
    [InlineData("[::1]", true)]
    public void The_browsers_rules_stop_local_addresses_only(string host, bool stopped) =>
        Assert.Equal(stopped, LocalNetwork.BrowserRules.Split(", ").Select(r => r.Split(' ')[1])
            .Any(pattern => Regex.IsMatch(host, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$")));
}

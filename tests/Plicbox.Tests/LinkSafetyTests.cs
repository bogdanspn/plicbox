using Plicbox.Mail;

namespace Plicbox.Tests;

public sealed class LinkSafetyTests
{
    [Theory]
    [InlineData("https://neoouija.bandcamp.com/album/oriens-occidens?from=fanpub_fnb&utm_source=album_release", null, "noreply@bandcamp.com", LinkRisk.Safe)]
    [InlineData("https://www.github.com/notifications", "View it on GitHub", "notifications@github.com", LinkRisk.Safe)]
    [InlineData("https://example.org/news", "Read more", "news@shop.example", LinkRisk.Neutral)]
    [InlineData("https://groups.io/g/team", null, null, LinkRisk.Neutral)]          // "ups" is a whole word only
    [InlineData("https://www.pineapple.com/", null, null, LinkRisk.Neutral)]        // and "apple" too
    [InlineData("https://www.paypal.com/signin", "www.paypal.com", "service@paypal.de", LinkRisk.Safe)]
    [InlineData("https://news.bbc.co.uk/x", "bbc.co.uk", "news@bbc.co.uk", LinkRisk.Safe)]
    public void Ordinary_links_are_not_flagged(string url, string? text, string? sender, LinkRisk expected) =>
        Assert.Equal(expected, LinkSafety.Assess(url, text, sender).Risk);

    [Theory]
    [InlineData("https://secure-login.xyz/paypal", "www.paypal.com")]                       // text promises another site
    [InlineData("https://paypal.com.account-verify.net/login", null)]                        // brand as a subdomain
    [InlineData("https://www.paypal.com@evil.example/login", null)]                          // user-info disguise
    [InlineData("http://192.168.10.5/bank/login", null)]                                     // bare IP
    [InlineData("https://xn--pypal-4ve.com/", null)]                                          // Cyrillic "а"
    public void Disguised_links_are_danger(string url, string? text) =>
        Assert.Equal(LinkRisk.Danger, LinkSafety.Assess(url, text, "alerts@paypal.com").Risk);

    [Theory]
    [InlineData("https://bit.ly/3xYz")]
    [InlineData("https://my-invoice.web.app/view")]
    [InlineData("https://docs-share.xyz/file")]
    [InlineData("http://shop.example/checkout")]
    [InlineData("https://paypal-support.com/help")]
    public void Hiding_or_cheap_links_are_caution(string url) =>
        Assert.Equal(LinkRisk.Caution, LinkSafety.Assess(url).Risk);

    [Fact]
    public void Click_trackers_that_carry_the_shown_site_are_not_phishing()
    {
        var v = LinkSafety.Assess("https://click.tracker.example/c?url=https%3A%2F%2Fwww.shop.example%2Fsale", "www.shop.example", "news@shop.example");
        Assert.NotEqual(LinkRisk.Danger, v.Risk);
        Assert.Equal("www.shop.example", v.Destination);
    }

    [Fact]
    public void Links_and_their_text_are_read_from_the_html()
    {
        var links = LinkSafety.ExtractLinks("""<p><a href="https://a.example/x">  Go   there </a> <a href="https://b.example/"><img alt="Logo"></a></p>""");
        Assert.Equal("Go there", links["https://a.example/x"]);
        Assert.Equal("Logo", links["https://b.example/"]);
    }

    [Theory]
    [InlineData("mail.google.com", "google.com")]
    [InlineData("news.bbc.co.uk", "bbc.co.uk")]
    [InlineData("evil.github.io", "evil.github.io")]
    [InlineData("example.com", "example.com")]
    public void Registrable_domain(string host, string expected) => Assert.Equal(expected, LinkSafety.RegistrableDomain(host));
}

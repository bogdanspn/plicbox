using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.Tests;

/// <summary>
/// The downloaded phishing and malware lists: reading them, what an entry covers, and what the link check, the sender
/// warning and rules make of a listed site. (These tests swap <see cref="ThreatLists.Current"/>; the sites they list
/// are made up, so other tests running meanwhile never meet them.)
/// </summary>
public sealed class ThreatListTests : IDisposable
{
    // The two shapes the malware-filter lists come in, plus hosts-file lines and comments.
    private const string Phishing = """
        ! Title: Phishing Domains Blocklist
        ! License: https://gitlab.com/malware-filter/phishing-filter#license
        fake-bank-login.test
        0.0.0.0 hosts-style.test
        ||sites.google.com/view/fake-bank-portal^$all
        ||docs.google.com/forms/d/e/1FAIpQLSfakeform/viewform?usp=sf_link&amp;x=1^$all
        ||cdn.example-shared.test/phish/*/login^$all
        sites.google.com
        google.com
        """;

    private const string Malware = """
        # Title: Online Malicious URL Blocklist
        ||payload-host.test/files/invoice.exe^$all
        bad-downloads.test
        203.0.113.7
        """;

    private readonly ThreatLists _before = ThreatLists.Current;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-threats-" + Guid.NewGuid().ToString("N"));

    public ThreatListTests()
    {
        var lists = new ThreatLists();
        lists.Add(ThreatKind.Phishing, Phishing);
        lists.Add(ThreatKind.Malware, Malware);
        ThreatLists.Current = lists;
    }

    public void Dispose()
    {
        ThreatLists.Current = _before;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ThreatKind Check(string url) => ThreatLists.Current.Check(new Uri(url));

    [Fact]
    public void A_listed_site_covers_its_pages_and_subdomains()
    {
        Assert.Equal(ThreatKind.Phishing, Check("https://fake-bank-login.test/"));
        Assert.Equal(ThreatKind.Phishing, Check("https://secure.FAKE-BANK-LOGIN.test/verify?id=1"));
        Assert.Equal(ThreatKind.Phishing, Check("http://hosts-style.test/x"));
        Assert.Equal(ThreatKind.Malware, Check("https://bad-downloads.test/a.zip"));
        Assert.Equal(ThreatKind.Malware, Check("http://203.0.113.7/run.exe"));
        Assert.Equal(ThreatKind.None, Check("https://not-fake-bank-login.test/")); // a different name, not a subdomain
        Assert.Equal(ThreatKind.None, Check("https://test/"));
    }

    [Fact]
    public void A_listed_page_on_a_shared_site_flags_that_page_only()
    {
        Assert.Equal(ThreatKind.Phishing, Check("https://sites.google.com/view/fake-bank-portal"));
        Assert.Equal(ThreatKind.Phishing, Check("https://sites.google.com/view/fake-bank-portal/home"));
        Assert.Equal(ThreatKind.None, Check("https://sites.google.com/view/fake-bank-portal-of-a-real-club")); // "^": the name ends there
        Assert.Equal(ThreatKind.None, Check("https://sites.google.com/view/our-family-recipes"));
        Assert.Equal(ThreatKind.Phishing, Check("https://docs.google.com/forms/d/e/1FAIpQLSfakeform/viewform?usp=sf_link&x=1"));
        Assert.Equal(ThreatKind.Phishing, Check("https://cdn.example-shared.test/phish/abc/login.html")); // up to the "*"
        Assert.Equal(ThreatKind.Malware, Check("https://payload-host.test/files/invoice.exe"));
        Assert.Equal(ThreatKind.None, Check("https://payload-host.test/files/readme.txt"));
    }

    [Fact]
    public void Whole_site_entries_for_shared_platforms_are_ignored()
    {
        // Lists sometimes name all of sites.google.com or google.com: that would flag everyone's pages.
        Assert.Equal(ThreatKind.None, Check("https://sites.google.com/view/our-family-recipes"));
        Assert.Equal(ThreatKind.None, Check("https://www.google.com/search?q=x"));
        Assert.Equal(ThreatKind.None, ThreatLists.Current.CheckDomain("google.com"));
    }

    [Fact]
    public void Links_to_listed_sites_are_danger_and_say_why()
    {
        var v = LinkSafety.Assess("https://secure.fake-bank-login.test/verify", "Verify your account", "service@bank.example");
        Assert.Equal(LinkRisk.Danger, v.Risk);
        Assert.Equal(ThreatKind.Phishing, v.Listed);
        Assert.Equal("Reported phishing site", v.Headline);
        Assert.Contains("phishing", v.Reasons[0]); // the reason shown first in the hover bar

        var malware = LinkSafety.Assess("https://payload-host.test/files/invoice.exe");
        Assert.Equal((LinkRisk.Danger, ThreatKind.Malware), (malware.Risk, malware.Listed));

        // A click tracker passing through to a listed site.
        var tracked = LinkSafety.Assess("https://click.mailer.example/c?url=https%3A%2F%2Ffake-bank-login.test%2Fx");
        Assert.Equal(ThreatKind.Phishing, tracked.Listed);

        Assert.Equal(ThreatKind.None, LinkSafety.Assess("https://www.example.org/news").Listed);
    }

    [Fact]
    public void Nothing_is_flagged_while_the_lists_are_empty()
    {
        ThreatLists.Current = ThreatLists.Empty;
        Assert.Equal(ThreatKind.None, Check("https://fake-bank-login.test/"));
        Assert.NotEqual(LinkRisk.Danger, LinkSafety.Assess("https://fake-bank-login.test/").Risk);
    }

    private static MimeMessage Message(string from, string html, string? text = null)
    {
        var body = new BodyBuilder { HtmlBody = html, TextBody = text };
        var m = new MimeMessage { Subject = "Your account" };
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse("you@example.com"));
        m.Body = body.ToMessageBody();
        return m;
    }

    [Fact]
    public void A_message_linking_to_a_listed_site_gets_a_warning()
    {
        var verdict = SenderCheck.Check(Message("service@bank.example", """<p>Please <a href="https://fake-bank-login.test/x">sign in</a>.</p>"""));
        Assert.Equal(SenderRisk.Danger, verdict.Risk);
        Assert.Equal("This message links to a reported phishing site", verdict.Title);
        Assert.Contains("fake-bank-login.test", verdict.Reasons[0]);

        var fromListed = SenderCheck.Check(Message("alerts@bad-downloads.test", "<p>Hello</p>"));
        Assert.Equal("This message comes from a site that spreads malware", fromListed.Title);

        // Written out in plain text, too.
        var plain = SenderCheck.Check(Message("a@friend.example", "<p>Hi</p>", "Get it at https://payload-host.test/files/invoice.exe."));
        Assert.Equal(SenderRisk.Danger, plain.Risk);

        Assert.Same(SenderVerdict.Safe, SenderCheck.Check(Message("a@friend.example", """<a href="https://www.example.org/">photos</a>""")));
    }

    [Fact]
    public void Rules_can_test_the_lists()
    {
        var rule = new MailRule
        {
            Conditions = [new RuleCondition { Field = RuleField.KnownThreat }],
            Actions = [new RuleAction { Kind = RuleActionKind.MoveToJunk }],
        };
        var mail = new MessageSummary { AccountId = 1, FromAddress = "news@shop.example", Subject = "Sale" };
        Assert.True(RuleMatcher.NeedsBody(rule));
        Assert.True(RuleMatcher.Matches(rule, mail, "", ["https://shop.example/", "https://sites.google.com/view/fake-bank-portal"]));
        Assert.False(RuleMatcher.Matches(rule, mail, "", ["https://shop.example/"]));
        Assert.False(RuleMatcher.Matches(rule, mail, null, null));          // not downloaded: only the sender counts
        Assert.True(RuleMatcher.Matches(rule, mail with { FromAddress = "x@fake-bank-login.test" }, null, null));

        var negated = rule with { Conditions = [new RuleCondition { Field = RuleField.KnownThreat, Operator = RuleOperator.DoesNotContain }] };
        Assert.True(RuleMatcher.Matches(negated, mail, "", ["https://shop.example/"]));

        Assert.Equal("On a phishing or malware list → move to junk", RuleMatcher.Summary(rule, _ => null));
    }

    // ───────── downloading ─────────

    private sealed class FakeWeb : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.ToString());
            return Task.FromResult(Answer(request));
        }
    }

    private static string BigList(string prefix) =>
        string.Join("\n", Enumerable.Range(0, 600).Select(i => $"{prefix}-{i}.test"));

    private static HttpResponseMessage Ok(string text, string etag)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
        r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    }

    [Fact]
    public async Task Lists_download_then_only_come_again_when_changed()
    {
        var web = new FakeWeb();
        var updater = new ThreatListUpdater(new HttpClient(web), _dir, NullLogger<ThreatListUpdater>.Instance);
        web.Answer = r => r.Headers.IfNoneMatch.Count > 0 ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Ok(BigList(r.RequestUri!.AbsolutePath.Contains("phishing") ? "phish" : "malw"), "\"v1\"");

        Assert.True(await updater.RefreshAsync());
        Assert.Equal(1200, ThreatLists.Current.Count);
        Assert.Equal(ThreatKind.Phishing, Check("https://phish-12.test/"));
        Assert.Equal(ThreatKind.Malware, Check("https://malw-12.test/"));
        Assert.NotNull(updater.LastChecked);

        // Unchanged: asked with the version tag, nothing downloaded, the lists stay.
        web.Asked.Clear();
        Assert.True(await updater.RefreshAsync());
        Assert.Equal(2, web.Asked.Count);
        Assert.Equal(1200, ThreatLists.Current.Count);

        // Loaded from disk on the next start.
        ThreatLists.Current = ThreatLists.Empty;
        updater.Load();
        Assert.Equal(1200, ThreatLists.Current.Count);
    }

    [Fact]
    public async Task A_broken_download_keeps_the_copy_we_have_and_mirrors_are_tried()
    {
        var web = new FakeWeb();
        var updater = new ThreatListUpdater(new HttpClient(web), _dir, NullLogger<ThreatListUpdater>.Instance);
        // The first address is down; the mirror answers.
        web.Answer = r => r.RequestUri!.Host.Contains("gitlab") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(BigList("site"), "\"a\"");
        Assert.True(await updater.RefreshAsync());
        Assert.Equal(ThreatKind.Phishing, Check("https://site-1.test/"));

        // An error page dressed as a list (too few entries) doesn't replace it.
        web.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Rate limited</html>") };
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "phishing.txt"), DateTime.UtcNow.AddDays(-1));
        await updater.RefreshAsync();
        updater.Load();
        Assert.Equal(ThreatKind.Phishing, Check("https://site-1.test/"));

        // Offline: says so, and the lists stay.
        web.Answer = _ => throw new HttpRequestException("offline");
        Assert.False(await updater.RefreshAsync());
        Assert.Equal(ThreatKind.Phishing, Check("https://site-1.test/"));
    }

    [Fact]
    public void Turning_the_setting_off_forgets_the_lists()
    {
        var updater = new ThreatListUpdater(new HttpClient(new FakeWeb()), _dir, NullLogger<ThreatListUpdater>.Instance);
        updater.SetEnabled(false);
        Assert.True(ThreatLists.Current.IsEmpty);
    }
}

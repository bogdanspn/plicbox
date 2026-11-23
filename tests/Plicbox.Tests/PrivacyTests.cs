using Plicbox.Mail;
using Plicbox.Storage;
using MimeKit;

namespace Plicbox.Tests;

public sealed class PrivacyTests
{
    private readonly MessageRenderer _renderer = new();

    private static MimeMessage Html(string html) => new() { Body = new TextPart("html") { Text = html } };

    [Theory]
    [InlineData("https://u123.ct.sendgrid.net/wf/open?upn=abc", null, null, null, "SendGrid")]
    [InlineData("https://x.us1.list-manage.com/track/open.php?u=1", null, null, null, "Mailchimp")]
    [InlineData("https://cdn.example.com/pixel.gif", "1", "1", null, Trackers.Generic)]
    [InlineData("https://cdn.example.com/p.gif", null, null, "display: none", Trackers.Generic)]
    [InlineData("https://cdn.example.com/hero.jpg", "600", "300", null, null)]
    [InlineData("data:image/png;base64,AAAA", "1", "1", null, null)] // inline data can't phone home
    // Meant to be seen: a newsletter picture on the service's own host, a responsive mail's mobile version (hidden
    // until a media query brings it out).
    [InlineData("https://gallery.mailchimp.com/abc/images/hero.jpg", "600", null, null, null)]
    [InlineData("https://cdn.example.com/hero-mobile.png", "302", "auto", "display: none;", null)]
    public void Identifies_trackers(string src, string? w, string? h, string? style, string? expected) =>
        Assert.Equal(expected, Trackers.Identify(src, w, h, style));

    [Fact]
    public void A_spacer_used_throughout_the_layout_is_not_a_tracker()
    {
        // Google's newsletters space their layout with the same 1×1 GIF, a dozen times over; a pixel that reports the
        // opening has an address of its own, once.
        Assert.Null(Trackers.Identify("https://www.gstatic.com/gumdrop/files/spacer-11.gif", "1", "1", "display: block;", uses: 17));
        var html = _renderer.Render(Html("""
            <img src="https://x.example/spacer.gif" width="1" height="1"><p>a</p><img src="https://x.example/spacer.gif" width="1" height="1">
            <img src="https://x.example/open?id=8f3a" width="1" height="1">
            """), allowRemoteContent: true);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html.Html, "spacer.gif").Count);
        Assert.DoesNotContain("open?id", html.Html);
    }

    [Fact]
    public void Quoting_leaves_tracking_pixels_out()
    {
        var quoted = _renderer.SanitizeForQuote(Html(
            """<p>Sale</p><img src="https://cdn.example.com/hero.jpg" width="600" height="300"><img src="https://u1.ct.sendgrid.net/wf/open?upn=abc">"""));
        Assert.Contains("hero.jpg", quoted);
        Assert.DoesNotContain("sendgrid", quoted);
    }

    [Fact]
    public void Web_pictures_are_placeholders_in_the_editor_and_go_out_as_they_were()
    {
        const string html = """<p>Hi</p><img src="https://cdn.example.com/hero.jpg" width="600" style="display:block"><img src="data:image/png;base64,iVBORw0KGgo=">""";
        var held = Composing.HoldRemotePictures(html);
        Assert.DoesNotContain(" src=\"https://cdn.example.com", held);
        Assert.Contains("data-mr-src=\"https://cdn.example.com/hero.jpg\"", held);

        var (sent, images) = Composing.PrepareHtml(held);
        Assert.Contains("src=\"https://cdn.example.com/hero.jpg\"", sent);
        Assert.Contains("style=\"display:block\"", sent);
        Assert.DoesNotContain("data-mr-src", sent);
        Assert.DoesNotContain("svg", sent);
        Assert.Single(images); // only the picture that really was embedded
    }

    [Fact]
    public void Describes_blocked_trackers()
    {
        Assert.Equal("SendGrid email tracking", Trackers.Describe(["SendGrid", Trackers.Generic]));
        Assert.Equal("Email tracking", Trackers.Describe([Trackers.Generic]));
        Assert.Equal("SendGrid and HubSpot email tracking", Trackers.Describe(["SendGrid", "HubSpot", "SendGrid"]));
    }

    [Fact]
    public void Tracking_pixels_are_removed_even_when_pictures_are_allowed()
    {
        var msg = Html("<img src=\"https://cdn.example.com/hero.jpg\" width=\"600\"><img src=\"https://u1.ct.sendgrid.net/wf/open?x=1\" width=\"1\" height=\"1\">");

        var withPictures = _renderer.Render(msg, allowRemoteContent: true);
        Assert.DoesNotContain("sendgrid", withPictures.Html);
        Assert.Contains("hero.jpg", withPictures.Html);
        Assert.Equal("SendGrid email tracking", withPictures.BlockedTracking);

        var blocked = _renderer.Render(msg, allowRemoteContent: false);
        Assert.True(blocked.HasRemoteContent); // the hero picture is still remote content to offer
    }

    [Fact]
    public void Allowing_tracking_lets_only_the_tracker_host_through()
    {
        var msg = Html("<p>Hi</p><img src=\"https://u1.ct.sendgrid.net/wf/open?x=1\" width=\"1\" height=\"1\">");
        var rendered = _renderer.Render(msg, allowRemoteContent: false, allowTracking: true);
        Assert.Contains("sendgrid", rendered.Html);
        Assert.Contains("img-src data: https://u1.ct.sendgrid.net;", rendered.Html);
        Assert.Null(rendered.BlockedTracking);
    }

    [Fact]
    public void Mail_without_trackers_reports_none()
    {
        Assert.Null(_renderer.Render(Html("<p>plain</p>"), false).BlockedTracking);
    }
}

public sealed class TrustedSenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _db;

    public TrustedSenderTests()
    {
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Trust_is_case_insensitive_and_persists()
    {
        new TrustedSenders(_db).Trust("News@Example.com");
        var fresh = new TrustedSenders(_db);
        Assert.True(fresh.IsTrusted("news@example.com"));
        fresh.Untrust("NEWS@example.com");
        Assert.False(new TrustedSenders(_db).IsTrusted("news@example.com"));
        Assert.False(fresh.IsTrusted(null));
    }
}

using Plicbox.Mail;
using MimeKit;

namespace Plicbox.Tests;

public sealed class RendererTests
{
    private readonly MessageRenderer _renderer = new();

    [Fact]
    public void Strips_scripts_and_event_handlers()
    {
        var html = _renderer.Render(Html("<p onclick=\"steal()\">Hi</p><script>alert(1)</script>"), false).Html;
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("onclick", html);
        Assert.Contains("Hi", html);
    }

    [Fact]
    public void Keeps_table_cell_background_pictures_but_not_scripts_in_them()
    {
        // Pinterest boards and other newsletters put their big pictures in <td background="…">.
        var html = _renderer.Render(Html("<table><tr><td background=\"https://i.example/board.jpg\">x</td><td background=\"javascript:alert(1)\">y</td></tr></table>"), true).Html;
        Assert.Contains("background=\"https://i.example/board.jpg\"", html);
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void Finds_the_unsubscribe_link_in_the_message_first()
    {
        var msg = Html("<p>news</p><a href=\"https://shop.example/help\">Help</a> <a href=\"https://shop.example/email/click?u=1\">Unsubscribe from emails like this</a>");
        msg.Headers.Add("List-Unsubscribe", "<mailto:leave@list.example?subject=unsubscribe%3Aabc>, <https://list.example/u/abc>, <http://plain.example/u>");
        var way = UnsubscribeOption.From(msg)!;
        Assert.Equal("https://shop.example/email/click?u=1", way.Page!.AbsoluteUri);   // the message's own link wins
        Assert.Equal("https://list.example/u/abc", way.Web!.AbsoluteUri);            // http is never used
        var mail = way.BuildMail(new MailboxAddress("Me", "me@example.com"))!;
        Assert.Equal("leave@list.example", ((MailboxAddress)mail.To[0]).Address);
        Assert.Equal("unsubscribe:abc", mail.Subject);

        Assert.Equal("https://news.example/abmelden", UnsubscribeOption.From(Html("<a href=\"https://news.example/abmelden\">Newsletter abbestellen</a>"))!.Page!.AbsoluteUri);
        Assert.Null(UnsubscribeOption.From(Html("<p>no way out</p><a href=\"https://x.example\">Read more</a>")));
    }

    [Fact]
    public void Blocks_remote_content_by_default_and_reports_it()
    {
        var msg = Html("<img src=\"https://track.example/p.gif\"><div style=\"background:url('http://x.example/a.png')\"></div>");

        var blocked = _renderer.Render(msg, allowRemoteContent: false);
        Assert.True(blocked.HasRemoteContent);
        Assert.Contains("img-src data:;", blocked.Html);

        var allowed = _renderer.Render(msg, allowRemoteContent: true);
        Assert.Contains("img-src data: https: http:;", allowed.Html);
    }

    [Fact]
    public void Keeps_the_senders_web_fonts_checked()
    {
        // PayPal's own typeface, a Google Fonts stylesheet both ways, and a data: font with ';' and ',' in its URL.
        var msg = Html("""
            <html><head><link href="https://fonts.googleapis.com/css?family=Open+Sans:400,700" rel="stylesheet">
            <style>
            @import url(https://fonts.googleapis.com/css?family=Open+Sans:400,600,700);
            @font-face { font-family: SupremeLLTest; font-style: normal; font-weight: 900;
              src: url('https://www.paypalobjects.com/fonts/SupremeLL-Black.woff2') format('woff2'), /*Modern*/
                   url("javascript:alert(1)") format('woff'); mso-font-alt: 'Calibri'; }
            @font-face { font-family: 'Inline'; src: url(data:font/woff2;base64,d09GMg==) format("woff2"); }
            @font-face { font-family: "x<img src=y>"; src: url(https://evil.example/f.woff2); }
            @font-face { font-family: Bad; src: url('https://x.example/a.woff2?"); color: red'); }
            h1 { font-family: SupremeLLTest, Arial; font-weight: 900 }
            </style></head><body><h1>Hi</h1></body></html>
            """);

        // The sender's fonts stay as written; the CSP decides what loads: data: fonts always, web fonts only with the
        // pictures (a script URL in a font source can't load at all).
        var rendered = _renderer.Render(msg, allowRemoteContent: false);
        var html = rendered.Html;
        Assert.True(rendered.HasRemoteContent);
        Assert.Contains("<link href=\"https://fonts.googleapis.com/css?family=Open+Sans:400,700\" rel=\"stylesheet\">", html);
        Assert.Contains("@import url(https://fonts.googleapis.com/css?family=Open+Sans:400,600,700);", html);
        Assert.Contains("src: url('https://www.paypalobjects.com/fonts/SupremeLL-Black.woff2') format('woff2')", html);
        Assert.Contains("url(data:font/woff2;base64,d09GMg==)", html);
        Assert.Contains("font-src data:\"", html); // still waits for "Download pictures"
        Assert.Contains("font-src data: https: http:\"", _renderer.Render(msg, allowRemoteContent: true).Html);
    }

    [Fact]
    public void Pictures_that_dont_load_get_a_quiet_placeholder_instead_of_the_broken_icon()
    {
        var html = _renderer.Render(Html("<img src=\"https://x.example/logo.png\" alt=\"IQOS\">"), allowRemoteContent: false).Html;
        Assert.Contains("img::before", html);
        Assert.Contains("content: attr(alt)", html);
    }

    [Fact]
    public void A_printed_message_has_its_subject_as_title_and_who_and_when_on_top()
    {
        var shown = _renderer.Render(Html("<p>Body</p>"), false).Html;
        var page = MessageRenderer.ForPrint(shown, "Invoice <March>", [("From", "Ana <ana@x.example>"), ("Cc", "")]);
        Assert.Contains("<title>Invoice &lt;March&gt;</title>", page);
        Assert.Contains("<th>From</th><td>Ana &lt;ana@x.example&gt;</td>", page);
        Assert.DoesNotContain("<th>Cc</th>", page); // empty fields are left out
        Assert.True(page.IndexOf("mr-print-head\"><h1>", StringComparison.Ordinal) < page.IndexOf("<p>Body</p>", StringComparison.Ordinal));
        Assert.Contains("Content-Security-Policy", page); // still the reading pane's rules
        Assert.Contains("counter(page) \" / \" counter(pages)", page); // page numbers in the bottom margin
    }

    [Fact]
    public void A_message_is_shown_as_its_sender_made_it()
    {
        // Doctype (it sets the browser's layout mode), head styles, the body's own colour and attributes, sizes and CSS
        // the pane used to rewrite: all left as written, so it looks as in other mail apps.
        const string doctype = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">";
        var html = _renderer.Render(Html($$"""
            {{doctype}}
            <html lang="en"><head><title>News</title><style>.card { clip-path: circle(50%); aspect-ratio: 1 }</style></head>
            <body bgcolor="#f3f2f0" class="canvas" style="margin: 0; background-color: #f3f2f0">
            <img src="https://x.example/spacer.gif" width="1" height="40" style="display: block;">
            <p style="font-size: 48px; color: #1A73E8;">Hi</p></body></html>
            """), true).Html;

        Assert.StartsWith(doctype, html);
        Assert.Contains("<style>.card { clip-path: circle(50%); aspect-ratio: 1 }</style>", html);
        Assert.Contains("<body bgcolor=\"#f3f2f0\" class=\"canvas\" style=\"margin: 0; background-color: #f3f2f0\">", html);
        Assert.Contains("width=\"1\" height=\"40\" style=\"display: block;\"", html);
        Assert.Contains("color: #1A73E8;", html);
        // The pane's own defaults can't outweigh any rule of the sender's.
        Assert.Contains(":where(body) {", html);
        Assert.DoesNotContain("max-width", html);
    }

    [Fact]
    public void Nothing_in_a_message_can_act()
    {
        var html = _renderer.Render(Html("""
            <html><head><meta http-equiv="refresh" content="0;url=https://evil.example"><base href="https://evil.example/">
            <link rel="prefetch" href="https://evil.example/p"><link rel="stylesheet" href="https://fonts.example/a.css">
            <script>steal()</script></head>
            <body onload="steal()"><img src="x" onerror="steal()" srcset="javascript:steal() 1x">
            <a href="javascript:steal()">a</a><a href=" java&#9;script:steal()">b</a><a href="data:text/html,<script>steal()</script>">c</a>
            <a href="https://ok.example/" ping="https://evil.example/ping" target="_blank">ok</a><a href="mailto:a@b.example">mail</a>
            <iframe src="https://evil.example"></iframe><object data="x.swf"></object><embed src="x.swf">
            <form action="https://evil.example/post"><input name="password" autofocus><button formaction="https://evil.example">Go</button></form>
            <svg><a xlink:href="javascript:steal()"><set attributeName="href" to="javascript:steal()"/><text>s</text></a></svg>
            <img src="data:image/png;base64,iVBORw0KGgo=" alt="inline"><img src="data:image/svg+xml,<svg onload=steal()>">
            </body></html>
            """), false).Html;

        foreach (var gone in new[] { "steal()", "evil.example", "<iframe", "<object", "<embed", "<form", "<base", "refresh", "autofocus", "<set" })
            Assert.DoesNotContain(gone, html);
        Assert.Contains("<input name=\"password\">", html); // the fields still show, there's just nothing to send them
        Assert.Contains("href=\"https://ok.example/\"", html);
        Assert.Contains("href=\"mailto:a@b.example\"", html);
        Assert.Contains("href=\"https://fonts.example/a.css\"", html); // a stylesheet stays, and waits for the CSP
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo=\"", html);
        // The policy comes first, so it covers everything the sender's head brings.
        Assert.Matches("^<html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:;", html);
    }

    [Fact]
    public void Plain_html_has_no_remote_content()
    {
        Assert.False(_renderer.Render(Html("<p>Just text <a href=\"https://example.com\">link</a></p>"), false).HasRemoteContent);
    }

    [Fact]
    public void Inlines_cid_images_as_data_uris()
    {
        var builder = new BodyBuilder { HtmlBody = "<img src=\"cid:logo@x\">" };
        var image = builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, new ContentType("image", "png"));
        image.ContentId = "logo@x";
        var msg = new MimeMessage { Body = builder.ToMessageBody() };

        var html = _renderer.Render(msg, false).Html;
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.DoesNotContain("cid:", html);
    }

    [Fact]
    public void Plain_text_is_escaped_and_linkified()
    {
        var msg = new MimeMessage { Body = new TextPart("plain") { Text = "<b>not bold</b> see https://example.com/x" } };
        var html = _renderer.Render(msg, false).Html;
        Assert.Contains("&lt;b&gt;not bold&lt;/b&gt;", html);
        Assert.Contains("<a href=\"https://example.com/x\">", html);
    }

    [Fact]
    public void Extract_text_drops_markup_and_styles()
    {
        var text = MessageRenderer.ExtractText(Html("<style>p{color:red}</style><p>Hello&nbsp;<b>world</b></p>"), 50);
        Assert.Equal("Hello world", text);
    }

    private static MimeMessage Html(string html) => new() { Body = new TextPart("html") { Text = html } };
}

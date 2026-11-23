using AngleSharp.Html.Parser;
using Plicbox.Mail;

namespace Plicbox.Tests;

/// <summary>Which messages are shown dark in dark mode: only those that leave their look to the reader.</summary>
public sealed class DarkPageTests
{
    private static bool Plain(string html) => MessageRenderer.LooksPlain(new HtmlParser().ParseDocument(html));

    [Theory]
    // A notification like the one in the report: a table, a link, no colours.
    [InlineData("<p>Subscriber details:</p><table border=1><tr><th>Email</th><td>a@example.com</td></tr></table><a href='https://example.com'>See the full profile</a>")]
    [InlineData("<div dir='ltr'>Hi,<br>see you tomorrow.</div><blockquote style='margin:0 0 0 .8ex;border-left:1px #ccc solid;padding-left:1ex'>Earlier</blockquote>")]
    // Outlook: black text, a white page, coloured links.
    [InlineData("<html><head><style>p.MsoNormal{margin:0cm;font-size:11pt;color:windowtext} a:link, span.MsoHyperlink{color:#0563C1;text-decoration:underline} a:visited{color:#954F72}</style></head><body lang=EN-US link='#0563C1' vlink='#954F72' style='background:white'><p class=MsoNormal>Thanks</p></body></html>")]
    [InlineData("<p style='color: rgb(34, 34, 34); font-size: 14px'>Dark grey text</p>")]
    [InlineData("<td bgcolor='#FFFFFF' style='background: #fff 0 0 / 10px no-repeat'>White cell</td>")]
    public void Messages_without_a_look_of_their_own_are_plain(string html) => Assert.True(Plain(html));

    [Theory]
    [InlineData("<p>Logo</p><img src='cid:logo'>")]
    [InlineData("<table bgcolor='#1a73e8'><tr><td>Banner</td></tr></table>")]
    [InlineData("<p style='color:#c00000'>Important</p>")]
    [InlineData("<p style='color: red'>Named colour</p>")]
    [InlineData("<div style='background-color: #f3e8ff'>Lilac box</div>")]
    [InlineData("<div style='background: url(https://example.com/x.png)'>Picture</div>")]
    [InlineData("<div style='background-image: linear-gradient(#fff, #000)'>Gradient</div>")]
    [InlineData("<style>.hero { background: #0b1d3a; color: #fff }</style><div class=hero>Newsletter</div>")]
    [InlineData("<font color='#2E9E4F'>Green</font>")]
    [InlineData("<p style='color: hsl(0, 0%, 10%)'>Not read here</p>")]
    [InlineData("<table><tr><td background='bg.png'>Old background picture</td></tr></table>")]
    public void Messages_with_colours_or_pictures_keep_their_white_page(string html) => Assert.False(Plain(html));

    [Fact]
    public void Printing_leaves_the_dark_style_out()
    {
        var page = "<html><head><meta charset=\"utf-8\"><style id=\"mr-dark\">:root{color-scheme:dark}</style></head><body>Hi</body></html>";
        Assert.True(MessageRenderer.IsDarkPage(page));
        var printed = MessageRenderer.ForPrint(page, "Subject", []);
        Assert.False(MessageRenderer.IsDarkPage(printed));
        Assert.Contains("Hi", printed);
    }
}

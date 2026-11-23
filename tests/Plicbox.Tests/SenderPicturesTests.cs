using System.Net;
using Plicbox.Mail;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plicbox.Tests;

public sealed class SenderPicturesTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private const string Logo = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="5" fill="#003087"/></svg>""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeWeb _web = new();
    private readonly Dictionary<string, string[]> _dns = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private SenderPictures Create() => new(new HttpClient(_web), _dir, NullLogger<SenderPictures>.Instance,
        (name, _) => Task.FromResult<IReadOnlyList<string>>(_dns.GetValueOrDefault(name) ?? []));

    [Fact]
    public async Task A_gravatar_is_found_once_and_then_served_from_the_cache()
    {
        _web.Respond(u => u.Host == "gravatar.com", Png);
        var pictures = Create();

        var path = await pictures.FindAsync("Ana@Example.org");
        Assert.NotNull(path);
        Assert.EndsWith(".png", path);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path));

        var requests = _web.Requests.Count;
        Assert.Equal(path, await Create().FindAsync("ana@example.org")); // a fresh instance: straight from disk
        Assert.Equal(requests, _web.Requests.Count);
    }

    [Fact]
    public async Task Misses_are_remembered_too()
    {
        var pictures = Create();
        Assert.Null(await pictures.FindAsync("nobody@gmail.com"));
        var requests = _web.Requests.Count;
        Assert.Null(await pictures.FindAsync("nobody@gmail.com"));
        Assert.Equal(requests, _web.Requests.Count);
    }

    [Fact]
    public async Task Companies_get_their_verified_bimi_logo_but_free_mail_never_gets_a_company_logo()
    {
        _dns["default._bimi.paypal.example"] = ["v=BIMI1; l=https://logos.example/paypal.svg; a=;"];
        _dns["default._bimi.gmail.com"] = ["v=BIMI1; l=https://logos.example/gmail.svg;"];
        _web.Respond(u => u.AbsolutePath.EndsWith(".svg"), System.Text.Encoding.UTF8.GetBytes(Logo));

        var logo = await Create().FindAsync("service@mail.paypal.example");
        Assert.NotNull(logo);
        Assert.EndsWith(".svg", logo);
        Assert.Null(await Create().FindAsync("friend@gmail.com"));
        Assert.DoesNotContain(_web.Requests, u => u.AbsolutePath == "/gmail.svg");
    }

    [Fact]
    public async Task Site_icons_are_only_used_for_automated_senders()
    {
        _web.Respond(u => u.AbsolutePath == "/apple-touch-icon.png", Png);
        Assert.NotNull(await Create().FindAsync("noreply@shop.example"));
        Assert.Null(await Create().FindAsync("jane.doe@shop.example")); // a person: their Gravatar or the letter
    }

    [Fact]
    public async Task A_web_page_served_instead_of_an_image_is_ignored()
    {
        _web.Respond(_ => true, "<html>not found</html>"u8.ToArray());
        Assert.Null(await Create().FindAsync("news@shop.example"));
    }

    [Theory]
    [InlineData("mail.news.example.com", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("shop.example.co.uk", "example.co.uk")]
    public void Organizational_domain(string domain, string expected) => Assert.Equal(expected, SenderPictures.OrganizationalDomain(domain));

    [Theory]
    [InlineData(Logo, true)]
    [InlineData("""<svg><defs><linearGradient id="g"/></defs><rect fill="url(#g)"/></svg>""", true)]
    [InlineData("""<svg><script>alert(1)</script></svg>""", false)]
    [InlineData("""<svg><image href="https://tracker.example/p.png"/></svg>""", false)]
    [InlineData("""<svg><use xlink:href="https://evil.example/x.svg#a"/></svg>""", false)]
    [InlineData("""<svg onload="x()"></svg>""", false)]
    [InlineData("""<svg><rect style="fill:url(https://evil.example/a)"/></svg>""", false)]
    [InlineData("<html></html>", false)]
    public void Only_self_contained_svg_logos_are_drawn(string svg, bool safe) => Assert.Equal(safe, SenderPictures.IsSafeSvg(svg));

    private sealed class FakeWeb : HttpMessageHandler
    {
        private readonly List<(Func<Uri, bool> Match, byte[] Body)> _routes = [];
        public List<Uri> Requests { get; } = [];

        public void Respond(Func<Uri, bool> match, byte[] body) => _routes.Add((match, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            var route = _routes.FirstOrDefault(r => r.Match(request.RequestUri!));
            return Task.FromResult(route.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(route.Body) });
        }
    }
}

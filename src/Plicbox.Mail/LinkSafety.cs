using Plicbox.Core;
using System.Net;
using AngleSharp.Html.Parser;

namespace Plicbox.Mail;

/// <summary>How much to trust a link in a message, from "it's the sender's own site" to "looks like phishing".</summary>
public enum LinkRisk { Safe, Neutral, Caution, Danger }

/// <param name="Host">Where the link really goes, readable (Unicode) form.</param>
/// <param name="Destination">For click-tracking links that carry their target: where it ends up.</param>
/// <param name="Listed">On a downloaded list of phishing or malware sites (<see cref="ThreatLists"/>).</param>
public sealed record LinkVerdict(LinkRisk Risk, string Url, string Host, IReadOnlyList<string> Reasons, string? Destination = null,
    ThreatKind Listed = ThreatKind.None)
{
    /// <summary>A few words for the hover bubble.</summary>
    public string Headline => Listed switch
    {
        ThreatKind.Phishing => L.T("Reported phishing site"),
        ThreatKind.Malware => L.T("Reported malware site"),
        _ => HeadlineByRisk,
    };

    private string HeadlineByRisk => Risk switch
    {
        LinkRisk.Safe => L.T("Sender's own site"),
        LinkRisk.Caution => L.T("Check before opening"),
        LinkRisk.Danger => L.T("Looks like phishing"),
        _ => L.T("Link"),
    };
}

/// <summary>
/// Rates links in received mail, the way you'd check one by eye before clicking: does the text promise one site
/// while the link goes to another, is the address disguised (look-alike letters, a bare IP, "paypal.com@evil.xyz"),
/// does a well-known brand appear in someone else's domain, is it hidden behind a shortener or a free website host.
/// Links to the sender's own domain are reassuring. A link on the downloaded lists of reported phishing and malware
/// sites (<see cref="ThreatLists"/>) is dangerous whatever else it looks like. Everything is checked on this
/// computer; nothing is looked up.
/// </summary>
public static class LinkSafety
{
    // Second-level suffixes under which a name is registered one label further down (bbc.co.uk, not co.uk).
    private static readonly HashSet<string> MultiPartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "com.au", "net.au", "org.au", "co.nz", "org.nz",
        "co.jp", "ne.jp", "or.jp", "com.br", "com.mx", "com.tr", "co.za", "com.cn", "com.hk", "co.in", "co.kr", "com.sg",
        "com.ar", "co.il", "com.pl", "com.ro", "com.ua", "co.at", "or.at", "com.es", "com.pt", "com.my", "com.ph", "com.vn",
    };

    // Free hosting where anyone gets a subdomain: the subdomain is the "site", and these are favourites for phishing pages.
    private static readonly HashSet<string> FreeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.io", "blogspot.com", "herokuapp.com", "azurewebsites.net", "web.app", "firebaseapp.com", "pages.dev",
        "workers.dev", "netlify.app", "vercel.app", "glitch.me", "weebly.com", "wixsite.com", "000webhostapp.com",
        "ngrok.io", "ngrok-free.app", "repl.co", "onrender.com", "surge.sh", "webflow.io", "framer.website", "godaddysites.com",
        "square.site", "mystrikingly.com", "carrd.co", "notion.site", "ipfs.io", "r2.dev", "appspot.com",
    };

    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at",
        "tiny.cc", "rb.gy", "t.ly", "s.id", "v.gd", "qrco.de", "lnkd.in", "bl.ink", "short.io", "urlz.fr",
    };

    // Top-level domains that cost next to nothing and show up in a large share of phishing.
    private static readonly HashSet<string> RiskyTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "mov", "xyz", "top", "click", "link", "buzz", "rest", "fit", "cfd", "sbs", "icu", "cyou", "monster", "quest",
        "gq", "tk", "ml", "cf", "ga", "work", "support", "country", "kim", "loan", "men", "date", "win", "bid", "review",
    };

    // Brands phishers dress up as, with the domains that really belong to them.
    private static readonly Dictionary<string, string[]> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paypal"] = ["paypal.com", "paypal.de", "paypal.me", "paypalobjects.com"],
        ["apple"] = ["apple.com", "icloud.com", "apple.news"],
        ["icloud"] = ["icloud.com", "apple.com"],
        ["microsoft"] = ["microsoft.com", "microsoftonline.com", "live.com", "office.com", "outlook.com", "msn.com", "azure.com"],
        ["office365"] = ["office.com", "microsoft.com"],
        ["outlook"] = ["outlook.com", "live.com", "office.com", "microsoft.com"],
        ["google"] = ["google.com", "google.de", "googleusercontent.com", "googleapis.com", "gstatic.com", "youtube.com", "goo.gl"],
        ["gmail"] = ["gmail.com", "google.com"],
        ["amazon"] = ["amazon.com", "amazon.de", "amazon.co.uk", "amazon.fr", "amazon.it", "amazon.es", "amazonaws.com", "amazon.jobs", "a.co"],
        ["netflix"] = ["netflix.com"],
        ["facebook"] = ["facebook.com", "fb.com", "fbcdn.net", "meta.com"],
        ["instagram"] = ["instagram.com"],
        ["whatsapp"] = ["whatsapp.com", "whatsapp.net", "wa.me"],
        ["linkedin"] = ["linkedin.com", "lnkd.in"],
        ["dropbox"] = ["dropbox.com", "dropboxusercontent.com", "db.tt"],
        ["docusign"] = ["docusign.com", "docusign.net"],
        ["adobe"] = ["adobe.com", "adobesign.com", "adobelogin.com"],
        ["ebay"] = ["ebay.com", "ebay.de", "ebay.co.uk", "ebaystatic.com"],
        ["dhl"] = ["dhl.com", "dhl.de"],
        ["fedex"] = ["fedex.com"],
        ["ups"] = ["ups.com"],
        ["dpd"] = ["dpd.com", "dpd.de", "dpd.co.uk"],
        ["sparkasse"] = ["sparkasse.de"],
        ["commerzbank"] = ["commerzbank.de", "commerzbank.com"],
        ["revolut"] = ["revolut.com"],
        ["binance"] = ["binance.com"],
        ["coinbase"] = ["coinbase.com"],
        ["steam"] = ["steampowered.com", "steamcommunity.com"],
        ["telekom"] = ["telekom.de", "telekom.com"],
        ["vodafone"] = ["vodafone.com", "vodafone.de"],
    };

    // Query parameters that click trackers use to carry the real target.
    private static readonly string[] RedirectParams = ["url", "u", "redirect", "redirect_url", "redirect_uri", "target", "dest", "destination", "to", "goto", "link", "r"];

    /// <summary>Every link in the message's HTML with the text it shows (or its image's alt text).</summary>
    public static IReadOnlyDictionary<string, string> ExtractLinks(string html)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var doc = new HtmlParser().ParseDocument(html);
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href")!.Trim();
                var text = string.Join(' ', a.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (text.Length == 0) text = a.QuerySelector("img[alt]")?.GetAttribute("alt")?.Trim() ?? "";
                if (!links.ContainsKey(href) || links[href].Length == 0) links[href] = text;
                if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && !links.ContainsKey(uri.AbsoluteUri)) links[uri.AbsoluteUri] = text;
            }
        }
        catch (Exception)
        {
            // A message we can't parse just gets checks without the link text.
        }
        return links;
    }

    /// <param name="linkText">What the link shows in the message, if known.</param>
    /// <param name="senderAddress">The message's From address: links to the sender's own domain are reassuring.</param>
    public static LinkVerdict Assess(string url, string? linkText = null, string? senderAddress = null)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return new LinkVerdict(LinkRisk.Neutral, url, url, []);
        if (uri.Scheme == Uri.UriSchemeMailto)
            return new LinkVerdict(LinkRisk.Neutral, url, uri.UserInfo.Length > 0 ? $"{uri.UserInfo}@{uri.Host}" : url[7..], [L.T("Writes an email.")]);

        var reasons = new List<string>();
        var risk = LinkRisk.Neutral;
        void Add(LinkRisk level, string reason)
        {
            reasons.Add(reason);
            if (level > risk) risk = level;
        }

        if (uri.Scheme is not ("http" or "https"))
            return new LinkVerdict(LinkRisk.Caution, url, uri.Scheme + ":", [L.F("Opens a \"{0}:\" link, not a web page.", uri.Scheme)]);

        var (host, asciiHost) = SenderCheck.DomainForms(uri.IdnHost.Length > 0 ? uri.IdnHost : uri.Host);
        var site = RegistrableDomain(asciiHost);
        var senderSite = senderAddress is { } s && s.Contains('@') ? RegistrableDomain(s[(s.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant()) : null;

        // Disguises in the address itself.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            Add(LinkRisk.Danger, L.T("Goes to a bare IP address instead of a named website."));
        if (uri.UserInfo.Length > 0)
            Add(LinkRisk.Danger, L.F("The part before \"@\" ({0}) is only decoration; the link really goes to {1}.", uri.UserInfo, host));
        if (SenderCheck.Confusable(host))
            Add(LinkRisk.Danger, L.F("{0} uses look-alike letters from another alphabet (it's really {1}).", host, asciiHost));

        // A brand somewhere in a domain that isn't the brand's.
        var labels = asciiHost.Split('.');
        var siteLabel = site.Split('.')[0];
        var siteWords = siteLabel.Split('-');
        foreach (var (brand, owned) in Brands)
        {
            if (owned.Any(d => site.Equals(d, StringComparison.OrdinalIgnoreCase))) continue;
            // "paypal.account-check.com", "paypal.com.secure-login.xyz": the brand as a subdomain of someone else's site.
            if (labels[..^site.Split('.').Length].Any(l => l.Equals(brand, StringComparison.OrdinalIgnoreCase))
                || owned.Any(d => asciiHost.StartsWith(d + ".", StringComparison.OrdinalIgnoreCase)))
            {
                Add(LinkRisk.Danger, L.F("Pretends to be {0}, but the site is {1}.", Capitalize(brand), site));
                break;
            }
            // "paypal-support.com", "paypalsecure.net", "paypal.xyz": the brand's name on a domain it doesn't own.
            // Whole words only (so "groups.io" isn't UPS and "pineapple.com" isn't Apple).
            if (siteWords.Any(w => w.Equals(brand, StringComparison.OrdinalIgnoreCase))
                || brand.Length >= 6 && siteLabel.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
            {
                Add(LinkRisk.Caution, L.F("Uses {0}'s name, but {1} isn't one of its sites.", Capitalize(brand), site));
                break;
            }
        }

        // The text promises one site, the link goes to another.
        if (linkText is { Length: > 0 } && ShownSite(linkText) is { } shown && !shown.Equals(site, StringComparison.OrdinalIgnoreCase))
        {
            var target = Destination(uri);
            var targetSite = target is null ? null : RegistrableDomain(target.IdnHost);
            if (targetSite is not null && targetSite.Equals(shown, StringComparison.OrdinalIgnoreCase))
                reasons.Add(L.F("Passes through {0} (click tracking) before reaching {1}.", site, shown));
            else if (senderSite is not null && site.Equals(senderSite, StringComparison.OrdinalIgnoreCase))
                reasons.Add(L.F("Shows {0} but goes through the sender's own {1} first.", shown, site));
            else
                Add(LinkRisk.Danger, L.F("The text says {0}, but the link goes to {1}.", shown, site));
        }

        // Hiding where it goes.
        if (Shorteners.Contains(site) || Shorteners.Contains(asciiHost))
            Add(LinkRisk.Caution, L.F("A shortened link ({0}): where it really leads is hidden.", asciiHost));
        if (FreeHosts.Contains(SuffixOf(asciiHost)))
            Add(LinkRisk.Caution, L.F("A page on a free website service ({0}) that anyone can sign up for.", SuffixOf(asciiHost)));
        if (RiskyTlds.Contains(labels[^1]))
            Add(LinkRisk.Caution, L.F("Uses the \".{0}\" ending, common among throwaway and scam sites.", labels[^1]));
        if (!uri.IsDefaultPort)
            Add(LinkRisk.Caution, L.F("Uses an unusual port ({0}).", uri.Port));
        if (uri.Scheme == "http")
            Add(LinkRisk.Caution, L.T("Not encrypted (http): anything you type there can be read on the way."));

        string? destination = null;
        if (Destination(uri) is { } dest && !RegistrableDomain(dest.IdnHost).Equals(site, StringComparison.OrdinalIgnoreCase))
            destination = SenderCheck.DomainForms(dest.IdnHost).Unicode;

        // Reported by others: said first, since it outweighs everything above.
        var listed = ThreatLists.Current.Check(uri);
        var listedHost = host;
        if (Destination(uri) is { } via && ThreatLists.Current.Check(via) is var viaListed && viaListed > listed)
        {
            listed = viaListed;
            listedHost = SenderCheck.DomainForms(via.IdnHost).Unicode;
        }
        if (listed != ThreatKind.None)
        {
            reasons.Insert(0, listed == ThreatKind.Phishing
                ? L.F("{0} is on public lists of reported phishing sites.", listedHost)
                : L.F("{0} is on public lists of sites that spread malware.", listedHost));
            risk = LinkRisk.Danger;
        }

        if (risk == LinkRisk.Neutral && senderSite is not null && SameOrganisation(site, senderSite))
            risk = LinkRisk.Safe;

        return new LinkVerdict(risk, uri.OriginalString, host, reasons, destination, listed);
    }

    /// <summary>
    /// A site where anyone can publish (a free host or shortener itself, or a big brand's own domain): a threat list
    /// naming the whole of it would be a mistake, since the phishing is one page there.
    /// </summary>
    internal static bool IsSharedPlatform(string host) => FreeHosts.Contains(host) || Shorteners.Contains(host) || BrandSites.Contains(host);

    private static readonly HashSet<string> BrandSites = new(Brands.Values.SelectMany(d => d), StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a click-tracking link really leads, when it says so in its query.</summary>
    internal static Uri? RedirectTarget(Uri uri) => Destination(uri);

    /// <summary>The same site, or two domains of the same well-known company (mail from paypal.de linking to paypal.com).</summary>
    private static bool SameOrganisation(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase)
        || Brands.Values.Any(owned => owned.Contains(a, StringComparer.OrdinalIgnoreCase) && owned.Contains(b, StringComparer.OrdinalIgnoreCase));

    /// <summary>The name someone registered: "mail.google.com" → "google.com", "news.bbc.co.uk" → "bbc.co.uk".</summary>
    public static string RegistrableDomain(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        var labels = host.Split('.');
        if (labels.Length <= 2 || IPAddress.TryParse(host, out _)) return host;
        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        // On free hosts and multi-part suffixes, the name is one label further down.
        var take = MultiPartSuffixes.Contains(lastTwo) || FreeHosts.Contains(lastTwo) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    private static string SuffixOf(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        return labels.Length >= 2 ? $"{labels[^2]}.{labels[^1]}" : host;
    }

    /// <summary>The site a link's text names, if the text looks like an address ("www.paypal.com", "https://x.org/login").</summary>
    private static string? ShownSite(string text)
    {
        var t = text.Trim().TrimEnd('/', '.', ')').TrimStart('(');
        if (t.Contains(' ') || !t.Contains('.')) return null;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var shown) || !shown.IdnHost.Contains('.')) return null;
        // "Read more…" or "v2.1" aren't addresses: need letters in the top-level domain.
        var tld = shown.IdnHost.Split('.')[^1];
        return tld.Length >= 2 && tld.All(char.IsLetter) ? RegistrableDomain(shown.IdnHost) : null;
    }

    /// <summary>A click tracker's real target, when it carries one in the query ("…?url=https://shop.example/…").</summary>
    private static Uri? Destination(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || !RedirectParams.Contains(pair[..eq], StringComparer.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (Uri.TryCreate(value, UriKind.Absolute, out var target) && target.Scheme is "http" or "https") return target;
        }
        return null;
    }

    private static readonly Dictionary<string, string> BrandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paypal"] = "PayPal", ["icloud"] = "iCloud", ["linkedin"] = "LinkedIn", ["docusign"] = "DocuSign", ["ebay"] = "eBay",
        ["whatsapp"] = "WhatsApp", ["dhl"] = "DHL", ["ups"] = "UPS", ["dpd"] = "DPD", ["fedex"] = "FedEx", ["office365"] = "Office 365",
    };

    private static string Capitalize(string s) =>
        BrandNames.TryGetValue(s, out var name) ? name : s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

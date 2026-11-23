using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plicbox.Mail;

public enum ThreatKind { None, Malware, Phishing }

/// <summary>
/// Sites reported for phishing or for spreading malware, from public lists downloaded now and then (see
/// <see cref="ThreatListUpdater"/>). Checks are made here on this computer: nothing about a message or a link is sent
/// anywhere. An entry is either a whole site ("evil.example", which covers its subdomains too) or one page on a
/// site everyone uses ("sites.google.com/view/fake-bank"), so a phishing page on Google Sites doesn't make all of
/// Google Sites look dangerous.
/// </summary>
public sealed partial class ThreatLists
{
    public static ThreatLists Empty { get; } = new();

    private static volatile ThreatLists _current = Empty;

    /// <summary>The lists in use (empty until they've been loaded, and while the setting is off).</summary>
    public static ThreatLists Current
    {
        get => _current;
        set => _current = value ?? Empty;
    }

    // Whole sites by name. Single pages (well over 100,000, mostly malware downloads) are kept as 64-bit fingerprints
    // of site + path rather than as text, which takes a fraction of the memory; the few written with a "*" are kept
    // as text, since they match anything that starts with them.
    private readonly Dictionary<string, ThreatKind> _sites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pageSites = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, ThreatKind> _pages = [];
    private readonly Dictionary<string, List<(string Prefix, ThreatKind Kind)>> _prefixes = new(StringComparer.Ordinal);

    /// <summary>How many sites and pages are listed.</summary>
    public int Count { get; private set; }

    public bool IsEmpty => Count == 0;

    // A whole-site entry for one of these would flag everyone's pages (a list's mistake): only single pages count there.
    private static readonly HashSet<string> SharedSites = new(StringComparer.OrdinalIgnoreCase)
    {
        "sites.google.com", "docs.google.com", "drive.google.com", "forms.gle", "storage.googleapis.com", "script.google.com",
        "s3.amazonaws.com", "onedrive.live.com", "1drv.ms", "sharepoint.com", "github.com", "gitlab.com", "dropbox.com",
        "dl.dropboxusercontent.com", "mega.nz", "wetransfer.com", "we.tl", "box.com", "canva.com", "linktr.ee", "notion.so",
        "translate.goog", "cloudflare-ipfs.com", "mandrillapp.com", "sendgrid.net", "mailchi.mp", "list-manage.com",
    };

    /// <summary>Reads a downloaded list: uBlock-style filters ("||site/page^$all"), plain domains, or hosts-file lines.</summary>
    public void Add(ThreatKind kind, string text)
    {
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '!' or '#' or '[') continue;
            if (line.StartsWith("0.0.0.0 ") || line.StartsWith("127.0.0.1 ")) line = line[(line.IndexOf(' ') + 1)..].Trim();
            AddEntry(kind, line.ToString());
        }
    }

    private void AddEntry(ThreatKind kind, string entry)
    {
        if (entry.StartsWith("@@", StringComparison.Ordinal) || entry.Contains(' ') || entry.Contains("##", StringComparison.Ordinal)) return;
        if (entry.StartsWith("||", StringComparison.Ordinal)) entry = entry[2..];
        var dollar = entry.LastIndexOf('$');
        if (dollar >= 0 && FilterOptions().IsMatch(entry[(dollar + 1)..])) entry = entry[..dollar];
        var exact = entry.EndsWith('^');
        entry = entry.TrimEnd('^').Replace("&amp;", "&", StringComparison.Ordinal);
        if (entry.IndexOf('*') is var star and >= 0)
        {
            entry = entry[..star];
            exact = false;
        }

        var cut = entry.IndexOfAny(['/', '?']);
        var host = (cut < 0 ? entry : entry[..cut]).Trim().TrimEnd('.').ToLowerInvariant();
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']') && host.Count(c => c == ':') == 1) host = host[..colon]; // "site:8080"
        if (host.Length < 3 || !host.Contains('.') || host == "localhost") return;
        var path = cut < 0 ? "" : entry[cut..];
        if (path == "/") path = "";
        if (path.Length == 0)
        {
            if (IsShared(host)) return;
            var known = _sites.GetValueOrDefault(host);
            if (known == ThreatKind.None) Count++;
            if (kind > known) _sites[host] = kind;
            return;
        }

        _pageSites.Add(host);
        Count++;
        if (exact)
        {
            var key = Fingerprint(host, path);
            if (kind > _pages.GetValueOrDefault(key)) _pages[key] = kind;
        }
        else
        {
            if (!_prefixes.TryGetValue(host, out var list)) _prefixes[host] = list = [];
            list.Add((path, kind));
        }
    }

    private static bool IsShared(string host) =>
        SharedSites.Contains(host) || LinkSafety.IsSharedPlatform(host);

    /// <summary>Whether a web address is listed: its site (or a site it's under), or the very page.</summary>
    public ThreatKind Check(Uri uri)
    {
        if (IsEmpty || uri.Scheme is not ("http" or "https")) return ThreatKind.None;
        var host = (uri.IdnHost.Length > 0 ? uri.IdnHost : uri.Host).Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        var found = ThreatKind.None;
        string[]? paths = null;
        foreach (var suffix in Suffixes(host))
        {
            if (_sites.GetValueOrDefault(suffix) > found) found = _sites[suffix];
            if (!_pageSites.Contains(suffix)) continue;
            paths ??= PathForms(uri);
            foreach (var path in paths)
            {
                // An entry ending in "^" matches when the address continues with a separator or ends: try the path
                // cut at each separator, and whole (the fingerprint grows a character at a time).
                var hash = Fingerprint(suffix, []);
                for (var i = 0; i < path.Length; i++)
                {
                    hash = Step(hash, char.ToLowerInvariant(path[i]));
                    if (i + 1 < path.Length && !IsSeparator(path[i + 1])) continue;
                    if (_pages.GetValueOrDefault(hash) is var kind && kind > found) found = kind;
                }
                if (_prefixes.TryGetValue(suffix, out var prefixes))
                    foreach (var (prefix, kind) in prefixes)
                        if (kind > found && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) found = kind;
            }
        }
        return found;
    }

    /// <summary>Whether a whole domain is listed (for a sender's domain: single pages don't count).</summary>
    public ThreatKind CheckDomain(string domain)
    {
        if (IsEmpty) return ThreatKind.None;
        var host = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var found = ThreatKind.None;
        foreach (var suffix in Suffixes(host))
            if (_sites.GetValueOrDefault(suffix) > found) found = _sites[suffix];
        return found;
    }

    /// <summary>The worst listed link (or the sender's domain) in a message, with where it goes.</summary>
    public (ThreatKind Kind, string? Host, bool FromSender) CheckMessage(string? fromAddress, IEnumerable<string> links)
    {
        if (IsEmpty) return (ThreatKind.None, null, false);
        var found = (Kind: ThreatKind.None, Host: (string?)null, FromSender: false);
        if (fromAddress is { } from && from.Contains('@') && CheckDomain(from[(from.LastIndexOf('@') + 1)..]) is var sender and not ThreatKind.None)
            found = (sender, from[(from.LastIndexOf('@') + 1)..].ToLowerInvariant(), true);
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) continue;
            var kind = Check(uri);
            if (LinkSafety.RedirectTarget(uri) is { } target && Check(target) is var viaTarget && viaTarget > kind)
            {
                kind = viaTarget;
                uri = target;
            }
            if (kind > found.Kind) found = (kind, SenderCheck.DomainForms(uri.IdnHost).Unicode, false);
            if (found.Kind == ThreatKind.Phishing) break;
        }
        return found;
    }

    // "a.b.example.com" → itself, "b.example.com", "example.com" (an entry for a site covers its subdomains).
    private static IEnumerable<string> Suffixes(string host)
    {
        yield return host;
        if (IPAddress.TryParse(host, out _)) yield break;
        for (var dot = host.IndexOf('.'); dot >= 0; dot = host.IndexOf('.', dot + 1))
        {
            var rest = host[(dot + 1)..];
            if (!rest.Contains('.')) yield break;
            yield return rest;
        }
    }

    // The path and query as written in the link and as .NET normalises it (lists record what was reported).
    private static string[] PathForms(Uri uri)
    {
        var normal = uri.PathAndQuery;
        var original = uri.OriginalString;
        var start = original.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return [normal];
        var slash = original.IndexOfAny(['/', '?', '#'], start + 3);
        var raw = slash < 0 ? "/" : original[slash..];
        var hash = raw.IndexOf('#');
        if (hash >= 0) raw = raw[..hash];
        return raw == normal ? [normal] : [normal, raw];
    }

    // uBlock's "^": anything but a letter, a digit or one of _ - . %
    private static bool IsSeparator(char c) => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '%');

    // FNV-1a over "site\npath", ignoring case (filters do). 64 bits: a chance match among this many entries is
    // around one in a hundred trillion per lookup.
    private static ulong Fingerprint(string host, ReadOnlySpan<char> path)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in host) hash = Step(hash, c);
        hash = Step(hash, '\n');
        foreach (var c in path) hash = Step(hash, char.ToLowerInvariant(c));
        return hash;
    }

    private static ulong Step(ulong hash, char c) => (hash ^ c) * 1099511628211UL;

    /// <summary>Every web link in a message: the HTML's links, or addresses written out in plain text.</summary>
    public static IReadOnlyCollection<string> LinksIn(MimeMessage message)
    {
        var links = new HashSet<string>(StringComparer.Ordinal);
        if (message.HtmlBody is { } html)
            foreach (var href in LinkSafety.ExtractLinks(html).Keys)
                links.Add(href);
        if (message.TextBody is { } text)
            foreach (Match m in WrittenUrl().Matches(text))
                links.Add(m.Value.TrimEnd('.', ',', ')', ']', '>', '"', '\''));
        return links;
    }

    [GeneratedRegex(@"^[a-z0-9_,=~-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterOptions();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WrittenUrl();
}

/// <summary>A list to download: what it lists, where from (the first address that answers), and its licence.</summary>
public sealed record ThreatFeed(string Name, ThreatKind Kind, IReadOnlyList<string> Urls);

/// <summary>
/// Keeps the threat lists on this computer up to date: loads what was downloaded before at start, then checks for a
/// newer copy twice a day (a list that hasn't changed isn't downloaded again). The lists come from the malware-filter
/// project, which builds them from OpenPhish, PhishTank and IPThreat (phishing) and abuse.ch URLhaus (malware), with
/// the world's most visited sites left out so they can't be flagged by mistake.
/// </summary>
public sealed class ThreatListUpdater(HttpClient http, string directory, ILogger<ThreatListUpdater> log)
{
    public static IReadOnlyList<ThreatFeed> Feeds { get; } =
    [
        new("phishing", ThreatKind.Phishing,
        [
            "https://malware-filter.gitlab.io/malware-filter/phishing-filter.txt",
            "https://curbengh.github.io/malware-filter/phishing-filter.txt",
            "https://malware-filter.pages.dev/phishing-filter.txt",
        ]),
        new("malware", ThreatKind.Malware,
        [
            "https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-online.txt",
            "https://curbengh.github.io/malware-filter/urlhaus-filter-online.txt",
            "https://malware-filter.pages.dev/urlhaus-filter-online.txt",
        ]),
    ];

    /// <summary>How often the lists are checked for a newer copy (they're rebuilt twice a day).</summary>
    public static TimeSpan RefreshEvery { get; } = TimeSpan.FromHours(12);

    // A download much smaller than this is an error page or a broken list: the copy we have stays.
    private const int MinimumEntries = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loop;

    /// <summary>The lists were (re)loaded.</summary>
    public event Action? Updated;

    /// <summary>When the lists were last checked for a newer copy; null if never.</summary>
    public DateTimeOffset? LastChecked => Feeds.All(f => File.Exists(PathOf(f)))
        ? new DateTimeOffset(Feeds.Min(f => File.GetLastWriteTimeUtc(PathOf(f))), TimeSpan.Zero)
        : null;

    /// <summary>Turns the checks on (load, then keep up to date) or off (forget the lists, download nothing).</summary>
    public void SetEnabled(bool enabled)
    {
        _loop?.Cancel();
        _loop = null;
        if (!enabled)
        {
            ThreatLists.Current = ThreatLists.Empty;
            Updated?.Invoke();
            return;
        }
        var cts = _loop = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (ThreatLists.Current.IsEmpty) Load();
            while (!ct.IsCancellationRequested)
            {
                if (Feeds.Any(f => Age(f) >= RefreshEvery)) await RefreshAsync(ct);
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Keeping the threat lists up to date failed");
        }
    }

    /// <summary>Downloads any list that changed, then reloads them all; false when none could be reached.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var changed = false;
            var reached = false;
            foreach (var feed in Feeds)
            {
                var result = await DownloadAsync(feed, ct);
                reached |= result is not null;
                changed |= result == true;
            }
            if (changed || ThreatLists.Current.IsEmpty) Load();
            return reached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>True: a new copy; false: unchanged; null: no address answered.</returns>
    private async Task<bool?> DownloadAsync(ThreatFeed feed, CancellationToken ct)
    {
        var path = PathOf(feed);
        var etagPath = path + ".etag";
        foreach (var url in feed.Urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // The version tag is only good for the address that gave it.
                if (File.Exists(path) && File.Exists(etagPath) && File.ReadAllLines(etagPath) is [var from, var etag] && from == url)
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // checked just now
                    return false;
                }
                if (!response.IsSuccessStatusCode)
                {
                    log.LogInformation("Threat list {Name}: {Url} answered {Status}", feed.Name, url, (int)response.StatusCode);
                    continue;
                }
                var text = await response.Content.ReadAsStringAsync(ct);
                var check = new ThreatLists();
                check.Add(feed.Kind, text);
                if (check.Count < MinimumEntries)
                {
                    log.LogWarning("Threat list {Name} from {Url} had only {Count} entries; keeping the copy we have", feed.Name, url, check.Count);
                    continue;
                }
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, text, ct);
                File.Move(tmp, path, overwrite: true);
                if (response.Headers.ETag is { } tag) await File.WriteAllLinesAsync(etagPath, [url, tag.ToString()], ct);
                else File.Delete(etagPath);
                log.LogInformation("Threat list {Name} updated: {Count} entries", feed.Name, check.Count);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                           || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogInformation("Threat list {Name}: {Url} couldn't be reached ({Error})", feed.Name, url, ex.Message);
            }
        }
        return null;
    }

    /// <summary>Reads the downloaded lists into <see cref="ThreatLists.Current"/>.</summary>
    public void Load()
    {
        var lists = new ThreatLists();
        foreach (var feed in Feeds)
        {
            try
            {
                if (File.Exists(PathOf(feed))) lists.Add(feed.Kind, File.ReadAllText(PathOf(feed)));
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Reading threat list {Name} failed", feed.Name);
            }
        }
        ThreatLists.Current = lists;
        log.LogInformation("Threat lists loaded: {Count} entries", lists.Count);
        Updated?.Invoke();
    }

    private string PathOf(ThreatFeed feed) => Path.Combine(directory, feed.Name + ".txt");

    private TimeSpan Age(ThreatFeed feed) =>
        File.Exists(PathOf(feed)) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(PathOf(feed)) : TimeSpan.MaxValue;
}

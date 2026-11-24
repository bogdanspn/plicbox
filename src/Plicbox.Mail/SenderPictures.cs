using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace Plicbox.Mail;

/// <summary>
/// Finds a picture for a sender, the way eM Client fills in contact images, without inventing one:
/// the person's Gravatar, else the company's verified brand logo (BIMI, the DNS-published logo Gmail shows),
/// else (only for addresses like noreply@ or news@) the website's touch icon. Free-mail domains never get a company logo.
/// Results (and misses) are cached on disk, so each sender is looked up at most about once a month; nothing about the
/// message itself is sent anywhere.
/// </summary>
public sealed partial class SenderPictures
{
    private static readonly TimeSpan KeepFound = TimeSpan.FromDays(30);
    private static readonly TimeSpan KeepMissing = TimeSpan.FromDays(14);
    private const int MaxImageBytes = 512 * 1024;
    private const int MaxSvgBytes = 64 * 1024;

    private static readonly string[] TwoLevelSuffixes =
    [
        "co.uk", "org.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "co.nz", "co.jp", "ne.jp", "or.jp",
        "com.br", "com.mx", "co.za", "com.tr", "com.cn", "co.in", "co.kr", "com.sg", "com.hk", "com.ar", "co.il",
    ];

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly ILogger _log;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _txt;
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly SemaphoreSlim _throttle = new(4);

    /// <param name="txtLookup">DNS TXT lookup (replaceable in tests); defaults to the system's resolvers.</param>
    public SenderPictures(HttpClient http, string cacheDir, ILogger<SenderPictures> log,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? txtLookup = null)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(6);
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("Plicbox/1.0");
        _cacheDir = cacheDir;
        _log = log;
        _txt = txtLookup ?? DnsTxtAsync;
        Directory.CreateDirectory(cacheDir);
    }

    private static async Task<IReadOnlyList<string>> DnsTxtAsync(string name, CancellationToken ct)
    {
        var dns = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4), UseCache = true });
        var result = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
        return result.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }

    /// <summary>Path of a cached image (.png/.jpg/.gif/.svg) for the sender, or null when there's nothing worth showing.</summary>
    public async Task<string?> FindAsync(string address, CancellationToken ct = default)
    {
        address = address.Trim().ToLowerInvariant();
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1) return null;
        var local = address[..at];
        var domain = address[(at + 1)..].TrimEnd('.');

        if (await CachedAsync("gravatar:" + address, () => GravatarAsync(address, ct)) is { } gravatar) return gravatar;
        var org = OrganizationalDomain(domain);
        if (SenderCheck.IsFreeMail(org)) return null;
        if (await CachedAsync("bimi:" + domain, () => BimiAsync(domain, org, ct)) is { } logo) return logo;
        // A company's site icon suits its automated mail, not each employee.
        return RoleAddress().IsMatch(local) ? await CachedAsync("icon:" + org, () => TouchIconAsync(org, ct)) : null;
    }

    /// <summary>The registrable domain: mail.news.example.co.uk -> example.co.uk.</summary>
    public static string OrganizationalDomain(string domain)
    {
        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2) return domain;
        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        var take = TwoLevelSuffixes.Contains(lastTwo, StringComparer.OrdinalIgnoreCase) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    // ───────── cache ─────────

    private Task<string?> CachedAsync(string key, Func<Task<(byte[] Data, string Extension)?>> fetch) =>
        _inFlight.GetOrAdd(key, k => LoadOrFetchAsync(k, fetch)).ContinueWith(t =>
        {
            _inFlight.TryRemove(key, out _);
            return t.IsCompletedSuccessfully ? t.Result : null;
        }, TaskScheduler.Default);

    // What's in the cache folder (file name without extension -> path), listed once: looking a file up by pattern
    // would scan the folder (thousands of files) for every sender, several times, on every folder switch.
    private Dictionary<string, string>? _cached;
    private readonly object _cachedLock = new();

    private string? CachedFile(string name)
    {
        lock (_cachedLock)
        {
            _cached ??= Directory.EnumerateFiles(_cacheDir)
                .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _cached.GetValueOrDefault(name);
        }
    }

    private void RememberCached(string name, string path)
    {
        lock (_cachedLock)
            if (_cached is not null) _cached[name] = path;
    }

    private async Task<string?> LoadOrFetchAsync(string key, Func<Task<(byte[] Data, string Extension)?>> fetch)
    {
        var stem = Path.Combine(_cacheDir, Hash(key));
        var found = CachedFile(Path.GetFileName(stem));
        if (found is not null)
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(found);
            var missing = found.EndsWith(".none", StringComparison.Ordinal);
            if (age < (missing ? KeepMissing : KeepFound)) return missing ? null : found;
        }

        await _throttle.WaitAsync();
        try
        {
            var result = await fetch();
            foreach (var old in Directory.EnumerateFiles(_cacheDir, Path.GetFileName(stem) + ".*")) File.Delete(old);
            if (result is not { } r)
            {
                await File.WriteAllBytesAsync(stem + ".none", []);
                RememberCached(Path.GetFileName(stem), stem + ".none");
                return null;
            }
            var path = stem + r.Extension;
            await File.WriteAllBytesAsync(path, r.Data);
            RememberCached(Path.GetFileName(stem), path);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DnsResponseException or IOException)
        {
            // Offline or a slow server: try again next time rather than remembering a miss.
            _log.LogDebug(ex, "Looking up a picture for {Key} failed", key);
            return found is not null && !found.EndsWith(".none", StringComparison.Ordinal) ? found : null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();

    // ───────── sources ─────────

    private async Task<(byte[], string)?> GravatarAsync(string address, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address))).ToLowerInvariant();
        return await DownloadImageAsync($"https://gravatar.com/avatar/{hash}?s=128&d=404", ct); // d=404: no picture -> nothing
    }

    private async Task<(byte[], string)?> BimiAsync(string domain, string org, CancellationToken ct)
    {
        foreach (var name in new[] { domain, org }.Distinct())
        {
            var record = (await _txt($"default._bimi.{name}", ct))
                .FirstOrDefault(t => t.StartsWith("v=BIMI1", StringComparison.OrdinalIgnoreCase));
            if (record is null) continue;
            var url = BimiLocation().Match(record) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var svg = await ReadLimitedAsync(response, MaxSvgBytes, ct);
            return svg is not null && IsSafeSvg(Encoding.UTF8.GetString(svg)) ? (svg, ".svg") : null;
        }
        return null;
    }

    private async Task<(byte[], string)?> TouchIconAsync(string org, CancellationToken ct)
    {
        foreach (var host in new[] { org, "www." + org })
            if (await DownloadImageAsync($"https://{host}/apple-touch-icon.png", ct) is { } icon) return icon;
        return null;
    }

    private async Task<(byte[], string)?> DownloadImageAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        var data = await ReadLimitedAsync(response, MaxImageBytes, ct);
        return data is null ? null : ImageExtension(data) is { } ext ? (data, ext) : null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>Only real raster images are kept (a web page served instead of an icon is ignored).</summary>
    public static string? ImageExtension(byte[] data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x47, 0x49, 0x46, 0x38, ..] => ".gif",
        _ => null,
    };

    /// <summary>
    /// BIMI logos are "SVG Tiny PS": no scripts, no external references. Anything else is refused rather than drawn,
    /// so a logo can never make Plicbox fetch more from the network or run code.
    /// </summary>
    public static bool IsSafeSvg(string svg) =>
        svg.Contains("<svg", StringComparison.OrdinalIgnoreCase) && !UnsafeSvg().IsMatch(svg);

    [GeneratedRegex(@"(?:^|;)\s*l=([^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex BimiLocation();

    [GeneratedRegex(@"<\s*(?:script|foreignObject|image|iframe|object|embed)\b|\bon\w+\s*=|(?:href|src)\s*=\s*[""']\s*(?!#)|url\(\s*[""']?\s*(?!#)|<!ENTITY|javascript:",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSvg();

    [GeneratedRegex(@"^(?:no-?reply|do-?not-?reply|donotreply|info|news(?:letters?)?|notifications?|notify|support|hello|hi|team|service|billing|mail(?:er)?|marketing|updates?|alerts?|accounts?|security|orders?|receipts?|digest|contact|help|invoices?|sales|feedback|reply|bounces?|postmaster|admin|newsroom|store|shop|offers?|deals|events?|community|members?|customer(?:service|care)?)(?:[._+-].*)?$")]
    private static partial Regex RoleAddress();
}

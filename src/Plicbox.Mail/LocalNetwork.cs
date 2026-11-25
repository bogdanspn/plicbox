using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Plicbox.Mail;

/// <summary>
/// Which addresses belong to this computer or its local network. Nothing in a message may load from there: a "picture"
/// at 192.168.1.1 would really be a request to the router, sent from inside the network.
/// </summary>
public static class LocalNetwork
{
    /// <summary>
    /// For the browser (its --host-resolver-rules). These also hold when a picture on the web redirects there. Patterns
    /// are matched as text, so each names two numbers: "10.*" or "0.*" would stop names like 0.gravatar.com too.
    /// </summary>
    public static string BrowserRules { get; } = string.Join(", ", new[] { "localhost", "*.localhost", "*.local", "*.localdomain", "*.lan", "*.home", "*.home.arpa", "*.internal", "fritz.box", "*.fritz.box", "0.0.0.0", "127.0.*", "169.254.*", "192.168.*", "[*" }
        .Concat(Enumerable.Range(16, 16).Select(n => $"172.{n}.*"))
        .Concat(Enumerable.Range(64, 64).Select(n => $"100.{n}.*"))
        .Concat(Enumerable.Range(0, 256).Select(n => $"10.{n}.*"))
        .Select(pattern => $"MAP {pattern} ~NOTFOUND"));

    private static readonly ConcurrentDictionary<string, (Task<bool> Answer, DateTime Until)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a host is on the local network, by its address or by what its name stands for. A name that can't be looked up isn't.</summary>
    public static Task<bool> IsLocalAsync(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address)) return Task.FromResult(IsLocal(address));
        var now = DateTime.UtcNow;
        if (Known.TryGetValue(host, out var known) && known.Until > now) return known.Answer;
        if (Known.Count > 2000) Known.Clear();
        var answer = LookUpAsync(host);
        Known[host] = (answer, now.AddMinutes(10));
        return answer;
    }

    private static async Task<bool> LookUpAsync(string host)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            return (await Dns.GetHostAddressesAsync(host, timeout.Token)).Any(IsLocal);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any);
        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] is >= 64 and < 128, // carrier NAT, Tailscale
            169 => b[1] == 254,
            172 => b[1] is >= 16 and < 32,
            192 => b[1] == 168,
            >= 224 => true,
            _ => false,
        };
    }
}

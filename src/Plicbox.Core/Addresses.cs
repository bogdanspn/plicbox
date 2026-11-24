using System.Text.RegularExpressions;

namespace Plicbox.Core;

/// <summary>Reads address lists as typed or stored: "Ana Popescu &lt;ana@x.com&gt;, "Doe, John" &lt;j@y.org&gt;, bob@z.net".</summary>
public static partial class Addresses
{
    public static IReadOnlyList<(string Name, string Address)> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<(string, string)>();
        foreach (Match m in Entry().Matches(text))
        {
            var address = (m.Groups["angle"].Success ? m.Groups["angle"].Value : m.Groups["bare"].Value).Trim();
            if (!address.Contains('@')) continue;
            var name = m.Groups["name"].Value.Trim().Trim('"', '\'').Trim();
            result.Add((name == address ? "" : name, address));
        }
        return result;
    }

    /// <summary>"Ana Popescu &lt;ana@x.com&gt;", quoting names that contain commas.</summary>
    public static string Format(string name, string address) =>
        string.IsNullOrWhiteSpace(name) ? address
        : name.IndexOfAny([',', ';', '<', '>', '@', '"']) >= 0 ? $"\"{name.Replace("\"", "")}\" <{address}>"
        : $"{name} <{address}>";

    /// <summary>
    /// Whether a sender list entry covers an address: an exact address ("ana@x.com"), or a whole domain ("@x.com",
    /// which also covers its subdomains such as news.x.com).
    /// </summary>
    public static bool Covers(string entry, string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        entry = entry.Trim();
        address = address.Trim();
        if (!entry.StartsWith('@')) return entry.Equals(address, StringComparison.OrdinalIgnoreCase);
        var domain = address[(address.LastIndexOf('@') + 1)..];
        var listed = entry[1..];
        return domain.Equals(listed, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + listed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tidies what someone typed into a sender list: "Shop.Example" or "*@shop.example" -> "@shop.example"; addresses as they are.</summary>
    public static string? NormalizeEntry(string text)
    {
        var t = text.Trim().Trim('<', '>').TrimStart('*').ToLowerInvariant();
        if (t.Length == 0) return null;
        if (t.StartsWith('@')) return t.Length > 3 && t.Contains('.') ? t : null;
        if (t.Contains('@')) return t.IndexOf('@') > 0 && t[(t.IndexOf('@') + 1)..].Contains('.') ? t : null;
        return t.Contains('.') ? "@" + t : null; // a bare domain
    }

    [GeneratedRegex("""(?<name>"[^"]*"|[^,;<>"]*?)\s*<(?<angle>[^<>]+)>|(?<bare>[^\s,;<>"]+@[^\s,;<>"]+)""")]
    private static partial Regex Entry();
}

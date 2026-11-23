using Dapper;
using Plicbox.Core;

namespace Plicbox.Storage;

/// <summary>Trusted senders, cached in memory: the check runs every time a message is shown.</summary>
public sealed class TrustedSenders(Database db) : ITrustedSenders
{
    private readonly Lock _lock = new();
    private HashSet<string>? _cache;

    public bool IsTrusted(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        address = address.Trim();
        lock (_lock)
        {
            var set = Load();
            if (set.Contains(address)) return true;
            // "@shop.example" covers the domain and its subdomains (news.shop.example).
            var domain = address[(address.LastIndexOf('@') + 1)..];
            for (var d = domain; d.Contains('.'); d = d[(d.IndexOf('.') + 1)..])
                if (set.Contains("@" + d)) return true;
            return false;
        }
    }

    public void Trust(string address)
    {
        address = address.Trim();
        using var conn = db.Open();
        conn.Execute("INSERT OR IGNORE INTO trusted_senders (address) VALUES (@address)", new { address });
        lock (_lock) Load().Add(address);
    }

    public void Untrust(string address)
    {
        address = address.Trim();
        using var conn = db.Open();
        conn.Execute("DELETE FROM trusted_senders WHERE address = @address", new { address });
        lock (_lock) Load().Remove(address);
    }

    public IReadOnlyList<string> All()
    {
        lock (_lock) return Load().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private HashSet<string> Load()
    {
        if (_cache is not null) return _cache;
        using var conn = db.Open();
        return _cache = new HashSet<string>(conn.Query<string>("SELECT address FROM trusted_senders"), StringComparer.OrdinalIgnoreCase);
    }
}

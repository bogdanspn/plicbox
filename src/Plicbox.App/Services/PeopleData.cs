using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Mail;

namespace Plicbox.App.Services;

/// <summary>
/// Contacts plus "who you exchange mail with", read once and shared by the Contacts module and the composer's
/// suggestions. New mail and contact changes only mark it stale (cheap); it's re-read the next time someone asks,
/// and at most every 10 minutes otherwise (for mail you send, which doesn't announce itself).
/// </summary>
public sealed class PeopleData
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private readonly IContactRepository _repo;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _current;
    private DateTime _readAt;
    private int _version;

    /// <summary>A consistent set of contacts and mail statistics; <see cref="Version"/> changes whenever it's re-read.</summary>
    public sealed record Snapshot(IReadOnlyList<Contact> Contacts, IReadOnlyList<PersonStats> Stats, int Version);

    /// <summary>The data went stale (new mail, changed contacts): whoever shows it may want to refresh. Raised on a background thread.</summary>
    public event Action? Changed;

    public PeopleData(IContactRepository repo, MailEngine mail, ContactsEngine contacts)
    {
        _repo = repo;
        mail.Synced += (_, newInbox) => { if (newInbox > 0) MarkStale(); };
        contacts.Changed += MarkStale;
    }

    public bool IsStale => _current is null || DateTime.UtcNow - _readAt > MaxAge;

    public void MarkStale()
    {
        _current = null;
        Changed?.Invoke();
    }

    public async Task<Snapshot> GetAsync(CancellationToken ct = default)
    {
        if (!IsStale) return _current!;
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsStale) return _current!;
            var contacts = await Task.Run(() => _repo.GetAllAsync(ct), ct);
            var stats = await Task.Run(() => _repo.GetPeopleStatsAsync(ct), ct);
            _readAt = DateTime.UtcNow;
            return _current = new Snapshot(contacts, stats, Interlocked.Increment(ref _version));
        }
        finally
        {
            _lock.Release();
        }
    }
}

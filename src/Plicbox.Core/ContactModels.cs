namespace Plicbox.Core;

/// <summary>Where a contact lives: only here, in Google Contacts, or among Google's automatically saved "other contacts".</summary>
public enum ContactSource { Local = 0, Google = 1, GoogleOther = 2 }

/// <summary>An email address, phone number or website, with its label ("work", "mobile").</summary>
public sealed record LabeledValue(string Value, string? Label = null);

/// <summary>A postal address in parts (as Google stores them), with its label ("work", "home").</summary>
public sealed record PostalAddress(string? Street, string? City, string? PostalCode, string? Region, string? Country, string? Label = null)
{
    public bool IsEmpty => new[] { Street, City, PostalCode, Region, Country }.All(string.IsNullOrWhiteSpace);

    /// <summary>Street, then postal code and city, region, country, one per line (European order; reads fine anywhere).</summary>
    public string Formatted => string.Join("\n", new[]
    {
        Street,
        string.Join(" ", new[] { PostalCode, City }.Where(x => !string.IsNullOrWhiteSpace(x))),
        Region,
        Country,
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record Contact
{
    public long Id { get; init; }
    /// <summary>The Google account it syncs with; null for contacts kept only in Plicbox.</summary>
    public long? AccountId { get; init; }
    /// <summary>Google's resource name ("people/c123", "otherContacts/c456").</summary>
    public string? RemoteId { get; init; }
    public string? Etag { get; init; }
    public ContactSource Source { get; init; }
    public string DisplayName { get; init; } = "";
    public string? GivenName { get; init; }
    public string? FamilyName { get; init; }
    public string? Nickname { get; init; }
    public string? Organization { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public IReadOnlyList<LabeledValue> Emails { get; init; } = [];
    public IReadOnlyList<LabeledValue> Phones { get; init; } = [];
    public IReadOnlyList<LabeledValue> Websites { get; init; } = [];
    public DateOnly? Birthday { get; init; }
    /// <summary>A birthday without a year (Google allows it) is stored in year 4: a leap year, so 29 February fits.</summary>
    public const int NoYear = 4;
    public bool BirthdayHasYear => Birthday is { Year: > NoYear };
    public string? Notes { get; init; }
    public string? PhotoUrl { get; init; }
    /// <summary>The first address as one block of text (for the card); <see cref="Addresses"/> has them in parts.</summary>
    public string? Address { get; init; }
    public IReadOnlyList<PostalAddress> Addresses { get; init; } = [];
    public bool IsFavorite { get; init; }
    public DateTimeOffset Updated { get; init; }

    public string? PrimaryEmail => Emails.FirstOrDefault()?.Value;
    /// <summary>Auto-saved contacts are read-only in Google; they can be saved to your contacts instead.</summary>
    public bool IsSaved => Source != ContactSource.GoogleOther;
}

/// <summary>How much mail went each way with one address (worked out from the local mail database).</summary>
public sealed record PersonStats
{
    public required string Address { get; init; }
    public string Name { get; init; } = "";
    public int Received { get; init; }
    public int Sent { get; init; }
    public DateTimeOffset? First { get; init; }
    public DateTimeOffset? LastReceived { get; init; }
    public DateTimeOffset? LastSent { get; init; }
    /// <summary>Messages in the last 90 days, both ways.</summary>
    public int Recent { get; init; }

    public int Total => Received + Sent;
    public DateTimeOffset? Last => LastReceived > LastSent || LastSent is null ? LastReceived : LastSent;
}

/// <summary>A meeting with someone, for the contact card.</summary>
public sealed record MeetingRef(string Title, DateTimeOffset Start, bool IsAllDay);

public interface IContactRepository
{
    Task<IReadOnlyList<Contact>> GetAllAsync(CancellationToken ct = default);
    Task<Contact?> GetAsync(long id, CancellationToken ct = default);
    Task<long> UpsertAsync(Contact contact, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>Replaces every contact of one account and source with <paramref name="contacts"/> (a full sync).</summary>
    Task ReplaceAsync(long accountId, ContactSource source, IReadOnlyList<Contact> contacts, CancellationToken ct = default);
    /// <summary>Incremental sync: adds/updates <paramref name="changed"/> and removes <paramref name="deletedRemoteIds"/>.</summary>
    Task ApplyChangesAsync(long accountId, IReadOnlyList<Contact> changed, IReadOnlyList<string> deletedRemoteIds, CancellationToken ct = default);
    Task<string?> GetSyncTokenAsync(long accountId, ContactSource source, CancellationToken ct = default);
    Task SetSyncTokenAsync(long accountId, ContactSource source, string? token, CancellationToken ct = default);

    /// <summary>Everyone mail went to or came from, with counts (Sent, Drafts, Trash and Junk don't count as received).</summary>
    Task<IReadOnlyList<PersonStats>> GetPeopleStatsAsync(CancellationToken ct = default);
    /// <summary>Recent messages from or to an address, newest first.</summary>
    Task<IReadOnlyList<MessageSummary>> GetConversationsWithAsync(string address, int limit, CancellationToken ct = default);
    /// <summary>Upcoming meetings that list the address as a guest or organiser.</summary>
    Task<IReadOnlyList<MeetingRef>> GetMeetingsWithAsync(string address, DateTimeOffset from, int limit, CancellationToken ct = default);

    // Changes to an account's Google contacts made while Google couldn't be reached, oldest first.
    Task<IReadOnlyList<PendingContactChange>> GetPendingChangesAsync(long accountId, CancellationToken ct = default);
    /// <summary>Queues a change; a Save for a contact that already has one waiting isn't added twice (it sends the latest anyway).</summary>
    Task AddPendingChangeAsync(PendingContactChange change, CancellationToken ct = default);
    Task RemovePendingChangeAsync(long id, CancellationToken ct = default);
    /// <summary>Drops whatever waits for a contact (made offline and deleted before it was ever sent).</summary>
    Task RemovePendingChangesForAsync(long contactId, CancellationToken ct = default);
}

/// <summary>
/// Save: send the contact as it is here then (created, or updated, starred). Delete: remove <see cref="PendingContactChange.RemoteId"/>.
/// SaveOther: Gmail's automatically saved address <see cref="PendingContactChange.RemoteId"/> becomes a real contact.
/// </summary>
public enum ContactChangeKind { Save = 1, Delete = 2, SaveOther = 3 }

public sealed record PendingContactChange(long Id, long AccountId, ContactChangeKind Kind, long ContactId, string? RemoteId);

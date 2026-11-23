using System.Globalization;
using System.Text.Json;
using Dapper;
using Plicbox.Core;
using Microsoft.Data.Sqlite;

namespace Plicbox.Storage;

public sealed class ContactRepository(Database db) : IContactRepository
{
    private const string Columns = """
        id, account_id AS AccountId, remote_id AS RemoteId, etag, source, display_name AS DisplayName, given_name AS GivenName,
        family_name AS FamilyName, organization, job_title AS JobTitle, emails, phones, websites, birthday, notes,
        photo_url AS PhotoUrl, address, is_favorite AS IsFavorite, updated_utc AS UpdatedUtc, nickname, department, addresses
        """;

    // Roles that don't count as "they wrote to you": Sent, Drafts, Trash, Junk.
    private static readonly int[] NotReceived = [(int)FolderRole.Sent, (int)FolderRole.Drafts, (int)FolderRole.Trash, (int)FolderRole.Junk];

    public async Task<IReadOnlyList<Contact>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM contacts ORDER BY display_name COLLATE NOCASE", cancellationToken: ct)))
            .Select(r => r.ToModel()).ToList();
    }

    public async Task<Contact?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM contacts WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<long> UpsertAsync(Contact contact, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var id = await UpsertCoreAsync(conn, tx, contact, ct);
        tx.Commit();
        return id;
    }

    public async Task<IReadOnlyList<PendingContactChange>> GetPendingChangesAsync(long accountId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<(long Id, long AccountId, int Kind, long ContactId, string? RemoteId)>(new CommandDefinition(
            "SELECT id, account_id, kind, contact_id, remote_id FROM contact_changes WHERE account_id = @accountId ORDER BY id",
            new { accountId }, cancellationToken: ct)))
            .Select(r => new PendingContactChange(r.Id, r.AccountId, (ContactChangeKind)r.Kind, r.ContactId, r.RemoteId)).ToList();
    }

    public async Task AddPendingChangeAsync(PendingContactChange change, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO contact_changes (account_id, kind, contact_id, remote_id)
            SELECT @AccountId, @Kind, @ContactId, @RemoteId
            WHERE @Kind <> 1 OR NOT EXISTS (SELECT 1 FROM contact_changes WHERE kind = 1 AND contact_id = @ContactId)
            """, new { change.AccountId, Kind = (int)change.Kind, change.ContactId, change.RemoteId }, cancellationToken: ct));
    }

    public async Task RemovePendingChangeAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contact_changes WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task RemovePendingChangesForAsync(long contactId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contact_changes WHERE contact_id = @contactId AND kind <> 2",
            new { contactId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contacts WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task ReplaceAsync(long accountId, ContactSource source, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var keep = contacts.Select(c => c.RemoteId).ToHashSet();
        var existing = await conn.QueryAsync<(long Id, string? RemoteId)>(new CommandDefinition(
            "SELECT id, remote_id FROM contacts WHERE account_id = @accountId AND source = @source", new { accountId, source = (int)source }, tx, cancellationToken: ct));
        foreach (var gone in existing.Where(e => !keep.Contains(e.RemoteId)))
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contacts WHERE id = @Id", new { gone.Id }, tx, cancellationToken: ct));
        foreach (var c in contacts) await UpsertCoreAsync(conn, tx, c with { AccountId = accountId, Source = source }, ct);
        tx.Commit();
    }

    public async Task ApplyChangesAsync(long accountId, IReadOnlyList<Contact> changed, IReadOnlyList<string> deletedRemoteIds, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var remoteId in deletedRemoteIds)
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contacts WHERE account_id = @accountId AND remote_id = @remoteId",
                new { accountId, remoteId }, tx, cancellationToken: ct));
        foreach (var c in changed) await UpsertCoreAsync(conn, tx, c with { AccountId = accountId }, ct);
        tx.Commit();
    }

    public async Task<string?> GetSyncTokenAsync(long accountId, ContactSource source, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sync_token FROM contact_sync WHERE account_id = @accountId AND source = @source", new { accountId, source = (int)source }, cancellationToken: ct));
    }

    public async Task SetSyncTokenAsync(long accountId, ContactSource source, string? token, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO contact_sync (account_id, source, sync_token) VALUES (@accountId, @source, @token)
            ON CONFLICT (account_id, source) DO UPDATE SET sync_token = excluded.sync_token
            """, new { accountId, source = (int)source, token }, cancellationToken: ct));
    }

    private static async Task<long> UpsertCoreAsync(SqliteConnection conn, SqliteTransaction tx, Contact c, CancellationToken ct)
    {
        var args = Row.From(c);
        long id;
        if (c.Id > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE contacts SET account_id = @AccountId, remote_id = @RemoteId, etag = @Etag, source = @Source, display_name = @DisplayName,
                    given_name = @GivenName, family_name = @FamilyName, organization = @Organization, job_title = @JobTitle, emails = @Emails,
                    phones = @Phones, websites = @Websites, birthday = @Birthday, notes = @Notes, photo_url = @PhotoUrl, address = @Address,
                    is_favorite = @IsFavorite, updated_utc = @UpdatedUtc, nickname = @Nickname, department = @Department, addresses = @Addresses
                WHERE id = @Id
                """, args, tx, cancellationToken: ct));
            id = c.Id;
        }
        else
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO contacts (account_id, remote_id, etag, source, display_name, given_name, family_name, organization, job_title,
                                      emails, phones, websites, birthday, notes, photo_url, address, is_favorite, updated_utc,
                                      nickname, department, addresses)
                VALUES (@AccountId, @RemoteId, @Etag, @Source, @DisplayName, @GivenName, @FamilyName, @Organization, @JobTitle,
                        @Emails, @Phones, @Websites, @Birthday, @Notes, @PhotoUrl, @Address, @IsFavorite, @UpdatedUtc,
                        @Nickname, @Department, @Addresses)
                ON CONFLICT (account_id, remote_id) DO UPDATE SET
                    etag = excluded.etag, source = excluded.source, display_name = excluded.display_name, given_name = excluded.given_name,
                    family_name = excluded.family_name, organization = excluded.organization, job_title = excluded.job_title,
                    emails = excluded.emails, phones = excluded.phones, websites = excluded.websites, birthday = excluded.birthday,
                    notes = excluded.notes, photo_url = excluded.photo_url, address = excluded.address, is_favorite = excluded.is_favorite,
                    updated_utc = excluded.updated_utc, nickname = excluded.nickname, department = excluded.department, addresses = excluded.addresses
                RETURNING id
                """, args, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contact_emails WHERE contact_id = @id", new { id }, tx, cancellationToken: ct));
        foreach (var email in c.Emails.Select(e => e.Value.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO contact_emails (contact_id, address) VALUES (@id, @email)", new { id, email }, tx, cancellationToken: ct));
        return id;
    }

    // ───────── people from mail ─────────

    public async Task<IReadOnlyList<PersonStats>> GetPeopleStatsAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        var recentSince = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds();
        // One pass over the received mail. (Removing Gmail's per-label copies by Message-ID cost 5× the time for
        // a handful of duplicates in a real 85k-message mailbox, so they're simply counted.)
        var received = await conn.QueryAsync<(string Address, string Name, long Count, long First, long Last, long Recent)>(new CommandDefinition("""
            SELECT lower(m.from_address), max(m.from_name), count(*), min(m.date), max(m.date), sum(m.date > @recentSince)
            FROM messages m
            WHERE m.folder_id IN (SELECT id FROM folders WHERE role NOT IN @NotReceived AND is_virtual = 0) AND m.from_address <> ''
            GROUP BY 1
            """, new { NotReceived, recentSince }, cancellationToken: ct));
        var sent = await conn.QueryAsync<(string Recipients, long Date)>(new CommandDefinition("""
            SELECT m.recipients, m.date FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.role = @sentRole AND m.recipients IS NOT NULL AND m.recipients <> ''
            """, new { sentRole = (int)FolderRole.Sent }, cancellationToken: ct));

        var people = new Dictionary<string, PersonStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in received)
            people[r.Address] = new PersonStats
            {
                Address = r.Address, Name = r.Name, Received = (int)r.Count, First = FromUnix(r.First), LastReceived = FromUnix(r.Last), Recent = (int)r.Recent,
            };
        foreach (var (recipients, date) in sent)
        {
            var when = FromUnix(date);
            foreach (var (name, address) in Addresses.Parse(recipients))
            {
                var p = people.GetValueOrDefault(address) ?? new PersonStats { Address = address.ToLowerInvariant(), Name = name };
                people[address] = p with
                {
                    Name = p.Name.Length > 0 ? p.Name : name,
                    Sent = p.Sent + 1,
                    First = p.First is null || when < p.First ? when : p.First,
                    LastSent = p.LastSent is null || when > p.LastSent ? when : p.LastSent,
                    Recent = p.Recent + (date > recentSince ? 1 : 0),
                };
            }
        }
        return people.Values.ToList();
    }

    public async Task<IReadOnlyList<MessageSummary>> GetConversationsWithAsync(string address, int limit, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<MessageRow>(new CommandDefinition("""
            SELECT m.id AS Id, m.account_id AS AccountId, m.folder_id AS FolderId, m.subject AS Subject, m.from_name AS FromName,
                   m.from_address AS FromAddress, m.date AS DateUnix, m.preview AS Preview, m.message_id AS MessageId,
                   m.has_attachments AS HasAttachments, m.thread_key AS ThreadKey
            FROM messages m
            WHERE m.id IN (
                -- From them: the from_address index. To them: only Sent folders are searched (an OR here scanned every message).
                SELECT id FROM messages WHERE from_address = @address COLLATE NOCASE
                  AND folder_id IN (SELECT id FROM folders WHERE is_virtual = 0 AND role NOT IN (@trash, @junk))
                UNION ALL
                SELECT id FROM messages WHERE recipients LIKE @like AND folder_id IN (SELECT id FROM folders WHERE role = @sent))
            ORDER BY m.date DESC LIMIT @limit
            """, new { address, like = $"%{address}%", limit = limit * 3, trash = (int)FolderRole.Trash, junk = (int)FolderRole.Junk, sent = (int)FolderRole.Sent },
            cancellationToken: ct));
        return rows.DistinctBy(r => r.MessageId ?? r.Id.ToString(CultureInfo.InvariantCulture)).Take(limit).Select(r => new MessageSummary
        {
            Id = r.Id, AccountId = r.AccountId, FolderId = r.FolderId, Subject = r.Subject, FromName = r.FromName, FromAddress = r.FromAddress,
            Date = FromUnix(r.DateUnix), Preview = r.Preview, MessageId = r.MessageId, HasAttachments = r.HasAttachments, ThreadKey = r.ThreadKey,
        }).ToList();
    }

    public async Task<IReadOnlyList<MeetingRef>> GetMeetingsWithAsync(string address, DateTimeOffset from, int limit, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<(string Title, long Start, bool AllDay)>(new CommandDefinition("""
            SELECT title, start_utc, is_all_day FROM events
            WHERE end_utc > @from AND status <> 'cancelled' AND (attendees LIKE @like OR organizer_email = @address COLLATE NOCASE)
            ORDER BY start_utc LIMIT @limit
            """, new { from = from.ToUnixTimeSeconds(), like = $"%\"{address}\"%", address, limit }, cancellationToken: ct));
        return rows.Select(r => new MeetingRef(r.Title, FromUnix(r.Start), r.AllDay)).ToList();
    }

    private static DateTimeOffset FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private sealed class MessageRow
    {
        public long Id { get; init; }
        public long AccountId { get; init; }
        public long FolderId { get; init; }
        public string Subject { get; init; } = "";
        public string FromName { get; init; } = "";
        public string FromAddress { get; init; } = "";
        public long DateUnix { get; init; }
        public string Preview { get; init; } = "";
        public string? MessageId { get; init; }
        public bool HasAttachments { get; init; }
        public string? ThreadKey { get; init; }
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public long? AccountId { get; init; }
        public string? RemoteId { get; init; }
        public string? Etag { get; init; }
        public int Source { get; init; }
        public string DisplayName { get; init; } = "";
        public string? GivenName { get; init; }
        public string? FamilyName { get; init; }
        public string? Organization { get; init; }
        public string? JobTitle { get; init; }
        public string Emails { get; init; } = "[]";
        public string Phones { get; init; } = "[]";
        public string Websites { get; init; } = "[]";
        public string? Birthday { get; init; }
        public string? Notes { get; init; }
        public string? PhotoUrl { get; init; }
        public string? Address { get; init; }
        public bool IsFavorite { get; init; }
        public long UpdatedUtc { get; init; }
        public string? Nickname { get; init; }
        public string? Department { get; init; }
        public string? Addresses { get; init; }

        private static IReadOnlyList<LabeledValue> List(string json) =>
            string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<LabeledValue>>(json) ?? [];

        public Contact ToModel() => new()
        {
            Id = Id, AccountId = AccountId, RemoteId = RemoteId, Etag = Etag, Source = (ContactSource)Source, DisplayName = DisplayName,
            GivenName = GivenName, FamilyName = FamilyName, Organization = Organization, JobTitle = JobTitle,
            Emails = List(Emails), Phones = List(Phones), Websites = List(Websites),
            Birthday = Birthday is null ? null : DateOnly.ParseExact(Birthday, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Notes = Notes, PhotoUrl = PhotoUrl, Address = Address, IsFavorite = IsFavorite, Updated = FromUnix(UpdatedUtc),
            Nickname = Nickname, Department = Department,
            Addresses = string.IsNullOrEmpty(Addresses) ? [] : JsonSerializer.Deserialize<List<PostalAddress>>(Addresses) ?? [],
        };

        public static object From(Contact c) => new
        {
            c.Id, c.AccountId, c.RemoteId, c.Etag, Source = (int)c.Source, c.DisplayName, c.GivenName, c.FamilyName, c.Organization, c.JobTitle,
            Emails = JsonSerializer.Serialize(c.Emails), Phones = JsonSerializer.Serialize(c.Phones), Websites = JsonSerializer.Serialize(c.Websites),
            Birthday = c.Birthday?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), c.Notes, c.PhotoUrl,
            Address = c.Addresses.FirstOrDefault(a => !a.IsEmpty)?.Formatted ?? c.Address, c.IsFavorite,
            UpdatedUtc = c.Updated.ToUnixTimeSeconds(), c.Nickname, c.Department,
            Addresses = c.Addresses.Count == 0 ? null : JsonSerializer.Serialize(c.Addresses),
        };
    }
}

using System.Globalization;
using Dapper;
using Plicbox.Core;
using Microsoft.Data.Sqlite;

namespace Plicbox.Storage;

public sealed class TaskRepository(Database db) : ITaskRepository
{
    private const string ListColumns = "id, account_id AS AccountId, remote_id AS RemoteId, title, updated_utc AS UpdatedUtc";
    private const string TaskColumns = """
        id, list_id AS ListId, remote_id AS RemoteId, parent_remote AS ParentRemoteId, title, notes, due, completed,
        completed_utc AS CompletedUtc, position, updated_utc AS UpdatedUtc, message_ref AS MessageRef, web_link AS WebLink, dirty
        """;

    public async Task<IReadOnlyList<TaskList>> GetListsAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<ListRow>(new CommandDefinition($"SELECT {ListColumns} FROM task_lists ORDER BY account_id, title COLLATE NOCASE", cancellationToken: ct)))
            .Select(r => r.ToModel()).ToList();
    }

    public async Task<long> UpsertListAsync(TaskList list, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var args = new { list.Id, list.AccountId, list.RemoteId, list.Title, UpdatedUtc = list.Updated.ToUnixTimeSeconds() };
        if (list.Id > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition("UPDATE task_lists SET title = @Title, updated_utc = @UpdatedUtc, remote_id = @RemoteId WHERE id = @Id", args, cancellationToken: ct));
            return list.Id;
        }
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO task_lists (account_id, remote_id, title, updated_utc) VALUES (@AccountId, @RemoteId, @Title, @UpdatedUtc)
            ON CONFLICT (account_id, remote_id) DO UPDATE SET title = excluded.title, updated_utc = excluded.updated_utc
            RETURNING id
            """, args, cancellationToken: ct));
    }

    public async Task DeleteListAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM task_lists WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task DeleteMissingListsAsync(long accountId, IReadOnlyCollection<string> keepRemoteIds, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM task_lists WHERE account_id = @accountId AND remote_id NOT IN @keepRemoteIds",
            new { accountId, keepRemoteIds }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<TaskRow>(new CommandDefinition($"SELECT {TaskColumns} FROM tasks", cancellationToken: ct))).Select(r => r.ToModel()).ToList();
    }

    public async Task<TaskItem?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition($"SELECT {TaskColumns} FROM tasks WHERE id = @id", new { id }, cancellationToken: ct)))?.ToModel();
    }

    public async Task<long> UpsertAsync(TaskItem task, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await UpsertCoreAsync(conn, null, task, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM tasks WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task ReplaceTasksAsync(long listId, IReadOnlyList<TaskItem> tasks, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var existing = (await conn.QueryAsync<(long Id, string? RemoteId, string? MessageRef, bool Dirty)>(new CommandDefinition(
            "SELECT id, remote_id, message_ref, dirty FROM tasks WHERE list_id = @listId", new { listId }, tx, cancellationToken: ct))).ToList();
        var keep = tasks.Select(t => t.RemoteId).ToHashSet();
        // Only tasks the server knows about are replaced; ones still being created (no remote id yet) or changed here
        // and not sent yet stay as they are.
        foreach (var gone in existing.Where(e => e.RemoteId is not null && !e.Dirty && !keep.Contains(e.RemoteId)))
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM tasks WHERE id = @Id", new { gone.Id }, tx, cancellationToken: ct));
        var links = existing.Where(e => e.RemoteId is not null && e.MessageRef is not null).ToDictionary(e => e.RemoteId!, e => e.MessageRef);
        var dirty = existing.Where(e => e.Dirty && e.RemoteId is not null).Select(e => e.RemoteId!).ToHashSet();
        foreach (var t in tasks.Where(t => !dirty.Contains(t.RemoteId!)))
            await UpsertCoreAsync(conn, tx, t with { ListId = listId, MessageRef = t.MessageRef ?? links.GetValueOrDefault(t.RemoteId!), IsDirty = false }, ct);
        tx.Commit();
    }

    private static async Task<long> UpsertCoreAsync(SqliteConnection conn, SqliteTransaction? tx, TaskItem t, CancellationToken ct)
    {
        var args = new
        {
            t.Id, t.ListId, t.RemoteId, ParentRemote = t.ParentRemoteId, t.Title, t.Notes,
            Due = t.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Completed = t.IsCompleted,
            CompletedUtc = t.CompletedAt?.ToUnixTimeSeconds(), t.Position, UpdatedUtc = t.Updated.ToUnixTimeSeconds(), t.MessageRef, t.WebLink, Dirty = t.IsDirty,
        };
        if (t.Id > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE tasks SET list_id = @ListId, remote_id = @RemoteId, parent_remote = @ParentRemote, title = @Title, notes = @Notes, due = @Due,
                    completed = @Completed, completed_utc = @CompletedUtc, position = @Position, updated_utc = @UpdatedUtc,
                    message_ref = @MessageRef, web_link = @WebLink, dirty = @Dirty
                WHERE id = @Id
                """, args, tx, cancellationToken: ct));
            return t.Id;
        }
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO tasks (list_id, remote_id, parent_remote, title, notes, due, completed, completed_utc, position, updated_utc, message_ref, web_link, dirty)
            VALUES (@ListId, @RemoteId, @ParentRemote, @Title, @Notes, @Due, @Completed, @CompletedUtc, @Position, @UpdatedUtc, @MessageRef, @WebLink, @Dirty)
            ON CONFLICT (list_id, remote_id) DO UPDATE SET
                parent_remote = excluded.parent_remote, title = excluded.title, notes = excluded.notes, due = excluded.due,
                completed = excluded.completed, completed_utc = excluded.completed_utc, position = excluded.position,
                updated_utc = excluded.updated_utc, message_ref = coalesce(excluded.message_ref, tasks.message_ref), web_link = excluded.web_link,
                dirty = excluded.dirty
            RETURNING id
            """, args, tx, cancellationToken: ct));
    }

    private sealed class ListRow
    {
        public long Id { get; init; }
        public long? AccountId { get; init; }
        public string? RemoteId { get; init; }
        public string Title { get; init; } = "";
        public long UpdatedUtc { get; init; }
        public TaskList ToModel() => new() { Id = Id, AccountId = AccountId, RemoteId = RemoteId, Title = Title, Updated = DateTimeOffset.FromUnixTimeSeconds(UpdatedUtc) };
    }

    private sealed class TaskRow
    {
        public long Id { get; init; }
        public long ListId { get; init; }
        public string? RemoteId { get; init; }
        public string? ParentRemoteId { get; init; }
        public string Title { get; init; } = "";
        public string? Notes { get; init; }
        public string? Due { get; init; }
        public bool Completed { get; init; }
        public long? CompletedUtc { get; init; }
        public string? Position { get; init; }
        public long UpdatedUtc { get; init; }
        public string? MessageRef { get; init; }
        public string? WebLink { get; init; }
        public bool Dirty { get; init; }

        public TaskItem ToModel() => new()
        {
            Id = Id, ListId = ListId, RemoteId = RemoteId, ParentRemoteId = ParentRemoteId, Title = Title, Notes = Notes,
            Due = Due is null ? null : DateOnly.ParseExact(Due, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            IsCompleted = Completed, CompletedAt = CompletedUtc is { } c ? DateTimeOffset.FromUnixTimeSeconds(c) : null, Position = Position,
            Updated = DateTimeOffset.FromUnixTimeSeconds(UpdatedUtc), MessageRef = MessageRef, WebLink = WebLink, IsDirty = Dirty,
        };
    }
}

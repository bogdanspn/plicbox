namespace Plicbox.Core;

/// <summary>A task list: one of a Google account's lists, or a list kept only in Plicbox.</summary>
public sealed record TaskList
{
    public long Id { get; init; }
    /// <summary>The Google account it syncs with; null for a list kept only here.</summary>
    public long? AccountId { get; init; }
    public string? RemoteId { get; init; }
    public string Title { get; init; } = "Tasks";
    public DateTimeOffset Updated { get; init; }
}

public sealed record TaskItem
{
    public long Id { get; init; }
    public long ListId { get; init; }
    public string? RemoteId { get; init; }
    /// <summary>The parent task's remote id, for subtasks.</summary>
    public string? ParentRemoteId { get; init; }
    public string Title { get; init; } = "";
    public string? Notes { get; init; }
    /// <summary>Google Tasks keeps a date only (no time).</summary>
    public DateOnly? Due { get; init; }
    public bool IsCompleted { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>Google's ordering key within the list (and parent).</summary>
    public string? Position { get; init; }
    public DateTimeOffset Updated { get; init; }
    /// <summary>The email it was made from (its Message-ID), so the task can open it.</summary>
    public string? MessageRef { get; init; }
    /// <summary>Gmail's own link back to the message, when the task was created in Gmail.</summary>
    public string? WebLink { get; init; }
    /// <summary>Changed here but not yet sent to Google.</summary>
    public bool IsDirty { get; init; }
}

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskList>> GetListsAsync(CancellationToken ct = default);
    Task<long> UpsertListAsync(TaskList list, CancellationToken ct = default);
    Task DeleteListAsync(long id, CancellationToken ct = default);
    /// <summary>Removes an account's lists that the server no longer has.</summary>
    Task DeleteMissingListsAsync(long accountId, IReadOnlyCollection<string> keepRemoteIds, CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default);
    Task<TaskItem?> GetAsync(long id, CancellationToken ct = default);
    Task<long> UpsertAsync(TaskItem task, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>Replaces a list's tasks with what the server has (keeping the local link to an email).</summary>
    Task ReplaceTasksAsync(long listId, IReadOnlyList<TaskItem> tasks, CancellationToken ct = default);
}

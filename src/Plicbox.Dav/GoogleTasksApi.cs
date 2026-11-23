using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plicbox.Auth;
using Plicbox.Core;

namespace Plicbox.Dav;

/// <summary>Google Tasks API v1: the task lists and tasks shown in Gmail, Google Calendar and the Tasks app.</summary>
public sealed class GoogleTasksApi(HttpClient http, GoogleAuth google)
{
    private const string Base = "https://tasks.googleapis.com/tasks/v1";

    public async Task<IReadOnlyList<TaskList>> ListListsAsync(Account account, CancellationToken ct)
    {
        var result = new List<TaskList>();
        string? page = null;
        do
        {
            using var doc = await SendAsync(HttpMethod.Get, $"{Base}/users/@me/lists?maxResults=100" + Page(page), account, null, ct);
            foreach (var item in Items(doc))
                result.Add(new TaskList { AccountId = account.Id, RemoteId = Str(item, "id"), Title = Str(item, "title") ?? L.T("Tasks"), Updated = Time(item, "updated") ?? DateTimeOffset.UtcNow });
            page = Str(doc.RootElement, "nextPageToken");
        } while (page is not null);
        return result;
    }

    /// <summary>Every task of a list, completed ones included (Google hides cleared ones unless asked).</summary>
    public async Task<IReadOnlyList<TaskItem>> ListTasksAsync(Account account, string listId, CancellationToken ct)
    {
        var result = new List<TaskItem>();
        string? page = null;
        do
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"{Base}/lists/{Uri.EscapeDataString(listId)}/tasks?maxResults=100&showCompleted=true&showHidden=true" + Page(page), account, null, ct);
            foreach (var item in Items(doc))
                if (!Bool(item, "deleted") && Parse(item) is { } task) result.Add(task);
            page = Str(doc.RootElement, "nextPageToken");
        } while (page is not null);
        return result;
    }

    public async Task<TaskItem> CreateAsync(Account account, string listId, TaskItem task, CancellationToken ct)
    {
        var url = $"{Base}/lists/{Uri.EscapeDataString(listId)}/tasks" + (task.ParentRemoteId is { } parent ? $"?parent={Uri.EscapeDataString(parent)}" : "");
        using var doc = await SendAsync(HttpMethod.Post, url, account, ToJson(task), ct);
        return Parse(doc.RootElement)!;
    }

    public async Task<TaskItem> UpdateAsync(Account account, string listId, TaskItem task, CancellationToken ct)
    {
        var body = ToJson(task);
        // Clearing a due date or reopening a task needs explicit nulls.
        if (task.Due is null) body["due"] = null;
        if (!task.IsCompleted) body["completed"] = null;
        using var doc = await SendAsync(HttpMethod.Patch, $"{Base}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(task.RemoteId!)}", account, body, ct);
        return Parse(doc.RootElement)!;
    }

    public async Task DeleteAsync(Account account, string listId, string taskId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"{Base}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", account, null, ct);
    }

    public async Task<TaskList> CreateListAsync(Account account, string title, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{Base}/users/@me/lists", account, new JsonObject { ["title"] = title }, ct);
        return new TaskList { AccountId = account.Id, RemoteId = Str(doc.RootElement, "id"), Title = Str(doc.RootElement, "title") ?? title, Updated = DateTimeOffset.UtcNow };
    }

    public async Task RenameListAsync(Account account, string listId, string title, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Patch, $"{Base}/users/@me/lists/{Uri.EscapeDataString(listId)}", account, new JsonObject { ["title"] = title }, ct);
    }

    public async Task DeleteListAsync(Account account, string listId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"{Base}/users/@me/lists/{Uri.EscapeDataString(listId)}", account, null, ct);
    }

    // ───────── JSON ↔ model ─────────

    internal static TaskItem? Parse(JsonElement e)
    {
        if (Str(e, "id") is not { } id) return null;
        // Google keeps the due date as midnight UTC of that day; only the date part means anything.
        DateOnly? due = Str(e, "due") is { } d && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? DateOnly.FromDateTime(dt.UtcDateTime) : null;
        var link = e.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray().Select(l => Str(l, "link")).FirstOrDefault(l => l is not null)
            : null;
        return new TaskItem
        {
            RemoteId = id, ParentRemoteId = Str(e, "parent"), Title = Str(e, "title") ?? "", Notes = Str(e, "notes"), Due = due,
            IsCompleted = Str(e, "status") == "completed", CompletedAt = Time(e, "completed"), Position = Str(e, "position"),
            Updated = Time(e, "updated") ?? DateTimeOffset.UtcNow, WebLink = link ?? Str(e, "webViewLink"),
        };
    }

    internal static JsonObject ToJson(TaskItem t)
    {
        var body = new JsonObject
        {
            ["title"] = t.Title,
            ["notes"] = t.Notes ?? "",
            ["status"] = t.IsCompleted ? "completed" : "needsAction",
        };
        if (t.Due is { } due) body["due"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000Z";
        return body;
    }

    // ───────── HTTP ─────────

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, Account account, JsonNode? body, CancellationToken ct)
    {
        var token = await google.GetAccessTokenAsync(account.Id, account.Email, ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GoogleSignInRequiredException(L.F("Google sign-in for {0} has expired. Right-click the account and choose \"Sign in again\".", account.Email)),
            HttpStatusCode.Forbidden when text.Contains("SERVICE_DISABLED", StringComparison.Ordinal) || text.Contains("accessNotConfigured", StringComparison.Ordinal) =>
                new CalendarApiDisabledException(L.T("The Google Tasks API is switched off in your Google Cloud project. Enable it under APIs & Services → Library → Google Tasks API.")),
            HttpStatusCode.Forbidden => new CalendarPermissionException(L.F("{0} hasn't given Plicbox access to tasks yet.", account.Email)),
            _ => new HttpRequestException(L.F("Google Tasks answered {0}: {1}", (int)response.StatusCode, ErrorMessage(text) ?? response.ReasonPhrase), null, response.StatusCode),
        };
    }

    private static string? ErrorMessage(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement.GetProperty("error"), "message"); }
        catch (Exception) { return null; }
    }

    private static string Page(string? token) => token is null ? "" : $"&pageToken={Uri.EscapeDataString(token)}";
    private static IEnumerable<JsonElement> Items(JsonDocument doc) => doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray() : [];
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

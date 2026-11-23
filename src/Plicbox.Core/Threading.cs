namespace Plicbox.Core;

public sealed record ThreadHead(MessageSummary Latest, int Count);

public static class Threading
{
    /// <summary>
    /// Collapses messages sharing a thread key into one entry showing the newest message.
    /// Messages without a thread key stand alone. Output is ordered newest first.
    /// </summary>
    public static IReadOnlyList<ThreadHead> Collapse(IEnumerable<MessageSummary> messages) =>
        messages
            .GroupBy(m => m.ThreadKey is null ? $"#{m.Id}" : $"{m.AccountId}:{m.ThreadKey}")
            .Select(g =>
            {
                var latest = g.MaxBy(m => m.Date)!;
                // A thread reads as unread if any message in it is unread.
                return new ThreadHead(latest with { IsRead = g.All(m => m.IsRead) }, g.Count());
            })
            .OrderByDescending(t => t.Latest.Date)
            .ToList();
}

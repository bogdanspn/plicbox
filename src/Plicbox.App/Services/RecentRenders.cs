using Plicbox.Mail;

namespace Plicbox.App.Services;

/// <summary>
/// The last few messages as rendered for display (sanitized HTML), so going back to one is instant: cleaning a big
/// newsletter's HTML can take a quarter of a second. A message's stored body never changes, so an entry stays right
/// as long as it's looked up with the same options. Bounded by total size; the least recently shown go first.
/// </summary>
public static class RecentRenders
{
    private const long MaxChars = 4_000_000; // ≈ 8 MB of text

    /// <summary>
    /// What was rendered and how. The database can give a deleted message's id to the next one that arrives, so the
    /// message is also told apart by its Message-ID header, date and subject.
    /// </summary>
    public readonly record struct Key(long MessageId, string? HeaderId, long DateTicks, string Subject, bool RemoteContent, bool Tracking, bool PlainText)
    {
        public static Key For(Plicbox.Core.MessageSummary m, bool remoteContent, bool tracking, bool plainText) =>
            new(m.Id, m.MessageId, m.Date.UtcTicks, m.Subject, remoteContent, tracking, plainText);
    }

    private static readonly LinkedList<(Key Key, RenderedMessage Rendered)> Order = new(); // most recent first
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, RenderedMessage Rendered)>> ByKey = new();
    private static readonly object Gate = new();
    private static long _chars;

    public static RenderedMessage? Get(Key key)
    {
        lock (Gate)
        {
            if (!ByKey.TryGetValue(key, out var node)) return null;
            Order.Remove(node);
            Order.AddFirst(node);
            return node.Value.Rendered;
        }
    }

    /// <summary>Forgets everything (the message font or size changed, so every entry looks out of date).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Order.Clear();
            ByKey.Clear();
            _chars = 0;
        }
    }

    public static void Add(Key key, RenderedMessage rendered)
    {
        if (rendered.Html.Length > MaxChars / 4) return; // one giant message shouldn't push out everything else
        lock (Gate)
        {
            if (ByKey.Remove(key, out var old))
            {
                Order.Remove(old);
                _chars -= old.Value.Rendered.Html.Length;
            }
            ByKey[key] = Order.AddFirst((key, rendered));
            _chars += rendered.Html.Length;
            while (_chars > MaxChars && Order.Last is { } last)
            {
                Order.RemoveLast();
                ByKey.Remove(last.Value.Key);
                _chars -= last.Value.Rendered.Html.Length;
            }
        }
    }
}

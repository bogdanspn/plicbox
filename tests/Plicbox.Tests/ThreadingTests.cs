using Plicbox.Core;

namespace Plicbox.Tests;

public sealed class ThreadingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Collapses_by_thread_key_showing_newest_and_unread_if_any_unread()
    {
        MessageSummary[] list =
        [
            new() { Id = 1, AccountId = 1, ThreadKey = "t", Subject = "first", Date = T0, IsRead = false },
            new() { Id = 2, AccountId = 1, ThreadKey = "t", Subject = "reply", Date = T0.AddHours(1), IsRead = true },
            new() { Id = 3, AccountId = 1, Subject = "solo", Date = T0.AddHours(2), IsRead = true },
        ];

        var heads = Threading.Collapse(list);

        Assert.Equal(["solo", "reply"], heads.Select(h => h.Latest.Subject));
        Assert.Equal(2, heads[1].Count);
        Assert.False(heads[1].Latest.IsRead);
    }

    [Fact]
    public void Same_thread_key_in_different_accounts_stays_separate()
    {
        MessageSummary[] list =
        [
            new() { Id = 1, AccountId = 1, ThreadKey = "t", Date = T0 },
            new() { Id = 2, AccountId = 2, ThreadKey = "t", Date = T0 },
        ];
        Assert.Equal(2, Threading.Collapse(list).Count);
    }
}

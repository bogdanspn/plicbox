using Plicbox.Core;

namespace Plicbox.App;

/// <summary>Fictional tasks for --demo: something overdue, today's, the week ahead, one made from an email, a few done.</summary>
internal static class DemoTasks
{
    public static async Task SeedAsync(IAccountRepository accounts, ITaskRepository tasks, IMessageRepository messages)
    {
        var all = await accounts.GetAllAsync();
        if (all.Count == 0 || (await tasks.GetListsAsync()).Count > 0) return;
        var personal = all[0].Id;
        var work = all.Count > 1 ? all[1].Id : personal;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTimeOffset.UtcNow;
        var n = 0;

        async Task<long> List(long account, string title) =>
            await tasks.UpsertListAsync(new TaskList { AccountId = account, RemoteId = $"demo-list-{++n}", Title = title, Updated = now });
        async Task<string> Add(long list, string title, int? dueIn = null, string? notes = null, bool done = false, string? parent = null, string? messageRef = null)
        {
            var id = $"demo-task-{++n}";
            await tasks.UpsertAsync(new TaskItem
            {
                ListId = list, RemoteId = id, ParentRemoteId = parent, Title = title, Notes = notes, Due = dueIn is { } d ? today.AddDays(d) : null,
                IsCompleted = done, CompletedAt = done ? now.AddHours(-n) : null, Position = n.ToString("D20"), Updated = now, MessageRef = messageRef,
            });
            return id;
        }

        var mine = await List(personal, "My Tasks");
        var home = await List(personal, "Home");
        var job = await List(work, "My Tasks");
        var mail = (await messages.GetUnifiedInboxAsync(5)).FirstOrDefault(m => m.MessageId is not null);

        await Add(mine, "Renew passport", -2, "Photos are in the drawer. Book the appointment online.");
        var trip = await Add(mine, "Plan the weekend hike with Ana", 0, "Check the weather on Friday.");
        await Add(mine, "Pick a trail", parent: trip, done: true);
        await Add(mine, "Book the cabin", parent: trip);
        await Add(mine, "Pack the first-aid kit", parent: trip);
        await Add(mine, "Call the dentist", 1);
        if (mail is not null) await Add(mine, Mail.RuleMatcher.CleanSubject(mail.Subject), 3, messageRef: mail.MessageId);
        await Add(mine, "Read the book club pick", null);
        await Add(home, "Fix the kitchen tap", 4);
        await Add(home, "Order new plant pots", null);
        await Add(home, "Pay the electricity bill", done: true);
        await Add(job, "Send the Q4 roadmap draft", 0);
        await Add(job, "Review Diego's pull request", 2, "The billing one, not the search refactor.");
        await Add(job, "Prepare slides for Monday", 6);
        await Add(job, "Expense report for September", done: true);
    }
}

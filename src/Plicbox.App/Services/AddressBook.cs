using Plicbox.App.ViewModels;
using Plicbox.Core;

namespace Plicbox.App.Services;

/// <summary>One address the composer can suggest.</summary>
public sealed record AddressSuggestion(string Name, string Address, bool IsContact, double Score)
{
    public string Display => Name.Length > 0 ? Name : Address;
    public string Formatted => Addresses.Format(Name, Address);
}

/// <summary>
/// Everyone you can write to, for the composer's suggestions: saved contacts and every address you've exchanged mail
/// with, ranked by how much and how recently you write to them (people you write to count more than people who write to you).
/// Built once and refreshed when contacts change or after a few minutes.
/// </summary>
public sealed class AddressBook(PeopleData people, IAccountRepository accounts)
{
    private IReadOnlyList<(AddressSuggestion Entry, string Folded)>? _entries;
    private int _builtFrom = -1;

    public async Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(string query, IReadOnlySet<string> exclude, int max = 8)
    {
        var q = ContactsViewModel.Fold(query.Trim());
        if (q.Length == 0) return [];
        var entries = await EntriesAsync();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return entries
            .Where(e => !exclude.Contains(e.Entry.Address) && words.All(w => e.Folded.Contains(w, StringComparison.Ordinal)))
            .Select(e => (e.Entry, Rank: Rank(e.Entry, e.Folded, words[0])))
            .OrderByDescending(x => x.Rank).ThenByDescending(x => x.Entry.Score)
            .Take(max).Select(x => x.Entry).ToList();
    }

    // A word of the name starting with what's typed beats the address starting with it, which beats "somewhere inside".
    private static int Rank(AddressSuggestion e, string folded, string first)
    {
        var name = ContactsViewModel.Fold(e.Name);
        var rank = name.StartsWith(first, StringComparison.Ordinal) ? 3
            : name.Split(' ').Any(w => w.StartsWith(first, StringComparison.Ordinal)) ? 2
            : e.Address.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? 2
            : 0;
        return rank * 2 + (e.IsContact ? 1 : 0);
    }

    private async Task<IReadOnlyList<(AddressSuggestion Entry, string Folded)>> EntriesAsync()
    {
        // Rebuilt only when the shared people data was re-read (new mail, changed contacts).
        var snapshot = await people.GetAsync();
        if (_entries is not null && _builtFrom == snapshot.Version) return _entries;

        var own = (await accounts.GetAllAsync()).SelectMany(a => a.Identities.Select(i => i.Address).Append(a.Email))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var saved = snapshot.Contacts;
        var stats = snapshot.Stats.GroupBy(s => s.Address, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        double Score(PersonStats? s) => s is null || s.Last is not { } last ? 0
            : (s.Sent * 3 + s.Received * 0.5 + s.Recent * 2) * Math.Exp(-Math.Max(0, (now - last).TotalDays) / 120);

        var result = new Dictionary<string, AddressSuggestion>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in saved.OrderBy(c => c.Source == ContactSource.GoogleOther))
            foreach (var e in c.Emails.Select(e => e.Value.Trim()).Where(e => e.Contains('@') && !own.Contains(e)))
                if (!result.ContainsKey(e))
                    result[e] = new AddressSuggestion(c.DisplayName.Contains('@') ? "" : c.DisplayName, e, c.IsSaved, Score(stats.GetValueOrDefault(e)) + (c.IsSaved ? 1 : 0));
        // People you've written to, even if never saved; senders who never got a reply only if they look human.
        foreach (var s in stats.Values.Where(s => !own.Contains(s.Address) && !result.ContainsKey(s.Address) && (s.Sent > 0 || s.Received is > 0 and < 15)))
            result[s.Address] = new AddressSuggestion(s.Name.Contains('@') ? "" : s.Name.Trim('"', '\'', ' '), s.Address, false, Score(s));

        _entries = await Task.Run(() => result.Values.Select(e => (e, ContactsViewModel.Fold($"{e.Name} {e.Address}"))).ToList());
        _builtFrom = snapshot.Version;
        return _entries;
    }
}

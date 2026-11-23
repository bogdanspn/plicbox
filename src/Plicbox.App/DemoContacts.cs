using Plicbox.Core;

namespace Plicbox.App;

/// <summary>Fictional contacts for --demo, matching the demo mail: a favorite with a birthday coming up, a duplicate to merge, work contacts.</summary>
internal static class DemoContacts
{
    public static async Task SeedAsync(IAccountRepository accounts, IContactRepository contacts)
    {
        var all = await accounts.GetAllAsync();
        if (all.Count == 0) return;
        var personal = all[0].Id;
        var work = all.Count > 1 ? all[1].Id : personal;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var n = 0;

        async Task Add(long? account, ContactSource source, string given, string family, string email, string? phone = null, string? org = null,
            string? title = null, DateOnly? birthday = null, bool favorite = false, string? notes = null)
        {
            await contacts.UpsertAsync(new Contact
            {
                AccountId = account, RemoteId = account is null ? null : $"people/demo{++n}", Source = source,
                DisplayName = $"{given} {family}".Trim(), GivenName = given, FamilyName = family, Organization = org, JobTitle = title,
                Emails = [new LabeledValue(email, "work")], Phones = phone is null ? [] : [new LabeledValue(phone, "mobile")],
                Birthday = birthday, IsFavorite = favorite, Notes = notes, Updated = DateTimeOffset.UtcNow,
            });
        }

        await Add(personal, ContactSource.Google, "Ana", "Popescu", "ana@example.org", "+40 721 555 010", birthday: new DateOnly(1992, today.AddDays(5).Month, today.AddDays(5).Day),
            favorite: true, notes: "Hiking buddy. Prefers mornings.");
        await Add(null, ContactSource.Local, "Ana", "Popescu", "ana.popescu@work.example", org: "Work Example");
        await Add(work, ContactSource.Google, "Diego", "Ruiz", "diego@work.example", "+49 160 555 2020", "Work Example", "Engineering Lead");
        await Add(work, ContactSource.Google, "Laura", "Brandt", "laura@work.example", org: "Work Example", title: "CEO");
        await Add(personal, ContactSource.GoogleOther, "", "", "trips@tailspin.example");
    }
}

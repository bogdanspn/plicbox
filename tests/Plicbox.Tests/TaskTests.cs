using Plicbox.Core;
using Plicbox.Storage;

namespace Plicbox.Tests;

public sealed class TaskPhrasesTests
{
    private static readonly DateOnly Friday = new(2026, 10, 16); // a Friday

    [Theory]
    [InlineData("Call Ana tomorrow", "Call Ana", "2026-10-17")]
    [InlineData("Pay invoice by friday", "Pay invoice", "2026-10-16")]
    [InlineData("Review roadmap next friday", "Review roadmap", "2026-10-23")]
    [InlineData("Send report today", "Send report", "2026-10-16")]
    [InlineData("Book flights in 3 days", "Book flights", "2026-10-19")]
    [InlineData("Plan Q1 next week", "Plan Q1", "2026-10-19")]
    [InlineData("Steuer bis 15.11.", "Steuer", "2026-11-15")]
    [InlineData("Anruf Mama morgen", "Anruf Mama", "2026-10-17")]
    [InlineData("Geschenk kaufen am Mittwoch", "Geschenk kaufen", "2026-10-21")]
    [InlineData("Dentist 3 Nov", "Dentist", "2026-11-03")]
    [InlineData("Renew passport March 2", "Renew passport", "2027-03-02")]   // already past this year: next year
    [InlineData("Clean garage this weekend", "Clean garage", "2026-10-17")]
    [InlineData("Termin 12.01.2027", "Termin", "2027-01-12")]
    [InlineData("Call Ana friday", "Call Ana", "2026-10-16")]
    [InlineData("Send slides by 20/10", "Send slides", "2026-10-20")]
    [InlineData("Sună-o pe Ana mâine", "Sună-o pe Ana", "2026-10-17")]
    [InlineData("Plătește chiria până vineri", "Plătește chiria", "2026-10-16")]
    [InlineData("Trimite raportul vinerea viitoare", "Trimite raportul", "2026-10-23")]
    [InlineData("Ședință luni", "Ședință", "2026-10-19")]
    [InlineData("Rezervă zborul peste 3 zile", "Rezervă zborul", "2026-10-19")]
    [InlineData("Reînnoiește abonamentul în 2 luni", "Reînnoiește abonamentul", "2026-12-16")]
    [InlineData("Dentist pe 3 noiembrie", "Dentist", "2026-11-03")]
    [InlineData("Taxe până pe 15.11.", "Taxe", "2026-11-15")]
    [InlineData("Planificare săptămâna viitoare", "Planificare", "2026-10-19")]
    [InlineData("Curățenie poimâine", "Curățenie", "2026-10-18")]
    [InlineData("Llamar a Ana mañana", "Llamar a Ana", "2026-10-17")]
    [InlineData("Enviar informe el viernes", "Enviar informe", "2026-10-16")]
    [InlineData("Revisar presupuesto el próximo lunes", "Revisar presupuesto", "2026-10-19")]
    [InlineData("Enviar el informe el viernes que viene", "Enviar el informe", "2026-10-23")]
    [InlineData("Reservar vuelos en 3 días", "Reservar vuelos", "2026-10-19")]
    [InlineData("Renovar suscripción dentro de 2 semanas", "Renovar suscripción", "2026-10-30")]
    [InlineData("Dentista el 3 de noviembre", "Dentista", "2026-11-03")]
    [InlineData("Impuestos para el 15.11.", "Impuestos", "2026-11-15")]
    [InlineData("Planificación la semana que viene", "Planificación", "2026-10-19")]
    [InlineData("Limpiar el garaje este fin de semana", "Limpiar el garaje", "2026-10-17")]
    [InlineData("Limpieza pasado mañana", "Limpieza", "2026-10-18")]
    [InlineData("Llamar al banco el martes por la mañana", "Llamar al banco por la mañana", "2026-10-20")] // "la mañana" is the morning
    public void Reads_the_due_date_and_cleans_the_title(string typed, string title, string due)
    {
        var (t, d) = TaskPhrases.Parse(typed, Friday);
        Assert.Equal(title, t);
        Assert.Equal(DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture), d);
    }

    [Theory]
    [InlineData("Read Monday Morning Memo")]   // a weekday inside a title is just a word
    [InlineData("Buy 2.5 kg of rice")]          // numbers aren't dates unless they clearly are
    [InlineData("Version 1.2 release notes")]
    [InlineData("Water the plants")]
    [InlineData("May the 4th party prep")]
    [InlineData("Mai 2 rapoarte de trimis")]     // Romanian "2 more reports", not May 2
    public void Leaves_ordinary_titles_alone(string typed)
    {
        var (t, d) = TaskPhrases.Parse(typed, Friday);
        Assert.Equal(typed, t);
        Assert.Null(d);
    }
}

public sealed class TaskRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly TaskRepository _repo;

    public TaskRepositoryTests()
    {
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Migrate();
        _repo = new TaskRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Server_refresh_keeps_the_email_link_and_unsent_tasks()
    {
        var list = await _repo.UpsertListAsync(new TaskList { Title = "My Tasks", Updated = DateTimeOffset.UtcNow });
        await _repo.UpsertAsync(new TaskItem { ListId = list, RemoteId = "a", Title = "Reply to Ana", MessageRef = "<m1@x>", Due = new DateOnly(2026, 10, 20) });
        await _repo.UpsertAsync(new TaskItem { ListId = list, RemoteId = "b", Title = "Gone on the server" });
        await _repo.UpsertAsync(new TaskItem { ListId = list, Title = "Not sent yet" });

        await _repo.ReplaceTasksAsync(list, [new TaskItem { RemoteId = "a", Title = "Reply to Ana (renamed)", IsCompleted = true }]);

        var all = (await _repo.GetAllAsync()).OrderBy(t => t.Title).ToList();
        Assert.Equal(["Not sent yet", "Reply to Ana (renamed)"], all.Select(t => t.Title));
        Assert.Equal(("<m1@x>", true), (all[1].MessageRef, all[1].IsCompleted));
    }

    [Fact]
    public async Task Server_refresh_does_not_undo_changes_not_sent_yet()
    {
        var list = await _repo.UpsertListAsync(new TaskList { Title = "My Tasks", Updated = DateTimeOffset.UtcNow });
        await _repo.UpsertAsync(new TaskItem { ListId = list, RemoteId = "a", Title = "Renamed offline", IsDirty = true });
        await _repo.UpsertAsync(new TaskItem { ListId = list, RemoteId = "b", Title = "Ticked offline", IsCompleted = true, IsDirty = true });

        // The server still has the old title, and no longer lists b (it can't be dropped before it's sent).
        await _repo.ReplaceTasksAsync(list, [new TaskItem { RemoteId = "a", Title = "Old title" }]);

        var all = (await _repo.GetAllAsync()).OrderBy(t => t.RemoteId).ToList();
        Assert.Equal(["Renamed offline", "Ticked offline"], all.Select(t => t.Title));
        Assert.All(all, t => Assert.True(t.IsDirty));
    }
}

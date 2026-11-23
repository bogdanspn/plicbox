using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Mail;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plicbox.App.ViewModels;

public enum PeopleList { All, Favorites, Frequent, Recent, KeepInTouch, NotSaved, Birthdays, Companies, Services }

/// <summary>A smart list in the sidebar, with how many people are in it.</summary>
public sealed partial class PeopleListItem(PeopleList kind, string title, string glyph, string hint) : ObservableObject
{
    public PeopleList Kind { get; } = kind;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Hint { get; } = hint;
    [ObservableProperty] private int _count;
}

/// <summary>
/// One person: a saved contact, Gmail's automatically saved address, or simply someone you've exchanged mail with
/// (often all three at once, merged by email address).
/// </summary>
public sealed partial class PersonViewModel : ObservableObject
{
    private static readonly Brush[] Palette = new[]
    {
        "#E91E63", "#9C27B0", "#3F51B5", "#03A9F4", "#009688", "#4CAF50", "#FF9800", "#795548", "#607D8B", "#F44336",
    }.Select(c => (Brush)new BrushConverter().ConvertFromString(c)!).Select(b => { b.Freeze(); return b; }).ToArray();

    public required string Key { get; init; }
    public required string Name { get; init; }
    public Contact? Contact { get; init; }
    public IReadOnlyList<string> Emails { get; init; } = [];
    public PersonStats? Stats { get; init; }
    public bool IsAutomated { get; init; }
    public string? CompanyName { get; init; }

    public string? Email => Emails.FirstOrDefault();
    public bool IsSaved => Contact?.IsSaved == true;
    public bool IsFavorite => Contact?.IsFavorite == true;
    public string Initial => Name.Length > 0 && char.IsLetterOrDigit(Name[0]) ? char.ToUpper(Name[0], CultureInfo.CurrentCulture).ToString() : "?";
    public Brush AvatarBrush => Palette[(int)((uint)StableHash(Email ?? Name) % Palette.Length)];
    [ObservableProperty] private ImageSource? _photo;

    public string Subtitle =>
        Contact is { JobTitle: { Length: > 0 } title } && CompanyName is { Length: > 0 } company ? $"{title}, {company}"
        : Contact?.JobTitle is { Length: > 0 } t ? t
        : CompanyName is { Length: > 0 } c ? $"{c}, {Email}"
        : Email ?? "";

    /// <summary>"3 days ago" on the right of the row.</summary>
    public string LastText => Stats?.Last is { } last ? ContactsViewModel.Ago(last) : "";

    /// <summary>How much you have to do with them now: lots of mail lately, both ways, counts most. Worked out once.</summary>
    public double Score => _score ??= ComputeScore();
    private double? _score;

    private double ComputeScore()
    {
        if (Stats is not { } s || s.Last is not { } last) return 0;
        var days = Math.Max(0, (DateTimeOffset.UtcNow - last).TotalDays);
        return (s.Recent * 3 + s.Sent * 2 + s.Received) * Math.Exp(-days / 60);
    }

    /// <summary>Name, addresses, company and title, folded for search (accents and case removed). Worked out once.</summary>
    internal string SearchText => _searchText ??= ContactsViewModel.Fold($"{Name} {string.Join(' ', Emails)} {CompanyName} {Contact?.JobTitle} {Contact?.Organization}");
    private string? _searchText;
    internal string FoldedName => _foldedName ??= ContactsViewModel.Fold(Name);
    private string? _foldedName;

    /// <summary>The next birthday (this year or next), if known.</summary>
    public DateOnly? NextBirthday
    {
        get
        {
            if (Contact?.Birthday is not { } b) return null;
            var today = DateOnly.FromDateTime(DateTime.Today);
            DateOnly On(int year) => new(year, b.Month, Math.Min(b.Day, DateTime.DaysInMonth(year, b.Month)));
            var next = On(today.Year);
            return next < today ? On(today.Year + 1) : next;
        }
    }

    private static int StableHash(string s)
    {
        var h = 23;
        foreach (var c in s.ToLowerInvariant()) h = h * 31 + c;
        return h;
    }
}

/// <summary>A detail found in someone's signature, offered with an "Add" button.</summary>
public sealed record SignatureSuggestion(string Kind, string Value, string Label);

/// <summary>The right-hand card: everything about the selected person, loaded when they're selected.</summary>
public sealed partial class PersonCard : ObservableObject
{
    public required PersonViewModel Person { get; init; }
    public ObservableCollection<MessageSummary> Conversations { get; } = [];
    public ObservableCollection<SignatureSuggestion> Suggestions { get; } = [];
    public ObservableCollection<PersonViewModel> Duplicates { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInTouch))] private string? _lastText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInTouch))] private string? _countsText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInTouch))] private string? _rhythmText;
    [ObservableProperty] private string? _nudgeText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInTouch))] private string? _meetingText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInTouch))] private string? _birthdayText;
    /// <summary>Anything for the "In touch" tiles (a number-only contact has none).</summary>
    public bool HasInTouch => (LastText ?? CountsText ?? RhythmText ?? MeetingText ?? BirthdayText) is not null;
    [ObservableProperty] private string? _saveHint;
    [ObservableProperty] private string? _sourceText;

    public bool HasPhones => Person.Contact?.Phones.Count > 0;
    public IReadOnlyList<LabeledValue> Phones => Person.Contact?.Phones ?? [];
    public IReadOnlyList<LabeledValue> EmailList => Person.Contact?.Emails is { Count: > 0 } e ? e : Person.Emails.Select(x => new LabeledValue(x)).ToList();
    public IReadOnlyList<LabeledValue> Websites => Person.Contact?.Websites ?? [];
    public string? Address => Person.Contact?.Address;
    public string? Notes => Person.Contact?.Notes;
}

/// <summary>The contact editor's fields (new contact, edit, or "save" someone from your mail).</summary>
/// <summary>One email address, phone number or website in the editor, with its label (Work, Home, Mobile…).</summary>
public sealed partial class EditableValue : ObservableObject
{
    private static readonly IReadOnlyList<Option<string>> EmailLabels = [new("work", L.T("Work")), new("home", L.T("Home")), new("other", L.T("Other"))];
    private static readonly IReadOnlyList<Option<string>> PhoneLabels = [new("mobile", L.T("Mobile")), new("work", L.T("Work")), new("home", L.T("Home")), new("other", L.T("Other"))];
    private static readonly IReadOnlyList<Option<string>> WebLabels = [new("work", L.T("Work")), new("homePage", L.T("Home page")), new("blog", L.T("Blog")), new("other", L.T("Other"))];

    public EditableValue(string kind, string value, string? label)
    {
        Kind = kind;
        _value = value;
        _label = Labels.FirstOrDefault(l => string.Equals(l.Value, label, StringComparison.OrdinalIgnoreCase))
                 ?? (label is { Length: > 0 } custom ? new Option<string>(custom, char.ToUpper(custom[0], CultureInfo.CurrentCulture) + custom[1..]) : Labels[0]);
    }

    /// <summary>"email", "phone" or "website".</summary>
    public string Kind { get; }
    [ObservableProperty] private string _value;
    [ObservableProperty] private Option<string> _label;

    public IReadOnlyList<Option<string>> Labels => Kind switch { "email" => EmailLabels, "phone" => PhoneLabels, _ => WebLabels };
    public string Glyph => Kind switch { "email" => "", "phone" => "", _ => "" };
    public string Hint => Kind switch { "email" => "name@example.com", "phone" => "+49 151 2345 6789", _ => "example.com" };

    public LabeledValue? ToModel() => Value.Trim().Length == 0 ? null : new LabeledValue(Value.Trim(), Label.Value);
}

/// <summary>A postal address in the editor, in parts.</summary>
public sealed partial class EditableAddress : ObservableObject
{
    public static IReadOnlyList<Option<string>> Labels { get; } = [new("work", L.T("Work")), new("home", L.T("Home")), new("other", L.T("Other"))];

    /// <summary>Countries in the user's language, for the Country box (anything else can still be typed).</summary>
    public static IReadOnlyList<string> Countries { get; } = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => { try { return new RegionInfo(c.Name).DisplayName; } catch (ArgumentException) { return null; } })
        .OfType<string>().Distinct().Order(StringComparer.CurrentCulture).ToList();

    public EditableAddress(PostalAddress? a = null)
    {
        _street = a?.Street ?? "";
        _city = a?.City ?? "";
        _postalCode = a?.PostalCode ?? "";
        _region = a?.Region ?? "";
        _country = a?.Country ?? (a is null ? RegionInfo.CurrentRegion.DisplayName : "");
        _label = Labels.FirstOrDefault(l => string.Equals(l.Value, a?.Label, StringComparison.OrdinalIgnoreCase)) ?? Labels[0];
    }

    [ObservableProperty] private string _street;
    [ObservableProperty] private string _city;
    [ObservableProperty] private string _postalCode;
    [ObservableProperty] private string _region;
    [ObservableProperty] private string _country;
    [ObservableProperty] private Option<string> _label;

    public PostalAddress ToModel() => new(Blank(Street), Blank(City), Blank(PostalCode), Blank(Region), Blank(Country), Label.Value);
    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>The contact editor (new contact, edit, or "save" someone from your mail): eM Client's sections, on one page.</summary>
public sealed partial class ContactEditor : ObservableObject
{
    public Contact? Original { get; init; }
    /// <summary>The person being edited, for the picture; null for a brand-new contact.</summary>
    public PersonViewModel? Person { get; init; }
    [ObservableProperty] private string _givenName = "";
    [ObservableProperty] private string _familyName = "";
    [ObservableProperty] private string _nickname = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _jobTitle = "";
    [ObservableProperty] private string _department = "";
    [ObservableProperty] private DateTime? _birthday;
    /// <summary>Google allows birthdays without a year (only the day is remembered then).</summary>
    [ObservableProperty] private bool _birthdayNoYear;
    [ObservableProperty] private string _notes = "";
    /// <summary>Where a new contact is saved: a Google account, or null for "only in Plicbox".</summary>
    [ObservableProperty] private Option<long?>? _saveTo;
    public ObservableCollection<EditableValue> Emails { get; } = [];
    public ObservableCollection<EditableValue> Phones { get; } = [];
    public ObservableCollection<EditableValue> Websites { get; } = [];
    public ObservableCollection<EditableAddress> Addresses { get; } = [];
    public bool IsNew => Original is null || !Original.IsSaved;
    public string Heading => Original is { IsSaved: true } ? L.T("Edit contact") : L.T("New contact");
    /// <summary>Where it can be saved (Google accounts, or only in Plicbox).</summary>
    public IReadOnlyList<Option<long?>> SaveTargets { get; init; } = [];
    /// <summary>Saves the contact; false keeps the window open (nothing to save, or it failed).</summary>
    public Func<ContactEditor, Task<bool>>? Save { get; init; }

    [RelayCommand] private void AddEmail() => Emails.Add(new EditableValue("email", "", Emails.Count == 0 ? "work" : "home"));
    [RelayCommand] private void AddPhone() => Phones.Add(new EditableValue("phone", "", Phones.Count == 0 ? "mobile" : "work"));
    [RelayCommand] private void AddWebsite() => Websites.Add(new EditableValue("website", "", "work"));
    [RelayCommand] private void AddAddress() =>
        Addresses.Add(new EditableAddress(new PostalAddress(null, null, null, null, RegionInfo.CurrentRegion.DisplayName, Addresses.Count == 0 ? "work" : "home")));

    [RelayCommand]
    private void RemoveValue(EditableValue value)
    {
        Emails.Remove(value);
        Phones.Remove(value);
        Websites.Remove(value);
    }

    [RelayCommand] private void RemoveAddress(EditableAddress address) => Addresses.Remove(address);
    public string Initial => GivenName.Length > 0 ? char.ToUpper(GivenName[0], CultureInfo.CurrentCulture).ToString() : "?";

    partial void OnGivenNameChanged(string value) => OnPropertyChanged(nameof(Initial));
}

public sealed partial class ContactsViewModel : ObservableObject
{
    private readonly ContactsEngine _engine;
    private readonly PeopleData _peopleData;
    private readonly IContactRepository _repo;
    private readonly IAccountRepository _accounts;
    private readonly MailEngine _mail;
    private readonly IBodyStore _bodies;
    private readonly Pictures _pictures;
    private readonly Dialogs _dialogs;
    private readonly ILogger<ContactsViewModel> _log;
    private readonly SynchronizationContext _ui;
    private List<PersonViewModel> _everyone = [];
    private Dictionary<string, long> _own = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _reloadCts, _cardCts;
    private long? _bannerAccountId;
    private int _builtFrom = -1;
    // Signatures already read, by address: revisiting a card is instant and never downloads again.
    private readonly Dictionary<string, SignatureFacts> _signatures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The module is on screen: changes refresh it right away (otherwise on the next visit).</summary>
    public bool IsShown { get; set; }

    public ContactsViewModel(ContactsEngine engine, PeopleData people, IContactRepository repo, IAccountRepository accounts, MailEngine mail,
        IBodyStore bodies, Pictures pictures, Dialogs dialogs, ILogger<ContactsViewModel> log)
    {
        (_engine, _peopleData, _repo, _accounts, _mail, _bodies, _pictures, _dialogs, _log) = (engine, people, repo, accounts, mail, bodies, pictures, dialogs, log);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        people.Changed += () => _ui.Post(_ => { if (IsShown) _ = ReloadSoonAsync(); }, null);
        engine.StatusChanged += () => _ui.Post(_ => UpdateBanner(), null);
        engine.ChangeRefused += message => _ui.Post(_ => _dialogs.Error(message), null);
        _selectedList = Lists[0];
    }

    // ───────── hooks into the other modules (set by the main window) ─────────

    public Action<string>? ComposeRequested { get; set; }
    public Func<long, Task>? OpenMessageRequested { get; set; }
    public Action<string>? ShowMailRequested { get; set; }
    public Action<string, string>? MeetingRequested { get; set; }
    public Action<string, string>? CreateRuleRequested { get; set; }

    public ObservableCollection<PeopleListItem> Lists { get; } =
    [
        new(PeopleList.All, L.T("All people"), "", L.T("Your contacts and everyone you write to")),
        new(PeopleList.Favorites, L.T("Favorites"), "", L.T("Starred contacts")),
        new(PeopleList.Frequent, L.T("Frequent"), "", L.T("Who you deal with most right now")),
        new(PeopleList.Recent, L.T("Recently in touch"), "", L.T("Mail in the last 30 days")),
        new(PeopleList.KeepInTouch, L.T("Keep in touch"), "", L.T("People you used to write to often, gone quiet")),
        new(PeopleList.NotSaved, L.T("Not saved yet"), "", L.T("People you write to who aren't contacts")),
        new(PeopleList.Birthdays, L.T("Birthdays"), Glyphs.Gift, L.T("In the next 60 days")),
        new(PeopleList.Companies, L.T("Companies"), "", L.T("Grouped by organisation")),
        new(PeopleList.Services, L.T("Services & newsletters"), "", L.T("Automated senders: who mails you most")),
    ];

    /// <summary>The people shown. Replaced as a whole (one redraw) rather than filled row by row.</summary>
    [ObservableProperty] private IReadOnlyList<PersonViewModel> _people = [];
    public List<Option<long?>> SaveTargets { get; } = [];
    [ObservableProperty] private PeopleListItem _selectedList;
    [ObservableProperty] private PersonViewModel? _selected;
    [ObservableProperty] private PersonCard? _card;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _listTitle = L.T("All people");
    [ObservableProperty] private string _listHint = "";
    [ObservableProperty] private bool _groupByCompany;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private string? _bannerAction;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _refreshToolTip = L.T("Refresh (F5)");
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _cardEmptyTitle = "";
    [ObservableProperty] private string _cardEmptyText = "";

    partial void OnSelectedListChanged(PeopleListItem value) => Refilter();
    partial void OnSearchTextChanged(string value) => Refilter();

    [RelayCommand] private void ClearSearch() => SearchText = "";

    partial void OnSelectedChanged(PersonViewModel? value)
    {
        _ = LoadCardAsync(value);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAddress));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteLabel));
    }

    // The toolbar's actions follow the selected person.
    public bool HasSelection => Selected is not null;
    public bool HasAddress => Selected?.Email is not null;
    public bool CanDelete => Selected?.IsSaved == true;
    public string EditLabel => Selected is { IsSaved: false } ? L.T("Save") : L.T("Edit");
    public string FavoriteGlyph => Selected?.IsFavorite == true ? "\uE735" : "\uE734";
    public string FavoriteLabel => Selected?.IsFavorite == true ? L.T("Unfavorite") : L.T("Favorite");

    // ───────── loading ─────────

    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            var accounts = await _accounts.GetAllAsync();
            _own = accounts.SelectMany(a => a.Identities.Select(i => (i.Address, a.Id)).Append((a.Email, a.Id)))
                .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);
            SaveTargets.Clear();
            foreach (var a in accounts.Where(a => a.Auth == AuthKind.GoogleOAuth)) SaveTargets.Add(new Option<long?>(a.Id, $"Google ({a.SidebarTitle})"));
            SaveTargets.Add(new Option<long?>(null, L.T("Only in Plicbox")));

            var snapshot = await _peopleData.GetAsync();
            if (snapshot.Version != _builtFrom)
            {
                _everyone = await Task.Run(() => Build(snapshot.Contacts, snapshot.Stats));
                _builtFrom = snapshot.Version;
                Refilter();
            }
            UpdateBanner();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading contacts failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Coming back to the module: refresh only if something changed meanwhile.</summary>
    public Task EnsureFreshAsync() => _builtFrom < 0 || _peopleData.IsStale ? LoadAsync() : Task.CompletedTask;

    private async Task ReloadSoonAsync()
    {
        _reloadCts?.Cancel();
        var cts = _reloadCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            var keep = Selected?.Key;
            await LoadAsync();
            if (keep is not null) Selected = People.FirstOrDefault(p => p.Key == keep) ?? Selected;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Merges saved contacts, Gmail's saved addresses and mail statistics into one person per email address.</summary>
    private List<PersonViewModel> Build(IReadOnlyList<Contact> contacts, IReadOnlyList<PersonStats> stats)
    {
        var byAddress = stats.Where(s => !_own.ContainsKey(s.Address)).ToDictionary(s => s.Address, StringComparer.OrdinalIgnoreCase);
        // A domain says "company" only when several different people write from it (a personal domain like ana-popescu.ro is one person's own).
        var allAddresses = byAddress.Keys.Concat(contacts.SelectMany(c => c.Emails.Select(e => e.Value))).Distinct(StringComparer.OrdinalIgnoreCase);
        _sharedDomains = allAddresses.Where(a => a.Contains('@')).GroupBy(a => LinkSafety.RegistrableDomain(a[(a.LastIndexOf('@') + 1)..]))
            .Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new List<PersonViewModel>();

        // Saved contacts first, then Gmail's auto-saved ones for addresses no saved contact has.
        foreach (var c in contacts.OrderBy(c => c.Source == ContactSource.GoogleOther))
        {
            var emails = c.Emails.Select(e => e.Value.Trim()).Where(e => e.Length > 0 && !_own.ContainsKey(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (c.Source == ContactSource.GoogleOther && (emails.Count == 0 || emails.All(claimed.Contains))) continue;
            if (c.Source != ContactSource.GoogleOther && c.Emails.Count > 0 && emails.Count == 0) continue; // one of your own addresses
            foreach (var e in emails) claimed.Add(e);
            var s = Combine(emails.Select(e => byAddress.GetValueOrDefault(e)).OfType<PersonStats>());
            people.Add(new PersonViewModel
            {
                Key = c.IsSaved ? $"contact:{c.Id}" : $"mail:{emails[0].ToLowerInvariant()}",
                Name = c.DisplayName.Length > 0 ? c.DisplayName : s?.Name is { Length: > 0 } n ? n : emails.FirstOrDefault() ?? c.Phones.FirstOrDefault()?.Value ?? L.T("(no name)"),
                Contact = c, Emails = emails, Stats = s,
                CompanyName = c.Organization ?? CompanyFromDomain(emails.FirstOrDefault()),
                IsAutomated = s is not null && IsAutomated(emails[0], s) && !c.IsSaved,
            });
        }
        // Everyone else you've exchanged mail with.
        foreach (var s in byAddress.Values.Where(s => !claimed.Contains(s.Address)))
            people.Add(new PersonViewModel
            {
                Key = $"mail:{s.Address}", Name = CleanName(s.Name, s.Address), Emails = [s.Address], Stats = s,
                CompanyName = CompanyFromDomain(s.Address), IsAutomated = IsAutomated(s.Address, s),
            });
        return people;
    }

    private static PersonStats? Combine(IEnumerable<PersonStats> parts)
    {
        PersonStats? total = null;
        foreach (var p in parts)
            total = total is null ? p : total with
            {
                Received = total.Received + p.Received, Sent = total.Sent + p.Sent, Recent = total.Recent + p.Recent,
                First = Min(total.First, p.First), LastReceived = Max(total.LastReceived, p.LastReceived), LastSent = Max(total.LastSent, p.LastSent),
            };
        return total;

        static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;
        static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }

    /// <summary>
    /// Mail from a machine rather than a person: you never wrote back, and the address looks like a robot's
    /// (noreply@, newsletter@, notifications@…) or it has sent you a lot.
    /// </summary>
    private static bool IsAutomated(string address, PersonStats s)
    {
        var local = address.Split('@')[0].ToLowerInvariant();
        // Relay and reply addresses made by machines ("3368784196@my.booking.com", "2_GEZTIOJTG…@imip.me.com") even if you replied.
        if (local.Count(char.IsDigit) >= 7 || local.Length >= 28) return true;
        if (s.Sent > 0) return false;
        string[] robots = ["noreply", "no-reply", "donotreply", "do-not-reply", "notification", "notify", "newsletter", "news", "mailer",
            "bounce", "info", "hello", "support", "team", "marketing", "updates", "update", "alerts", "alert", "service", "billing", "orders",
            "order", "account", "accounts", "security", "digest", "contact", "reply", "help", "invoice", "receipts", "shop", "store", "sales"];
        return robots.Any(r => local == r || local.StartsWith(r + "-") || local.StartsWith(r + ".") || local.StartsWith(r + "_") || local.Contains("noreply"))
               || s.Received >= 15;
    }

    private HashSet<string> _sharedDomains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Acme" from "jo@mail.acme.co.uk", when several people write from acme.co.uk; null for free mail.</summary>
    private string? CompanyFromDomain(string? address)
    {
        if (address is null || !address.Contains('@')) return null;
        var domain = address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant();
        if (SenderCheck.IsFreeMail(domain)) return null;
        var site = LinkSafety.RegistrableDomain(domain);
        if (!_sharedDomains.Contains(site)) return null;
        var stem = site.Split('.')[0];
        return stem.Length < 2 ? null : char.ToUpper(stem[0], CultureInfo.InvariantCulture) + stem[1..];
    }

    private static string CleanName(string name, string address)
    {
        var n = name.Trim().Trim('"', '\'').Trim();
        return n.Length == 0 || n.Contains('@') ? address : n;
    }

    // ───────── lists and search ─────────

    private void Refilter()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTimeOffset.UtcNow;
        var humans = _everyone.Where(p => !p.IsAutomated).ToList();
        IEnumerable<PersonViewModel> Of(PeopleList kind) => kind switch
        {
            PeopleList.All => humans.Where(p => p.IsSaved || p.Stats?.Sent > 0).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            PeopleList.Favorites => humans.Where(p => p.IsFavorite).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            PeopleList.Frequent => humans.Where(p => p.Stats is { Total: >= 3 } && p.Score > 0.5).OrderByDescending(p => p.Score).Take(40),
            PeopleList.Recent => humans.Where(p => p.Stats?.Last > now.AddDays(-30) && (p.IsSaved || p.Stats.Sent > 0)).OrderByDescending(p => p.Stats!.Last),
            PeopleList.KeepInTouch => humans.Where(p => IsFading(p, now)).OrderByDescending(p => p.Stats!.Total),
            PeopleList.NotSaved => humans.Where(p => !p.IsSaved && p.Stats?.Sent > 0).OrderByDescending(p => p.Stats!.Total),
            PeopleList.Birthdays => _everyone.Where(p => p.NextBirthday is { } b && b.DayNumber - today.DayNumber <= 60).OrderBy(p => p.NextBirthday),
            PeopleList.Companies => humans.Where(p => p.CompanyName is not null && (p.IsSaved || p.Stats?.Sent > 0))
                .OrderBy(p => p.CompanyName, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            PeopleList.Services => _everyone.Where(p => p.IsAutomated).OrderByDescending(p => p.Stats?.Received ?? 0).Take(200),
            _ => [],
        };
        // Counts only change with the data, not with typing.
        if (_countedFrom != _builtFrom)
        {
            _countedFrom = _builtFrom;
            foreach (var list in Lists) list.Count = Count(list.Kind);
        }

        IEnumerable<PersonViewModel> shown;
        var query = Fold(SearchText.Trim());
        if (query.Length > 0)
        {
            // Search looks at everyone, ranked: name starts with it, then contains, then the rest.
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var digits = new string(query.Where(char.IsDigit).ToArray());
            shown = _everyone.Where(p => words.All(w => p.SearchText.Contains(w, StringComparison.Ordinal)) || digits.Length >= 4 && PhoneDigits(p).Contains(digits))
                .OrderByDescending(p => p.FoldedName.StartsWith(words[0], StringComparison.Ordinal))
                .ThenByDescending(p => p.IsSaved).ThenByDescending(p => p.Score).Take(300);
            ListTitle = L.F("Search: “{0}”", SearchText.Trim());
            ListHint = L.T("Names, addresses, companies and phone numbers");
            GroupByCompany = false;
        }
        else
        {
            shown = Of(SelectedList.Kind);
            ListTitle = SelectedList.Title;
            ListHint = SelectedList.Hint;
            GroupByCompany = SelectedList.Kind == PeopleList.Companies;
        }

        var keep = Selected?.Key;
        People = shown.ToList();
        IsEmpty = People.Count == 0;
        IsSearching = query.Length > 0;
        (EmptyTitle, EmptyText) = IsSearching
            ? (L.F("No one matches “{0}”", SearchText.Trim()), L.T("Check the spelling, or try part of a name, address or company."))
            : (L.T("Nobody here yet."), "");
        // The card pane beside an empty list: a way to add someone, with the search filled in.
        (CardEmptyTitle, CardEmptyText) = IsSearching
            ? (L.T("Someone new?"), L.F("Add “{0}” as a contact, and they'll be here next time.", SearchText.Trim()))
            : (L.T("No one to show"), L.T("Add a contact, or pick another list."));
        Selected = People.FirstOrDefault(p => p.Key == keep) ?? People.FirstOrDefault();
        _ = LoadPhotosAsync(People.Take(120).ToList());
    }

    private int _countedFrom = -1;

    // The same filters as the lists themselves, without sorting.
    private int Count(PeopleList kind)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTimeOffset.UtcNow;
        return kind switch
        {
            PeopleList.All => _everyone.Count(p => !p.IsAutomated && (p.IsSaved || p.Stats?.Sent > 0)),
            PeopleList.Favorites => _everyone.Count(p => !p.IsAutomated && p.IsFavorite),
            PeopleList.Frequent => Math.Min(40, _everyone.Count(p => !p.IsAutomated && p.Stats is { Total: >= 3 } && p.Score > 0.5)),
            PeopleList.Recent => _everyone.Count(p => !p.IsAutomated && p.Stats?.Last > now.AddDays(-30) && (p.IsSaved || p.Stats.Sent > 0)),
            PeopleList.KeepInTouch => _everyone.Count(p => !p.IsAutomated && IsFading(p, now)),
            PeopleList.NotSaved => _everyone.Count(p => !p.IsAutomated && !p.IsSaved && p.Stats?.Sent > 0),
            PeopleList.Birthdays => _everyone.Count(p => p.NextBirthday is { } b && b.DayNumber - today.DayNumber <= 60),
            PeopleList.Companies => _everyone.Count(p => !p.IsAutomated && p.CompanyName is not null && (p.IsSaved || p.Stats?.Sent > 0)),
            PeopleList.Services => Math.Min(200, _everyone.Count(p => p.IsAutomated)),
            _ => 0,
        };
    }

    private static bool IsFading(PersonViewModel p, DateTimeOffset now)
    {
        if (p.Stats is not { Total: >= 6, Sent: >= 2, First: { } first, Last: { } last }) return false;
        var quiet = (now - last).TotalDays;
        var gap = (last - first).TotalDays / Math.Max(1, p.Stats.Total - 1);
        // Quiet for 6× their usual rhythm (at least 45 days), but not a lost cause (under two years).
        return quiet is > 45 and < 730 && quiet > gap * 6;
    }

    private static string PhoneDigits(PersonViewModel p) =>
        new(string.Join(' ', p.Contact?.Phones.Select(x => x.Value) ?? []).Where(char.IsDigit).ToArray());

    /// <summary>Lower case without accents, so "rosu" finds "Roșu" and "muller" finds "Müller".</summary>
    internal static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c switch { 'ß' => "ss", 'ı' => "i", _ => c.ToString() });
        return sb.ToString();
    }

    private async Task LoadPhotosAsync(IReadOnlyList<PersonViewModel> people)
    {
        foreach (var p in people.Where(p => p.Photo is null))
        {
            try
            {
                if (p.Contact?.PhotoUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = uri;
                    image.DecodePixelWidth = 96;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    p.Photo = image;
                }
                else if (p.Email is { } email)
                {
                    p.Photo = await _pictures.ForSenderAsync(email, _own, mayLookUp: !p.IsAutomated || p.Stats?.Received > 0);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "No picture for {Name}", p.Name);
            }
        }
    }

    // ───────── the card ─────────

    private async Task LoadCardAsync(PersonViewModel? person)
    {
        _cardCts?.Cancel();
        if (person is null)
        {
            Card = null;
            return;
        }
        var cts = _cardCts = new CancellationTokenSource();
        var card = new PersonCard { Person = person };
        Describe(card);
        Card = card;
        try
        {
            var conversations = new List<MessageSummary>();
            foreach (var email in person.Emails.Take(4))
                conversations.AddRange(await _repo.GetConversationsWithAsync(email, 8, cts.Token));
            foreach (var m in conversations.DistinctBy(m => m.MessageId ?? m.Id.ToString(CultureInfo.InvariantCulture)).OrderByDescending(m => m.Date).Take(8))
                card.Conversations.Add(m);

            var meetings = new List<MeetingRef>();
            foreach (var email in person.Emails.Take(4)) meetings.AddRange(await _repo.GetMeetingsWithAsync(email, DateTimeOffset.Now, 1, cts.Token));
            if (meetings.OrderBy(m => m.Start).FirstOrDefault() is { } next)
            {
                var start = next.Start.LocalDateTime;
                var day = start.ToString("ddd d MMM", CultureInfo.CurrentCulture);
                card.MeetingText = next.IsAllDay ? $"{day}: {next.Title}" : $"{day}, {start.ToString("t", CultureInfo.CurrentCulture)}: {next.Title}";
            }

            var name = person.FoldedName;
            foreach (var other in _everyone.Where(o => o != person && o.IsSaved && person.IsSaved && o.FoldedName == name && name.Contains(' ')))
                card.Duplicates.Add(other);

            // Their signature, from their last few messages (the latest is often a short reply without one).
            if (!person.IsAutomated && person.Email is { } key)
            {
                if (!_signatures.TryGetValue(key, out var facts))
                {
                    var theirs = conversations.Where(m => person.Emails.Contains(m.FromAddress, StringComparer.OrdinalIgnoreCase))
                        .OrderByDescending(m => m.Date).Take(3).ToList();
                    facts = await Task.Run(() => ReadSignaturesAsync(person, theirs, cts.Token), cts.Token);
                    _signatures[key] = facts;
                }
                if (!cts.IsCancellationRequested) Suggest(card, facts);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Loading the card for {Name} failed", person.Name);
        }
    }

    /// <summary>
    /// Reads the signatures of someone's last few messages. Messages already on this computer are used first;
    /// at most one is downloaded (and only when none of them is here yet), so opening a card stays light.
    /// </summary>
    private async Task<SignatureFacts> ReadSignaturesAsync(PersonViewModel person, IReadOnlyList<MessageSummary> theirs, CancellationToken ct)
    {
        var found = new List<SignatureFacts>();
        var downloaded = false;
        foreach (var m in theirs)
        {
            var stream = _bodies.Open(m.Id);
            if (stream is null)
            {
                if (downloaded || found.Count > 0 || !await _mail.EnsureBodyAsync(m, ct)) continue;
                downloaded = true;
                stream = _bodies.Open(m.Id);
                if (stream is null) continue;
            }
            await using (stream)
            {
                var mime = await MimeMessage.LoadAsync(stream, ct);
                // The signature's line breaks matter: HTML-only mail is turned into text line by line.
                var text = !string.IsNullOrWhiteSpace(mime.TextBody) ? mime.TextBody : MessageRenderer.HtmlToText(mime.HtmlBody ?? "");
                found.Add(SignatureReader.Read(text, person.Name, m.FromAddress));
            }
        }
        // Newest first: each detail from the most recent message that has it.
        return new SignatureFacts(
            found.SelectMany(f => f.Phones).DistinctBy(p => new string(p.Number.Where(char.IsDigit).ToArray())).ToList(),
            found.Select(f => f.JobTitle).FirstOrDefault(t => t is not null),
            found.Select(f => f.Company).FirstOrDefault(c => c is not null),
            found.SelectMany(f => f.Websites).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void Describe(PersonCard card)
    {
        var p = card.Person;
        card.SourceText = p.Contact switch
        {
            { Source: ContactSource.Google } => L.T("Google contact"),
            { Source: ContactSource.GoogleOther } => L.T("Saved automatically by Gmail"),
            { Source: ContactSource.Local } => L.T("Contact in Plicbox"),
            _ => L.T("From your mail"),
        };
        if (p.Stats is { } s)
        {
            if (s.Last is { } last)
                card.LastText = $"{Ago(last)}, {(s.LastSent == last ? L.T("you wrote") : L.T("they wrote"))}";
            card.CountsText = L.F("{0} from them, {1} from you", s.Received, s.Sent);
            if (s.First is { } first && s.Total >= 4 && (s.Last - first)?.TotalDays is > 14 and var span)
            {
                var gap = span / (s.Total - 1);
                card.RhythmText = gap switch
                {
                    < 2 => L.T("Almost daily"),
                    < 5 => L.T("A few times a week"),
                    < 10 => L.T("About weekly"),
                    < 21 => L.T("Every couple of weeks"),
                    < 45 => L.T("About monthly"),
                    _ => L.F("Every {0} months or so", Math.Round(gap / 30)),
                };
                if (IsFading(p, DateTimeOffset.UtcNow))
                    card.NudgeText = L.F("You used to be in touch {0}; it's been {1}.", card.RhythmText.ToLower(CultureInfo.CurrentCulture), Elapsed(s.Last!.Value, ago: false));
            }
            if (!p.IsSaved && s.Sent > 0)
                card.SaveHint = s.Total == 1
                    ? L.F("You've exchanged one email with {0}. Save them as a contact?", FirstName(p.Name))
                    : L.F("You've exchanged {0} emails with {1}. Save them as a contact?", s.Total, FirstName(p.Name));
        }
        if (p.NextBirthday is { } b)
        {
            var days = b.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
            var when = days switch { 0 => L.T("today") + " 🎂", 1 => L.T("tomorrow"), _ => L.F("in {0} days", days) };
            var age = p.Contact!.BirthdayHasYear ? ", " + L.F("turns {0}", b.Year - p.Contact.Birthday!.Value.Year) : "";
            card.BirthdayText = $"{b.ToString("d MMMM", CultureInfo.CurrentCulture)}, {when}{age}";
        }
    }

    private static void Suggest(PersonCard card, SignatureFacts facts)
    {
        var c = card.Person.Contact;
        static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
        var known = (c?.Phones ?? []).Select(x => Digits(x.Value)).ToHashSet();
        foreach (var (number, label) in facts.Phones.Where(f => !known.Any(k => k.EndsWith(Digits(f.Number)[^Math.Min(8, Digits(f.Number).Length)..], StringComparison.Ordinal))))
            card.Suggestions.Add(new SignatureSuggestion("phone", number, label == "mobile" ? L.T("Mobile") : L.T("Phone")));
        if (facts.JobTitle is { } title && !string.Equals(title, c?.JobTitle, StringComparison.OrdinalIgnoreCase))
            card.Suggestions.Add(new SignatureSuggestion("title", title, L.T("Job title")));
        if (facts.Company is { } company && !string.Equals(company, c?.Organization, StringComparison.OrdinalIgnoreCase))
            card.Suggestions.Add(new SignatureSuggestion("company", company, L.T("Company")));
        foreach (var site in facts.Websites.Where(w => !(c?.Websites ?? []).Any(x => x.Value.Contains(w.Replace("https://", "").Replace("http://", ""), StringComparison.OrdinalIgnoreCase))).Take(2))
            card.Suggestions.Add(new SignatureSuggestion("website", site, L.T("Website")));
    }

    internal static string Ago(DateTimeOffset when) => Elapsed(when, ago: true);

    /// <summary>"3 weeks ago", or just "3 weeks" (for "it's been 3 weeks").</summary>
    private static string Elapsed(DateTimeOffset when, bool ago)
    {
        var days = (DateTime.Today - when.LocalDateTime.Date).TotalDays;
        return days switch
        {
            < 1 => L.T("today"),
            < 2 => L.T("yesterday"),
            < 7 => ago ? L.F("{0} days ago", (int)days) : L.F("{0} days", (int)days),
            < 14 => ago ? L.T("a week ago") : L.T("a week"),
            < 31 => ago ? L.F("{0} weeks ago", (int)(days / 7)) : L.F("{0} weeks", (int)(days / 7)),
            < 60 => ago ? L.T("a month ago") : L.T("a month"),
            < 365 => ago ? L.F("{0} months ago", (int)(days / 30)) : L.F("{0} months", (int)(days / 30)),
            < 730 => ago ? L.T("a year ago") : L.T("a year"),
            _ => ago ? L.F("{0} years ago", (int)(days / 365)) : L.F("{0} years", (int)(days / 365)),
        };
    }

    private static string FirstName(string name) => name.Contains('@') ? name : name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    // ───────── actions ─────────

    [RelayCommand] private void ShowList(PeopleListItem list)
    {
        SearchText = "";
        SelectedList = list;
    }

    [RelayCommand] private void Email(string? address)
    {
        if (Selected is not { } p) return;
        var to = address ?? p.Email;
        if (to is not null) ComposeRequested?.Invoke(Addresses.Format(p.Name.Contains('@') ? "" : p.Name, to));
    }

    [RelayCommand] private void Meeting()
    {
        if (Selected is { Email: { } email } p) MeetingRequested?.Invoke(p.Name.Contains('@') ? email : FirstName(p.Name), email);
    }

    [RelayCommand] private void ShowAllMail()
    {
        if (Selected is { Email: { } email }) ShowMailRequested?.Invoke(email);
    }

    [RelayCommand] private void CreateRule()
    {
        if (Selected is { Email: { } email } p) CreateRuleRequested?.Invoke(p.Name, email);
    }

    [RelayCommand] private Task OpenConversation(MessageSummary m) => OpenMessageRequested?.Invoke(m.Id) ?? Task.CompletedTask;

    [RelayCommand]
    private static void Call(string number)
    {
        var digits = new string(number.Where(c => char.IsDigit(c) || c == '+').ToArray());
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"tel:{digits}") { UseShellExecute = true });
    }

    [RelayCommand] private static void Copy(string text) => System.Windows.Clipboard.SetText(text);

    [RelayCommand]
    private static void OpenWebsite(string url)
    {
        var u = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task ToggleFavorite()
    {
        if (Selected is not { } p) return;
        if (p.Contact is not { IsSaved: true } c)
        {
            _dialogs.EditContact(EditorFor(p)); // favorites are saved contacts: save first
            return;
        }
        await Try(() => _engine.SetFavoriteAsync(c, !c.IsFavorite), L.T("Couldn't change the favorite"));
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { Contact: { IsSaved: true } c } p) return;
        var text = L.T("Your mail with them stays.") + (c.Source == ContactSource.Google ? " " + L.T("It's deleted from Google Contacts too.") : "");
        if (!_dialogs.Confirm(L.F("Delete {0}?", p.Name), text, L.T("Delete"), DialogKind.Danger)) return;
        await Try(() => _engine.DeleteAsync(c), L.T("Couldn't delete the contact"));
    }

    [RelayCommand]
    private async Task Merge(PersonViewModel other)
    {
        if (Selected is not { Contact: { } keep } p || other.Contact is not { } drop) return;
        if (!_dialogs.Confirm(L.F("Merge the two {0} contacts?", p.Name), L.T("Addresses, numbers and details of the other one are added to this one, and the other is deleted."), L.T("Merge"))) return;
        await Try(() => _engine.MergeAsync(keep, drop), L.T("Couldn't merge"));
    }

    /// <summary>Adds a detail found in their signature: saved straight away for contacts, put in the editor otherwise.</summary>
    [RelayCommand]
    private async Task AddSuggestion(SignatureSuggestion s)
    {
        if (Selected is not { } p) return;
        if (p.Contact is not { IsSaved: true } c)
        {
            var editor = EditorFor(p);
            Apply(editor, s);
            _dialogs.EditContact(editor);
            return;
        }
        var updated = s.Kind switch
        {
            "phone" => c with { Phones = [.. c.Phones, new LabeledValue(s.Value, s.Label == L.T("Mobile") ? "mobile" : "work")] },
            "title" => c with { JobTitle = s.Value },
            "company" => c with { Organization = s.Value },
            _ => c with { Websites = [.. c.Websites, new LabeledValue(s.Value)] },
        };
        Card?.Suggestions.Remove(s);
        await Try(() => _engine.SaveAsync(updated), L.T("Couldn't add it"));
    }

    private static void Apply(ContactEditor e, SignatureSuggestion s)
    {
        static void Add(ObservableCollection<EditableValue> list, string kind, string value, string label)
        {
            if (list.Any(v => v.Value.Trim().Equals(value, StringComparison.OrdinalIgnoreCase))) return;
            if (list.FirstOrDefault(v => v.Value.Trim().Length == 0) is { } empty) list.Remove(empty);
            list.Add(new EditableValue(kind, value, label));
        }
        switch (s.Kind)
        {
            case "phone": Add(e.Phones, "phone", s.Value, s.Label == L.T("Mobile") ? "mobile" : "work"); break;
            case "title": e.JobTitle = s.Value; break;
            case "company": e.Company = s.Value; break;
            default: Add(e.Websites, "website", s.Value, "work"); break;
        }
    }

    // ───────── editing ─────────

    /// <summary>A new contact, in its own window (like a new mail or event).</summary>
    /// <summary>Opens someone's card from elsewhere (a name in a message): by their address, among everyone.</summary>
    public async Task ShowPersonAsync(string address)
    {
        await EnsureFreshAsync();
        SearchText = "";
        SelectedList = Lists[0];
        var person = People.FirstOrDefault(p => p.Emails.Contains(address, StringComparer.OrdinalIgnoreCase));
        if (person is null)
        {
            SearchText = address; // not listed as a person (e.g. one of your own addresses): show the search instead
            person = People.FirstOrDefault();
        }
        Selected = person;
    }

    /// <summary>Saves every contact (yours, not the addresses Google saved by itself) as one .vcf file.</summary>
    public async Task ExportContactsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = L.T("Export contacts"), FileName = L.T("Contacts") + ".vcf", Filter = L.T("Contact files") + " (*.vcf)|*.vcf" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var contacts = (await _repo.GetAllAsync()).Where(c => c.IsSaved).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            await System.IO.File.WriteAllTextAsync(dialog.FileName, Plicbox.Dav.VcfFormat.Write(contacts), new System.Text.UTF8Encoding(false));
            _dialogs.Info(L.T("Contacts exported"), contacts.Count == 1 ? L.T("1 contact was saved.") : L.F("{0} contacts were saved.", contacts.Count));
        }
        catch (Exception ex) { _dialogs.Error(L.F("Couldn't export the contacts: {0}", ex.Message)); }
    }

    /// <summary>Brings in contacts from a .vcf file, kept on this computer; ones already here (same address, or same name) are skipped.</summary>
    public async Task ImportContactsAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.T("Import contacts"), Filter = L.T("Contact files") + " (*.vcf)|*.vcf" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var incoming = Plicbox.Dav.VcfFormat.Read(await System.IO.File.ReadAllTextAsync(dialog.FileName));
            var existing = (await _repo.GetAllAsync()).Where(c => c.IsSaved).ToList();
            var addresses = existing.SelectMany(c => c.Emails).Select(e => e.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = existing.Select(c => c.DisplayName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            int added = 0, skipped = 0;
            foreach (var c in incoming)
            {
                var known = c.Emails.Count > 0 ? c.Emails.Any(e => addresses.Contains(e.Value)) : names.Contains(c.DisplayName);
                if (known) { skipped++; continue; }
                await _engine.SaveAsync(c);
                foreach (var e in c.Emails) addresses.Add(e.Value);
                names.Add(c.DisplayName);
                added++;
            }
            var text = added == 1 ? L.T("1 contact was added on this computer.") : L.F("{0} contacts were added on this computer.", added);
            if (skipped > 0) text += " " + (skipped == 1 ? L.T("1 was already here.") : L.F("{0} were already here.", skipped));
            _dialogs.Info(L.T("Contacts imported"), text);
        }
        catch (Exception ex) { _dialogs.Error(L.F("Couldn't import the contacts: {0}", ex.Message)); }
    }

    [RelayCommand]
    public void NewContact()
    {
        var editor = new ContactEditor { SaveTo = SaveTargets.FirstOrDefault(), SaveTargets = SaveTargets, Save = SaveAsync };
        // Like eM Client: an email, a mobile number and a web page to fill in.
        editor.Emails.Add(new EditableValue("email", "", "work"));
        editor.Phones.Add(new EditableValue("phone", "", "mobile"));
        editor.Websites.Add(new EditableValue("website", "", "work"));
        _dialogs.EditContact(editor);
    }

    /// <summary>A search found no one: a new contact with what was searched for filled in (an address, a phone number, or a name).</summary>
    [RelayCommand]
    private void NewContactFromSearch()
    {
        var query = SearchText.Trim();
        var editor = new ContactEditor { SaveTo = SaveTargets.FirstOrDefault(), SaveTargets = SaveTargets, Save = SaveAsync };
        var isEmail = query.Contains('@');
        var isPhone = !isEmail && query.Count(char.IsDigit) >= 4 && query.All(c => char.IsDigit(c) || " +-()./".Contains(c));
        if (!isEmail && !isPhone && query.Length > 0)
        {
            var space = query.IndexOf(' ');
            (editor.GivenName, editor.FamilyName) = space > 0 ? (query[..space], query[(space + 1)..].Trim()) : (query, "");
        }
        editor.Emails.Add(new EditableValue("email", isEmail ? query : "", "work"));
        editor.Phones.Add(new EditableValue("phone", isPhone ? query : "", "mobile"));
        editor.Websites.Add(new EditableValue("website", "", "work"));
        _dialogs.EditContact(editor);
    }

    /// <summary>Edit the selected contact, or save someone from your mail (filled in from their name and signature).</summary>
    [RelayCommand]
    private void Edit()
    {
        if (Selected is { } p) _dialogs.EditContact(EditorFor(p));
    }

    private ContactEditor EditorFor(PersonViewModel p)
    {
        var c = p.Contact;
        var (given, family) = SplitName(c?.GivenName, c?.FamilyName, p.Name);
        var year = c is { BirthdayHasYear: true } ? c.Birthday!.Value.Year : DateTime.Today.Year;
        var editor = new ContactEditor
        {
            Original = c, Person = p, SaveTargets = SaveTargets, Save = SaveAsync,
            GivenName = given, FamilyName = family, Nickname = c?.Nickname ?? "",
            Company = c?.Organization ?? p.CompanyName ?? "", JobTitle = c?.JobTitle ?? "", Department = c?.Department ?? "",
            Birthday = c?.Birthday is { } b ? new DateTime(year, b.Month, Math.Min(b.Day, DateTime.DaysInMonth(year, b.Month))) : null,
            BirthdayNoYear = c?.Birthday is not null && !c.BirthdayHasYear,
            Notes = c?.Notes ?? "",
            SaveTo = c is { IsSaved: true } ? SaveTargets.FirstOrDefault(t => t.Value == c.AccountId) ?? SaveTargets[^1]
                : SaveTargets.FirstOrDefault(t => t.Value == c?.AccountId) ?? SaveTargets.FirstOrDefault(),
        };
        foreach (var e in c?.Emails is { Count: > 0 } list ? list : p.Emails.Select(x => new LabeledValue(x, "work")).ToList())
            editor.Emails.Add(new EditableValue("email", e.Value, e.Label));
        foreach (var x in c?.Phones ?? []) editor.Phones.Add(new EditableValue("phone", x.Value, x.Label));
        foreach (var x in c?.Websites ?? []) editor.Websites.Add(new EditableValue("website", x.Value, x.Label));
        foreach (var a in c?.Addresses ?? []) editor.Addresses.Add(new EditableAddress(a));
        if (c?.Addresses is not { Count: > 0 } && c?.Address is { Length: > 0 } text) editor.Addresses.Add(new EditableAddress(new PostalAddress(text, null, null, null, null)));
        if (editor.Emails.Count == 0) editor.Emails.Add(new EditableValue("email", "", "work"));
        if (editor.Phones.Count == 0) editor.Phones.Add(new EditableValue("phone", "", "mobile"));
        // Saving someone from your mail: take what their signature says.
        if (c is not { IsSaved: true } && Card is { Person: var shown } card && shown == p)
            foreach (var s in card.Suggestions) Apply(editor, s);
        return editor;
    }

    /// <summary>The window's Save &amp; Close: false keeps it open.</summary>
    private async Task<bool> SaveAsync(ContactEditor e)
    {
        var emails = e.Emails.Select(v => v.ToModel()).OfType<LabeledValue>().ToList();
        var name = $"{e.GivenName.Trim()} {e.FamilyName.Trim()}".Trim();
        if (name.Length == 0 && emails.Count == 0 && e.Company.Trim().Length == 0)
        {
            _dialogs.Info(L.T("Nothing to save"), L.T("Give the contact a name, an email address or a company."));
            return false;
        }
        DateOnly? birthday = e.Birthday is { } date
            ? new DateOnly(e.BirthdayNoYear ? Contact.NoYear : date.Year, date.Month, date.Day)
            : null;
        var original = e.Original;
        var addresses = e.Addresses.Select(a => a.ToModel()).Where(a => !a.IsEmpty).ToList();
        var contact = (original is { IsSaved: true } ? original : new Contact { Source = ContactSource.Local }) with
        {
            AccountId = original is { IsSaved: true } ? original.AccountId : e.SaveTo?.Value,
            Source = original is { IsSaved: true } ? original.Source : e.SaveTo?.Value is null ? ContactSource.Local : ContactSource.Google,
            RemoteId = original is { IsSaved: true } ? original.RemoteId : null,
            DisplayName = name.Length > 0 ? name : emails.FirstOrDefault()?.Value ?? e.Company.Trim(),
            GivenName = Blank(e.GivenName), FamilyName = Blank(e.FamilyName), Nickname = Blank(e.Nickname),
            Organization = Blank(e.Company), JobTitle = Blank(e.JobTitle), Department = Blank(e.Department),
            Emails = emails,
            Phones = e.Phones.Select(v => v.ToModel()).OfType<LabeledValue>().ToList(),
            Websites = e.Websites.Select(v => v.ToModel()).OfType<LabeledValue>().ToList(),
            Birthday = birthday, Addresses = addresses, Address = addresses.FirstOrDefault()?.Formatted,
            Notes = Blank(e.Notes), PhotoUrl = original?.PhotoUrl,
        };
        try
        {
            var saved = await _engine.SaveAsync(contact);
            await LoadAsync();
            var key = $"contact:{saved.Id}";
            if (People.FirstOrDefault(p => p.Key == key) is { } inList) Selected = inList;
            else if (_everyone.FirstOrDefault(p => p.Key == key) is { } found) Selected = Show(found);
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't save the contact: {0}", ex.Message));
            return false;
        }
    }

    /// <summary>Shows someone who isn't in the current list (after saving) by switching to All people.</summary>
    private PersonViewModel Show(PersonViewModel person)
    {
        SelectedList = Lists[0];
        return People.FirstOrDefault(p => p.Key == person.Key) ?? person;
    }

    private static (string Given, string Family) SplitName(string? given, string? family, string display)
    {
        if (given is not null || family is not null) return (given ?? "", family ?? "");
        if (display.Contains('@')) return ("", "");
        var parts = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? (display, "") : (string.Join(' ', parts[..^1]), parts[^1]);
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ───────── sync banner ─────────

    private void UpdateBanner()
    {
        var problem = _engine.Statuses.FirstOrDefault(s => s.Value.State is CalendarSyncState.NeedsPermission or CalendarSyncState.NeedsSignIn or CalendarSyncState.Error);
        IsBusy = _engine.Statuses.Values.Any(s => s.State == CalendarSyncState.Syncing);
        RefreshToolTip = IsBusy ? L.T("Refresh (F5), syncing…")
            : _engine.Statuses.Values.Select(s => s.LastSync).Where(t => t.HasValue).Min() is { } last ? L.F("Refresh (F5), updated {0:t}", last.LocalDateTime)
            : L.T("Refresh (F5)");
        _bannerAccountId = problem.Value is null ? null : problem.Key;
        Banner = problem.Value?.State == CalendarSyncState.NeedsPermission
            ? problem.Value.Message + " " + L.T("Sign in once more and allow access to your contacts.")
            : problem.Value?.Message;
        BannerAction = problem.Value?.State is CalendarSyncState.NeedsPermission or CalendarSyncState.NeedsSignIn ? L.T("Allow access") : problem.Value is null ? null : L.T("Try again");
    }

    [RelayCommand]
    private async Task FixBanner()
    {
        if (_bannerAccountId is not { } id) return;
        if (BannerAction == L.T("Allow access") && await _accounts.GetAsync(id) is { } account) await _dialogs.SignInAgainAsync(account);
        _engine.RequestSync(id);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        _engine.RequestSync();
        await LoadAsync();
    }

    private async Task Try(Func<Task> action, string failure)
    {
        try { await action(); }
        catch (Exception ex) { _dialogs.Error(L.F("{0}: {1}", failure, ex.Message)); }
    }
}

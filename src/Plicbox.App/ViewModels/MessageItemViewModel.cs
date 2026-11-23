using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Plicbox.Core;

namespace Plicbox.App.ViewModels;

public sealed partial class MessageItemViewModel : ObservableObject
{
    private static readonly Brush[] AvatarBrushes = new[]
    {
        "#E91E63", "#9C27B0", "#3F51B5", "#03A9F4", "#009688", "#4CAF50", "#FF9800", "#795548", "#607D8B", "#F44336",
    }.Select(c => (Brush)new BrushConverter().ConvertFromString(c)!).Select(b => { b.Freeze(); return b; }).ToArray();

    /// <param name="parent">Set for the extra rows of an expanded conversation; they sit under their thread's head.</param>
    /// <param name="folderHint">For a conversation row from another folder (e.g. "Sent"), shown as a small label.</param>
    public MessageItemViewModel(MessageSummary model, int threadCount, DateTime now,
        MessageItemViewModel? parent = null, string? folderHint = null)
    {
        Model = model;
        ThreadCount = threadCount;
        Parent = parent;
        FolderHint = folderHint;
        _isRead = model.IsRead;
        _isFlagged = model.IsFlagged;
        _isImportant = model.IsImportant;

        var local = model.Date.LocalDateTime;
        // Conversation rows stay in their head's date group instead of jumping to their own day.
        DateGroup = parent?.DateGroup ?? MessageDates.Group(local.Date, now.Date);
        Group = parent?.Group ?? DateGroup;
        // Note: inside a custom format "t" is the AM/PM designator, so weekday + time is composed from two formats.
        var culture = CultureInfo.CurrentCulture;
        DisplayTime = local.Date == now.Date ? local.ToString("t", culture)
            : (now.Date - local.Date).TotalDays < 7 ? $"{local.ToString("ddd", culture)} {local.ToString("t", culture)}"
            : local.ToString("d", culture);
        FullDate = $"{local.ToString("ddd", culture)} {local.ToString("g", culture)}";
    }

    public MessageSummary Model { get; }
    public int ThreadCount { get; }
    public bool IsThread => ThreadCount > 1;
    public MessageItemViewModel? Parent { get; }
    public bool IsThreadChild => Parent is not null;
    public string? FolderHint { get; }
    public bool HasFolderHint => FolderHint is not null;
    [ObservableProperty] private bool _isExpanded;
    public string DateGroup { get; }
    /// <summary>The list heading this row sits under; depends on the sort (date, sender, flagged, …). Set before the row is shown.</summary>
    public string? Group { get; set; }
    public string DisplayTime { get; }
    public string FullDate { get; }

    public string Sender => string.IsNullOrWhiteSpace(Model.FromName) ? Model.FromAddress : Model.FromName;
    public string Subject => string.IsNullOrWhiteSpace(Model.Subject) ? L.T("(no subject)") : Model.Subject;
    public string Initial => Sender.Length > 0 ? char.ToUpperInvariant(Sender[0]).ToString() : "?";

    /// <summary>The sender as a person (for the header's menu).</summary>
    public HeaderPerson SenderPerson => new(Sender, Model.FromAddress, Last: true);

    /// <summary>
    /// Everyone the message went to, one by one: the header shows their names, each with its address on hover and a menu.
    /// Mail synced before addresses were kept only has the names.
    /// </summary>
    public IReadOnlyList<HeaderPerson> ToPeople => _toPeople ??= BuildToPeople();
    private IReadOnlyList<HeaderPerson>? _toPeople;

    private List<HeaderPerson> BuildToPeople()
    {
        var parsed = Addresses.Parse(Model.Recipients);
        var people = parsed.Count > 0
            ? parsed.Select(p => (Name: p.Name.Length > 0 ? p.Name : p.Address, Address: (string?)p.Address)).ToList()
            : Model.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => (Name: n, Address: (string?)null)).ToList();
        return people.Select((p, i) => new HeaderPerson(p.Name, p.Address, Last: i == people.Count - 1)).ToList();
    }
    public Brush AvatarBrush => AvatarBrushes[(int)((uint)StableHash(Model.FromAddress) % AvatarBrushes.Length)];

    [ObservableProperty] private bool _isRead;
    /// <summary>Arrived while the list was on screen: the row glows briefly (then this turns off, so scrolling doesn't replay it).</summary>
    [ObservableProperty] private bool _isFresh;
    [ObservableProperty] private bool _isFlagged;
    /// <summary>Gmail's Important label: a small amber dot before the subject.</summary>
    [ObservableProperty] private bool _isImportant;
    /// <summary>Something happens to it in the background (downloading, a change on its way to the server): a small spinner.</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>
    /// The sender's picture (your account picture, a Gravatar or a brand logo); null shows the coloured letter.
    /// Looked up the first time something shows it, so only rows on screen (the list is virtualized) cost a lookup,
    /// however many thousands are listed.
    /// </summary>
    public ImageSource? Picture
    {
        get
        {
            if (!_pictureRequested && PictureLoader is { } load)
            {
                _pictureRequested = true;
                _ = load(this);
            }
            return _picture;
        }
        set => SetProperty(ref _picture, value);
    }
    private ImageSource? _picture;
    private bool _pictureRequested;

    /// <summary>Set by whoever lists the row: fetches its picture when it's first needed.</summary>
    public Func<MessageItemViewModel, Task>? PictureLoader { get; set; }

    /// <summary>Look the picture up again the next time it's shown (a setting or an account picture changed).</summary>
    public void ForgetPicture()
    {
        _pictureRequested = false;
        Picture = null; // tells what shows it, which asks again
    }

    // string.GetHashCode is randomized per process; avatars need a stable color.
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s.ToLowerInvariant()) h = h * 31 + c;
            return h;
        }
    }
}

/// <summary>A person named in a message's header: shown by name, their address on hover.</summary>
public sealed record HeaderPerson(string Name, string? Address, bool Last)
{
    public string Display => Last ? Name : Name + ",";
    /// <summary>The address as a tooltip, when it isn't already what's shown.</summary>
    public string? Tip => Address is { } a && !string.Equals(a, Name, StringComparison.OrdinalIgnoreCase) ? a : null;
}

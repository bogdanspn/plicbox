using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Plicbox.Core;

namespace Plicbox.App.ViewModels;

/// <summary>Outbox: every account's Outbox in one (Favorites), shown even when nothing waits.</summary>
/// <summary>Trash: every account's Trash together; mail dropped on it goes to its own account's Trash.</summary>
public enum NodeKind { Group, UnifiedInbox, Unread, Flagged, Folder, Outbox, Trash }

/// <summary>One row in the flat sidebar list. Groups (Favorites, each account) collapse their children.</summary>
public sealed partial class SidebarNode : ObservableObject
{

    public required string Title { get; init; }
    public string Glyph { get; init; } = "";
    public NodeKind Kind { get; init; }
    public SidebarNode? Parent { get; init; }
    public long AccountId { get; init; }
    public long FolderId { get; init; }
    public FolderRole Role { get; init; }
    /// <summary>Nesting depth: 0 = account/Favorites, 1 = folders, 2 = folders under "More".</summary>
    public int Level { get; init; }
    /// <summary>The folder icon's colour (Folder Color in the folder menu); null for the theme's grey.</summary>
    public Brush? GlyphBrush { get; init; }
    public bool HasGlyphColor => GlyphBrush is not null;
    /// <summary>"Show if unread": listed only while it has unread mail.</summary>
    public bool ShowOnlyIfUnread { get; init; }
    /// <summary>An account row: the account's colour (its icon in the folded sidebar).</summary>
    public string? AccountColor { get; init; }
    /// <summary>A folder's copy under Favorites (the folder itself stays under its account too).</summary>
    public bool IsFavoriteShortcut { get; init; }

    [ObservableProperty] private int _count;
    /// <summary>An account row: unread mail in any of its folders (as the Unread view counts it), for the folded sidebar's dot.</summary>
    [ObservableProperty] private bool _hasUnread;
    [ObservableProperty] private bool _isExpanded = true;
    /// <summary>Highlighted while a dragged message hovers over this folder.</summary>
    [ObservableProperty] private bool _isDropTarget;
    /// <summary>A folder action is running on the server (mark all as read, empty, rename…): a small spinner replaces the count.</summary>
    [ObservableProperty] private bool _isBusy;

    public bool IsGroup => Kind == NodeKind.Group;
    /// <summary>Drafts and the Outbox: the count is how many are waiting there, not unread mail (shown muted).</summary>
    public bool CountIsTotal => Kind == NodeKind.Outbox || (Kind == NodeKind.Folder && Role is FolderRole.Drafts or FolderRole.Outbox);
    public bool IsVisible => Parent is null || (Parent.IsExpanded && Parent.IsVisible);
}

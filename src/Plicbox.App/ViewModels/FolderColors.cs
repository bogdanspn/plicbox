using Plicbox.Core;
using System.Windows.Media;

namespace Plicbox.App.ViewModels;

/// <summary>Colours offered under Folder Color (the folder icon in the sidebar).</summary>
public static class FolderColors
{
    public static readonly IReadOnlyList<(string Name, string Hex)> Palette =
    [
        (L.T("Red"), "#D13438"), (L.T("Orange"), "#F7630C"), (L.T("Yellow"), "#E8B100"), (L.T("Green"), "#2F9E44"),
        (L.T("Teal"), "#0B8A8A"), (L.T("Blue"), "#1A73E8"), (L.T("Purple"), "#8764B8"), (L.T("Pink"), "#C239B3"), (L.T("Gray"), "#7A7A7A"),
    ];

    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush? Brush(string hex)
    {
        if (Cache.TryGetValue(hex, out var cached)) return cached;
        Brush brush;
        try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return null; }
        brush.Freeze();
        return Cache[hex] = brush;
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Plicbox.App;

/// <summary>
/// A wrap panel whose rows fill the width: items go side by side at their own width while they fit, and the room left
/// in a row is shared out among its items, so a row of tiles ends at the right edge rather than part-way. A last row
/// under full ones keeps its items' own widths (one tile stretched across would look like a banner).
/// </summary>
public sealed class StretchWrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(available.Width, double.PositiveInfinity));
        var rows = Rows(available.Width);
        var width = double.IsInfinity(available.Width) ? rows.Select(r => r.Width).DefaultIfEmpty(0).Max() : available.Width;
        return new Size(width, rows.Sum(r => r.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var rows = Rows(final.Width);
        var y = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var lastUnder = i == rows.Count - 1 && rows.Count > 1;
            var share = lastUnder ? 0 : Math.Max(0, final.Width - row.Width) / row.Items.Count;
            var x = 0.0;
            foreach (var child in row.Items)
            {
                var width = Math.Min(final.Width, child.DesiredSize.Width + share);
                child.Arrange(new Rect(x, y, width, row.Height));
                x += width;
            }
            y += row.Height;
        }
        return final;
    }

    private sealed record Row(List<UIElement> Items, double Width, double Height);

    private List<Row> Rows(double available)
    {
        var rows = new List<Row>();
        var items = new List<UIElement>();
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (items.Count > 0 && width + size.Width > available)
            {
                rows.Add(new Row(items, width, height));
                items = [];
                width = height = 0;
            }
            items.Add(child);
            width += size.Width;
            height = Math.Max(height, size.Height);
        }
        if (items.Count > 0) rows.Add(new Row(items, width, height));
        return rows;
    }
}

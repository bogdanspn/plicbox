using System.Windows;

namespace Plicbox.App;

/// <summary>
/// The width of the scrollbars inside an element: <c>local:Gutter.ScrollBarWidth="8"</c> on a list whose scrollbar sits
/// in the narrow gutter beside it. Inherited, so the list's own scrollbar picks it up (the scrollbar style binds to it);
/// done this way rather than with a derived scrollbar style, which would lose the style's fade-in on hover.
/// </summary>
public static class Gutter
{
    public static readonly DependencyProperty ScrollBarWidthProperty = DependencyProperty.RegisterAttached("ScrollBarWidth", typeof(double), typeof(Gutter),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetScrollBarWidth(DependencyObject d) => (double)d.GetValue(ScrollBarWidthProperty);
    public static void SetScrollBarWidth(DependencyObject d, double value) => d.SetValue(ScrollBarWidthProperty, value);
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// Grey hint text inside an empty text or password box ("Enter email", "Street"…): <c>local:Placeholder.Text="…"</c>.
/// Drawn over the box (an adorner), so it works with the theme's own text box look.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Placeholder),
        new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not (TextBox or PasswordBox)) return;
        var box = (Control)d;
        box.Loaded -= Refresh;
        box.IsVisibleChanged -= VisibilityChanged;
        box.Loaded += Refresh;
        if (box is TextBox text)
        {
            text.TextChanged -= Refresh;
            text.TextChanged += Refresh;
        }
        else if (box is PasswordBox password)
        {
            password.PasswordChanged -= Refresh;
            password.PasswordChanged += Refresh;
        }
        // The hint is drawn on the window's adorner layer: it has to go when its box is hidden (another module shown).
        box.IsVisibleChanged += VisibilityChanged;
        if (box.IsLoaded) Update(box);
    }

    private static void Refresh(object sender, RoutedEventArgs e) => Update((Control)sender);
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((Control)sender);

    private static bool IsEmpty(Control box) => box switch
    {
        TextBox t => t.Text.Length == 0,
        PasswordBox p => p.SecurePassword.Length == 0,
        _ => false,
    };

    private static void Update(Control box)
    {
        if (AdornerLayer.GetAdornerLayer(box) is not { } layer) return;
        var existing = layer.GetAdorners(box)?.OfType<HintAdorner>().FirstOrDefault();
        var show = IsEmpty(box) && box.IsVisible && GetText(box) is { Length: > 0 };
        if (show && existing is null) layer.Add(new HintAdorner(box));
        else if (!show && existing is not null) layer.Remove(existing);
        else existing?.InvalidateVisual();
    }

    private sealed class HintAdorner : Adorner
    {
        private static Brush Hint => Services.Theme.Brush("HintBrush");

        public HintAdorner(Control box) : base(box) => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc)
        {
            var box = (Control)AdornedElement;
            var text = new FormattedText(GetText(box) ?? "", CultureInfo.CurrentCulture, box.FlowDirection,
                new Typeface(box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), box.FontSize, Hint,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, box.ActualWidth - box.Padding.Left - box.Padding.Right - 16),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            // Where the theme puts the caret: border + padding + the text view's own small margin.
            var x = box.BorderThickness.Left + box.Padding.Left + 2;
            var y = box is TextBox && box.VerticalContentAlignment == VerticalAlignment.Top
                ? box.BorderThickness.Top + box.Padding.Top
                : (box.ActualHeight - text.Height) / 2;
            dc.DrawText(text, new Point(x, y));
        }
    }
}

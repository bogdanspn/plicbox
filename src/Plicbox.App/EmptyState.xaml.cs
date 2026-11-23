using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Plicbox.App;

/// <summary>An empty list or a search with no results: icon, title, a line on what to try, and an optional button.</summary>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState),
        new PropertyMetadata("")); // the magnifying glass
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState),
        new PropertyMetadata(""));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, _) => ((EmptyState)d).UpdateParts()));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState),
        new PropertyMetadata(null, (d, _) => ((EmptyState)d).UpdateParts()));
    public static readonly DependencyProperty ActionGlyphProperty = DependencyProperty.Register(nameof(ActionGlyph), typeof(string), typeof(EmptyState),
        new PropertyMetadata("")); // ✕, for Clear search
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(EmptyState));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>The button's label; no button when empty.</summary>
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public string ActionGlyph { get => (string)GetValue(ActionGlyphProperty); set => SetValue(ActionGlyphProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }

    public EmptyState()
    {
        InitializeComponent();
        UpdateParts();
    }

    private void UpdateParts()
    {
        if (Detail is null) return; // before InitializeComponent
        Detail.Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
        Action.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
    }
}

using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Plicbox.App.Services;

/// <summary>
/// Light and dark: every colour the app uses has a name here with a value for each mode. XAML refers to them as
/// {DynamicResource Name}, code reads <see cref="Brush"/>; switching swaps them all at once, the Windows controls
/// included (Fluent's ThemeMode). Emails stay on white "paper" in both modes, as they are designed for it.
/// </summary>
public static class Theme
{
    /// <summary>Settings value: follow Windows' "app mode" (the default).</summary>
    public const string System = "system";

    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        // surfaces
        ["ChromeBrush"] = ("#F3F3F3", "#202020"),
        ["SurfaceBrush"] = ("#FFFFFF", "#272727"),
        ["AltRowBrush"] = ("#F5F6F8", "#2C2D2F"),
        ["PanelBackdropBrush"] = ("#F3F4F6", "#2F3033"),
        ["ReadingBackdropBrush"] = ("#ECECEC", "#1A1A1A"),
        ["FieldBrush"] = ("#FFFFFF", "#2B2B2B"),
        ["SubtleFillBrush"] = ("#F0F0F0", "#333333"),
        ["SubtleStrongFillBrush"] = ("#E3E3E3", "#3D3D3D"),
        ["SegmentTrackBrush"] = ("#EDEDED", "#2C2C2C"),
        ["SegmentSelectedBrush"] = ("#FFFFFF", "#454545"),
        ["SidebarSelectedBrush"] = ("#FFFFFF", "#323232"),
        // states
        ["HoverBrush"] = ("#EAF1FB", "#2E3747"),
        ["SelectionBrush"] = ("#D6E6FA", "#29466B"),
        ["ListHoverBrush"] = ("#EBEDF0", "#34363A"),
        ["ChromeHoverBrush"] = ("#E6E6E6", "#383838"),
        ["ChromePressedBrush"] = ("#D9D9D9", "#444444"),
        // lines
        ["DividerBrush"] = ("#E2E2E2", "#3A3A3A"),
        ["ToolbarLineBrush"] = ("#E2E2E2", "#3A3A3A"),
        ["StrongDividerBrush"] = ("#D0D0D0", "#4A4A4A"),
        ["MenuBrush"] = ("#F9F9F9", "#2C2C2C"),
        ["MenuBorderBrush"] = ("#E0E0E0", "#3E3E3E"),
        ["AvatarRingBrush"] = ("#1A000000", "#18FFFFFF"), // hairline round pictures
        // text
        ["TextBrush"] = ("#1F1F1F", "#ECECEC"),
        ["SecondaryTextBrush"] = ("#555555", "#C4C4C4"),
        ["MutedTextBrush"] = ("#707070", "#A3A3A3"),
        ["HintBrush"] = ("#9A9A9A", "#8A8A8A"),
        ["LinkBrush"] = ("#1A73E8", "#6AA6F8"),
        ["CountBrush"] = ("#1A73E8", "#6AA6F8"),
        ["UnreadDotBrush"] = ("#1A73E8", "#6AA6F8"),
        ["FlagBrush"] = ("#D13438", "#F06A6F"),
        ["ImportantBrush"] = ("#F0B400", "#F5C542"), // Gmail's Important: the small amber dot
        ["DangerTextBrush"] = ("#C42B1C", "#FF7B72"),
        // notes and banners
        ["WarningFillBrush"] = ("#FFF7E6", "#3A301C"),
        ["WarningStrokeBrush"] = ("#F5D7A1", "#6E5627"),
        ["InfoFillBrush"] = ("#EEF5FF", "#1F2C3D"),
        ["InfoStrokeBrush"] = ("#CFE0FA", "#34507A"),
        ["CautionFillBrush"] = ("#FFF8E1", "#3A321A"),
        ["CautionStrokeBrush"] = ("#E9CF7E", "#7A6527"),
        ["CautionTextBrush"] = ("#8A5300", "#F2C46B"),
        ["CautionStrongTextBrush"] = ("#5C3B00", "#FFD98A"),
        ["DangerFillBrush"] = ("#FDE7E9", "#44262A"),
        ["DangerStrokeBrush"] = ("#E8A3A9", "#8A3C44"),
        // scrollbars
        ["ScrollThumbBrush"] = ("#5C000000", "#66FFFFFF"),
        ["ScrollThumbHoverBrush"] = ("#73000000", "#80FFFFFF"),
        ["ScrollThumbDragBrush"] = ("#8C000000", "#99FFFFFF"),
        // the Windows controls' fields (Fluent's own keys)
        ["TextControlBackground"] = ("#F8F8F9", "#2D2D2D"),
        ["TextControlBackgroundPointerOver"] = ("#F3F3F5", "#333333"),
        ["TextControlBackgroundFocused"] = ("#FFFFFF", "#1F1F1F"),
        ["DatePickerBackground"] = ("#F8F8F9", "#2D2D2D"),
        ["DatePickerBackgroundPointerOver"] = ("#F3F3F5", "#333333"),
        ["DatePickerBackgroundFocused"] = ("#FFFFFF", "#1F1F1F"),
        // ticked checkboxes and radio buttons: neutral, not Windows' accent colour (which ignores the one set in Plicbox)
        ["CheckBoxCheckBackgroundFillChecked"] = ("#3D3D3D", "#D0D0D0"),
        ["CheckBoxCheckBackgroundFillCheckedPointerOver"] = ("#4D4D4D", "#DEDEDE"),
        ["CheckBoxCheckBackgroundFillCheckedPressed"] = ("#5C5C5C", "#BDBDBD"),
        ["CheckBoxCheckBackgroundFillIndeterminate"] = ("#3D3D3D", "#D0D0D0"),
        ["CheckBoxCheckBackgroundFillIndeterminatePointerOver"] = ("#4D4D4D", "#DEDEDE"),
        ["CheckBoxCheckBackgroundFillIndeterminatePressed"] = ("#5C5C5C", "#BDBDBD"),
        ["CheckBoxCheckBackgroundStrokeChecked"] = ("#3D3D3D", "#D0D0D0"),
        ["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = ("#4D4D4D", "#DEDEDE"),
        ["CheckBoxCheckBackgroundStrokeCheckedPressed"] = ("#5C5C5C", "#BDBDBD"),
        ["CheckBoxCheckBackgroundStrokeIndeterminate"] = ("#3D3D3D", "#D0D0D0"),
        ["CheckBoxCheckBackgroundStrokeIndeterminatePointerOver"] = ("#4D4D4D", "#DEDEDE"),
        ["CheckBoxCheckBackgroundStrokeIndeterminatePressed"] = ("#5C5C5C", "#BDBDBD"),
        ["CheckBoxCheckGlyphForeground"] = ("#FFFFFF", "#1F1F1F"),
        ["CheckBoxCheckGlyphForegroundPressed"] = ("#E6E6E6", "#333333"),
        ["RadioButtonOuterEllipseCheckedFill"] = ("#3D3D3D", "#D0D0D0"),
        ["RadioButtonOuterEllipseCheckedStroke"] = ("#3D3D3D", "#D0D0D0"),
        ["RadioButtonCheckOuterEllipseCheckedFillPointerOver"] = ("#4D4D4D", "#DEDEDE"),
        ["RadioButtonOuterEllipseCheckedStrokePointerOver"] = ("#4D4D4D", "#DEDEDE"),
        ["RadioButtonCheckOuterEllipseCheckedFillPressed"] = ("#5C5C5C", "#BDBDBD"),
        ["RadioButtonCheckOuterEllipseCheckedStrokePressed"] = ("#5C5C5C", "#BDBDBD"),
        ["RadioButtonCheckGlyphFill"] = ("#FFFFFF", "#1F1F1F"),
    };

    public static IEnumerable<string> Keys => Palette.Keys;

    /// <summary>A colour as the palette has it for the current mode (before the accent setting recolours anything).</summary>
    public static Color PaletteColor(string key) =>
        (Color)ColorConverter.ConvertFromString(IsDark ? Palette[key].Dark : Palette[key].Light);

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the mode changed (on the UI thread), for views that draw their own colours.</summary>
    public static event Action? Changed;

    /// <summary>A theme colour for code that builds visuals (read again after <see cref="Changed"/>).</summary>
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static Color Color(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    /// <summary>Dev aid (--dark): dark whatever the settings say, for screenshots.</summary>
    public static bool ForceDark { get; set; }

    /// <summary>"system", "light" or "dark" → whether to draw dark now.</summary>
    public static bool WantsDark(string? mode) => ForceDark || mode switch
    {
        "dark" => true,
        "light" => false,
        _ => WindowsIsDark(),
    };

    public static void Apply(bool dark)
    {
        var first = !_applied;
        _applied = true;
        if (!first && dark == IsDark) return;
        IsDark = dark;
        var resources = Application.Current.Resources;
        foreach (var (key, (light, darkHex)) in Palette)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(dark ? darkHex : light));
            brush.Freeze();
            resources[key] = brush;
        }
        // Controls with a style of ours that isn't based on the Fluent one fall back to Windows' classic theme, which
        // draws text in the system "control text" colour (black): make that the theme's text colour too.
        var text = (SolidColorBrush)resources["TextBrush"];
        resources[SystemColors.ControlTextBrushKey] = text;
        resources[SystemColors.WindowTextBrushKey] = text;
        // …and lists without a background of their own take the system "window" colour (white): the page colour.
        resources[SystemColors.WindowBrushKey] = resources["SurfaceBrush"];
#pragma warning disable WPF0001 // ThemeMode is marked experimental
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        if (!first) Changed?.Invoke();
    }

    private static bool _applied;

    /// <summary>Windows' setting for apps (Settings → Personalization → Colors → Choose your mode).</summary>
    public static bool WindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

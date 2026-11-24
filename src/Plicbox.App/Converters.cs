using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Plicbox.App;

/// <summary>Collapsed when the bound value is null (e.g. no picture), visible otherwise.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible; // empty text counts as nothing

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sidebar indentation: ConverterParameter "left,right" plus <see cref="Step"/> per nesting level.</summary>
public sealed class IndentConverter : IValueConverter
{
    public double Step { get; set; } = 22;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = ((string)parameter).Split(',');
        var left = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var right = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return new Thickness(left + (int)value * Step, 0, right, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>RadioButton ↔ enum: checked when the value equals ConverterParameter; checking sets it.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter) : Binding.DoNothing;
}

/// <summary>"#RRGGBB" -> brush (calendar colours in templates).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ViewModels.CalendarBrushes.For(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when a count is above zero (empty sections hide).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Whether a date heading in the message list is open: groups folded by clicking their heading stay folded (by name,
/// such as "Last Week") while the app runs, also when the list is reloaded or another folder shown.
/// </summary>
public sealed class GroupOpenConverter : IValueConverter
{
    public static readonly HashSet<string> Folded = [];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !Folded.Contains(value?.ToString() ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

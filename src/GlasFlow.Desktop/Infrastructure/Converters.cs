using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GlasFlow.Desktop.Infrastructure;

public static class StatusInfo
{
    public static string Label(string? status) => status switch
    {
        "Draft" => "Borrador",
        "Confirmed" => "Confirmado",
        "Shipped" => "Embarcado",
        "Cancelled" => "Cancelado",
        "Planned" => "Planeada",
        "InProgress" => "En proceso",
        "Completed" => "Completada",
        _ => status ?? ""
    };

    public static (string bg, string fg) Colors(string? status) => status switch
    {
        "Draft" => ("#E5E7EB", "#374151"),
        "Confirmed" => ("#DBEAFE", "#1E40AF"),
        "Shipped" => ("#D1FAE5", "#065F46"),
        "Cancelled" => ("#FEE2E2", "#991B1B"),
        _ => ("#E5E7EB", "#374151")
    };
}

/// <summary>Order status -> chip colors (parameter "bg" or "fg") or Spanish label (parameter "label").</summary>
public sealed class StatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as string;
        var mode = parameter as string;
        if (mode == "label") return StatusInfo.Label(status);
        var (bg, fg) = StatusInfo.Colors(status);
        return (SolidColorBrush)new BrushConverter().ConvertFromString(mode == "fg" ? fg : bg)!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter as string == "invert";
        var visible = value is not null && !(value is string s && s.Length == 0);
        return visible ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter as string == "invert";
        return (value is true) ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

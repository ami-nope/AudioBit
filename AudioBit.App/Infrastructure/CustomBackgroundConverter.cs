using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AudioBit.App.Infrastructure;

internal static class CustomBackgroundParser
{
    private static readonly BrushConverter BrushConverter = new();

    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public static bool TryParse(string? value, out Brush? brush)
    {
        brush = null;

        var normalized = Normalize(value);
        if (normalized is null)
        {
            return false;
        }

        if ((TryParseSimpleBrush(normalized, out brush) || TryParseGradient(normalized, out brush)) && brush is not null)
        {
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return true;
        }

        return false;
    }

    public static bool TryGetRepresentativeColor(string? value, out Color color)
    {
        color = default;

        if (!TryParse(value, out var brush) || brush is null)
        {
            return false;
        }

        switch (brush)
        {
            case SolidColorBrush solidColorBrush:
                color = solidColorBrush.Color;
                return true;
            case GradientBrush gradientBrush when gradientBrush.GradientStops.Count > 0:
                color = gradientBrush.GradientStops[0].Color;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseSimpleBrush(string value, out Brush? brush)
    {
        try
        {
            brush = BrushConverter.ConvertFromInvariantString(value) as Brush;
            return brush is not null;
        }
        catch
        {
            brush = null;
            return false;
        }
    }

    private static bool TryParseGradient(string value, out Brush? brush)
    {
        brush = null;

        var tokens = value.Contains("->", StringComparison.Ordinal)
            ? value.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 2)
        {
            return false;
        }

        var gradientStops = new GradientStopCollection();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (ColorConverter.ConvertFromString(tokens[i]) is not Color color)
            {
                return false;
            }

            var offset = tokens.Length == 1 ? 0 : i / (double)(tokens.Length - 1);
            gradientStops.Add(new GradientStop(color, offset));
        }

        brush = new LinearGradientBrush(gradientStops, new Point(0, 0), new Point(1, 1));
        return true;
    }
}

internal sealed class CustomBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && CustomBackgroundParser.TryParse(text, out var brush) && brush is not null)
        {
            return brush;
        }

        if (parameter is string resourceKey
            && Application.Current.TryFindResource(resourceKey) is Brush fallbackBrush)
        {
            return fallbackBrush;
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

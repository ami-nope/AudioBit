using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioBit.Installer;

public sealed class ProgressToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var numeric = value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            _ => System.Convert.ToDouble(value, CultureInfo.InvariantCulture)
        };

        return new GridLength(Math.Max(numeric, 0), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}

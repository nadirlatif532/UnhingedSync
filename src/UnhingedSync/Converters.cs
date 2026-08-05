using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace UnhingedSync;

/// <summary>Turns the row's hex badge colour into a brush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly BrushConverter Inner = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return (Brush)Inner.ConvertFromString(hex)!; }
            catch (FormatException) { }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

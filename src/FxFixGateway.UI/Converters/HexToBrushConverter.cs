using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FxFixGateway.UI.Converters
{
    /// <summary>
    /// Turns a hex colour string ("#4CAF50") into a brush. An opacity can be given as
    /// ConverterParameter ("0.15") — used for status pills tinted in the status colour.
    /// </summary>
    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var color = Colors.Gray;

            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    color = (Color)ColorConverter.ConvertFromString(hex);
                }
                catch
                {
                    color = Colors.Gray;
                }
            }

            if (parameter is string text
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            {
                color.A = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1));
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}

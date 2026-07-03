using System;
using System.Globalization;
using System.Windows.Data;

namespace ChessOverMesh.Gui;

/// <summary>Scales a font size by the factor given as ConverterParameter. Used to size the dim
/// metadata/expiry lines proportionally below a chat message so they grow and shrink together with it
/// (the message inherits the chat list's font size; these bind to it × a &lt;1 factor to stay smaller).</summary>
public sealed class FontScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double size = value is double d ? d : 12;
        double factor =
            parameter is double pd ? pd :
            parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f :
            1.0;
        return size * factor;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

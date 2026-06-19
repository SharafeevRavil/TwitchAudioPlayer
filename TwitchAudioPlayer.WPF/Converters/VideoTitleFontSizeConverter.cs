using System.Globalization;
using System.Windows.Data;

namespace TwitchAudioPlayer.WPF.Converters;

public sealed class VideoTitleFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var height = value is double actualHeight ? actualHeight : 0;
        return Math.Clamp(height * 0.045, 18, 34);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

using System.Globalization;
using System.Windows.Data;

namespace TwitchAudioPlayer.WPF.Converters;

public class AudioDurationSecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (value is not double seconds) return "0:00";
        return $"{(int)seconds / 60}:{(int)seconds % 60:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string str) return 0f;

        var split = str.Split(":");
        if (split.Length == 2 && int.TryParse(split[0], out var minutes) && int.TryParse(split[1], out var seconds))
            return minutes * 60 + seconds;

        return 0f;
    }
}
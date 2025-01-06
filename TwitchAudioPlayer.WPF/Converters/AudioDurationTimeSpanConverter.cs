using System.Globalization;
using System.Windows.Data;

namespace TwitchAudioPlayer.WPF.Converters;

public class AudioDurationTimeSpanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (value is not TimeSpan timeSpan) return "0:00";
        return $"{(int)timeSpan.TotalMinutes}:{timeSpan.Seconds:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string str) return TimeSpan.Zero;

        var split = str.Split(":");
        if (split.Length == 2 && int.TryParse(split[0], out var minutes) && int.TryParse(split[1], out var seconds))
            return new TimeSpan(0, minutes, seconds);
        return TimeSpan.Zero;
    }
}
namespace TwitchAudioPlayer.WPF.Helpers;

public static class VolumeCurve
{
    private const double Exponent = 2.2;

    public static double SliderToVolume(double sliderPosition)
    {
        var normalized = Math.Clamp(sliderPosition, 0, 1);
        return normalized <= 0 ? 0 : Math.Pow(normalized, Exponent);
    }

    public static double VolumeToSlider(double volume)
    {
        var normalized = Math.Clamp(volume, 0, 1);
        return normalized <= 0 ? 0 : Math.Pow(normalized, 1 / Exponent);
    }
}

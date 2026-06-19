using System.Windows;
using System.Windows.Threading;

namespace TwitchAudioPlayer.WPF.Services;

public static class WindowBoundsHelper
{
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(500);

    public static void Apply(Window window, WindowBoundsSettings bounds)
    {
        if (!TryCreateRect(bounds, out var rect) || !IntersectsVirtualScreen(rect))
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = rect.Left;
        window.Top = rect.Top;
        window.Width = Math.Max(window.MinWidth, rect.Width);
        window.Height = Math.Max(window.MinHeight, rect.Height);

        if (bounds.IsMaximized)
            window.WindowState = WindowState.Maximized;
    }

    public static void CenterOver(Window window, WindowBoundsSettings bounds)
    {
        if (!TryCreateRect(bounds, out var ownerRect) || !IntersectsVirtualScreen(ownerRect))
            return;

        var width = GetStartupDimension(window.Width, window.MinWidth);
        var height = GetStartupDimension(window.Height, window.MinHeight);
        var virtualScreen = GetVirtualScreen();

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Clamp(ownerRect.Left + (ownerRect.Width - width) / 2, virtualScreen.Left, virtualScreen.Right - width);
        window.Top = Clamp(ownerRect.Top + (ownerRect.Height - height) / 2, virtualScreen.Top, virtualScreen.Bottom - height);
    }

    public static void Capture(Window window, WindowBoundsSettings bounds)
    {
        var rect = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (!IsUsable(rect))
            return;

        bounds.Left = rect.Left;
        bounds.Top = rect.Top;
        bounds.Width = Math.Max(window.MinWidth, rect.Width);
        bounds.Height = Math.Max(window.MinHeight, rect.Height);
        bounds.IsMaximized = window.WindowState == WindowState.Maximized;
    }

    public static void AttachAutoSave(Window window, WindowBoundsSettings bounds, IUserSettingsManager userSettingsManager)
    {
        var saveTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = AutoSaveDelay
        };

        saveTimer.Tick += async (_, _) =>
        {
            saveTimer.Stop();
            Capture(window, bounds);
            await userSettingsManager.SaveSettingsSilentlyAsync();
        };

        void ScheduleSave(object? sender, EventArgs e)
        {
            if (!window.IsLoaded)
                return;

            saveTimer.Stop();
            saveTimer.Start();
        }

        window.LocationChanged += ScheduleSave;
        window.SizeChanged += ScheduleSave;
        window.StateChanged += ScheduleSave;
        window.Closing += (_, _) =>
        {
            saveTimer.Stop();
            Capture(window, bounds);
            userSettingsManager.SaveSettingsSilently();
        };
    }

    private static bool TryCreateRect(WindowBoundsSettings bounds, out Rect rect)
    {
        rect = Rect.Empty;
        if (bounds is not { Left: { } left, Top: { } top, Width: { } width, Height: { } height })
            return false;

        rect = new Rect(left, top, width, height);
        return IsUsable(rect);
    }

    private static bool IsUsable(Rect rect) =>
        !rect.IsEmpty &&
        !double.IsNaN(rect.Left) &&
        !double.IsNaN(rect.Top) &&
        !double.IsNaN(rect.Width) &&
        !double.IsNaN(rect.Height) &&
        rect.Width > 0 &&
        rect.Height > 0;

    private static bool IntersectsVirtualScreen(Rect rect)
    {
        var virtualScreen = GetVirtualScreen();

        return virtualScreen.IntersectsWith(rect);
    }

    private static Rect GetVirtualScreen() =>
        new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

    private static double GetStartupDimension(double requested, double minimum) =>
        double.IsNaN(requested) || requested <= 0 ? minimum : Math.Max(requested, minimum);

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Min(Math.Max(value, min), max);
}

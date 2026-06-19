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
            await userSettingsManager.SaveSettingsAsync();
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
            userSettingsManager.SaveSettingsAsync().GetAwaiter().GetResult();
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
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return virtualScreen.IntersectsWith(rect);
    }
}

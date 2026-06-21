using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TwitchAudioPlayer.WPF.Services;

public static class WindowTopmostHelper
{
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static void Apply(Window window, bool topmost)
    {
        window.Topmost = topmost;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
            return;
        SetWindowPos(handle, topmost ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public static void ApplyAfterLayout(Window window, bool topmost) =>
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => Apply(window, topmost)));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

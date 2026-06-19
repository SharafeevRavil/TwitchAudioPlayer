using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TwitchAudioPlayer.WPF.Views;

namespace TwitchAudioPlayer.WPF.Services;

public sealed class BrowserPlayerWindowService(BrowserPlayerWindow window)
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    public void Show(Window owner)
    {
        if (!window.IsVisible)
            window.Show();

        PutPlayerAfterMainWindow(owner);
        owner.Dispatcher.BeginInvoke(() =>
        {
            PutPlayerAfterMainWindow(owner);
            owner.Activate();
        }, DispatcherPriority.ApplicationIdle);
    }

    public void SyncWithMainWindowState(WindowState state)
    {
        if (state == WindowState.Minimized)
        {
            window.MinimizeWithMainWindow();
            return;
        }

        window.RestoreWithMainWindow();
    }

    private void PutPlayerAfterMainWindow(Window owner)
    {
        var playerHandle = new WindowInteropHelper(window).Handle;
        var ownerHandle = new WindowInteropHelper(owner).Handle;

        if (playerHandle == IntPtr.Zero || ownerHandle == IntPtr.Zero)
            return;

        SetWindowPos(playerHandle, ownerHandle, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}

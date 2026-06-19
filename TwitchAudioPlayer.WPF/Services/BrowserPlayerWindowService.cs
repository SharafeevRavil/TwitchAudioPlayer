using System.Windows;
using TwitchAudioPlayer.WPF.Views;

namespace TwitchAudioPlayer.WPF.Services;

public sealed class BrowserPlayerWindowService(BrowserPlayerWindow window)
{
    public void Show(Window owner)
    {
        if (window.Owner == null && !window.IsVisible)
            window.Owner = owner;

        if (!window.IsVisible)
            window.Show();
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
}

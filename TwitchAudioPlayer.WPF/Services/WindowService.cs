using Microsoft.Extensions.DependencyInjection;
using TwitchAudioPlayer.WPF.Views.Modals;

namespace TwitchAudioPlayer.WPF.Services;

public class WindowService(IServiceProvider serviceProvider) : IWindowService
{
    public void OpenVkSettingsWindow() => serviceProvider.GetRequiredService<VkSettingsView>().ShowDialog();
    public void OpenYtSettingsWindow() => serviceProvider.GetRequiredService<YtSettingsView>().ShowDialog();
    public void OpenHotkeySettingsWindow() => serviceProvider.GetRequiredService<HotkeySettingsView>().ShowDialog();
}

public interface IWindowService
{
    void OpenVkSettingsWindow();
    void OpenYtSettingsWindow();
    void OpenHotkeySettingsWindow();
}
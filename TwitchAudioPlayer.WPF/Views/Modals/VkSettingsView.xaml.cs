using System.Windows;
using TwitchAudioPlayer.WPF.ViewModels.Modals;

namespace TwitchAudioPlayer.WPF.Views.Modals;

public partial class VkSettingsView : Window
{
    public VkSettingsView(VkSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = Close;
    }
}
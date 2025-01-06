using System.Windows;
using TwitchAudioPlayer.WPF.ViewModels.Modals;

namespace TwitchAudioPlayer.WPF.Views.Modals;

public partial class YtSettingsView : Window
{
    public YtSettingsView(YtSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = Close;
    }
}
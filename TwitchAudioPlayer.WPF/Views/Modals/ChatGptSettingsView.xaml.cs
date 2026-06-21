using System.Windows;
using TwitchAudioPlayer.WPF.ViewModels.Modals;

namespace TwitchAudioPlayer.WPF.Views.Modals;

public partial class ChatGptSettingsView : Window
{
    public ChatGptSettingsView(ChatGptSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = Close;
        Closed += (_, _) => viewModel.Dispose();
    }
}

using System.Windows;
using TwitchAudioPlayer.WPF.ViewModels.Modals;

namespace TwitchAudioPlayer.WPF.Views.Modals;

public partial class ObsSetupView : Window
{
    public ObsSetupView(ObsSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = Close;
        Loaded += (_, _) => ObsPasswordBox.Password = viewModel.Password ?? "";
        ObsPasswordBox.PasswordChanged += (_, _) => viewModel.Password = ObsPasswordBox.Password;
    }
}

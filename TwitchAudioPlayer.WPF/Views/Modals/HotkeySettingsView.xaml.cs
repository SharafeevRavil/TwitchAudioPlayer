using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TwitchAudioPlayer.WPF.ViewModels.Modals;

namespace TwitchAudioPlayer.WPF.Views.Modals;

public partial class HotkeySettingsView : Window
{
    public HotkeySettingsView(HotkeySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = Close;
    }

    private void HotkeyTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeySettingsViewModel viewModel) return;

        switch (e)
        {
            case { Key: Key.LeftShift }:
            case { Key: Key.LeftCtrl }:
            case { Key: Key.LeftAlt }:
                return;
        }

        if (sender is TextBox textBox) viewModel.UpdateText(textBox.Name, e.Key, Keyboard.Modifiers);

        e.Handled = true;
    }

}
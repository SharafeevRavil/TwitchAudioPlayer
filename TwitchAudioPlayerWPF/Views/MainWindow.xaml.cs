using System.Windows;
using TwitchAudioPlayerWPF.ViewModels;

namespace TwitchAudioPlayerWPF.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        DataContext = mainViewModel;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        throw new NotImplementedException();
    }
}


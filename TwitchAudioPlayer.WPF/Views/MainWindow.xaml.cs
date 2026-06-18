using System.Windows;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly BrowserPlayerWindowService _browserPlayerWindowService;

    public MainWindow(MainWindowViewModel mainWindowViewModel, VkAudioView audioPanel, AudioPlayerView audioPlayerView,
        YtAudioView youTubeAudioView, BrowserPlayerWindowService browserPlayerWindowService)
    {
        InitializeComponent();
        DataContext = mainWindowViewModel;
        _browserPlayerWindowService = browserPlayerWindowService;
        StateChanged += OnWindowStateChanged;

        VkAudioView.Content = audioPanel;
        YouTubeAudioView.Content = youTubeAudioView;
        AudioPlayer.Content = audioPlayerView;
    }

    // долбоебская хуйня
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _browserPlayerWindowService.Show();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaximizeIcon.Kind = WindowState == WindowState.Maximized
            ? PackIconKind.WindowRestore
            : PackIconKind.WindowMaximize;

        _browserPlayerWindowService.SyncWithMainWindowState(WindowState);
    }
}

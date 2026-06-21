using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
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
    private readonly IUserSettingsManager _userSettingsManager;

    public MainWindow(MainWindowViewModel mainWindowViewModel, VkAudioView audioPanel, AudioPlayerView audioPlayerView,
        YtAudioView youTubeAudioView, BrowserPlayerWindowService browserPlayerWindowService,
        IUserSettingsManager userSettingsManager)
    {
        InitializeComponent();
        DataContext = mainWindowViewModel;
        _browserPlayerWindowService = browserPlayerWindowService;
        _userSettingsManager = userSettingsManager;

        WindowBoundsHelper.Apply(this, _userSettingsManager.Settings.MainWindowBounds);
        WindowBoundsHelper.AttachAutoSave(this, _userSettingsManager.Settings.MainWindowBounds, _userSettingsManager);
        StateChanged += OnWindowStateChanged;
        SourceInitialized += (_, _) => ApplyTopmost();
        Activated += (_, _) =>
        {
            if (mainWindowViewModel.IsPinned)
                WindowTopmostHelper.Apply(this, true);
        };
        mainWindowViewModel.PropertyChanged += OnViewModelPropertyChanged;
        OnWindowStateChanged(this, EventArgs.Empty);

        VkAudioView.Content = audioPanel;
        YouTubeAudioView.Content = youTubeAudioView;
        AudioPlayer.Content = audioPlayerView;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTopmost();
        _browserPlayerWindowService.Show(this);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPinned))
            ApplyTopmost();
    }

    private void ApplyTopmost()
    {
        var value = DataContext is MainWindowViewModel { IsPinned: true };
        WindowTopmostHelper.Apply(this, value);
        WindowTopmostHelper.ApplyAfterLayout(this, value);
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

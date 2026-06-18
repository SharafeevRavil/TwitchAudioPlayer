using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class YtAudioView : UserControl
{
    private const string PlayerHostName = "tap-player.local";
    private readonly YtAudioViewModel _viewModel;
    private TaskCompletionSource _pageLoaded = NewPageLoadedSource();
    private bool _webViewInitialized;

    public YtAudioView(YtAudioViewModel ytAudioViewModel)
    {
        InitializeComponent();
        _viewModel = ytAudioViewModel;
        DataContext = ytAudioViewModel;

        Loaded += OnLoaded;

        _viewModel.BrowserPlaybackRequested += ViewModelOnBrowserPlaybackRequested;
        _viewModel.BrowserPlayRequested += ViewModelOnBrowserPlayRequested;
        _viewModel.BrowserPauseRequested += ViewModelOnBrowserPauseRequested;
        _viewModel.BrowserStopRequested += ViewModelOnBrowserStopRequested;
    }

    private static TaskCompletionSource NewPageLoadedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel.IsBrowserPlaybackMode)
            await EnsureWebViewAsync();
    }

    private async void ViewModelOnBrowserPlaybackRequested(object? sender, YouTubeBrowserPlaybackRequest e)
    {
        await ExecutePlayerCommandAsync($"load({JsonSerializer.Serialize(e.VideoId)}, {FormatSeconds(e.StartPosition)})");
    }

    private async void ViewModelOnBrowserPlayRequested(object? sender, EventArgs e)
    {
        await ExecutePlayerCommandAsync("play()");
    }

    private async void ViewModelOnBrowserPauseRequested(object? sender, EventArgs e)
    {
        await ExecutePlayerCommandAsync("pause()");
    }

    private async void ViewModelOnBrowserStopRequested(object? sender, EventArgs e)
    {
        await ExecutePlayerCommandAsync("stop()", reportFailure: false);
    }

    private async Task ExecutePlayerCommandAsync(string command, bool reportFailure = true)
    {
        try
        {
            await EnsureWebViewAsync();
            await WaitForPageAsync();
            await YoutubeWebView.ExecuteScriptAsync($"window.tapPlayer && window.tapPlayer.{command};");
        }
        catch (Exception ex)
        {
            if (reportFailure)
                await _viewModel.BrowserPlaybackFailedAsync($"YouTube browser player failed: {ex.Message}");
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewInitialized)
            return;

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchAudioPlayer",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await YoutubeWebView.EnsureCoreWebView2Async(environment);

        YoutubeWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        YoutubeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        YoutubeWebView.CoreWebView2.WebMessageReceived += CoreWebView2OnWebMessageReceived;
        YoutubeWebView.NavigationCompleted += YoutubeWebViewOnNavigationCompleted;
        YoutubeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            PlayerHostName,
            GetPlayerPageFolder(),
            CoreWebView2HostResourceAccessKind.Allow);

        _pageLoaded = NewPageLoadedSource();
        YoutubeWebView.Source = GetPlayerPageUri();
        _webViewInitialized = true;
    }

    private async Task WaitForPageAsync()
    {
        var completed = await Task.WhenAny(_pageLoaded.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != _pageLoaded.Task)
            throw new TimeoutException("local YouTube player page did not finish loading");

        await _pageLoaded.Task;
    }

    private void YoutubeWebViewOnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
            _pageLoaded.TrySetResult();
        else
            _pageLoaded.TrySetException(new InvalidOperationException($"navigation failed: {e.WebErrorStatus}"));
    }

    private async void CoreWebView2OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    _viewModel.BrowserPlayerReady();
                    break;
                case "ended":
                    await _viewModel.BrowserPlaybackEndedAsync();
                    break;
                case "position":
                    await _viewModel.BrowserPositionChangedAsync(
                        TimeSpan.FromSeconds(root.GetProperty("position").GetDouble()),
                        TimeSpan.FromSeconds(root.GetProperty("duration").GetDouble()));
                    break;
                case "error":
                    var errorCode = root.TryGetProperty("errorCode", out var error)
                        ? error.GetInt32().ToString(CultureInfo.InvariantCulture)
                        : "unknown";
                    await _viewModel.BrowserPlaybackFailedAsync($"YouTube player error: {errorCode}");
                    break;
            }
        }
        catch (Exception ex)
        {
            await _viewModel.BrowserPlaybackFailedAsync($"YouTube browser message failed: {ex.Message}");
        }
    }

    private static string GetPlayerPageFolder() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "WebView2");

    private static Uri GetPlayerPageUri() =>
        new($"https://{PlayerHostName}/youtube-player.html");

    private static string FormatSeconds(TimeSpan time) =>
        time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}

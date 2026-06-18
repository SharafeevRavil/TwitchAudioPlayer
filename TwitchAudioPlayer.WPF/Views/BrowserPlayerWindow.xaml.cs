using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class BrowserPlayerWindow : Window
{
    private const string PlayerHostName = "tap-player.local";
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;
    private const double AspectRatio = 16d / 9d;

    private readonly BrowserPlayerService _browserPlayer;
    private readonly BrowserPlayerViewModel _viewModel;
    private TaskCompletionSource _pageLoaded = NewPageLoadedSource();
    private bool _webViewInitialized;
    private bool _isResizingToAspect;
    private bool _wasMinimizedWithMainWindow;

    public BrowserPlayerWindow(BrowserPlayerViewModel viewModel, BrowserPlayerService browserPlayer)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _browserPlayer = browserPlayer;
        DataContext = viewModel;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnSizeChanged;
        _viewModel.PinChanged += (_, value) => Topmost = value;
        _browserPlayer.LoadRequested += BrowserPlayerOnLoadRequested;
        _browserPlayer.PlayRequested += BrowserPlayerOnPlayRequested;
        _browserPlayer.PauseRequested += BrowserPlayerOnPauseRequested;
        _browserPlayer.StopRequested += BrowserPlayerOnStopRequested;
        _browserPlayer.SeekRequested += BrowserPlayerOnSeekRequested;
        _browserPlayer.VolumeRequested += BrowserPlayerOnVolumeRequested;
        _browserPlayer.MuteRequested += BrowserPlayerOnMuteRequested;
    }

    public void MinimizeWithMainWindow()
    {
        if (_viewModel.IsPinned || WindowState == WindowState.Minimized)
            return;

        _wasMinimizedWithMainWindow = true;
        WindowState = WindowState.Minimized;
    }

    public void RestoreWithMainWindow()
    {
        if (!_wasMinimizedWithMainWindow)
            return;

        _wasMinimizedWithMainWindow = false;
        WindowState = WindowState.Normal;
    }

    private static TaskCompletionSource NewPageLoadedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_browserPlayer.IsYouTubeActive)
            await EnsureWebViewAsync();
    }

    private async void BrowserPlayerOnLoadRequested(object? sender, YouTubeBrowserPlaybackRequest e)
    {
        await ExecutePlayerCommandAsync($"load({JsonSerializer.Serialize(e.VideoId)}, {FormatSeconds(e.StartPosition)})");
        await ExecutePlayerCommandAsync($"setVolume({FormatNumber(_browserPlayer.Volume)})", reportFailure: false);
        await ExecutePlayerCommandAsync(_browserPlayer.IsMuted ? "mute()" : "unMute()", reportFailure: false);
    }

    private async void BrowserPlayerOnPlayRequested(object? sender, EventArgs e)
    {
        await ExecutePlayerCommandAsync("play()");
    }

    private async void BrowserPlayerOnPauseRequested(object? sender, EventArgs e)
    {
        await ExecutePlayerCommandAsync("pause()");
    }

    private async void BrowserPlayerOnStopRequested(object? sender, EventArgs e)
    {
        await ExecutePlayerCommandAsync("stop()", reportFailure: false);
    }

    private async void BrowserPlayerOnSeekRequested(object? sender, TimeSpan e)
    {
        await ExecutePlayerCommandAsync($"seek({FormatSeconds(e)})");
    }

    private async void BrowserPlayerOnVolumeRequested(object? sender, double e)
    {
        await ExecutePlayerCommandAsync($"setVolume({FormatNumber(e)})", reportFailure: false);
    }

    private async void BrowserPlayerOnMuteRequested(object? sender, bool e)
    {
        await ExecutePlayerCommandAsync(e ? "mute()" : "unMute()", reportFailure: false);
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
                _browserPlayer.ReportFailure($"YouTube browser player failed: {ex.Message}");
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

        YoutubeWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
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

    private void CoreWebView2OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    _browserPlayer.PlayerReady();
                    break;
                case "ended":
                    _browserPlayer.ReportEnded();
                    break;
                case "state":
                    if (root.TryGetProperty("state", out var state))
                        _browserPlayer.ReportPlaybackState(state.GetInt32() is 1 or 3);
                    break;
                case "position":
                    _browserPlayer.ReportPosition(
                        TimeSpan.FromSeconds(root.GetProperty("position").GetDouble()),
                        TimeSpan.FromSeconds(root.GetProperty("duration").GetDouble()));
                    break;
                case "volume":
                    _browserPlayer.ReportVolume(
                        root.GetProperty("volume").GetDouble(),
                        root.GetProperty("muted").GetBoolean());
                    break;
                case "error":
                    var errorCode = root.TryGetProperty("errorCode", out var error)
                        ? error.GetInt32().ToString(CultureInfo.InvariantCulture)
                        : "unknown";
                    _browserPlayer.ReportFailure($"YouTube player error: {errorCode}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _browserPlayer.ReportFailure($"YouTube browser message failed: {ex.Message}");
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isResizingToAspect || WindowState != WindowState.Normal)
            return;

        _isResizingToAspect = true;
        try
        {
            var targetHeight = Width / AspectRatio;
            if (Math.Abs(Height - targetHeight) > 1)
                Height = Math.Max(MinHeight, targetHeight);
        }
        finally
        {
            _isResizingToAspect = false;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSizing)
            return IntPtr.Zero;

        var rect = Marshal.PtrToStructure<Rect>(lParam);
        KeepSizingRectAtAspectRatio(wParam.ToInt32(), ref rect);
        Marshal.StructureToPtr(rect, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private static void KeepSizingRectAtAspectRatio(int edge, ref Rect rect)
    {
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var targetWidth = (int)Math.Round(height * AspectRatio);
        var targetHeight = (int)Math.Round(width / AspectRatio);

        switch (edge)
        {
            case WmszLeft:
            case WmszRight:
                rect.Bottom = rect.Top + targetHeight;
                break;
            case WmszTop:
            case WmszBottom:
                rect.Right = rect.Left + targetWidth;
                break;
            case WmszTopLeft:
                if (width / (double)height > AspectRatio)
                    rect.Left = rect.Right - targetWidth;
                else
                    rect.Top = rect.Bottom - targetHeight;
                break;
            case WmszTopRight:
                if (width / (double)height > AspectRatio)
                    rect.Right = rect.Left + targetWidth;
                else
                    rect.Top = rect.Bottom - targetHeight;
                break;
            case WmszBottomLeft:
                if (width / (double)height > AspectRatio)
                    rect.Left = rect.Right - targetWidth;
                else
                    rect.Bottom = rect.Top + targetHeight;
                break;
            case WmszBottomRight:
                if (width / (double)height > AspectRatio)
                    rect.Right = rect.Left + targetWidth;
                else
                    rect.Bottom = rect.Top + targetHeight;
                break;
        }
    }

    private void DragStrip_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private static string GetPlayerPageFolder() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "WebView2");

    private static Uri GetPlayerPageUri() =>
        new($"https://{PlayerHostName}/youtube-player.html");

    private static string FormatSeconds(TimeSpan time) =>
        FormatNumber(time.TotalSeconds);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

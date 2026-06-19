using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class BrowserPlayerWindow : Window
{
    private const string PlayerHostName = "tap-player.local";
    private const string HideYouTubeChromeCss = """
        .ytp-chrome-top,
        .ytp-chrome-bottom,
        .ytp-gradient-top,
        .ytp-gradient-bottom,
        .ytp-title,
        .ytp-title-channel,
        .ytp-title-text,
        .ytp-title-link,
        .ytp-show-cards-title,
        .ytp-pause-overlay,
        .ytp-bezel,
        .ytp-bezel-text-wrapper,
        .ytp-large-play-button,
        .ytp-watermark,
        .ytp-youtube-button,
        .ytp-share-button,
        .ytp-watch-later-button,
        .ytp-copylink-button,
        .ytp-cards-button,
        .ytp-cards-teaser,
        .ytp-ce-element,
        .ytp-ce-covering-overlay,
        .ytp-ce-expanding-overlay,
        .ytp-autonav-endscreen-upnext,
        .ytp-endscreen-content,
        .ytp-related-on-error-overlay,
        #player-controls,
        .ytPlayerControlsContainerHost,
        .ytPlayerControlsContainerRendered,
        .ytPlayerControlsContainer,
        .ytmPlayerControlsContainer,
        ytm-custom-control,
        .ytm-custom-control,
        ytm-player-controls,
        .ytm-player-controls,
        ytm-player-controls-overlay,
        .ytm-player-controls-overlay,
        ytm-mobile-topbar-renderer,
        ytm-player-endscreen,
        #bottom-sheet-wrapper,
        ytm-bottom-sheet-renderer,
        ytm-menu-popup-renderer,
        ytm-endscreen,
        ytm-watch-next-secondary-results-renderer,
        ytm-related-chip-cloud-renderer,
        ytm-horizontal-card-list-renderer,
        ytm-reel-shelf-renderer,
        ytm-compact-video-renderer,
        ytm-video-with-context-renderer,
        ytm-playlist-panel-renderer,
        ytm-autonav-endscreen,
        ytm-up-next,
        ytm-structured-description-content-renderer,
        .player-controls,
        .player-controls-content,
        .player-controls-background,
        .player-controls-bottom,
        .player-controls-top,
        .player-overlay,
        .player-endscreen,
        .endscreen,
        .related-videos,
        .watch-on-youtube,
        .videowall-endscreen,
        .html5-endscreen {
            display: none !important;
            opacity: 0 !important;
            visibility: hidden !important;
            pointer-events: none !important;
        }

        """;

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
    private const int DefaultPlayerChromeHeight = 166;

    private readonly BrowserPlayerService _browserPlayer;
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly BrowserPlayerViewModel _viewModel;
    private TaskCompletionSource _pageLoaded = NewPageLoadedSource();
    private Task? _webViewInitializationTask;
    private bool _webViewInitialized;
    private bool _isResizingToAspect;
    private bool _wasMinimizedWithMainWindow;
    private int _activeRequestId;

    public BrowserPlayerWindow(
        BrowserPlayerViewModel viewModel,
        BrowserPlayerService browserPlayer,
        IUserSettingsManager userSettingsManager)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _browserPlayer = browserPlayer;
        _userSettingsManager = userSettingsManager;
        DataContext = viewModel;

        WindowBoundsHelper.Apply(this, _userSettingsManager.Settings.BrowserPlayerWindowBounds);
        WindowBoundsHelper.AttachAutoSave(this, _userSettingsManager.Settings.BrowserPlayerWindowBounds, _userSettingsManager);
        _viewModel.IsFullScreen = WindowState == WindowState.Maximized;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnSizeChanged;
        _viewModel.PinChanged += (_, value) => Topmost = value;
        _viewModel.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        _viewModel.FullScreenRequested += (_, _) => ToggleFullScreen();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserPlayerViewModel.OverlayTitle))
                Dispatcher.BeginInvoke(RestartTitleMarquee);
        };
        TrackTitleTextBlock.Loaded += (_, _) => RestartTitleMarquee();
        TrackTitleTextBlock.SizeChanged += (_, _) => RestartTitleMarquee();
        TrackTitleStrip.SizeChanged += (_, _) => RestartTitleMarquee();
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
        await ExecuteStartupAsync();
    }

    private async Task ExecuteStartupAsync()
    {
        try
        {
            await EnsureWebViewAsync();
        }
        catch (Exception ex)
        {
            _browserPlayer.ReportFailure($"YouTube browser player failed: {ex.Message}");
        }
    }

    private async void BrowserPlayerOnLoadRequested(object? sender, YouTubeBrowserPlaybackRequest e)
    {
        _activeRequestId = e.RequestId;
        await ExecutePlayerCommandAsync(
            $"load({JsonSerializer.Serialize(e.VideoId)}, {FormatSeconds(e.StartPosition)}, {e.RequestId})",
            requestId: e.RequestId);
        await ExecutePlayerCommandAsync(
            $"setVolume({FormatNumber(_browserPlayer.Volume)})",
            reportFailure: false,
            requestId: e.RequestId);
        await ExecutePlayerCommandAsync(
            _browserPlayer.IsMuted ? "mute()" : "unMute()",
            reportFailure: false,
            requestId: e.RequestId);
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
        _activeRequestId = 0;
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

    private async Task ExecutePlayerCommandAsync(string command, bool reportFailure = true, int? requestId = null)
    {
        try
        {
            await EnsureWebViewAsync();
            await WaitForPageAsync();
            if (requestId is not null && requestId.Value != _activeRequestId)
                return;

            await YoutubeWebView.ExecuteScriptAsync($"window.tapPlayer && window.tapPlayer.{command};");
        }
        catch (Exception ex)
        {
            if (reportFailure && (requestId is null || requestId.Value == _activeRequestId))
                _browserPlayer.ReportFailure($"YouTube browser player failed: {ex.Message}");
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewInitialized)
            return;

        _webViewInitializationTask ??= InitializeWebViewAsync();
        try
        {
            await _webViewInitializationTask;
        }
        catch
        {
            _webViewInitializationTask = null;
            throw;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (YoutubeWebView.CoreWebView2 is null)
            await YoutubeWebView.EnsureCoreWebView2Async();

        if (_webViewInitialized)
            return;

        YoutubeWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        YoutubeWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        YoutubeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        YoutubeWebView.CoreWebView2.FrameCreated += CoreWebView2OnFrameCreated;
        await YoutubeWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetHideYouTubeChromeScript());
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

    private void CoreWebView2OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        e.Frame.DOMContentLoaded += async (_, _) => await InjectYouTubeChromeCssAsync(e.Frame);
    }

    private static async Task InjectYouTubeChromeCssAsync(CoreWebView2Frame frame)
    {
        try
        {
            await frame.ExecuteScriptAsync(GetHideYouTubeChromeScript());
        }
        catch
        {
            // Some transient YouTube frames navigate away before script injection completes.
        }
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
            var isReadyMessage = type == "ready";
            if (!isReadyMessage && !IsCurrentRequestMessage(root))
                return;

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
                        ? error.GetInt32()
                        : 0;
                    _browserPlayer.ReportFailure(GetYouTubeErrorMessage(errorCode));
                    break;
            }
        }
        catch (Exception ex)
        {
            _browserPlayer.ReportFailure($"YouTube browser message failed: {ex.Message}");
        }
    }

    private bool IsCurrentRequestMessage(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var requestIdElement))
            return _activeRequestId == 0;

        if (!requestIdElement.TryGetInt32(out var requestId))
            return false;

        return requestId != 0 && requestId == _activeRequestId;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isResizingToAspect || WindowState != WindowState.Normal)
            return;

        _isResizingToAspect = true;
        try
        {
            var targetHeight = Width / AspectRatio + GetPlayerChromeHeight();
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

    private void KeepSizingRectAtAspectRatio(int edge, ref Rect rect)
    {
        var chromeHeight = GetPlayerChromeHeight();
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var videoHeight = Math.Max(1, height - chromeHeight);
        var targetWidth = (int)Math.Round(videoHeight * AspectRatio);
        var targetHeight = (int)Math.Round(width / AspectRatio + chromeHeight);

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

    private double GetPlayerChromeHeight()
    {
        var actualHeight = TopChrome.ActualHeight + TrackTitleStrip.ActualHeight + ControlsPanel.ActualHeight;
        return actualHeight > 0 ? actualHeight : DefaultPlayerChromeHeight;
    }

    private void DragStrip_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            return;

        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void RestartTitleMarquee()
    {
        TrackTitleTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        TrackTitleTranslateTransform.X = 0;
        TrackTitleTextBlock.ClearValue(WidthProperty);

        TrackTitleTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var availableWidth = TrackTitleViewport.ActualWidth;
        var textWidth = TrackTitleTextBlock.DesiredSize.Width;
        if (availableWidth <= 0 || textWidth <= availableWidth + 1)
            return;

        TrackTitleTextBlock.Width = textWidth;
        var distance = textWidth - availableWidth + 64;
        var hold = TimeSpan.FromSeconds(1.4);
        var travel = TimeSpan.FromSeconds(Math.Clamp(distance / 42d, 5, 18));
        var resetHold = TimeSpan.FromSeconds(0.8);
        var total = hold + travel + resetHold;

        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(hold + travel)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(total)));

        TrackTitleTranslateTransform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private static string GetPlayerPageFolder() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "WebView2");

    private static Uri GetPlayerPageUri() =>
        new($"https://{PlayerHostName}/youtube-player.html");

    private static string FormatSeconds(TimeSpan time) =>
        FormatNumber(time.TotalSeconds);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string GetYouTubeErrorMessage(int errorCode) =>
        errorCode switch
        {
            2 => "YouTube player error 2: invalid video id or playback parameter.",
            5 => "YouTube player error 5: this video cannot be played in the HTML5 player.",
            100 => "YouTube player error 100: video not found, private, or removed.",
            101 or 150 => $"YouTube player error {errorCode}: owner disabled embedded playback. Open on YouTube.",
            _ => errorCode == 0
                ? "YouTube player error: unknown playback error."
                : $"YouTube player error {errorCode}: playback failed."
        };

    private static string GetHideYouTubeChromeScript()
    {
        var css = JsonSerializer.Serialize(HideYouTubeChromeCss);
        return $$"""
            (() => {
                const css = {{css}};
                const styleId = "tap-hide-youtube-chrome";
                const hiddenSelectors = [
                    ".ytp-chrome-top",
                    ".ytp-chrome-bottom",
                    ".ytp-gradient-top",
                    ".ytp-gradient-bottom",
                    ".ytp-title",
                    ".ytp-title-channel",
                    ".ytp-title-text",
                    ".ytp-title-link",
                    ".ytp-show-cards-title",
                    ".ytp-pause-overlay",
                    ".ytp-bezel",
                    ".ytp-bezel-text-wrapper",
                    ".ytp-large-play-button",
                    ".ytp-watermark",
                    ".ytp-youtube-button",
                    ".ytp-share-button",
                    ".ytp-watch-later-button",
                    ".ytp-copylink-button",
                    ".ytp-cards-button",
                    ".ytp-cards-teaser",
                    ".ytp-ce-element",
                    ".ytp-ce-covering-overlay",
                    ".ytp-ce-expanding-overlay",
                    ".ytp-autonav-endscreen-upnext",
                    ".ytp-endscreen-content",
                    ".ytp-related-on-error-overlay",
                    "#player-controls",
                    ".ytPlayerControlsContainerHost",
                    ".ytPlayerControlsContainerRendered",
                    ".ytPlayerControlsContainer",
                    ".ytmPlayerControlsContainer",
                    "ytm-custom-control",
                    ".ytm-custom-control",
                    "ytm-player-controls",
                    ".ytm-player-controls",
                    "ytm-player-controls-overlay",
                    ".ytm-player-controls-overlay",
                    "ytm-mobile-topbar-renderer",
                    "ytm-player-endscreen",
                    "#bottom-sheet-wrapper",
                    "ytm-bottom-sheet-renderer",
                    "ytm-menu-popup-renderer",
                    "ytm-endscreen",
                    "ytm-watch-next-secondary-results-renderer",
                    "ytm-related-chip-cloud-renderer",
                    "ytm-horizontal-card-list-renderer",
                    "ytm-reel-shelf-renderer",
                    "ytm-compact-video-renderer",
                    "ytm-video-with-context-renderer",
                    "ytm-playlist-panel-renderer",
                    "ytm-autonav-endscreen",
                    "ytm-up-next",
                    "ytm-structured-description-content-renderer",
                    ".player-controls",
                    ".player-controls-content",
                    ".player-controls-background",
                    ".player-controls-bottom",
                    ".player-controls-top",
                    ".player-overlay",
                    ".player-endscreen",
                    ".endscreen",
                    ".related-videos",
                    ".watch-on-youtube",
                    ".videowall-endscreen",
                    ".html5-endscreen"
                ];

                const apply = () => {
                    if (!document.documentElement) {
                        return;
                    }

                    if (!document.getElementById(styleId)) {
                        const style = document.createElement("style");
                        style.id = styleId;
                        style.textContent = css;
                        document.documentElement.appendChild(style);
                    }

                    for (const selector of hiddenSelectors) {
                        for (const element of document.querySelectorAll(selector)) {
                            element.style.setProperty("display", "none", "important");
                            element.style.setProperty("opacity", "0", "important");
                            element.style.setProperty("visibility", "hidden", "important");
                            element.style.setProperty("pointer-events", "none", "important");
                            const tagName = element.tagName.toLowerCase();
                            if (element.id === "player-controls" ||
                                element.id === "bottom-sheet-wrapper" ||
                                tagName.startsWith("ytm-") ||
                                element.classList.contains("ytPlayerControlsContainerHost") ||
                                element.classList.contains("ytPlayerControlsContainerRendered")) {
                                element.remove();
                            }
                        }
                    }
                };

                const start = () => {
                    if (!document.documentElement) {
                        window.setTimeout(start, 25);
                        return;
                    }

                    apply();
                    window.setInterval(apply, 100);
                    new MutationObserver(apply).observe(document.documentElement, {
                        childList: true,
                        subtree: true,
                        attributes: true
                    });
                };

                start();
            })();
            """;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

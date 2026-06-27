using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Serilog;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class BrowserPlayerWindow : Window
{
    private readonly ILogger _logger = Log.ForContext<BrowserPlayerWindow>();
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

    private const int WmSysCommand = 0x0112;
    private const int WmSizing = 0x0214;
    private const int ScMinimize = 0xF020;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const uint DwmWindowCornerDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const double AspectRatio = 16d / 9d;
    private const int DefaultPlayerChromeHeight = 222;

    private readonly BrowserPlayerService _browserPlayer;
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly BrowserPlayerViewModel _viewModel;
    private TaskCompletionSource _pageLoaded = NewPageLoadedSource();
    private Task? _webViewInitializationTask;
    private bool _webViewInitialized;
    private bool _isResizingToAspect;
    private bool _isParkedForObsCapture;
    private WindowState _windowStateBeforeParking = WindowState.Normal;
    private System.Windows.Rect? _boundsBeforeParking;
    private int _activeRequestId;
    private int _playbackStartRequestId;
    private TaskCompletionSource<bool>? _playbackStarted;

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
        Activated += OnActivated;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
        _viewModel.PinChanged += ViewModelOnPinChanged;
        _viewModel.MinimizeRequested += ViewModelOnMinimizeRequested;
        _viewModel.FullScreenRequested += ViewModelOnFullScreenRequested;
        TrackTitleStrip.SizeChanged += (_, _) => ScheduleAspectCorrection();
        ResolverPanel.IsVisibleChanged += (_, _) => ScheduleAspectCorrection();
        _browserPlayer.LoadRequested += BrowserPlayerOnLoadRequested;
        _browserPlayer.PlayRequested += BrowserPlayerOnPlayRequested;
        _browserPlayer.PauseRequested += BrowserPlayerOnPauseRequested;
        _browserPlayer.StopRequested += BrowserPlayerOnStopRequested;
        _browserPlayer.SeekRequested += BrowserPlayerOnSeekRequested;
        _browserPlayer.VolumeRequested += BrowserPlayerOnVolumeRequested;
        _browserPlayer.MuteRequested += BrowserPlayerOnMuteRequested;
    }

    private void ViewModelOnPinChanged(object? sender, bool value) => ApplyTopmost(value);

    private void ViewModelOnMinimizeRequested(object? sender, EventArgs e) =>
        ParkForObsCapture();

    private void ViewModelOnFullScreenRequested(object? sender, EventArgs e) => ToggleFullScreen();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PinChanged -= ViewModelOnPinChanged;
        _viewModel.MinimizeRequested -= ViewModelOnMinimizeRequested;
        _viewModel.FullScreenRequested -= ViewModelOnFullScreenRequested;
        _browserPlayer.LoadRequested -= BrowserPlayerOnLoadRequested;
        _browserPlayer.PlayRequested -= BrowserPlayerOnPlayRequested;
        _browserPlayer.PauseRequested -= BrowserPlayerOnPauseRequested;
        _browserPlayer.StopRequested -= BrowserPlayerOnStopRequested;
        _browserPlayer.SeekRequested -= BrowserPlayerOnSeekRequested;
        _browserPlayer.VolumeRequested -= BrowserPlayerOnVolumeRequested;
        _browserPlayer.MuteRequested -= BrowserPlayerOnMuteRequested;
    }

    public void ParkForObsCapture()
    {
        if (_isParkedForObsCapture)
            return;

        _isParkedForObsCapture = true;
        _windowStateBeforeParking = WindowState;
        _boundsBeforeParking = WindowState == WindowState.Normal
            ? new System.Windows.Rect(Left, Top, Width, Height)
            : RestoreBounds;

        WindowState = WindowState.Normal;
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Left = virtualRight + 64;
        Top = virtualBottom + 64;
        ActivateFallbackWindow();
    }

    public void RestoreFromObsCaptureParking()
    {
        if (!_isParkedForObsCapture)
            return;

        _isParkedForObsCapture = false;
        if (_boundsBeforeParking is { } bounds)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(MinWidth, bounds.Width);
            Height = Math.Max(MinHeight, bounds.Height);
            _boundsBeforeParking = null;
        }

        WindowState = _windowStateBeforeParking == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    private void ActivateFallbackWindow()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!_isParkedForObsCapture)
                return;

            var fallbackWindow = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window != this &&
                                          window.IsVisible &&
                                          window.WindowState != WindowState.Minimized);
            fallbackWindow?.Activate();
        }));
    }

    private static TaskCompletionSource NewPageLoadedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
            ApplyDwmWindowStyle(source.Handle);
        }

        ApplyTopmost(_viewModel.IsPinned);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        RestoreFromObsCaptureParking();
        if (_viewModel.IsPinned)
            WindowTopmostHelper.Apply(this, true);
    }

    private static void ApplyDwmWindowStyle(IntPtr handle)
    {
        var cornerPreference = DwmWindowCornerDoNotRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference,
            ref cornerPreference, sizeof(uint));

        var borderColor = DwmColorNone;
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, sizeof(uint));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTopmost(_viewModel.IsPinned);
        ScheduleAspectCorrection();
        await ExecuteStartupAsync();
    }

    private void ApplyTopmost(bool value)
    {
        WindowTopmostHelper.Apply(this, value);
        WindowTopmostHelper.ApplyAfterLayout(this, value);
    }

    private void EnsureVisibleForObsCapture(BrowserPlaybackOwner owner)
    {
        if (owner != BrowserPlaybackOwner.MusicOrder)
            return;

        RestoreFromObsCaptureParking();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        if (!IsVisible)
            Show();

        _logger.Information(
            "Prepared browser player window for OBS capture: owner={Owner}, state={State}, left={Left:0.##}, top={Top:0.##}, width={Width:0.##}, height={Height:0.##}",
            owner, WindowState, Left, Top, Width, Height);
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
        EnsureVisibleForObsCapture(e.Owner);
        _activeRequestId = e.RequestId;
        _playbackStartRequestId = e.RequestId;
        _playbackStarted?.TrySetCanceled();
        _playbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logger.Information(
            "Browser player load request {RequestId}: video={VideoId}, start={StartSeconds:0.###}, owner={Owner}",
            e.RequestId, e.VideoId, e.StartPosition.TotalSeconds, e.Owner);
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
        _ = MonitorPlaybackStartAsync(e.RequestId, e.VideoId, _playbackStarted);
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
        _playbackStartRequestId = 0;
        _playbackStarted?.TrySetCanceled();
        await ExecutePlayerCommandAsync("stop()", reportFailure: false);
    }

    private async Task MonitorPlaybackStartAsync(
        int requestId,
        string videoId,
        TaskCompletionSource<bool> started)
    {
        try
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(18));
            var completed = await Task.WhenAny(started.Task, timeout);
            if (requestId != _activeRequestId || requestId != _playbackStartRequestId)
                return;

            if (completed == timeout)
            {
                _logger.Warning("Browser player did not start request {RequestId}, video {VideoId}, within 18 seconds",
                    requestId, videoId);
                started.TrySetResult(false);
                _browserPlayer.ReportFailure("YouTube iframe did not start within 18 seconds");
                return;
            }

            if (await started.Task)
                _logger.Information("Browser player started request {RequestId}, video {VideoId}", requestId, videoId);
        }
        catch (OperationCanceledException)
        {
            // Superseded load/stop.
        }
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
            try
            {
                await WaitForPageAsync();
            }
            catch (TimeoutException)
            {
                _logger.Warning("Local player navigation is still pending; waiting for the iframe API bridge");
                await _pageLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            if (requestId is not null && requestId.Value != _activeRequestId)
                return;

            var scriptResult = await YoutubeWebView.ExecuteScriptAsync(
                $"window.tapPlayer ? (window.tapPlayer.{command}, true) : false;");
            if (scriptResult == "false")
                throw new InvalidOperationException("local YouTube player bridge is unavailable");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Browser player command failed: {Command}", command);
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
        _browserPlayer.StatusText = "Starting YouTube WebView2...";
        _logger.Information("Initializing browser player WebView2. Runtime={Runtime}",
            CoreWebView2Environment.GetAvailableBrowserVersionString());
        if (YoutubeWebView.CoreWebView2 is null)
        {
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-quic --disable-gpu --disable-gpu-compositing --disable-accelerated-video-decode " +
                "--disable-direct-composition --disable-features=DirectCompositionVideoOverlays");
            var environment = await CreateWebViewEnvironmentAsync(options);
            await YoutubeWebView.EnsureCoreWebView2Async(environment);
        }

        if (_webViewInitialized)
            return;

        YoutubeWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        YoutubeWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        YoutubeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        YoutubeWebView.CoreWebView2.FrameCreated += CoreWebView2OnFrameCreated;
        await YoutubeWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetHideYouTubeChromeScript());
        YoutubeWebView.CoreWebView2.WebMessageReceived += CoreWebView2OnWebMessageReceived;
        YoutubeWebView.CoreWebView2.ProcessFailed += CoreWebView2OnProcessFailed;
        YoutubeWebView.NavigationCompleted += YoutubeWebViewOnNavigationCompleted;
        YoutubeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            PlayerHostName,
            GetPlayerPageFolder(),
            CoreWebView2HostResourceAccessKind.Allow);

        _pageLoaded = NewPageLoadedSource();
        YoutubeWebView.Source = GetPlayerPageUri();
        _webViewInitialized = true;
    }

    private void CoreWebView2OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.Error("Browser player WebView2 process failed: {Kind}; reason={Reason}; exit={ExitCode}",
            e.ProcessFailedKind, e.Reason, e.ExitCode);
        if (_activeRequestId != 0)
            _browserPlayer.ReportFailure($"YouTube WebView2 process failed: {e.ProcessFailedKind}");
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
        {
            _logger.Information("Browser player local page loaded");
            _browserPlayer.StatusText = "YouTube WebView2 page is loaded";
            _pageLoaded.TrySetResult();
        }
        else
        {
            _logger.Warning("Browser player navigation failed: {Status}", e.WebErrorStatus);
            _pageLoaded.TrySetException(new InvalidOperationException($"navigation failed: {e.WebErrorStatus}"));
        }
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
                    _logger.Information("YouTube iframe API is ready");
                    _pageLoaded.TrySetResult();
                    _browserPlayer.PlayerReady();
                    break;
                case "ended":
                    _browserPlayer.ReportEnded();
                    break;
                case "state":
                    if (root.TryGetProperty("state", out var state))
                    {
                        var stateValue = state.GetInt32();
                        if (stateValue is 1 or 3)
                            _playbackStarted?.TrySetResult(true);
                        _logger.Debug("YouTube iframe state {State} for request {RequestId}",
                            stateValue, _activeRequestId);
                        _browserPlayer.ReportPlaybackState(stateValue is 1 or 3);
                    }
                    break;
                case "position":
                    _playbackStarted?.TrySetResult(true);
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
                    _playbackStarted?.TrySetResult(false);
                    var message = GetYouTubeErrorMessage(errorCode);
                    _logger.Warning("YouTube iframe error {ErrorCode} for request {RequestId}: {Message}",
                        errorCode, _activeRequestId, message);
                    _browserPlayer.ReportFailure(message);
                    break;
                case "diagnostic":
                    _logger.Warning("YouTube iframe diagnostic for request {RequestId}: {Message}",
                        _activeRequestId,
                        root.TryGetProperty("message", out var diagnostic) ? diagnostic.GetString() : "unknown");
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

    private void ScheduleAspectCorrection() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CorrectAspectRatio));

    private void CorrectAspectRatio()
    {
        if (_isResizingToAspect || WindowState != WindowState.Normal || ActualWidth <= 0)
            return;

        _isResizingToAspect = true;
        try
        {
            var targetHeight = ActualWidth / AspectRatio + GetPlayerChromeHeight();
            if (Math.Abs(ActualHeight - targetHeight) > 0.5)
                Height = Math.Max(MinHeight, targetHeight);
        }
        finally
        {
            _isResizingToAspect = false;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && (wParam.ToInt32() & 0xFFF0) == ScMinimize)
        {
            ParkForObsCapture();
            handled = true;
            return IntPtr.Zero;
        }

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
                if (width / (double)videoHeight > AspectRatio)
                    rect.Left = rect.Right - targetWidth;
                else
                    rect.Top = rect.Bottom - targetHeight;
                break;
            case WmszTopRight:
                if (width / (double)videoHeight > AspectRatio)
                    rect.Right = rect.Left + targetWidth;
                else
                    rect.Top = rect.Bottom - targetHeight;
                break;
            case WmszBottomLeft:
                if (width / (double)videoHeight > AspectRatio)
                    rect.Left = rect.Right - targetWidth;
                else
                    rect.Bottom = rect.Top + targetHeight;
                break;
            case WmszBottomRight:
                if (width / (double)videoHeight > AspectRatio)
                    rect.Right = rect.Left + targetWidth;
                else
                    rect.Bottom = rect.Top + targetHeight;
                break;
        }
    }

    private double GetPlayerChromeHeight()
    {
        var actualHeight = TopChrome.ActualHeight + TrackTitleStrip.ActualHeight + ControlsPanel.ActualHeight +
                           ResolverPanel.ActualHeight;
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

    private static string GetPlayerPageFolder() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "WebView2");

    private static string GetWebViewUserDataFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchAudioPlayer", "WebView2");

    private static async Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync(
        CoreWebView2EnvironmentOptions options)
    {
        var primaryFolder = GetWebViewUserDataFolder();
        try
        {
            Directory.CreateDirectory(primaryFolder);
            return await CoreWebView2Environment.CreateAsync(
                userDataFolder: primaryFolder,
                options: options);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            var fallbackFolder = Path.Combine(Path.GetTempPath(), "TwitchAudioPlayer", "WebView2");
            Directory.CreateDirectory(fallbackFolder);
            return await CoreWebView2Environment.CreateAsync(
                userDataFolder: fallbackFolder,
                options: options);
        }
    }

    private static Uri GetPlayerPageUri()
    {
        var playerPage = Path.Combine(GetPlayerPageFolder(), "youtube-player.html");
        var assetVersion = File.Exists(playerPage)
            ? File.GetLastWriteTimeUtc(playerPage).Ticks
            : 0;
        return new Uri($"https://{PlayerHostName}/youtube-player.html?v={assetVersion}");
    }

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
            153 => "YouTube player error 153: embedded playback could not identify the client/referrer.",
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref uint value,
        int valueSize);
}

using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Serilog;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.Services;

public sealed class ObsBrowserSourceService : IDisposable
{
    private const string PreferredSourceHost = "tap-player.localhost";
    private const string LocalhostSourceHost = "localhost";
    private const string LoopbackSourceHost = "127.0.0.1";
    private const int PreferredPort = 38173;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger = Log.ForContext<ObsBrowserSourceService>();
    private readonly BrowserPlayerService _browserPlayer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateLock = new();
    private TcpListener? _listener;
    private ObsState _state = ObsState.Inactive;

    public ObsBrowserSourceService(BrowserPlayerService browserPlayer)
    {
        _browserPlayer = browserPlayer;
        _browserPlayer.LoadRequested += BrowserPlayerOnLoadRequested;
        _browserPlayer.StopRequested += BrowserPlayerOnStopRequested;
        _browserPlayer.PlaybackEnded += BrowserPlayerOnPlaybackEnded;
        _browserPlayer.PropertyChanged += BrowserPlayerOnPropertyChanged;

        StartServer();
    }

    public Uri? SourceUri { get; private set; }
    public Uri? LocalhostSourceUri { get; private set; }
    public Uri? LoopbackSourceUri { get; private set; }

    public void Dispose()
    {
        _browserPlayer.LoadRequested -= BrowserPlayerOnLoadRequested;
        _browserPlayer.StopRequested -= BrowserPlayerOnStopRequested;
        _browserPlayer.PlaybackEnded -= BrowserPlayerOnPlaybackEnded;
        _browserPlayer.PropertyChanged -= BrowserPlayerOnPropertyChanged;
        _shutdown.Cancel();
        _listener?.Stop();
        _shutdown.Dispose();
    }

    private void StartServer()
    {
        for (var port = PreferredPort; port < PreferredPort + 10; port++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                SourceUri = CreateSourceUri(PreferredSourceHost, port);
                LocalhostSourceUri = CreateSourceUri(LocalhostSourceHost, port);
                LoopbackSourceUri = CreateSourceUri(LoopbackSourceHost, port);
                _logger.Information(
                    "OBS YouTube browser source started at {Url}; fallback URLs: {LocalhostUrl}, {LoopbackUrl}",
                    SourceUri,
                    LocalhostSourceUri,
                    LoopbackSourceUri);
                _ = AcceptLoopAsync(_shutdown.Token);
                return;
            }
            catch (SocketException ex)
            {
                _logger.Warning(ex, "OBS browser source port {Port} is unavailable", port);
                _listener?.Stop();
                _listener = null;
            }
        }

        _logger.Error("Could not start OBS YouTube browser source on ports {FirstPort}-{LastPort}",
            PreferredPort, PreferredPort + 9);
    }

    private static Uri CreateSourceUri(string host, int port) =>
        new($"http://{host}:{port}/obs-youtube.html");

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OBS browser source accept failed");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
            if (path.StartsWith("/state", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, "application/json", GetStateJson(), cancellationToken);
                return;
            }

            if (path is "/" or "/obs-youtube.html")
            {
                await WriteResponseAsync(stream, "text/html; charset=utf-8", ObsPageHtml, cancellationToken);
                return;
            }

            await WriteResponseAsync(stream, "text/plain; charset=utf-8", "Not found", cancellationToken,
                "404 Not Found");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "OBS browser source request failed");
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string contentType,
        string body,
        CancellationToken cancellationToken,
        string status = "200 OK")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private string GetStateJson()
    {
        lock (_stateLock)
            return JsonSerializer.Serialize(_state, JsonOptions);
    }

    private void BrowserPlayerOnLoadRequested(object? sender, YouTubeBrowserPlaybackRequest request)
    {
        lock (_stateLock)
        {
            _state = new ObsState(
                true,
                request.VideoId,
                request.RequestId,
                request.StartPosition.TotalSeconds,
                request.Track.Data.Duration.TotalSeconds,
                true,
                GetUnixTimeSeconds());
        }
    }

    private void BrowserPlayerOnStopRequested(object? sender, EventArgs e) => SetInactive();

    private void BrowserPlayerOnPlaybackEnded(object? sender, EventArgs e) => SetInactive();

    private void BrowserPlayerOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_browserPlayer.IsYouTubeActive)
        {
            SetInactive();
            return;
        }

        if (e.PropertyName is not (nameof(BrowserPlayerService.Position)
            or nameof(BrowserPlayerService.Duration)
            or nameof(BrowserPlayerService.IsPlaying)
            or nameof(BrowserPlayerService.IsYouTubeActive)))
            return;

        lock (_stateLock)
        {
            _state = _state with
            {
                Active = _browserPlayer.IsYouTubeActive,
                Position = _browserPlayer.Position.TotalSeconds,
                Duration = _browserPlayer.Duration.TotalSeconds,
                IsPlaying = _browserPlayer.IsPlaying,
                UpdatedAt = GetUnixTimeSeconds()
            };
        }
    }

    private void SetInactive()
    {
        lock (_stateLock)
            _state = _state with { Active = false, IsPlaying = false, UpdatedAt = GetUnixTimeSeconds() };
    }

    private static double GetUnixTimeSeconds() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

    private sealed record ObsState(
        bool Active,
        string? VideoId,
        int RequestId,
        double Position,
        double Duration,
        bool IsPlaying,
        double UpdatedAt)
    {
        public static ObsState Inactive { get; } = new(false, null, 0, 0, 0, false, 0);
    }

    private const string ObsPageHtml = """
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>TwitchAudioPlayer OBS YouTube</title>
    <style>
        html, body, #player, #chrome-shield {
            width: 100%;
            height: 100%;
            margin: 0;
            overflow: hidden;
            background: transparent;
        }

        #player {
            position: absolute;
            inset: 0;
            background: #000;
        }

        body.idle #player {
            opacity: 0;
        }

        body.idle #chrome-shield {
            display: none;
        }

        #player iframe {
            width: 100% !important;
            height: 100% !important;
            pointer-events: none;
        }

        #chrome-shield {
            position: absolute;
            inset: 0;
            z-index: 10;
            pointer-events: auto;
        }

    </style>
</head>
<body class="idle">
<div id="player"></div>
<div id="chrome-shield"></div>
<script>
    let player;
    let isReady = false;
    let lastRequestId = 0;
    let lastActive = false;
    let pendingState = null;
    let lastSeekAt = 0;
    let lastTargetPosition = 0;
    let iframeInjectionAllowed = null;

    const hideYouTubeChromeCss = `
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
    `;

    function nowSeconds() {
        return Date.now() / 1000;
    }

    function getTargetPosition(state) {
        const base = Math.max(0, Number(state.position) || 0);
        if (!state.isPlaying || !state.updatedAt) {
            return base;
        }

        return base + Math.max(0, nowSeconds() - state.updatedAt);
    }

    function injectYouTubeChromeCss() {
        try {
            const iframe = document.querySelector("#player iframe");
            const documentToPatch = iframe?.contentDocument || iframe?.contentWindow?.document;
            if (!documentToPatch?.head) {
                return false;
            }

            let style = documentToPatch.getElementById("tap-hide-youtube-chrome");
            if (!style) {
                style = documentToPatch.createElement("style");
                style.id = "tap-hide-youtube-chrome";
                documentToPatch.head.appendChild(style);
            }

            if (style.textContent !== hideYouTubeChromeCss) {
                style.textContent = hideYouTubeChromeCss;
            }

            iframeInjectionAllowed = true;
            return true;
        } catch (error) {
            if (iframeInjectionAllowed !== false) {
                console.warn(
                    "YouTube iframe CSS injection is blocked. Start OBS with --disable-web-security to allow identical WebView2-style injection.",
                    error);
            }

            iframeInjectionAllowed = false;
            return false;
        }
    }

    function onYouTubeIframeAPIReady() {
        player = new YT.Player("player", {
            width: "100%",
            height: "100%",
            playerVars: {
                autoplay: 1,
                controls: 0,
                disablekb: 1,
                fs: 0,
                iv_load_policy: 3,
                cc_load_policy: 0,
                color: "white",
                modestbranding: 1,
                playsinline: 1,
                rel: 0,
                origin: window.location.origin,
                widget_referrer: window.location.origin
            },
            events: {
                onReady: () => {
                    isReady = true;
                    player.mute();
                    player.setVolume(0);
                    injectYouTubeChromeCss();
                    if (pendingState) {
                        applyState(pendingState, true);
                    }
                },
                onError: event => {
                    document.body.classList.add("youtube-error");
                    console.warn("YouTube OBS source error", event.data);
                }
            }
        });
    }

    async function pollState() {
        try {
            const response = await fetch("/state?t=" + Date.now(), { cache: "no-store" });
            applyState(await response.json(), false);
        } catch {
        }
    }

    function applyState(state, force) {
        pendingState = state;
        if (!isReady || !player) {
            return;
        }

        player.mute();
        player.setVolume(0);

        if (!state.active || !state.videoId) {
            if (lastActive) {
                player.stopVideo();
            }

            lastActive = false;
            document.body.classList.add("idle");
            return;
        }

        document.body.classList.remove("idle");
        document.body.classList.remove("youtube-error");
        injectYouTubeChromeCss();
        const targetPosition = getTargetPosition(state);
        lastTargetPosition = targetPosition;
        if (force || state.requestId !== lastRequestId) {
            lastRequestId = state.requestId;
            lastActive = true;
            player.loadVideoById({
                videoId: state.videoId,
                startSeconds: targetPosition
            });
            player.mute();
            player.setVolume(0);
            window.setTimeout(injectYouTubeChromeCss, 100);
            window.setTimeout(injectYouTubeChromeCss, 500);
            if (!state.isPlaying) {
                window.setTimeout(() => {
                    player.seekTo(targetPosition, true);
                    player.pauseVideo();
                }, 250);
            }
            return;
        }

        lastActive = true;
        const playerState = player.getPlayerState ? player.getPlayerState() : -1;
        const current = player.getCurrentTime ? player.getCurrentTime() : 0;
        const drift = current - targetPosition;
        const absDrift = Math.abs(drift);
        const seekThreshold = state.isPlaying ? 0.45 : 0.06;
        const seekCooldownMs = state.isPlaying ? 900 : 120;
        const canSeek = performance.now() - lastSeekAt > seekCooldownMs;

        if (state.isPlaying) {
            if (playerState !== YT.PlayerState.PLAYING && playerState !== YT.PlayerState.BUFFERING) {
                player.playVideo();
            }

            if (absDrift > seekThreshold && canSeek) {
                player.seekTo(targetPosition, true);
                lastSeekAt = performance.now();
            }
        } else if (playerState === YT.PlayerState.PLAYING || playerState === YT.PlayerState.BUFFERING) {
            if (absDrift > seekThreshold && canSeek) {
                player.seekTo(targetPosition, true);
                lastSeekAt = performance.now();
            }

            player.pauseVideo();
        } else if (absDrift > seekThreshold && canSeek) {
            player.seekTo(targetPosition, true);
            lastSeekAt = performance.now();
        }
    }

    window.setInterval(pollState, 250);
    window.setInterval(injectYouTubeChromeCss, 500);
    window.setInterval(() => {
        if (!pendingState || !pendingState.active || !pendingState.videoId) {
            return;
        }

        applyState(pendingState, false);
    }, 250);
    pollState();
</script>
<script src="https://www.youtube.com/iframe_api"></script>
</body>
</html>
""";
}

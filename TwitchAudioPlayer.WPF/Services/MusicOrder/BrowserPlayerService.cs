using CommunityToolkit.Mvvm.ComponentModel;
using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public sealed partial class BrowserPlayerService : ObservableObject
{
    private int _nextRequestId;
    [ObservableProperty] private bool _isYouTubeActive;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private double _volume = 1;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private PlaylistTrack? _currentTrack;
    [ObservableProperty] private BrowserPlaybackOwner _currentOwner;
    [ObservableProperty] private string _statusText = "YouTube browser player is starting...";
    [ObservableProperty] private uint _webViewBrowserProcessId;

    public event EventHandler<YouTubeBrowserPlaybackRequest>? LoadRequested;
    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler<bool>? MuteRequested;
    public event EventHandler? SkipRequested;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackFailed;

    public int CreateRequestId() => Interlocked.Increment(ref _nextRequestId);

    public void Load(YouTubeBrowserPlaybackRequest request)
    {
        CurrentOwner = request.Owner;
        CurrentTrack = request.Track;
        Position = request.StartPosition;
        Duration = request.Track.Data.Duration;
        IsYouTubeActive = true;
        IsPlaying = true;
        StatusText = $"Playing in browser: {request.Track.Title}";
        LoadRequested?.Invoke(this, request);
    }

    public void Play()
    {
        if (!IsYouTubeActive)
            return;

        IsPlaying = true;
        PlayRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!IsYouTubeActive)
            return;

        IsPlaying = false;
        PauseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        IsYouTubeActive = false;
        IsPlaying = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        CurrentTrack = null;
        CurrentOwner = BrowserPlaybackOwner.None;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position)
    {
        if (!IsYouTubeActive)
            return;

        Position = position;
        SeekRequested?.Invoke(this, position);
    }

    public void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        if (Math.Abs(Volume - volume) < 0.001)
            return;

        Volume = volume;
        VolumeRequested?.Invoke(this, volume);
    }

    public void SetMuted(bool isMuted)
    {
        if (IsMuted == isMuted)
            return;

        IsMuted = isMuted;
        MuteRequested?.Invoke(this, isMuted);
    }

    public void RequestSkip()
    {
        if (IsYouTubeActive)
            SkipRequested?.Invoke(this, EventArgs.Empty);
    }

    public void PlayerReady()
    {
        StatusText = IsYouTubeActive
            ? StatusText
            : "YouTube browser player is ready.";
    }

    public void ReportPlaybackState(bool isPlaying)
    {
        if (!IsYouTubeActive)
            return;

        IsPlaying = isPlaying;
    }

    public void ReportPosition(TimeSpan position, TimeSpan duration)
    {
        if (!IsYouTubeActive)
            return;

        Position = position;
        if (duration > TimeSpan.Zero)
            Duration = duration;

        StatusText = Duration > TimeSpan.Zero
            ? $"Playing in browser: {FormatTime(Position)} / {FormatTime(Duration)}"
            : $"Playing in browser: {FormatTime(Position)}";
    }

    public void ReportVolume(double volume, bool isMuted)
    {
        // YouTube can report transient iframe volume during startup/buffering.
        // The app volume slider is the source of truth, so incoming values are ignored.
    }

    public void ReportWebViewBrowserProcessId(uint processId)
    {
        if (WebViewBrowserProcessId == processId)
            return;

        WebViewBrowserProcessId = processId;
    }

    public void ReportEnded()
    {
        if (!IsYouTubeActive)
            return;

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void ReportFailure(string message)
    {
        StatusText = message;
        PlaybackFailed?.Invoke(this, message);
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
}

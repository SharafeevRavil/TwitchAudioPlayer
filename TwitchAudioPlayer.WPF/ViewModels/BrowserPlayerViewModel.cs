using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class BrowserPlayerViewModel : ObservableObject
{
    private readonly BrowserPlayerService _browserPlayer;
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly PlayerService _player;
    private bool _isUpdatingFromPlayer;
    private CancellationTokenSource? _hideArtworkDelayCts;
    private double _currentPosition;
    private double _volume = 1;

    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _pinIcon = "Pin";
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string _fullScreenIcon = "Fullscreen";
    [ObservableProperty] private bool _isYouTubeVisible;
    [ObservableProperty] private bool _isArtworkVisible;
    [ObservableProperty] private bool _hasVideoContent;
    [ObservableProperty] private string _headerText = "";
    [ObservableProperty] private string _currentCoverUrl = "pack://application:,,,/Assets/default.png";
    [ObservableProperty] private string _currentTitle = "";
    [ObservableProperty] private string _currentArtist = "";
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private string _currentPositionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _playPauseIcon = "Play";
    [ObservableProperty] private string _volumeIcon = "VolumeHigh";
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _artworkCoverUrl = "pack://application:,,,/Assets/default.png";
    [ObservableProperty] private string _artworkTitle = "";
    [ObservableProperty] private string _artworkArtist = "";

    public BrowserPlayerViewModel(BrowserPlayerService browserPlayer, IUserSettingsManager userSettingsManager)
    {
        _browserPlayer = browserPlayer;
        _userSettingsManager = userSettingsManager;
        _player = StaticService.Container.GetRequiredService<PlayerService>();

        var dispatcher = Dispatcher.CurrentDispatcher;
        _browserPlayer.PropertyChanged += (_, _) => dispatcher.Invoke(UpdateState);
        _player.TrackChangedEvent += (_, _) => dispatcher.Invoke(UpdateState);
        _player.PlayStateChangedEvent += (_, _) => dispatcher.Invoke(UpdateState);
        _player.PositionTrackChangedEvent += (_, _) => dispatcher.Invoke(UpdateState);
        _player.VolumeChanged += (_, _) => dispatcher.Invoke(UpdateState);
        _player.IsMutedChanged += (_, _) => dispatcher.Invoke(UpdateState);

        IsPinned = _userSettingsManager.Settings.BrowserPlayerTopmost;
        UpdatePinIcon();
        UpdateState();
    }

    public event EventHandler<bool>? PinChanged;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? FullScreenRequested;

    public double CurrentPosition
    {
        get => _currentPosition;
        set
        {
            if (!SetProperty(ref _currentPosition, value))
                return;

            CurrentPositionText = FormatTime(TimeSpan.FromSeconds(value));
            if (_isUpdatingFromPlayer)
                return;

            if (_browserPlayer.IsYouTubeActive)
                _browserPlayer.Seek(TimeSpan.FromSeconds(value));
            else
                _player.Seek(TimeSpan.FromSeconds(value));
        }
    }

    public double DurationSeconds => Math.Max(0, Duration.TotalSeconds);

    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref _volume, value))
                return;

            UpdateVolumeIcon();
            if (_isUpdatingFromPlayer)
                return;

            if (_browserPlayer.IsYouTubeActive)
                _browserPlayer.SetVolume(value);
            else
                _player.Volume = value;
        }
    }

    partial void OnIsPinnedChanged(bool value)
    {
        UpdatePinIcon();
        _userSettingsManager.Settings.BrowserPlayerTopmost = value;
        _ = _userSettingsManager.SaveSettingsAsync();
        PinChanged?.Invoke(this, value);
    }

    partial void OnIsFullScreenChanged(bool value)
    {
        FullScreenIcon = value ? "FullscreenExit" : "Fullscreen";
    }

    private void UpdateState()
    {
        IsYouTubeVisible = _browserPlayer.IsYouTubeActive;

        var track = _browserPlayer.IsYouTubeActive ? _browserPlayer.CurrentTrack : _player.CurrentTrack;
        if (track is not null)
        {
            CancelDelayedArtworkHide();
            var coverUrl = track.AlbumId?.BigCoverUrl
                           ?? track.AlbumId?.CoverUrl
                           ?? "pack://application:,,,/Assets/default.png";
            CurrentCoverUrl = coverUrl;
            CurrentTitle = track.Title;
            CurrentArtist = track.GetArtistsString();
            HeaderText = $"{CurrentTitle} - {CurrentArtist}";
            ArtworkCoverUrl = coverUrl;
            ArtworkTitle = track.Title;
            ArtworkArtist = track.GetArtistsString();
            IsArtworkVisible = !IsYouTubeVisible;
        }
        else
        {
            ScheduleDelayedArtworkHide();
        }

        HasVideoContent = IsYouTubeVisible || IsArtworkVisible;

        var position = _browserPlayer.IsYouTubeActive ? _browserPlayer.Position : _player.Position;
        var duration = _browserPlayer.IsYouTubeActive ? _browserPlayer.Duration : _player.Duration;
        var isPlaying = _browserPlayer.IsYouTubeActive ? _browserPlayer.IsPlaying : _player.IsPlaying;
        var volume = _browserPlayer.IsYouTubeActive ? _browserPlayer.Volume : _player.Volume;
        var isMuted = _browserPlayer.IsYouTubeActive ? _browserPlayer.IsMuted : _player.IsMuted;

        _isUpdatingFromPlayer = true;
        try
        {
            Duration = duration;
            OnPropertyChanged(nameof(DurationSeconds));
            DurationText = FormatTime(duration);
            CurrentPosition = position.TotalSeconds;
            Volume = volume;
            IsMuted = isMuted;
            PlayPauseIcon = isPlaying ? "Pause" : "Play";
            UpdateVolumeIcon();
        }
        finally
        {
            _isUpdatingFromPlayer = false;
        }
    }

    private void ScheduleDelayedArtworkHide()
    {
        if (!IsArtworkVisible)
        {
            ClearTrackDisplay();
            return;
        }

        if (_hideArtworkDelayCts is not null)
            return;

        var cts = new CancellationTokenSource();
        _hideArtworkDelayCts = cts;
        _ = HideArtworkAfterDelayAsync(cts);
    }

    private async Task HideArtworkAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
            IsArtworkVisible = false;
            HasVideoContent = IsYouTubeVisible;
            ClearTrackDisplay();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_hideArtworkDelayCts, cts))
                _hideArtworkDelayCts = null;

            cts.Dispose();
        }
    }

    private void CancelDelayedArtworkHide()
    {
        var cts = _hideArtworkDelayCts;
        _hideArtworkDelayCts = null;
        cts?.Cancel();
    }

    private void ClearTrackDisplay()
    {
        CurrentCoverUrl = "pack://application:,,,/Assets/default.png";
        CurrentTitle = "";
        CurrentArtist = "";
        HeaderText = "";
        ArtworkCoverUrl = "pack://application:,,,/Assets/default.png";
        ArtworkTitle = "";
        ArtworkArtist = "";
    }

    private void UpdatePinIcon()
    {
        PinIcon = IsPinned ? "Pin" : "PinOff";
    }

    private void UpdateVolumeIcon()
    {
        VolumeIcon = IsMuted || Volume < double.Epsilon
            ? "VolumeMute"
            : Volume switch
            {
                > 0.6 => "VolumeHigh",
                > 0.3 => "VolumeMedium",
                _ => "VolumeLow"
            };
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
    }

    [RelayCommand]
    private void Minimize()
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
        FullScreenRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_browserPlayer.IsYouTubeActive)
        {
            _browserPlayer.Seek(TimeSpan.Zero);
            return;
        }

        await _player.PreviousTrack();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_browserPlayer.IsYouTubeActive)
        {
            _browserPlayer.RequestSkip();
            return;
        }

        await _player.NextTrack();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_browserPlayer.IsYouTubeActive)
        {
            if (_browserPlayer.IsPlaying)
                _browserPlayer.Pause();
            else
                _browserPlayer.Play();

            return;
        }

        _player.PlayPause();
    }

    [RelayCommand]
    private void Mute()
    {
        if (_browserPlayer.IsYouTubeActive)
            _browserPlayer.SetMuted(!_browserPlayer.IsMuted);
        else
            _player.IsMuted = !_player.IsMuted;
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
}

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
using TwitchAudioPlayer.WPF.Helpers;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class BrowserPlayerViewModel : ObservableObject
{
    private static readonly TimeSpan VolumeSaveDelay = TimeSpan.FromSeconds(3);

    private readonly BrowserPlayerService _browserPlayer;
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly PlayerService _player;
    private bool _isUpdatingFromPlayer;
    private CancellationTokenSource? _volumeSaveCts;
    private CancellationTokenSource? _hideArtworkDelayCts;
    private double _currentPosition;
    private double _volume = 1;
    private double _volumeSliderPosition = 1;

    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _pinIcon = "Pin";
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string _fullScreenIcon = "Fullscreen";
    [ObservableProperty] private bool _isYouTubeVisible;
    [ObservableProperty] private bool _isArtworkVisible;
    [ObservableProperty] private bool _hasVideoContent;
    [ObservableProperty] private string _headerText = "";
    [ObservableProperty] private string _overlayTitle = "";
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

    private const string CommonVolumeBrush = "#FF8A49E6";
    private const string VkVolumeBrush = "#FF2787F5";
    private const string YouTubeVolumeBrush = "#FFFF0033";

    [ObservableProperty] private string _volumeAccentBrush = "#FF8A49E6";

    public VkYouTubePlaybackService VkYouTube { get; }

    public BrowserPlayerViewModel(BrowserPlayerService browserPlayer, IUserSettingsManager userSettingsManager,
        VkYouTubePlaybackService vkYouTube)
    {
        _browserPlayer = browserPlayer;
        _userSettingsManager = userSettingsManager;
        VkYouTube = vkYouTube;
        _player = StaticService.Container.GetRequiredService<PlayerService>();

        var dispatcher = Dispatcher.CurrentDispatcher;
        _browserPlayer.PropertyChanged += (_, _) => dispatcher.Invoke(UpdateState);
        _userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(UpdateState);
        _player.TrackChangedEvent += (_, _) => dispatcher.Invoke(UpdateState);
        _player.PlayStateChangedEvent += (_, _) => dispatcher.Invoke(UpdateState);
        _player.PositionTrackChangedEvent += (_, _) => dispatcher.Invoke(UpdateState);
        _player.VolumeChanged += (_, _) => dispatcher.Invoke(UpdateState);
        _player.IsMutedChanged += (_, _) => dispatcher.Invoke(UpdateState);

        IsPinned = _userSettingsManager.Settings.BrowserPlayerTopmost;
        ApplySavedVolumes();
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

            SetProperty(ref _volumeSliderPosition, VolumeCurve.VolumeToSlider(value), nameof(VolumeSliderPosition));
            UpdateVolumeIcon();
            if (_isUpdatingFromPlayer)
                return;

            SetActiveVolume(value);
            ScheduleActiveVolumeSave(value);
        }
    }

    public double VolumeSliderPosition
    {
        get => _volumeSliderPosition;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref _volumeSliderPosition, value))
                return;

            Volume = VolumeCurve.SliderToVolume(value);
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
            OverlayTitle = FormatTrackTitle(CurrentTitle, CurrentArtist);
            HeaderText = OverlayTitle;
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
        var volume = GetActiveVolume();
        var isMuted = GetActiveMuted();

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
            UpdateVolumeAccentBrush();
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
        OverlayTitle = "";
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

    private bool IsYouTubeVolumeActive() =>
        _userSettingsManager.Settings.UseSeparateSourceVolumes && _browserPlayer.IsYouTubeActive;

    private double GetActiveVolume() => IsYouTubeVolumeActive() ? _browserPlayer.Volume : _player.Volume;

    private bool GetActiveMuted() => IsYouTubeVolumeActive() ? _browserPlayer.IsMuted : _player.IsMuted;

    private void SetActiveVolume(double volume)
    {
        if (IsYouTubeVolumeActive())
        {
            _browserPlayer.SetVolume(volume);
            return;
        }

        _player.Volume = volume;
        if (!_userSettingsManager.Settings.UseSeparateSourceVolumes)
            _browserPlayer.SetVolume(volume);
    }

    private void ApplySavedVolumes()
    {
        _player.Volume = ClampVolume(_userSettingsManager.Settings.VkVolume);
        _browserPlayer.SetVolume(ClampVolume(_userSettingsManager.Settings.YouTubeVolume));
    }

    private void ScheduleActiveVolumeSave(double volume)
    {
        var settings = _userSettingsManager.Settings;
        volume = ClampVolume(volume);

        if (IsYouTubeVolumeActive())
        {
            settings.YouTubeVolume = volume;
        }
        else
        {
            settings.VkVolume = volume;
            if (!settings.UseSeparateSourceVolumes)
                settings.YouTubeVolume = volume;
        }

        _volumeSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _volumeSaveCts = cts;
        _ = SaveVolumeAfterDelayAsync(cts);
    }

    private async Task SaveVolumeAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(VolumeSaveDelay, cts.Token);
            await _userSettingsManager.SaveSettingsSilentlyAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_volumeSaveCts, cts))
                _volumeSaveCts = null;

            cts.Dispose();
        }
    }

    private void SetActiveMuted(bool isMuted)
    {
        if (IsYouTubeVolumeActive())
        {
            _browserPlayer.SetMuted(isMuted);
            return;
        }

        _player.IsMuted = isMuted;
        if (!_userSettingsManager.Settings.UseSeparateSourceVolumes)
            _browserPlayer.SetMuted(isMuted);
    }

    private void UpdateVolumeAccentBrush()
    {
        VolumeAccentBrush = !_userSettingsManager.Settings.UseSeparateSourceVolumes
            ? CommonVolumeBrush
            : _browserPlayer.IsYouTubeActive
                ? YouTubeVolumeBrush
                : VkVolumeBrush;
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
        SetActiveMuted(!GetActiveMuted());
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");

    private static string FormatTrackTitle(string title, string artist) =>
        string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";

    private static double ClampVolume(double volume) => Math.Clamp(volume, 0, 1);
}

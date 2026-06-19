using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Shared.Player;
using NHotkey.Wpf;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;
using Serilog;
using TwitchAudioPlayer.WPF.Helpers;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class AudioPlayerViewModel : ObservableObject
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly BrowserPlayerService _browserPlayer;
    private readonly PlayerService _player;
    private readonly ILogger _logger = Log.ForContext<AudioPlayerViewModel>();
    private double _currentPosition;
    [ObservableProperty] private bool _isMuted;

    private PlaylistTrack? _currentTrack;
    [ObservableProperty] private string _coverUrl;
    [ObservableProperty] private string _title;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private string _artistName;
    [ObservableProperty] private bool _isTrackSet;

    [ObservableProperty] private string _playPauseIcon = "Play";
    [ObservableProperty] private bool _shuffleEnabled;

    private double _volume;
    private double _volumeSliderPosition = 1;

    [ObservableProperty] private string _volumeIcon = "VolumeHigh";

    [ObservableProperty] private string _volumeAccentBrush = "#FF8A49E6";

    private const string CommonVolumeBrush = "#FF8A49E6";
    private const string VkVolumeBrush = "#FF2787F5";
    private const string YouTubeVolumeBrush = "#FFFF0033";

    public AudioPlayerViewModel(IUserSettingsManager userSettingsManager, BrowserPlayerService browserPlayer)
    {
        _userSettingsManager = userSettingsManager;
        _browserPlayer = browserPlayer;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(() =>
        {
            SetSettings();
            SyncCommonVolumeToBrowserIfNeeded();
            UpdateActiveVolumeState();
        });
        _browserPlayer.PropertyChanged += (_, e) => dispatcher.Invoke(() => OnBrowserPlayerChanged(e.PropertyName));
        SetSettings();

        _player = StaticService.Container.GetRequiredService<PlayerService>();
        SyncCommonVolumeToBrowserIfNeeded();

        SetTrack(_browserPlayer.IsYouTubeActive ? _browserPlayer.CurrentTrack : _player.CurrentTrack);

        OnAudioPlayerOnPlaybackPositionChanged(_browserPlayer.IsYouTubeActive ? _browserPlayer.Position : _player.Position);
        UpdateActiveVolumeState();
        OnAudioPlayerOnPlaybackStateChanged(_browserPlayer.IsYouTubeActive ? _browserPlayer.IsPlaying : _player.IsPlaying);
        // OnAudioPlayerOnShuffleChanged(_player.SetShuffle().ShuffleEnabled);


        _player.TrackChangedEvent += (_, _) =>
        {
            if (!_browserPlayer.IsYouTubeActive)
                OnAudioPlayerTrackChanged(_player.CurrentTrack);
        };
        _player.PositionTrackChangedEvent += (_, args) =>
        {
            if (!_browserPlayer.IsYouTubeActive)
                OnAudioPlayerOnPlaybackPositionChanged(args);
        };
        _player.VolumeChanged += (_, volume) =>
        {
            if (!_userSettingsManager.Settings.UseSeparateSourceVolumes)
                _browserPlayer.SetVolume(volume);

            if (!IsYouTubeVolumeActive())
                OnAudioPlayerOnVolumeChanged(volume);
        };
        _player.PlayStateChangedEvent += (_, _) =>
        {
            if (!_browserPlayer.IsYouTubeActive)
                OnAudioPlayerOnPlaybackStateChanged(_player.IsPlaying);
        };
        // _player.ShuffleChanged += (_, shuffleEnabled) => OnAudioPlayerOnShuffleChanged(shuffleEnabled);
        _player.IsMutedChanged += (_, isMuted) =>
        {
            if (!_userSettingsManager.Settings.UseSeparateSourceVolumes)
                _browserPlayer.SetMuted(isMuted);

            if (!IsYouTubeVolumeActive())
                OnAudioPlayerOnMutedChanged(isMuted);
        };
    }

    private void SetSettings()
    {
        try
        {
            HotkeyManager.Current.AddOrReplace("Previous", _userSettingsManager.Settings.PrevKey,
                _userSettingsManager.Settings.PrevModifiers, (_, _) => PreviousTrack());
            HotkeyManager.Current.AddOrReplace("Pause", _userSettingsManager.Settings.PauseKey,
                _userSettingsManager.Settings.PauseModifiers, (_, _) => PlayPause());
            HotkeyManager.Current.AddOrReplace("Next", _userSettingsManager.Settings.NextKey,
                _userSettingsManager.Settings.NextModifiers, (_, _) => NextTrack());
            HotkeyManager.Current.AddOrReplace("VolMute", _userSettingsManager.Settings.VolMuteKey,
                _userSettingsManager.Settings.VolMuteModifiers, (_, _) => Mute());
            HotkeyManager.Current.AddOrReplace("VolDown", _userSettingsManager.Settings.VolDownKey,
                _userSettingsManager.Settings.VolDownModifiers, (_, _) => ChangeVolume(-0.03d));
            HotkeyManager.Current.AddOrReplace("VolUp", _userSettingsManager.Settings.VolUpKey,
                _userSettingsManager.Settings.VolUpModifiers, (_, _) => ChangeVolume(0.03d));
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while setting hotkeys");
        }
    }

    private void ChangeVolume(double volume)
    {
        try
        {
            VolumeSliderPosition = Math.Clamp(VolumeSliderPosition + volume, 0, 1);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while changing volume");
        }
    }

    private void SetTrack(PlaylistTrack? track)
    {
        _currentTrack = track ?? null;
        Title = _currentTrack?.Title ?? "Track not selected";
        CoverUrl = _currentTrack?.AlbumId?.CoverUrl ?? "pack://application:,,,/Assets/default.png";
        Duration = _currentTrack?.Data.Duration ?? TimeSpan.Zero;
        ArtistName = track?.GetArtistsString() ?? "No artist specified";
        IsTrackSet = _currentTrack is not null;
    }

    public double CurrentPosition
    {
        get => _currentPosition;
        set
        {
            SetProperty(ref _currentPosition, value);
            if (_browserPlayer.IsYouTubeActive)
                _browserPlayer.Seek(TimeSpan.FromSeconds(CurrentPosition));
            else
                _player.Seek(TimeSpan.FromSeconds(CurrentPosition));
        }
    }
    // когда не замьючено
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            SetProperty(ref _volume, value);
            SetProperty(ref _volumeSliderPosition, VolumeCurve.VolumeToSlider(value), nameof(VolumeSliderPosition));
            SetActiveVolume(value);
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
    // когда замьючено
    public double Volume2
    {
        get => VolumeSliderPosition;
        set
        {
            VolumeSliderPosition = value;
            SetActiveMuted(false);
        }
    }

    // AudioPlayer event handlers:
    private void OnAudioPlayerOnPlaybackStateChanged(bool playing)
    {
        PlayPauseIcon = !playing ? "Play" : "Pause";
    }

    private void OnAudioPlayerOnVolumeChanged(double volume)
    {
        VolumeIcon = volume switch
        {
            < double.Epsilon => "VolumeMute",
            > 0.6 => "VolumeHigh",
            > 0.3 => "VolumeMedium",
            _ => "VolumeLow"
        };

        volume = Math.Clamp(volume, 0, 1);
        SetProperty(ref _volume, volume);
        SetProperty(ref _volumeSliderPosition, VolumeCurve.VolumeToSlider(volume), nameof(VolumeSliderPosition));
    }

    private void OnAudioPlayerOnPlaybackPositionChanged(TimeSpan args)
    {
        SetProperty(ref _currentPosition, args.TotalSeconds, nameof(CurrentPosition));
    }

    // private void OnAudioPlayerOnShuffleChanged(bool shuffleEnabled) => ShuffleEnabled = shuffleEnabled;

    private void OnAudioPlayerTrackChanged(PlaylistTrack? audio)
    {
        SetTrack(audio);
        OnAudioPlayerOnPlaybackPositionChanged(TimeSpan.Zero);
    }

    private void OnAudioPlayerOnMutedChanged(bool isMuted)
    {
        IsMuted = isMuted;
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

    private void SyncCommonVolumeToBrowserIfNeeded()
    {
        if (_userSettingsManager.Settings.UseSeparateSourceVolumes || _player is null)
            return;

        _browserPlayer.SetVolume(_player.Volume);
        _browserPlayer.SetMuted(_player.IsMuted);
    }

    private void UpdateActiveVolumeState()
    {
        if (_player is null)
            return;

        OnAudioPlayerOnVolumeChanged(GetActiveVolume());
        OnAudioPlayerOnMutedChanged(GetActiveMuted());
        UpdateVolumeAccentBrush();
    }

    private void UpdateVolumeAccentBrush()
    {
        VolumeAccentBrush = !_userSettingsManager.Settings.UseSeparateSourceVolumes
            ? CommonVolumeBrush
            : _browserPlayer.IsYouTubeActive
                ? YouTubeVolumeBrush
                : VkVolumeBrush;
    }

    private void OnBrowserPlayerChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(BrowserPlayerService.IsYouTubeActive):
                SyncCommonVolumeToBrowserIfNeeded();
                SetTrack(_browserPlayer.IsYouTubeActive ? _browserPlayer.CurrentTrack : _player.CurrentTrack);
                OnAudioPlayerOnPlaybackPositionChanged(_browserPlayer.IsYouTubeActive
                    ? _browserPlayer.Position
                    : _player.Position);
                UpdateActiveVolumeState();
                OnAudioPlayerOnPlaybackStateChanged(_browserPlayer.IsYouTubeActive
                    ? _browserPlayer.IsPlaying
                    : _player.IsPlaying);
                break;
            case nameof(BrowserPlayerService.CurrentTrack):
                if (_browserPlayer.IsYouTubeActive)
                    SetTrack(_browserPlayer.CurrentTrack);
                break;
            case nameof(BrowserPlayerService.Position):
                if (_browserPlayer.IsYouTubeActive)
                    OnAudioPlayerOnPlaybackPositionChanged(_browserPlayer.Position);
                break;
            case nameof(BrowserPlayerService.Duration):
                if (_browserPlayer.IsYouTubeActive)
                    Duration = _browserPlayer.Duration;
                break;
            case nameof(BrowserPlayerService.Volume):
                if (IsYouTubeVolumeActive())
                    OnAudioPlayerOnVolumeChanged(_browserPlayer.Volume);
                break;
            case nameof(BrowserPlayerService.IsMuted):
                if (IsYouTubeVolumeActive())
                    OnAudioPlayerOnMutedChanged(_browserPlayer.IsMuted);
                break;
            case nameof(BrowserPlayerService.IsPlaying):
                if (_browserPlayer.IsYouTubeActive)
                    OnAudioPlayerOnPlaybackStateChanged(_browserPlayer.IsPlaying);
                break;
        }
    }

    // Commands:
    [RelayCommand]
    private async Task PreviousTrack()
    {
        try
        {
            if (_browserPlayer.IsYouTubeActive)
            {
                _browserPlayer.Seek(TimeSpan.Zero);
                return;
            }

            await _player.PreviousTrack();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while switching to previous track");
        }
    }

    [RelayCommand]
    private async Task NextTrack()
    {
        try
        {
            if (_browserPlayer.IsYouTubeActive)
            {
                _browserPlayer.RequestSkip();
                return;
            }

            await _player.NextTrack();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while switching to next track");
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        try
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
        catch (Exception e)
        {
            _logger.Error(e, "Error while toggling play/pause");
        }
    }

    [RelayCommand]
    private void Shuffle()
    {
        _player.SetShuffle(ShuffleEnabled);
    }

    [RelayCommand]
    private void Mute()
    {
        try
        {
            SetActiveMuted(!GetActiveMuted());
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while toggling mute");
        }
    }
}

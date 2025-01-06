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

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class AudioPlayerViewModel : ObservableObject
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly PlayerService _player;
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

    [ObservableProperty] private string _volumeIcon = "VolumeHigh";

    public AudioPlayerViewModel(IUserSettingsManager userSettingsManager)
    {
        _userSettingsManager = userSettingsManager;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(SetSettings);
        SetSettings();

        _player = StaticService.Container.GetRequiredService<PlayerService>();

        SetTrack(_player.CurrentTrack);

        OnAudioPlayerOnPlaybackPositionChanged(_player.Position);
        OnAudioPlayerOnVolumeChanged(_player.Volume);
        OnAudioPlayerOnPlaybackStateChanged(_player.IsPlaying);
        // OnAudioPlayerOnShuffleChanged(_player.SetShuffle().ShuffleEnabled);
        OnAudioPlayerOnMutedChanged(_player.IsMuted);


        _player.TrackChangedEvent += (_, _) => OnAudioPlayerTrackChanged(_player.CurrentTrack);
        _player.PositionTrackChangedEvent += (_, args) => OnAudioPlayerOnPlaybackPositionChanged(args);
        _player.VolumeChanged += (_, volume) => OnAudioPlayerOnVolumeChanged(volume);
        _player.PlayStateChangedEvent += (_, _) => OnAudioPlayerOnPlaybackStateChanged(_player.IsPlaying);
        // _player.ShuffleChanged += (_, shuffleEnabled) => OnAudioPlayerOnShuffleChanged(shuffleEnabled);
        _player.IsMutedChanged += (_, isMuted) => OnAudioPlayerOnMutedChanged(isMuted);
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
            Console.WriteLine(e.Message);
        }
    }

    private void ChangeVolume(double volume)
    {
        try
        {
            Volume = Math.Clamp(Volume + volume, 0, 1);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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
            _player.Seek(new TimeSpan(0, 0, (int)CurrentPosition));
        }
    }
    // когда не замьючено
    public double Volume
    {
        get => _volume;
        set
        {
            SetProperty(ref _volume, value);
            _player.Volume = Volume;
            // _audioPlayer.ChangeVolume(Volume);
        }
    }
    // когда замьючено
    public double Volume2
    {
        get => 0;
        set
        {
            Volume = value;
            _player.IsMuted = false;
            // _audioPlayer.Mute(false);
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

        SetProperty(ref _volume, volume);
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

    // Commands:
    [RelayCommand]
    private async Task PreviousTrack()
    {
        try
        {
            await _player.PreviousTrack();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    [RelayCommand]
    private async Task NextTrack()
    {
        try
        {
            await _player.NextTrack();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        try
        {
            _player.PlayPause();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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
            _player.IsMuted = !_player.IsMuted;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}
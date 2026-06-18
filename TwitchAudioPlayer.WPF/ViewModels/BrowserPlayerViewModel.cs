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

    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _pinIcon = "Pin";
    [ObservableProperty] private bool _isYouTubeVisible;
    [ObservableProperty] private bool _isArtworkVisible;
    [ObservableProperty] private bool _hasVisibleContent;
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

        IsPinned = _userSettingsManager.Settings.BrowserPlayerTopmost;
        UpdatePinIcon();
        UpdateState();
    }

    public event EventHandler<bool>? PinChanged;

    partial void OnIsPinnedChanged(bool value)
    {
        UpdatePinIcon();
        _userSettingsManager.Settings.BrowserPlayerTopmost = value;
        _ = _userSettingsManager.SaveSettingsAsync();
        PinChanged?.Invoke(this, value);
    }

    private void UpdateState()
    {
        IsYouTubeVisible = _browserPlayer.IsYouTubeActive;
        IsArtworkVisible = !IsYouTubeVisible && _player is { CurrentTrack: not null, IsPlaying: true };
        HasVisibleContent = IsYouTubeVisible || IsArtworkVisible;

        if (_player.CurrentTrack is { } track)
        {
            ArtworkCoverUrl = track.AlbumId?.BigCoverUrl
                              ?? track.AlbumId?.CoverUrl
                              ?? "pack://application:,,,/Assets/default.png";
            ArtworkTitle = track.Title;
            ArtworkArtist = track.GetArtistsString();
        }
        else
        {
            ArtworkCoverUrl = "pack://application:,,,/Assets/default.png";
            ArtworkTitle = "";
            ArtworkArtist = "";
        }
    }

    private void UpdatePinIcon()
    {
        PinIcon = IsPinned ? "Pin" : "PinOff";
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
    }
}

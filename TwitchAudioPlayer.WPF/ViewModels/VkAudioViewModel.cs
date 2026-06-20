using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Core.Services;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;
using TwitchAudioPlayer.WPF.MusicX.ViewModels;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class VkAudioViewModel : ObservableObject
{
    private readonly PlayerService _player;
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly VkService _vkService;
    private readonly IWindowService _windowService;

    public VkYouTubePlaybackService VkYouTube { get; }

    private List<PlaylistTrack> _audioTracks = [];

    [ObservableProperty] private ObservableCollection<AudioTrackViewModel> _audioTrackViewModels;
    [ObservableProperty] private AudioTrackViewModel? _selectedTrack;

    private VkUserPlaylist? _currentPlaylist;

    private bool _loading;

    public VkAudioViewModel(IWindowService windowService, IUserSettingsManager userSettingsManager,
        VkYouTubePlaybackService vkYouTube)
    {
        _windowService = windowService;
        _userSettingsManager = userSettingsManager;
        VkYouTube = vkYouTube;
        
        var dispatcher = Dispatcher.CurrentDispatcher;
        _userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(async () => await OnSettingsChangedAsync());

        _vkService = StaticService.Container.GetRequiredService<VkService>();
        _player = StaticService.Container.GetRequiredService<PlayerService>();

        _player.Volume = Math.Clamp(_userSettingsManager.Settings.VkVolume, 0, 1);

        _player.TrackChangedEvent += (sender, args) =>
        {
            var track = _player.CurrentTrack;

            foreach (var trackViewModel in AudioTrackViewModels)
            {
                var isThisTrack = trackViewModel.AudioTrack == track;

                trackViewModel.IsPlaying = isThisTrack;
                if (isThisTrack) SelectedTrack = trackViewModel;
            }
        };

        AudioTrackViewModels = [];
        
        dispatcher.Invoke(async () => await ShuffleTracksAsync());
    }

    private async Task OnSettingsChangedAsync()
    {
        NotifyCanLoadChanged();
        if (_currentPlaylist == null) await ShuffleTracksAsync();
    }

    private bool CheckSettings() => _userSettingsManager.Settings.VkUserId > 0;

    private bool CanLoad()
    {
        return !_loading && CheckSettings();
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAudioTracksAsync()
    {
        if (!CheckSettings() || !CanLoad()) return;
        _loading = true;
        NotifyCanLoadChanged();

        var ownerId = _userSettingsManager.Settings.VkUserId!.Value;
        _currentPlaylist = new VkUserPlaylist(_vkService, new UserPlaylistData(ownerId));
        await InitPlaylist();

        _loading = false;
        NotifyCanLoadChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task ShuffleTracksAsync()
    {
        if (!CheckSettings() || !CanLoad()) return;
        _loading = true;
        NotifyCanLoadChanged();

        var ownerId = _userSettingsManager.Settings.VkUserId!.Value;
        var playlist = new VkUserPlaylist(_vkService, new UserPlaylistData(ownerId));
        _currentPlaylist = playlist.ShuffleWithSeed(Random.Shared.Next()) as VkUserPlaylist;
        await InitPlaylist();

        _loading = false;
        NotifyCanLoadChanged();
    }

    private void NotifyCanLoadChanged()
    {
        LoadAudioTracksCommand.NotifyCanExecuteChanged();
        ShuffleTracksCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Settings()
    {
        _windowService.OpenVkSettingsWindow();
    }

    private async Task InitPlaylist()
    {
        if (_currentPlaylist == null) return;

        _currentPlaylist.TrackAdded += (sender, tracks) =>
        {
            if (tracks.initiator == this) return;
            AddTracks(tracks.newTracks);
        };

        AudioTrackViewModels.Clear();
        _audioTracks = [];
        await LoadMore();
    }

    private async Task LoadMore()
    {
        if (_currentPlaylist is not { CanLoad: true })
            return;

        var newTracks = await _currentPlaylist.LoadAsync(this).ToArrayAsync();
        AddTracks(newTracks);
    }

    private void AddTracks(IEnumerable<PlaylistTrack> tracks)
    {
        var index = _audioTracks.Count;
        foreach (var track in tracks)
        {
            _audioTracks.Add(track);
            AddTrack(track, index);
            index++;
        }
    }

    private void AddTrack(PlaylistTrack track, int index)
    {
        var trackViewModel = new AudioTrackViewModel(track);
        trackViewModel.PlayPauseRequested += async (sender, args) =>
            await PlayPauseTrackAsync(args.ViewModel, args.IsPlaying);
        AudioTrackViewModels.Add(trackViewModel);
        trackViewModel.Index = index;
    }

    public async Task PlayPauseTrackAsync(AudioTrackViewModel viewModel, bool isPlaying)
    {
        if (_currentPlaylist is null) return;

        if (isPlaying)
        {
            _player.Pause();

            viewModel.IsPlaying = false;
            return;
        }

        viewModel.IsPlaying = true;
        var index = viewModel.Index;
        await _player.PlayAsync(_currentPlaylist, index);
    }


    [RelayCommand]
    private async Task OnScrollNearEnd()
    {
        if (_loading) return;

        _loading = true;
        await LoadMore();
        _loading = false;
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;
using TwitchAudioPlayer.WPF.Services.Proxy;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class YtAudioViewModel : ObservableObject
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly MusicOrderService _musicOrderService;
    private readonly IWindowService _windowService;
    private readonly IProxyService _proxyService;
    private readonly PlayerService _player;

    public YtAudioViewModel(IWindowService windowService, IUserSettingsManager userSettingsManager,
        MusicOrderService musicOrderService, IProxyService proxyService)
    {
        _windowService = windowService;
        _userSettingsManager = userSettingsManager;
        _musicOrderService = musicOrderService;
        _proxyService = proxyService;

        var dispatcher = Dispatcher.CurrentDispatcher;

        _musicOrderService.TwitchEnabledChanged += (_, b) => TwitchEnabled = b;
        _musicOrderService.DaEnabledChanged += (_, b) => DaEnabled = b;
        _musicOrderService.DaPing += (_, _) => dispatcher.Invoke(async () =>
        {
            DaColor = "Yellow";
            await Task.Delay(500);
            DaColor = "Green";
        });
        _musicOrderService.TwitchPing += (_, _) => dispatcher.Invoke(async () =>
        {
            TwitchColor = "Yellow";
            await Task.Delay(500);
            TwitchColor = "Green";
        });
        _proxyService.StatusChanged += (_, status) => dispatcher.Invoke(async () =>
        {
            UpdateProxyStatus(status);
            if (status.Status is ProxyRuntimeStatus.Running or ProxyRuntimeStatus.Disabled)
                await RetryFailedTracksAsync();
        });
        UpdateProxyStatus(_proxyService.CurrentStatus);
        _musicOrderService.OrdersAdded += (_, e) => dispatcher.Invoke(async () => await OnOrdersAdded(e));

        _player = StaticService.Container.GetRequiredService<PlayerService>();
        _player.TrackChangedEvent += (_, _) =>
            dispatcher.Invoke(async () => await OnPlayerTrackChangedAsync(_player.CurrentTrack));
        _player.PositionTrackChangedEvent += (_, e) =>
            dispatcher.Invoke(async () => await PlayerOnPositionTrackChangedEvent(e));

        _userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(() => SetSettings(dispatcher));
        SetSettings();

        PlayedTracksViewModels = [];
        QueuedTracksViewModels = [];

        SetGridLengths();

        dispatcher.Invoke(async () => await ReloadOrdersAsync(startTrackLoading: true));
    }

    // Прерывает проигрывание трека в случае, если он уже длится дольше, чем указано в настройках
    private async Task PlayerOnPositionTrackChangedEvent(TimeSpan e)
    {
        if (!(e.TotalSeconds >= _maxYtMinutes * 60)) return;
        if (!ReferenceEquals(_player.CurrentPlaylist, _playlist)) return;
        await _player.NextTrack();
    }

    private async Task OnOrdersAdded(List<MusicOrderWithTrack> e)
    {
        AddTracks(e);
        if (!AutoPlayEnabled) return;
        // если включен автоплей - если играет другая очередь - ищем в очереди доступные треки и играем первый 
        if (!ReferenceEquals(_player.CurrentPlaylist, _playlist))
        {
            var viewModel = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel != null);
            if (viewModel?.AudioTrackViewModel != null) await PlayTrackAsync(viewModel.AudioTrackViewModel);
        }
    }


    [ObservableProperty] private bool _loading = true;
    
    [ObservableProperty] private bool _twitchEnabled;
    [ObservableProperty] private bool _daEnabled;
    [ObservableProperty] private bool _proxyEnabled;
    [ObservableProperty] private string _twitchColor = "Green";
    [ObservableProperty] private string _daColor = "Green";
    [ObservableProperty] private string _proxyColor = "Crimson";
    [ObservableProperty] private string _proxyStatusText = "Proxy disabled";

    [ObservableProperty] private bool _autoPlayEnabled = true;
    [ObservableProperty] private bool _trackLoadingEnabled = true;
    [ObservableProperty] private GridLength _trackLoadingGridLength;
    [ObservableProperty] private GridLength _trackLoadingGridLength2;

    [ObservableProperty] private ObservableCollection<YtAudioTrackViewModel> _playedTracksViewModels;
    [ObservableProperty] private ObservableCollection<YtAudioTrackViewModel> _queuedTracksViewModels;
    [ObservableProperty] private YtAudioTrackViewModel? _playedSelectedTrack;
    [ObservableProperty] private YtAudioTrackViewModel? _queuedSelectedTrack;

    private double _maxYtMinutes;

    private readonly List<MusicOrderWithTrack> _tracks = [];
    private YouTubeQueuePlaylist _playlist = new([]);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private PlaylistTrack? _currentTrack;
    private PlayerState? _interceptedState;

    private async Task OnPlayerTrackChangedAsync(PlaylistTrack? track)
    {
        if (_currentTrack != null) MarkTrackAsPlayed(_currentTrack);
        _currentTrack = track;

        // play 1st from queued
        var playedVm = PlayedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel?.AudioTrack == track);
        if (playedVm != null)
        {
            var index = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel != null)?.AudioTrackViewModel?.Index;
            if (index.HasValue)
            {
                await _player.PlayTrackFromQueueAsync(index.Value, TimeSpan.Zero);
                return;
            }
        }

        foreach (var trackViewModel in PlayedTracksViewModels.Concat(QueuedTracksViewModels))
        {
            if (trackViewModel.AudioTrackViewModel == null)
                continue;

            var isThisTrack = trackViewModel.AudioTrackViewModel.AudioTrack == track;

            trackViewModel.AudioTrackViewModel.IsPlaying = isThisTrack;
            if (isThisTrack) QueuedSelectedTrack = trackViewModel;
        }

        //if queued count = 0 then continue play interceptedPlaylist
        if (ReferenceEquals(_player.CurrentPlaylist, _playlist) && QueuedTracksViewModels.All(x => x.AudioTrackViewModel == null))
        {
            if (_interceptedState == null)
            {
                // тут по идее надо нахер офать
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    _player.Pause();
                });
                return;
            }

            await _player.RestoreFromStateAsync(_interceptedState);
        }
    }

    private void MarkTrackAsPlayed(PlaylistTrack track)
    {
        var order = _tracks.FirstOrDefault(x => x.PlaylistTrack == track);
        if (order == null) return;
        // mark as played
        _musicOrderService.MarkPlayed(order.MusicOrder);
        // move track vm
        var vm = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrack == order);
        if (vm == null) return;
        PlayedTracksViewModels.Add(vm);
        QueuedTracksViewModels.Remove(vm);
    }

    private bool CanTouchButtons() => !Loading && CheckSettings();
    private bool CheckSettings() => _userSettingsManager.Settings is
        { DaWidgetToken: not null, TwitchRewardCost: not null, TwitchRewardPrompt: not null, TwitchRewardTitle: not null};
    
    private async Task ReloadOrdersAsync(bool startTrackLoading)
    {
        if (!await _reloadGate.WaitAsync(0))
            return;

        Loading = true;
        NotifyCanTouchButtons();

        try
        {
            if (!CheckSettings()) return;

            await _proxyService.EnsureProxyAsync();

            _musicOrderService.EnsureOldTracksDisabled();
            var orders = await _musicOrderService.GetTracks();

            var playedComparer = new PlayedComparer();
            var orderedTracks = orders
                .OrderBy(x => x.MusicOrder.Played, playedComparer)
                .ThenBy(x => x.MusicOrder.Date)
                .ToList();

            _playlist = new YouTubeQueuePlaylist([]);
            _tracks.Clear();
            PlayedTracksViewModels.Clear();
            QueuedTracksViewModels.Clear();
            AddTracks(orderedTracks);

            if (startTrackLoading)
                await SwitchTrackLoadingAsync();
        }
        finally
        {
            Loading = false;
            NotifyCanTouchButtons();
            _reloadGate.Release();
        }
    }


    private void AddTracks(IEnumerable<MusicOrderWithTrack> tracks)
    {
        foreach (var track in tracks) AddTrack(track);
    }

    private void AddTrack(MusicOrderWithTrack track)
    {
        var trackViewModel = new YtAudioTrackViewModel(track);
        trackViewModel.RetryRequested += async (_, vm) => await RetryTrackAsync(vm);
        if (trackViewModel.AudioTrackViewModel != null)
            AttachPlayableTrack(track, trackViewModel);

        if (track.MusicOrder.Played == Played.Played)
            PlayedTracksViewModels.Add(trackViewModel);
        else
            QueuedTracksViewModels.Add(trackViewModel);
    }

    private void AttachPlayableTrack(MusicOrderWithTrack track, YtAudioTrackViewModel trackViewModel)
    {
        if (track.PlaylistTrack == null || trackViewModel.AudioTrackViewModel == null)
            return;

        _tracks.Add(track);
        trackViewModel.AudioTrackViewModel.PlayPauseRequested += async (_, args) =>
            await PlayPauseTrackAsync(args.ViewModel, !args.IsPlaying);
        var index = _playlist.AddTrack(track.PlaylistTrack);
        trackViewModel.AudioTrackViewModel.Index = index;
    }

    private async Task RetryTrackAsync(YtAudioTrackViewModel trackViewModel)
    {
        if (!trackViewModel.CanRetry)
            return;

        trackViewModel.IsRetrying = true;

        try
        {
            var reloaded = await _musicOrderService.LoadTrack(trackViewModel.AudioTrack.MusicOrder);
            trackViewModel.ReplaceAudioTrack(reloaded);

            if (trackViewModel.AudioTrackViewModel != null)
                AttachPlayableTrack(trackViewModel.AudioTrack, trackViewModel);
        }
        finally
        {
            trackViewModel.IsRetrying = false;
        }
    }

    private async Task RetryFailedTracksAsync()
    {
        var failedTracks = QueuedTracksViewModels
            .Concat(PlayedTracksViewModels)
            .Where(x => x is { IsFailed: true, CanRetry: true, IsRetrying: false })
            .ToList();

        foreach (var track in failedTracks)
            await RetryTrackAsync(track);
    }

    private async Task PlayPauseTrackAsync(AudioTrackViewModel viewModel, bool toPlay)
    {
        if (!toPlay)
        {
            viewModel.IsPlaying = false;
            _player.Pause();
            return;
        }

        // если был прожат трек в Played, то трек перемещается в несыгранные и больше ничего
        var playedVm = PlayedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel == viewModel);
        if (playedVm != null)
        {
            QueuedTracksViewModels.Add(playedVm);
            PlayedTracksViewModels.Remove(playedVm);
            viewModel.IsPlaying = false;
            return;
        }

        await PlayTrackAsync(viewModel);
    }

    private async Task PlayTrackAsync(AudioTrackViewModel viewModel)
    {
        viewModel.IsPlaying = true;
        if (!ReferenceEquals(_player.CurrentPlaylist, _playlist))
            _interceptedState = PlayerState.CreateOrNull(_player);

        await _player.PlayAsync(_playlist, viewModel.Index);
    }

    private void SetSettings(Dispatcher? dispatcher = null)
    {
        _maxYtMinutes = _userSettingsManager.Settings.MaxMinutesLength;
        NotifyCanTouchButtons();
        if (dispatcher != null)
            dispatcher.Invoke(async () => await RetryFailedTracksAsync());
    }

    [RelayCommand]
    private void Settings()
    {
        _windowService.OpenYtSettingsWindow();
    }
    
    private void NotifyCanTouchButtons()
    {
        SwitchAutoPlayCommand.NotifyCanExecuteChanged();
        SwitchTrackLoadingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTouchButtons))]
    private Task SwitchAutoPlayAsync()
    {
        // do nothing for now (AutoPlayEnabled is changed via binding)
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanTouchButtons))]
    private async Task SwitchTrackLoadingAsync()
    {
        SetGridLengths();
        if (TrackLoadingEnabled)
        {
            await _musicOrderService.Start();
        }
        else
        {
            await _musicOrderService.Stop();
        }
    }

    private void SetGridLengths()
    {
        if (TrackLoadingEnabled)
        {
            TrackLoadingGridLength = new GridLength(1, GridUnitType.Star);
            TrackLoadingGridLength2 = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            TrackLoadingGridLength = new GridLength(3, GridUnitType.Star);
            TrackLoadingGridLength2 = new GridLength(0);
        }
    }

    private void UpdateProxyStatus(ProxyStatusSnapshot status)
    {
        ProxyEnabled = status.Status is ProxyRuntimeStatus.Running or ProxyRuntimeStatus.Checking or ProxyRuntimeStatus.Starting;
        ProxyColor = status.Status switch
        {
            ProxyRuntimeStatus.Running => "Green",
            ProxyRuntimeStatus.Checking or ProxyRuntimeStatus.Starting => "Yellow",
            ProxyRuntimeStatus.Error => "Crimson",
            _ => "Crimson"
        };

        ProxyStatusText = status.Status switch
        {
            ProxyRuntimeStatus.Disabled => "Proxy disabled",
            ProxyRuntimeStatus.Running when status.CurrentNode is not null =>
                $"Proxy working: {status.CurrentNode.Name}",
            ProxyRuntimeStatus.Checking or ProxyRuntimeStatus.Starting when status.CurrentNode is not null =>
                $"Proxy connecting: {status.CurrentNode.Name}",
            ProxyRuntimeStatus.Error => $"Proxy failed: {status.Message}",
            _ => status.Message
        };
    }
}

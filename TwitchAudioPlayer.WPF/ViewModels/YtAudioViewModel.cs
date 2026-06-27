using System.Collections.ObjectModel;
using System.Windows;
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

public partial class YtAudioViewModel : ObservableObject
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly MusicOrderService _musicOrderService;
    private readonly IWindowService _windowService;
    private readonly BrowserPlayerService _browserPlayer;
    private readonly PlayerService _player;

    private double _maxYtMinutes;
    private readonly List<MusicOrderWithTrack> _tracks = [];
    private YouTubeQueuePlaylist _playlist = new([]);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private PlaylistTrack? _currentTrack;
    private PlaylistTrack? _browserCurrentTrack;
    private YtAudioTrackViewModel? _browserCurrentViewModel;
    private PlayerState? _interceptedState;
    private bool _isBrowserPlaying;

    public YtAudioViewModel(IWindowService windowService, IUserSettingsManager userSettingsManager,
        MusicOrderService musicOrderService, BrowserPlayerService browserPlayer)
    {
        _windowService = windowService;
        _userSettingsManager = userSettingsManager;
        _musicOrderService = musicOrderService;
        _browserPlayer = browserPlayer;

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
        _musicOrderService.OrdersAdded += (_, e) => dispatcher.Invoke(async () => await OnOrdersAdded(e));
        _browserPlayer.PlaybackEnded += (_, _) => dispatcher.Invoke(async () =>
        {
            if (_browserPlayer.CurrentOwner == BrowserPlaybackOwner.MusicOrder)
                await BrowserPlaybackEndedAsync();
        });
        _browserPlayer.PlaybackFailed += (_, message) =>
            dispatcher.Invoke(async () =>
            {
                if (_browserPlayer.CurrentOwner == BrowserPlaybackOwner.MusicOrder)
                    await BrowserPlaybackFailedAsync(message);
            });
        _browserPlayer.SkipRequested += (_, _) => dispatcher.Invoke(async () =>
        {
            if (_browserPlayer.CurrentOwner == BrowserPlaybackOwner.MusicOrder)
                await CompleteBrowserTrackAndAdvanceAsync();
        });
        _browserPlayer.PropertyChanged += (_, e) =>
        {
            if (_browserPlayer.CurrentOwner != BrowserPlaybackOwner.MusicOrder)
                return;

            if (e.PropertyName == nameof(BrowserPlayerService.IsPlaying))
                dispatcher.Invoke(SyncBrowserPlayState);
            else if (e.PropertyName == nameof(BrowserPlayerService.Position))
                dispatcher.Invoke(async () => await BrowserPositionChangedAsync(_browserPlayer.Position, _browserPlayer.Duration));
            else if (e.PropertyName == nameof(BrowserPlayerService.StatusText))
                dispatcher.Invoke(() => BrowserPlayerStatusText = _browserPlayer.StatusText);
        };

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

    [ObservableProperty] private bool _loading = true;

    [ObservableProperty] private bool _twitchEnabled;
    [ObservableProperty] private bool _daEnabled;
    [ObservableProperty] private string _twitchColor = "Green";
    [ObservableProperty] private string _daColor = "Green";

    [ObservableProperty] private bool _isBrowserPlaybackMode;
    [ObservableProperty] private string _browserPlayerStatusText = "YouTube browser player is starting...";

    [ObservableProperty] private bool _autoPlayEnabled = true;
    [ObservableProperty] private bool _trackLoadingEnabled = true;
    [ObservableProperty] private GridLength _trackLoadingGridLength;
    [ObservableProperty] private GridLength _trackLoadingGridLength2;

    [ObservableProperty] private ObservableCollection<YtAudioTrackViewModel> _playedTracksViewModels;
    [ObservableProperty] private ObservableCollection<YtAudioTrackViewModel> _queuedTracksViewModels;
    [ObservableProperty] private YtAudioTrackViewModel? _playedSelectedTrack;
    [ObservableProperty] private YtAudioTrackViewModel? _queuedSelectedTrack;

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

        if (IsBrowserPlaybackMode)
        {
            if (!_isBrowserPlaying)
            {
                var viewModel = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel != null);
                if (viewModel?.AudioTrackViewModel != null) await PlayTrackAsync(viewModel.AudioTrackViewModel);
            }

            return;
        }

        if (!ReferenceEquals(_player.CurrentPlaylist, _playlist))
        {
            var viewModel = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel != null);
            if (viewModel?.AudioTrackViewModel != null) await PlayTrackAsync(viewModel.AudioTrackViewModel);
        }
    }

    private async Task OnPlayerTrackChangedAsync(PlaylistTrack? track)
    {
        StopBrowserPlaybackForNativeTrack(track);

        if (_currentTrack != null) MarkTrackAsPlayed(_currentTrack);
        _currentTrack = track;

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

        if (ReferenceEquals(_player.CurrentPlaylist, _playlist) && QueuedTracksViewModels.All(x => x.AudioTrackViewModel == null))
        {
            if (_interceptedState == null)
            {
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

    private void StopBrowserPlaybackForNativeTrack(PlaylistTrack? track)
    {
        if (!_browserPlayer.IsYouTubeActive ||
            _browserPlayer.CurrentOwner != BrowserPlaybackOwner.MusicOrder ||
            track is null || _tracks.Any(x => x.PlaylistTrack == track))
            return;

        _browserPlayer.Stop();
        _player.IsPlaybackSuppressed = false;
        ClearBrowserCurrentTrack();
        _interceptedState = null;
    }

    private void MarkTrackAsPlayed(PlaylistTrack track)
    {
        var order = _tracks.FirstOrDefault(x => x.PlaylistTrack == track);
        if (order == null) return;

        _musicOrderService.MarkPlayed(order.MusicOrder);

        var vm = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrack == order);
        if (vm == null) return;

        PlayedTracksViewModels.Add(vm);
        QueuedTracksViewModels.Remove(vm);
        PlayedSelectedTrack = vm;
        if (QueuedSelectedTrack == vm)
            QueuedSelectedTrack = null;
    }

    private bool CanTouchButtons() => !Loading && CheckSettings();

    private bool CheckSettings() => _userSettingsManager.Settings is
        { DaWidgetToken: not null, TwitchRewardCost: not null, TwitchRewardPrompt: not null, TwitchRewardTitle: not null };

    private async Task ReloadOrdersAsync(bool startTrackLoading)
    {
        if (!await _reloadGate.WaitAsync(0))
            return;

        Loading = true;
        NotifyCanTouchButtons();

        try
        {
            if (!CheckSettings()) return;

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
            if (IsBrowserPlaybackMode && _browserCurrentViewModel?.AudioTrackViewModel == viewModel)
            {
                _isBrowserPlaying = false;
                _browserPlayer.Pause();
            }
            else
            {
                _player.Pause();
            }

            return;
        }

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
        if (IsBrowserPlaybackMode)
        {
            await PlayBrowserTrackAsync(viewModel);
            return;
        }

        viewModel.IsPlaying = true;
        if (!ReferenceEquals(_player.CurrentPlaylist, _playlist))
            _interceptedState = PlayerState.CreateOrNull(_player);

        await _player.PlayAsync(_playlist, viewModel.Index);
    }

    private async Task PlayBrowserTrackAsync(AudioTrackViewModel viewModel)
    {
        var order = _tracks.FirstOrDefault(x => x.PlaylistTrack == viewModel.AudioTrack);
        if (order?.PlaylistTrack == null)
            return;

        var requestedViewModel = QueuedTracksViewModels.Concat(PlayedTracksViewModels)
            .FirstOrDefault(x => x.AudioTrack == order);
        requestedViewModel?.ClearPlaybackError();

        if (_browserCurrentTrack == order.PlaylistTrack)
        {
            _player.IsPlaybackSuppressed = true;
            _isBrowserPlaying = true;
            viewModel.IsPlaying = true;
            BrowserPlayerStatusText = $"Playing in browser: {viewModel.Title}";
            _browserPlayer.Play();
            return;
        }

        var videoId = TryExtractYouTubeId(order.MusicOrder.Uri);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            BrowserPlayerStatusText = "Could not extract YouTube video id from order URL.";
            return;
        }

        if (_browserCurrentTrack != null)
            MarkTrackAsPlayed(_browserCurrentTrack);

        if (!ReferenceEquals(_player.CurrentPlaylist, _playlist) && _interceptedState == null)
            _interceptedState = PlayerState.CreateOrNull(_player);

        _player.IsPlaybackSuppressed = true;
        _player.Pause();

        foreach (var trackViewModel in PlayedTracksViewModels.Concat(QueuedTracksViewModels))
            if (trackViewModel.AudioTrackViewModel != null)
                trackViewModel.AudioTrackViewModel.IsPlaying = false;

        _browserCurrentTrack = order.PlaylistTrack;
        _browserCurrentViewModel = requestedViewModel;
        _isBrowserPlaying = true;
        viewModel.IsPlaying = true;
        QueuedSelectedTrack = _browserCurrentViewModel;
        BrowserPlayerStatusText = $"Playing in browser: {viewModel.Title}";

        if (!_userSettingsManager.Settings.UseSeparateSourceVolumes)
        {
            _browserPlayer.SetVolume(_player.Volume);
            _browserPlayer.SetMuted(_player.IsMuted);
        }

        _browserPlayer.Load(new YouTubeBrowserPlaybackRequest(
            videoId,
            TimeSpan.Zero,
            order.PlaylistTrack,
            _browserPlayer.CreateRequestId()));
        await Task.CompletedTask;
    }

    public void BrowserPlayerReady()
    {
        BrowserPlayerStatusText = _browserPlayer.StatusText;
    }

    public async Task BrowserPlaybackEndedAsync()
    {
        await CompleteBrowserTrackAndAdvanceAsync();
    }

    public async Task BrowserPlaybackFailedAsync(string message)
    {
        if (_browserPlayer.CurrentOwner != BrowserPlaybackOwner.MusicOrder)
            return;

        BrowserPlayerStatusText = message;
        if (_browserCurrentViewModel != null)
            _browserCurrentViewModel.SetPlaybackError(message);

        if (IsEmbedTransportFailure(message))
        {
            _browserPlayer.Stop();
            _player.IsPlaybackSuppressed = false;
            ClearBrowserCurrentTrack();
            BrowserPlayerStatusText = $"YouTube queue paused after a global embed failure: {message}";
            if (_interceptedState != null)
            {
                var state = _interceptedState;
                _interceptedState = null;
                await _player.RestoreFromStateAsync(state);
            }
            return;
        }

        if (_browserCurrentTrack != null)
            MarkTrackAsPlayed(_browserCurrentTrack);

        _browserPlayer.Stop();
        _player.IsPlaybackSuppressed = false;
        ClearBrowserCurrentTrack();

        var nextViewModel = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel != null);
        if (nextViewModel?.AudioTrackViewModel != null)
        {
            await PlayBrowserTrackAsync(nextViewModel.AudioTrackViewModel);
            return;
        }

        if (_interceptedState != null)
        {
            var state = _interceptedState;
            _interceptedState = null;
            await _player.RestoreFromStateAsync(state);
        }
    }

    private static bool IsEmbedTransportFailure(string message) =>
        message.Contains("error 101", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("error 150", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("error 153", StringComparison.OrdinalIgnoreCase);

    public async Task BrowserPositionChangedAsync(TimeSpan position, TimeSpan duration)
    {
        if (!IsBrowserPlaybackMode || !_isBrowserPlaying ||
            _browserPlayer.CurrentOwner != BrowserPlaybackOwner.MusicOrder)
            return;

        if (duration > TimeSpan.Zero)
            BrowserPlayerStatusText = $"Playing in browser: {FormatTime(position)} / {FormatTime(duration)}";

        if (position.TotalSeconds >= _maxYtMinutes * 60)
            await CompleteBrowserTrackAndAdvanceAsync();
    }

    private async Task CompleteBrowserTrackAndAdvanceAsync()
    {
        if (!IsBrowserPlaybackMode ||
            _browserPlayer.CurrentOwner != BrowserPlaybackOwner.MusicOrder)
            return;

        if (_browserCurrentTrack != null)
            MarkTrackAsPlayed(_browserCurrentTrack);
        ClearBrowserCurrentTrack();

        var nextViewModel = QueuedTracksViewModels.FirstOrDefault(x => x.AudioTrackViewModel != null);
        if (nextViewModel?.AudioTrackViewModel != null)
        {
            await PlayBrowserTrackAsync(nextViewModel.AudioTrackViewModel);
            return;
        }

        _browserPlayer.Stop();
        _player.IsPlaybackSuppressed = false;
        BrowserPlayerStatusText = "YouTube queue finished.";

        if (_interceptedState != null)
        {
            var state = _interceptedState;
            _interceptedState = null;
            await _player.RestoreFromStateAsync(state);
        }
    }

    private void ClearBrowserCurrentTrack()
    {
        if (_browserCurrentViewModel?.AudioTrackViewModel != null)
            _browserCurrentViewModel.AudioTrackViewModel.IsPlaying = false;

        _browserCurrentTrack = null;
        _browserCurrentViewModel = null;
        _isBrowserPlaying = false;
    }

    private void SyncBrowserPlayState()
    {
        if (!IsBrowserPlaybackMode || _browserPlayer.CurrentOwner != BrowserPlaybackOwner.MusicOrder ||
            _browserCurrentViewModel?.AudioTrackViewModel == null)
            return;

        _isBrowserPlaying = _browserPlayer.IsPlaying;
        _browserCurrentViewModel.AudioTrackViewModel.IsPlaying = _browserPlayer.IsPlaying;
    }

    private void SetSettings(Dispatcher? dispatcher = null)
    {
        _maxYtMinutes = _userSettingsManager.Settings.MaxMinutesLength;
        IsBrowserPlaybackMode = true;

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

    private static string? TryExtractYouTubeId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return null;

        if (parsed.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return parsed.AbsolutePath.Trim('/').Split('/').FirstOrDefault();

        if (parsed.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = parsed.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "v");
            if (query != null)
                return Uri.UnescapeDataString(query[1]);

            var segments = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed")
                return segments[1];
        }

        return null;
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
}

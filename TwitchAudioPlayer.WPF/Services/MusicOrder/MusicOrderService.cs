using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Windows.Threading;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.Services.DonationAlerts;
using TwitchAudioPlayer.WPF.Services.Twitch;
using Serilog;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public class MusicOrderService
{
    private static readonly TimeSpan RecentOrderHistory = TimeSpan.FromDays(7);

    private readonly DonationAlertsOrdersNotifier _daOrdersNotifier;
    private readonly TwitchOrdersNotifier _twitchOrdersNotifier;

    private readonly MusicOrderRepository _musicOrderRepository;
    private readonly YouTubeService _youTubeService;

    public MusicOrderService(DonationAlertsOrdersNotifier daOrdersNotifier, TwitchOrdersNotifier twitchOrdersNotifier,
        MusicOrderRepository musicOrderRepository, YouTubeService youTubeService)
    {
        _musicOrderRepository = musicOrderRepository;
        _youTubeService = youTubeService;

        var dispatcher = Dispatcher.CurrentDispatcher;

        _daOrdersNotifier = daOrdersNotifier;
        _daOrdersNotifier.MusicOrdersAdd +=
            (sender, list) => dispatcher.Invoke(async () => await OnMusicOrdersAdd(sender, list));
        var lastDaOrder = _musicOrderRepository.GetLastOrder(OrderType.DonationAlerts);
        _daOrdersNotifier.InitTimer(2, lastDaOrder?.Date ?? new DateTimeOffset());

        _twitchOrdersNotifier = twitchOrdersNotifier;
        _twitchOrdersNotifier.MusicOrdersAdd +=
            (sender, list) => dispatcher.Invoke(async () => await OnMusicOrdersAdd(sender, list));
        var lastTwOrder = _musicOrderRepository.GetLastOrder(OrderType.Twitch);
        _twitchOrdersNotifier.InitTimer(2, lastTwOrder?.Date ?? new DateTimeOffset());

        // Task.Run(async () => { await Start(); });
    }

    public event EventHandler<List<MusicOrderWithTrack>>? OrdersAdded;

    public event EventHandler<bool>? TwitchEnabledChanged
    {
        add => _twitchOrdersNotifier.EnabledChanged += value;
        remove => _twitchOrdersNotifier.EnabledChanged -= value;
    }

    public event EventHandler<bool>? DaEnabledChanged
    {
        add => _daOrdersNotifier.EnabledChanged += value;
        remove => _daOrdersNotifier.EnabledChanged -= value;
    }

    public event EventHandler? TwitchPing
    {
        add => _twitchOrdersNotifier.Ping += value;
        remove => _twitchOrdersNotifier.Ping -= value;
    }

    public event EventHandler? DaPing
    {
        add => _daOrdersNotifier.Ping += value;
        remove => _daOrdersNotifier.Ping -= value;
    }

    public async Task Start()
    {
        var taskDa = _daOrdersNotifier.Start();
        var taskTw = _twitchOrdersNotifier.Start();
        await Task.WhenAll(taskDa , taskTw);
    }

    public async Task Stop()
    {
        var taskDa = _daOrdersNotifier.Stop();
        var taskTw = _twitchOrdersNotifier.Stop();
        await Task.WhenAll(taskDa, taskTw);
    }

    public void MarkPlayed(MusicOrder order, Played played = Played.Played)
    {
        if (!_musicOrderRepository.MarkPlayed(order, played))
            Log.Warning("Music order was marked as {Played}, but no database row matched. Uri: {Uri}, Date: {Date:o}, Type: {Type}",
                played, order.Uri, order.Date, order.Type);

        order.Played = played;
    }

    public void EnsureOldTracksDisabled()
    {
        var historyStart = DateTimeOffset.Now.Subtract(RecentOrderHistory);
        var restoredCount = _musicOrderRepository.RestoreRecentInvalidOrders(historyStart);
        if (restoredCount > 0)
            Log.Information("Restored {Count} recent invalid music orders for retry.", restoredCount);

        _musicOrderRepository.MarkOrdersInactiveBefore(historyStart, Played.Played);
    }
    
    public async Task<List<MusicOrderWithTrack>> GetTracks()
    {
        var orders = _musicOrderRepository.GetValidOrders();
        return (await GetMusicOrderWithTracks(orders)).Results;
    }

    public async Task<MusicOrderWithTrack> LoadTrack(MusicOrder order)
    {
        var (track, error) = await _youTubeService.GetPlaylistTrack(order.Uri);
        return CreateTrackResult(order, track, error);
    }

    private async Task<(List<MusicOrderWithTrack> Results, List<MusicOrder> Invalid)> GetMusicOrderWithTracks(List<MusicOrder> orders)
    {
        var tracks = new List<(PlaylistTrack? Track, YtTrackError? Error)>(orders.Count);
        foreach (var order in orders)
            tracks.Add(await _youTubeService.GetPlaylistTrack(order.Uri));

        var results = new List<MusicOrderWithTrack>();
        var invalidOrders = new List<MusicOrder>();
        for (var i = 0; i < orders.Count; i++)
        {
            var (track, error) = tracks[i];
            switch (error)
            {
                // удаляю треки, для которых нет ютуб видео или это бесконечный стрим (его не получить)
                case YtTrackError.YtNotFound:
                case YtTrackError.TrackZeroDuration:
                    invalidOrders.Add(orders[i]);
                    break;
                case YtTrackError.FailedToGetInfo:
                    results.Add(CreateTrackResult(orders[i], track, error));
                    break;
                case null:
                    if (track == null) break;
                    results.Add(CreateTrackResult(orders[i], track, null));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (invalidOrders.Count > 0)
            RemoveInvalidOrders(invalidOrders);

        return (results, invalidOrders);
    }

    private void RemoveInvalidOrders(IEnumerable<MusicOrder> orders)
    {
        foreach (var order in orders) RemoveInvalidOrder(order);
    }

    private void RemoveInvalidOrder(MusicOrder order)
    {
        _musicOrderRepository.MarkPlayed(order, Played.Invalid);
    }


    private async Task OnMusicOrdersAdd(object? sender, List<MusicOrder> e)
    {
        Log.Information("OnMusicOrdersAdd вызван.");
        var addedOrders = _musicOrderRepository.AddOrders(e);
        if (addedOrders.Count == 0)
            return;

        var (orders, invalidOrders) = await GetMusicOrderWithTracks(addedOrders);
        OrdersAdded?.Invoke(this, orders);
        if (sender is NewOrdersNotifier notifier)
        {
            await notifier.OrdersAccepted(orders.Where(x => x.IsAvailable).Select(x => x.MusicOrder).ToList(), true);
            await notifier.OrdersAccepted(invalidOrders, false);
        }
    }

    private static MusicOrderWithTrack CreateTrackResult(MusicOrder order, PlaylistTrack? track, YtTrackError? error) =>
        new()
        {
            MusicOrder = order,
            PlaylistTrack = track,
            Error = error,
            ErrorMessage = GetErrorMessage(error)
        };

    private static string? GetErrorMessage(YtTrackError? error) =>
        error switch
        {
            null => null,
            YtTrackError.YtNotFound => "YouTube video is unavailable.",
            YtTrackError.TrackZeroDuration => "Live streams and zero-duration videos are not supported.",
            YtTrackError.FailedToGetInfo => "Could not load YouTube video info. Retry later.",
            _ => "Could not load YouTube track."
        };
}

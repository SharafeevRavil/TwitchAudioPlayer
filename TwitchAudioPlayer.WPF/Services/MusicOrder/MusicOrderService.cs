using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Windows.Threading;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.Services.DonationAlerts;
using TwitchAudioPlayer.WPF.Services.Twitch;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public class MusicOrderService
{
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
        _musicOrderRepository.MarkPlayed(order, played);
        order.Played = played;
    }

    public void EnsureOldTracksDisabled()
    {
        _musicOrderRepository.MarkOrdersInactiveBefore(DateTimeOffset.Now.AddDays(-3), Played.Played);
    }
    
    public async Task<List<MusicOrderWithTrack>> GetTracks()
    {
        var orders = _musicOrderRepository.GetValidOrders();
        return (await GetMusicOrderWithTracks(orders)).Available;
    }

    private async Task<(List<MusicOrderWithTrack> Available, List<MusicOrder> WithError)> GetMusicOrderWithTracks(List<MusicOrder> orders)
    {
        var trackTasks = orders.Select(x => Task.Run(() => _youTubeService.GetPlaylistTrack(x.Uri)));
        var tracks = await Task.WhenAll(trackTasks);

        var toReturn = new List<MusicOrderWithTrack>();
        var ordersToDelete = new List<MusicOrder>();
        for (var i = 0; i < orders.Count; i++)
        {
            var (track, error) = tracks[i];
            switch (error)
            {
                // удаляю треки, для которых нет ютуб видео или это бесконечный стрим (его не получить)
                case YtTrackError.YtNotFound:
                case YtTrackError.TrackZeroDuration:
                    ordersToDelete.Add(orders[i]);
                    break;
                case YtTrackError.FailedToGetStream:
                    break;
                case null:
                    if (track == null) break;
                    toReturn.Add(new MusicOrderWithTrack { MusicOrder = orders[i], PlaylistTrack = track });
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (ordersToDelete.Count > 0)
            RemoveInvalidOrders(ordersToDelete);

        return (toReturn, ordersToDelete);
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
        Console.WriteLine("OnMusicOrdersAdd");
        _musicOrderRepository.AddOrders(e);
        var (orders, errors) = await GetMusicOrderWithTracks(e);
        OrdersAdded?.Invoke(this, orders);
        if (sender is NewOrdersNotifier notifier)
        {
            await notifier.OrdersAccepted(orders.Select(x => x.MusicOrder).ToList(), true);
            await notifier.OrdersAccepted(errors, false);
        }
    }
}
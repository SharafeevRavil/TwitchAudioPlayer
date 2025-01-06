using System.Windows.Threading;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public abstract class NewOrdersNotifier
{
    private DispatcherTimer? _timer;
    private DateTimeOffset _lastOrderDate;

    public EventHandler<List<MusicOrder>>? MusicOrdersAdd;
    public EventHandler<bool>? EnabledChanged;
    public EventHandler? Ping;

    public void InitTimer(double seconds, DateTimeOffset lastOrderDate) =>
        InitTimer(TimeSpan.FromSeconds(seconds), lastOrderDate);
    public void InitTimer(TimeSpan time, DateTimeOffset lastOrderDate)
    {
        _timer?.Stop();
        EnabledChanged?.Invoke(this, IsRunning);
        _lastOrderDate = lastOrderDate;

        _timer = new DispatcherTimer
        {
            Interval = time
        };
        _timer.Tick += Timer_Tick;
    }

    public async Task RestartTimer()
    {
        if(_timer == null) return;
        // я не понимаю почему, но я почему-то реализовал рестарт именно так. какая-то хуйня, зачем таймер пересоздавать??
        // InitTimer(_timer.Interval, _lastOrderDate);
        await Stop();
        await Start();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        var orders = await MakeApiRequest(_lastOrderDate);
        Ping?.Invoke(this, EventArgs.Empty);
        if (orders is not { Count: > 0 }) return;
        _lastOrderDate = orders.Max(x => x.Date);
        MusicOrdersAdd?.Invoke(this, orders);
    }

    protected abstract Task<List<MusicOrder>?> MakeApiRequest(DateTimeOffset lastOrderDate);

    public async Task<bool> Start()
    {
        if (_timer == null) return false;
        if (IsRunning)
        {
            EnabledChanged?.Invoke(this, IsRunning);
            await Stop();
        }
        if (!await BeforeTimerStartCheck()) return false;
        _timer.Start();
        EnabledChanged?.Invoke(this, IsRunning);
        return true;
    }

    protected virtual async Task<bool> BeforeTimerStartCheck() => true;

    public async Task<bool> Stop()
    {
        if (_timer == null) return false;
        if(!await BeforeTimerStopCheck()) return false;
        _timer.Stop();
        EnabledChanged?.Invoke(this, IsRunning);
        return true;
    }

    protected virtual async Task<bool> BeforeTimerStopCheck() => true;

    public bool IsRunning => _timer is { IsEnabled: true };

    public virtual Task OrdersAccepted(List<MusicOrder> orders, bool isAccepted = true) => Task.CompletedTask;
}
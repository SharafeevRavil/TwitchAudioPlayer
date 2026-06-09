namespace TwitchAudioPlayer.WPF.Services.Proxy;

public sealed record ProxyTestResult(bool Success, string Message, Uri? ProxyUri, string? NodeName);

public interface IProxyService : IDisposable
{
    event EventHandler<ProxyStatusSnapshot>? StatusChanged;

    ProxyStatusSnapshot Status { get; }

    ProxyStatusSnapshot CurrentStatus { get; }

    Uri? CurrentProxyUri { get; }

    string? LastWorkingNodeId { get; }

    Task<ProxyStatusSnapshot> ApplyAsync(ProxySettings settings, CancellationToken cancellationToken = default);

    Task<ProxyStatusSnapshot> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<ProxyStatusSnapshot> StopAsync(CancellationToken cancellationToken = default);

    Task<Uri?> EnsureProxyAsync(CancellationToken cancellationToken = default);

    Task<ProxyTestResult> TestProxyAsync(CancellationToken cancellationToken = default);
}

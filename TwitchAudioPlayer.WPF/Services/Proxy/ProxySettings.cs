namespace TwitchAudioPlayer.WPF.Services.Proxy;

public class ProxySettings
{
    public const int DefaultLocalHttpPort = 18088;

    public ProxyMode Mode { get; set; } = ProxyMode.Disabled;

    public string? ExternalProxyUrl { get; set; }

    public string? SingleNodeUri { get; set; }

    public string? SubscriptionUrl { get; set; }

    public int LocalHttpPort { get; set; } = DefaultLocalHttpPort;

    public string? XrayExecutablePath { get; set; }

    public string? LastWorkingNodeId { get; set; }

    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(12);

    public string HealthCheckUrl { get; set; } = ProxyHealthChecker.DefaultHealthCheckUrl;
}

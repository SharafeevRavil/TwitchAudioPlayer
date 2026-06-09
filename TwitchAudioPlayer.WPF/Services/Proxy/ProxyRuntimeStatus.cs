namespace TwitchAudioPlayer.WPF.Services.Proxy;

public enum ProxyRuntimeStatus
{
    Disabled = 0,
    Stopped = 10,
    Starting = 20,
    Running = 30,
    Checking = 40,
    Error = 50
}

public sealed record ProxyStatusSnapshot(
    ProxyMode Mode,
    ProxyRuntimeStatus Status,
    string Message,
    ProxyNode? CurrentNode,
    Uri? ProxyUri,
    string? LastWorkingNodeId,
    bool IsHealthy,
    int? LocalPort,
    DateTimeOffset UpdatedAt)
{
    public static ProxyStatusSnapshot Disabled(string message = "Proxy is disabled.") =>
        new(ProxyMode.Disabled, ProxyRuntimeStatus.Disabled, message, null, null, null, false, null, DateTimeOffset.Now);
}

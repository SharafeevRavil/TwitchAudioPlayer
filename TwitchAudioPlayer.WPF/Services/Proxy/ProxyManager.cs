using System.IO;
using System.Net;
using System.Net.Sockets;

namespace TwitchAudioPlayer.WPF.Services.Proxy;

public sealed class ProxyManager : IProxyService, IAsyncDisposable
{
    private readonly IUserSettingsManager? _settingsManager;
    private readonly ProxyNodeParser _nodeParser;
    private readonly SubscriptionLoader _subscriptionLoader;
    private readonly XrayConfigGenerator _configGenerator;
    private readonly XrayProcessManager _processManager;
    private readonly ProxyHealthChecker _healthChecker;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProxySettings? _lastSettings;
    private string? _lastAppliedFingerprint;

    public ProxyManager(
        IUserSettingsManager? settingsManager = null,
        ProxyNodeParser? nodeParser = null,
        SubscriptionLoader? subscriptionLoader = null,
        XrayConfigGenerator? configGenerator = null,
        XrayProcessManager? processManager = null,
        ProxyHealthChecker? healthChecker = null)
    {
        _settingsManager = settingsManager;
        _nodeParser = nodeParser ?? new ProxyNodeParser();
        _subscriptionLoader = subscriptionLoader ?? new SubscriptionLoader(nodeParser: _nodeParser);
        _configGenerator = configGenerator ?? new XrayConfigGenerator();
        _processManager = processManager ?? new XrayProcessManager();
        _healthChecker = healthChecker ?? new ProxyHealthChecker();
        CurrentStatus = ProxyStatusSnapshot.Disabled();
    }

    public event EventHandler<ProxyStatusSnapshot>? StatusChanged;

    public ProxyStatusSnapshot Status => CurrentStatus;

    public ProxyStatusSnapshot CurrentStatus { get; private set; }

    public Uri? CurrentProxyUri => CurrentStatus.ProxyUri;

    public string? LastWorkingNodeId { get; private set; }

    public async Task<ProxyStatusSnapshot> ApplyAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            _lastSettings = settings;
            LastWorkingNodeId = settings.LastWorkingNodeId ?? LastWorkingNodeId;

            return settings.Mode switch
            {
                ProxyMode.Disabled => await DisableInternalAsync(settings, cancellationToken),
                ProxyMode.External => await UseExternalProxyAsync(settings, cancellationToken),
                ProxyMode.Single => await UseSingleNodeAsync(settings, cancellationToken),
                ProxyMode.Subscription => await UseSubscriptionAsync(settings, cancellationToken),
                _ => SetStatus(settings.Mode, ProxyRuntimeStatus.Error, "Selected proxy mode is not supported.", null, null, false,
                    null)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProxyStatusSnapshot> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (CurrentStatus.ProxyUri == null)
                return SetStatus(CurrentStatus.Mode, ProxyRuntimeStatus.Stopped, "Proxy is not running.", CurrentStatus.CurrentNode,
                    null, false, CurrentStatus.LocalPort);

            var settings = _lastSettings ?? new ProxySettings();
            var health = await _healthChecker.CheckAsync(CurrentStatus.ProxyUri, settings.HealthCheckTimeout,
                settings.HealthCheckUrl, cancellationToken);

            return SetStatus(
                CurrentStatus.Mode,
                health.IsHealthy ? ProxyRuntimeStatus.Running : ProxyRuntimeStatus.Error,
                health.Message,
                CurrentStatus.CurrentNode,
                CurrentStatus.ProxyUri,
                health.IsHealthy,
                CurrentStatus.LocalPort);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProxyStatusSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await _processManager.StopAsync(cancellationToken);
            return SetStatus(CurrentStatus.Mode, ProxyRuntimeStatus.Stopped, "Proxy is stopped.", null, null, false, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _processManager.DisposeAsync();
        _gate.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async Task<Uri?> EnsureProxyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsManager?.Settings.ProxySettings ?? _lastSettings ?? new ProxySettings();
        var fingerprint = BuildSettingsFingerprint(settings);
        if (CurrentStatus is { IsHealthy: true, ProxyUri: not null } &&
            string.Equals(_lastAppliedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return CurrentStatus.ProxyUri;
        }

        var previousLastWorkingNodeId = settings.LastWorkingNodeId;
        var previousLocalHttpPort = settings.LocalHttpPort;
        var status = await ApplyAsync(settings, cancellationToken);
        if (status.IsHealthy)
            _lastAppliedFingerprint = BuildSettingsFingerprint(settings);

        if (_settingsManager is not null &&
            (!string.Equals(previousLastWorkingNodeId, settings.LastWorkingNodeId, StringComparison.OrdinalIgnoreCase) ||
             previousLocalHttpPort != settings.LocalHttpPort))
        {
            await _settingsManager.SaveSettingsAsync();
        }

        return status.IsHealthy || status.Status == ProxyRuntimeStatus.Running ? status.ProxyUri : null;
    }

    public async Task<ProxyTestResult> TestProxyAsync(CancellationToken cancellationToken = default)
    {
        var status = await ApplyAsync(_settingsManager?.Settings.ProxySettings ?? _lastSettings ?? new ProxySettings(),
            cancellationToken);

        if (_settingsManager is not null)
            await _settingsManager.SaveSettingsAsync();

        if (status.Mode == ProxyMode.Disabled)
            return new ProxyTestResult(true, status.Message, null, null);

        return new ProxyTestResult(status.IsHealthy, status.Message, status.ProxyUri, status.CurrentNode?.Name);
    }

    private async Task<ProxyStatusSnapshot> DisableInternalAsync(ProxySettings settings, CancellationToken cancellationToken)
    {
        await _processManager.StopAsync(cancellationToken);
        return SetStatus(ProxyMode.Disabled, ProxyRuntimeStatus.Disabled, "Proxy is disabled.", null, null, false,
            settings.LocalHttpPort);
    }

    private async Task<ProxyStatusSnapshot> UseExternalProxyAsync(ProxySettings settings, CancellationToken cancellationToken)
    {
        await _processManager.StopAsync(cancellationToken);

        var proxyUri = NormalizeHttpProxyUri(settings.ExternalProxyUrl);
        if (proxyUri == null)
            return SetStatus(ProxyMode.External, ProxyRuntimeStatus.Error, "External proxy URL is invalid.", null, null, false,
                null);

        SetStatus(ProxyMode.External, ProxyRuntimeStatus.Checking, "Checking external proxy...", null, proxyUri, false, null);

        var health = await _healthChecker.CheckAsync(proxyUri, settings.HealthCheckTimeout, settings.HealthCheckUrl,
            cancellationToken);

        return SetStatus(
            ProxyMode.External,
            health.IsHealthy ? ProxyRuntimeStatus.Running : ProxyRuntimeStatus.Error,
            health.Message,
            null,
            proxyUri,
            health.IsHealthy,
            null);
    }

    private async Task<ProxyStatusSnapshot> UseSingleNodeAsync(ProxySettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SingleNodeUri))
            return SetStatus(ProxyMode.Single, ProxyRuntimeStatus.Error, "Proxy node link is empty.", null, null, false,
                settings.LocalHttpPort);

        if (!_nodeParser.TryParse(settings.SingleNodeUri, out var node, out var parseError) || node == null)
            return SetStatus(ProxyMode.Single, ProxyRuntimeStatus.Error, parseError ?? "Proxy node link is invalid.", null,
                null, false, settings.LocalHttpPort);

        return await TryStartNodeAsync(settings, node, ProxyMode.Single, cancellationToken);
    }

    private async Task<ProxyStatusSnapshot> UseSubscriptionAsync(ProxySettings settings, CancellationToken cancellationToken)
    {
        SubscriptionLoadResult subscription;

        try
        {
            SetStatus(ProxyMode.Subscription, ProxyRuntimeStatus.Starting, "Loading proxy subscription...", null, null, false,
                settings.LocalHttpPort);
            subscription = await _subscriptionLoader.LoadAsync(settings.SubscriptionUrl ?? string.Empty, cancellationToken);
        }
        catch (ProxyException ex)
        {
            return SetStatus(ProxyMode.Subscription, ProxyRuntimeStatus.Error, ex.Message, null, null, false,
                settings.LocalHttpPort);
        }

        if (subscription.Nodes.Count == 0)
            return SetStatus(ProxyMode.Subscription, ProxyRuntimeStatus.Error, "Subscription has no usable VLESS nodes.", null,
                null, false, settings.LocalHttpPort);

        var nodes = OrderSubscriptionNodes(subscription.Nodes, settings.LastWorkingNodeId ?? LastWorkingNodeId);
        var lastError = "No subscription node passed the health check.";

        foreach (var node in nodes)
        {
            var status = await TryStartNodeAsync(settings, node, ProxyMode.Subscription, cancellationToken, stopOnHealthError: true);
            if (status.IsHealthy)
                return status;

            lastError = status.Message;
        }

        await _processManager.StopAsync(cancellationToken);
        return SetStatus(ProxyMode.Subscription, ProxyRuntimeStatus.Error, lastError, null, null, false, settings.LocalHttpPort);
    }

    private async Task<ProxyStatusSnapshot> TryStartNodeAsync(
        ProxySettings settings,
        ProxyNode node,
        ProxyMode mode,
        CancellationToken cancellationToken,
        bool stopOnHealthError = false)
    {
        var requestedLocalHttpPort = NormalizeLocalPort(settings.LocalHttpPort);
        var localHttpPort = GetAvailablePort(requestedLocalHttpPort);
        if (settings.LocalHttpPort != localHttpPort)
            settings.LocalHttpPort = localHttpPort;

        var portMessagePrefix = localHttpPort == requestedLocalHttpPort
            ? string.Empty
            : $"Local proxy port was changed from {requestedLocalHttpPort} to {localHttpPort} because the requested port is busy. ";

        SetStatus(mode, ProxyRuntimeStatus.Starting, $"{portMessagePrefix}Starting proxy node '{node.Name}'...", node, null, false,
            localHttpPort);

        XrayConfigFile config;
        try
        {
            config = _configGenerator.Generate(node, localHttpPort);
            await _processManager.StartAsync(ResolveXrayExecutablePath(settings), config.FilePath, localHttpPort,
                settings.StartTimeout, cancellationToken);
        }
        catch (ProxyException ex)
        {
            return SetStatus(mode, ProxyRuntimeStatus.Error, ex.Message, node, null, false, localHttpPort);
        }

        var proxyUri = new Uri($"http://{config.LocalAddress}:{config.LocalPort}");
        SetStatus(mode, ProxyRuntimeStatus.Checking, $"{portMessagePrefix}Checking proxy node '{node.Name}'...", node, proxyUri, false,
            localHttpPort);

        var health = await _healthChecker.CheckAsync(proxyUri, settings.HealthCheckTimeout, settings.HealthCheckUrl,
            cancellationToken);

        if (!health.IsHealthy)
        {
            if (stopOnHealthError)
                await _processManager.StopAsync(cancellationToken);

            return SetStatus(mode, ProxyRuntimeStatus.Error, health.Message, node, proxyUri, false, localHttpPort);
        }

        LastWorkingNodeId = node.Id;
        settings.LastWorkingNodeId = node.Id;

        return SetStatus(mode, ProxyRuntimeStatus.Running, $"{portMessagePrefix}Proxy node '{node.Name}' is working.", node, proxyUri, true,
            localHttpPort);
    }

    private ProxyStatusSnapshot SetStatus(
        ProxyMode mode,
        ProxyRuntimeStatus status,
        string message,
        ProxyNode? currentNode,
        Uri? proxyUri,
        bool isHealthy,
        int? localPort)
    {
        CurrentStatus = new ProxyStatusSnapshot(
            mode,
            status,
            message,
            currentNode,
            proxyUri,
            LastWorkingNodeId,
            isHealthy,
            localPort,
            DateTimeOffset.Now);

        StatusChanged?.Invoke(this, CurrentStatus);
        return CurrentStatus;
    }

    private static IEnumerable<ProxyNode> OrderSubscriptionNodes(
        IReadOnlyList<ProxyNode> nodes,
        string? preferredNodeId)
    {
        var indexedNodes = nodes.Select((node, index) => new { Node = node, Index = index });

        if (string.IsNullOrWhiteSpace(preferredNodeId))
            return nodes;

        return indexedNodes
            .OrderByDescending(item => string.Equals(item.Node.Id, preferredNodeId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Index)
            .Select(item => item.Node);
    }

    private static string ResolveXrayExecutablePath(ProxySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.XrayExecutablePath))
            return settings.XrayExecutablePath;

        return Path.Combine(AppContext.BaseDirectory, "xray.exe");
    }

    private static Uri? NormalizeHttpProxyUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "http://" + normalized;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme is "http" or "https" && uri.Port > 0 ? uri : null;
    }

    private static int GetAvailablePort(int preferredPort)
    {
        if (IsPortAvailable(preferredPort))
            return preferredPort;

        for (var port = preferredPort + 1; port <= 65535 && port < preferredPort + 100; port++)
        {
            if (IsPortAvailable(port))
                return port;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int NormalizeLocalPort(int port) =>
        port is > 0 and <= 65535 ? port : ProxySettings.DefaultLocalHttpPort;

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string BuildSettingsFingerprint(ProxySettings settings) =>
        string.Join("|",
            settings.Mode,
            settings.ExternalProxyUrl,
            settings.SingleNodeUri,
            settings.SubscriptionUrl,
            settings.LocalHttpPort,
            settings.XrayExecutablePath,
            settings.HealthCheckUrl,
            settings.StartTimeout,
            settings.HealthCheckTimeout);
}

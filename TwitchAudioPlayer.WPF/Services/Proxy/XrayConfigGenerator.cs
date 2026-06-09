using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchAudioPlayer.WPF.Services.Proxy;

public sealed record XrayConfigFile(string FilePath, string DirectoryPath, string LocalAddress, int LocalPort);

public sealed class XrayConfigGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public XrayConfigFile Generate(ProxyNode node, int localHttpPort, string? directoryPath = null)
    {
        if (node.Kind != ProxyNodeKind.Vless)
            throw new ProxyException("Only VLESS nodes are supported by the embedded proxy backend.");

        if (localHttpPort <= 0 || localHttpPort > 65535)
            throw new ProxyException("Local proxy port is invalid.");

        var directory = directoryPath ?? Path.Combine(Path.GetTempPath(), "TwitchAudioPlayer", "Proxy", node.Id);
        Directory.CreateDirectory(directory);

        var configPath = Path.Combine(directory, "config.json");
        var config = BuildVlessConfig(node, localHttpPort);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));

        return new XrayConfigFile(configPath, directory, "127.0.0.1", localHttpPort);
    }

    private static object BuildVlessConfig(ProxyNode node, int localHttpPort)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["protocol"] = "vless",
            ["tag"] = "proxy",
            ["settings"] = new
            {
                vnext = new[]
                {
                    new
                    {
                        address = node.Address,
                        port = node.Port,
                        users = new[]
                        {
                            new
                            {
                                id = node.UserId,
                                encryption = string.IsNullOrWhiteSpace(node.Encryption) ? "none" : node.Encryption,
                                flow = EmptyToNull(node.Flow)
                            }
                        }
                    }
                }
            },
            ["streamSettings"] = BuildStreamSettings(node)
        };

        return new
        {
            log = new { loglevel = "warning" },
            inbounds = new[]
            {
                new
                {
                    listen = "127.0.0.1",
                    port = localHttpPort,
                    protocol = "http",
                    settings = new { timeout = 0 },
                    sniffing = new
                    {
                        enabled = true,
                        destOverride = new[] { "http", "tls" }
                    }
                }
            },
            outbounds = new object[]
            {
                outbound,
                new { protocol = "freedom", tag = "direct" },
                new { protocol = "blackhole", tag = "block" }
            }
        };
    }

    private static Dictionary<string, object?> BuildStreamSettings(ProxyNode node)
    {
        var transport = string.IsNullOrWhiteSpace(node.Transport) ? "tcp" : node.Transport;
        var security = string.IsNullOrWhiteSpace(node.Security) ? "none" : node.Security;

        var streamSettings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["network"] = transport,
            ["security"] = security
        };

        AddTransportSettings(streamSettings, node, transport);
        AddSecuritySettings(streamSettings, node, security);

        return streamSettings;
    }

    private static void AddTransportSettings(IDictionary<string, object?> streamSettings, ProxyNode node, string transport)
    {
        switch (transport.ToLowerInvariant())
        {
            case "ws":
                streamSettings["wsSettings"] = new
                {
                    path = EmptyToNull(node.Path),
                    headers = string.IsNullOrWhiteSpace(node.Host) ? null : new { Host = node.Host }
                };
                break;
            case "grpc":
                streamSettings["grpcSettings"] = new
                {
                    serviceName = EmptyToNull(node.ServiceName),
                    multiMode = string.Equals(node.GrpcMode, "multi", StringComparison.OrdinalIgnoreCase)
                };
                break;
            case "http":
            case "h2":
                streamSettings["httpSettings"] = new
                {
                    path = EmptyToNull(node.Path),
                    host = SplitCsv(node.Host)
                };
                break;
        }
    }

    private static void AddSecuritySettings(IDictionary<string, object?> streamSettings, ProxyNode node, string security)
    {
        switch (security.ToLowerInvariant())
        {
            case "tls":
                streamSettings["tlsSettings"] = new
                {
                    serverName = EmptyToNull(node.ServerName),
                    fingerprint = EmptyToNull(node.Fingerprint),
                    alpn = SplitCsv(node.Alpn),
                    allowInsecure = node.AllowInsecure
                };
                break;
            case "reality":
                streamSettings["realitySettings"] = new
                {
                    serverName = EmptyToNull(node.ServerName),
                    fingerprint = EmptyToNull(node.Fingerprint),
                    publicKey = EmptyToNull(node.PublicKey),
                    shortId = EmptyToNull(node.ShortId),
                    spiderX = EmptyToNull(node.SpiderX)
                };
                break;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[]? SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

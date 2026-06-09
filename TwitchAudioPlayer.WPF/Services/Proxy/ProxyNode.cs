using System.Security.Cryptography;
using System.Text;

namespace TwitchAudioPlayer.WPF.Services.Proxy;

public enum ProxyNodeKind
{
    Vless = 10,
    Trojan = 20,
    Shadowsocks = 30,
    Vmess = 40
}

public sealed record ProxyNode
{
    public required string Id { get; init; }

    public required ProxyNodeKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Address { get; init; }

    public required int Port { get; init; }

    public string? UserId { get; init; }

    public string? Encryption { get; init; }

    public string? Flow { get; init; }

    public string Transport { get; init; } = "tcp";

    public string Security { get; init; } = "none";

    public string? ServerName { get; init; }

    public string? Fingerprint { get; init; }

    public string? PublicKey { get; init; }

    public string? ShortId { get; init; }

    public string? SpiderX { get; init; }

    public string? Path { get; init; }

    public string? Host { get; init; }

    public string? Alpn { get; init; }

    public string? ServiceName { get; init; }

    public string? GrpcMode { get; init; }

    public bool AllowInsecure { get; init; }

    public IReadOnlyDictionary<string, string> QueryParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static string CreateStableId(ProxyNodeKind kind, string address, int port, string? userId)
    {
        var value = $"{kind}|{address.Trim().ToLowerInvariant()}|{port}|{userId?.Trim().ToLowerInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }
}

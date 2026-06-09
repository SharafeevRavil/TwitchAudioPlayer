namespace TwitchAudioPlayer.WPF.Services.Proxy;

public interface IProxyNodeSchemeParser
{
    string Scheme { get; }

    bool TryParse(Uri uri, out ProxyNode? node, out string? errorMessage);
}

public sealed class ProxyNodeParser
{
    private readonly Dictionary<string, IProxyNodeSchemeParser> _parsers;

    public ProxyNodeParser(IEnumerable<IProxyNodeSchemeParser>? parsers = null)
    {
        _parsers = new Dictionary<string, IProxyNodeSchemeParser>(StringComparer.OrdinalIgnoreCase);

        foreach (var parser in parsers ?? [new VlessProxyNodeParser()])
            _parsers[parser.Scheme] = parser;
    }

    public IReadOnlyCollection<string> SupportedSchemes => _parsers.Keys.ToArray();

    public bool TryParse(string value, out ProxyNode? node, out string? errorMessage)
    {
        node = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            errorMessage = "Proxy node link is empty.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            errorMessage = "Proxy node link is not a valid URI.";
            return false;
        }

        if (!_parsers.TryGetValue(uri.Scheme, out var parser))
        {
            errorMessage = $"Proxy protocol '{uri.Scheme}' is not supported yet.";
            return false;
        }

        return parser.TryParse(uri, out node, out errorMessage);
    }

    public ProxyNode Parse(string value)
    {
        if (TryParse(value, out var node, out var errorMessage) && node != null)
            return node;

        throw new ProxyException(errorMessage ?? "Proxy node link could not be parsed.");
    }
}

public sealed class VlessProxyNodeParser : IProxyNodeSchemeParser
{
    public string Scheme => "vless";

    public bool TryParse(Uri uri, out ProxyNode? node, out string? errorMessage)
    {
        node = null;
        errorMessage = null;

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "This parser only supports VLESS links.";
            return false;
        }

        var userId = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty).Trim();
        if (!Guid.TryParse(userId, out _))
        {
            errorMessage = "VLESS link has an invalid user id.";
            return false;
        }

        var address = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(address))
        {
            errorMessage = "VLESS link does not contain a server address.";
            return false;
        }

        if (uri.Port <= 0 || uri.Port > 65535)
        {
            errorMessage = "VLESS link has an invalid server port.";
            return false;
        }

        var query = ParseQuery(uri.Query);
        var name = DecodeName(uri.Fragment);
        var security = Get(query, "security") ?? "none";
        var transport = Get(query, "type") ?? "tcp";

        node = new ProxyNode
        {
            Id = ProxyNode.CreateStableId(ProxyNodeKind.Vless, address, uri.Port, userId),
            Kind = ProxyNodeKind.Vless,
            Name = string.IsNullOrWhiteSpace(name) ? "VLESS node" : name,
            Address = address,
            Port = uri.Port,
            UserId = userId,
            Encryption = Get(query, "encryption") ?? "none",
            Flow = Get(query, "flow"),
            Transport = transport,
            Security = security,
            ServerName = Get(query, "sni") ?? Get(query, "serverName"),
            Fingerprint = Get(query, "fp"),
            PublicKey = Get(query, "pbk"),
            ShortId = Get(query, "sid"),
            SpiderX = Get(query, "spx"),
            Path = Get(query, "path"),
            Host = Get(query, "host"),
            Alpn = Get(query, "alpn"),
            ServiceName = Get(query, "serviceName"),
            GrpcMode = Get(query, "mode"),
            AllowInsecure = string.Equals(Get(query, "allowInsecure"), "1", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Get(query, "allowInsecure"), "true", StringComparison.OrdinalIgnoreCase),
            QueryParameters = query
        };

        return true;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
            return result;

        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            var rawKey = separatorIndex >= 0 ? part[..separatorIndex] : part;
            var rawValue = separatorIndex >= 0 ? part[(separatorIndex + 1)..] : string.Empty;
            var key = DecodeQueryPart(rawKey);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            result[key] = DecodeQueryPart(rawValue);
        }

        return result;
    }

    private static string DecodeQueryPart(string value) =>
        Uri.UnescapeDataString(value.Replace("+", "%20"));

    private static string? Get(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string DecodeName(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
            return string.Empty;

        var value = fragment[0] == '#' ? fragment[1..] : fragment;
        return Uri.UnescapeDataString(value.Replace("+", "%20")).Trim();
    }
}

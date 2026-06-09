using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace TwitchAudioPlayer.WPF.Services.Proxy;

public sealed record SubscriptionLoadResult(
    IReadOnlyList<ProxyNode> Nodes,
    int DiscoveredLinks,
    int FailedLinks,
    bool WasBase64Decoded,
    IReadOnlyList<string> Warnings);

public sealed class SubscriptionLoader
{
    private static readonly Regex VlessLinkRegex = new(
        @"vless://[^\s<>'""]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly ProxyNodeParser _nodeParser;

    public SubscriptionLoader(HttpClient? httpClient = null, ProxyNodeParser? nodeParser = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _nodeParser = nodeParser ?? new ProxyNodeParser();
    }

    public async Task<SubscriptionLoadResult> LoadAsync(string subscriptionUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionUrl))
            throw new ProxyException("Subscription URL is empty.");

        if (!Uri.TryCreate(subscriptionUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ProxyException("Subscription URL must be a valid HTTP or HTTPS link.");
        }

        string response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var httpResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            httpResponse.EnsureSuccessStatusCode();
            response = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProxyException("Could not download the proxy subscription.", ex);
        }

        var content = DecodeBase64IfNeeded(response, out var wasBase64Decoded);
        var links = ExtractVlessLinks(content);
        var nodes = new List<ProxyNode>();
        var warnings = new List<string>();
        var failedLinks = 0;

        foreach (var link in links)
        {
            if (_nodeParser.TryParse(link, out var node, out _))
            {
                if (node != null)
                    nodes.Add(node);
            }
            else
            {
                failedLinks++;
            }
        }

        var distinctNodes = nodes
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (links.Count == 0)
            warnings.Add("Subscription does not contain VLESS nodes.");

        if (failedLinks > 0)
            warnings.Add($"{failedLinks} VLESS node(s) could not be parsed.");

        return new SubscriptionLoadResult(distinctNodes, links.Count, failedLinks, wasBase64Decoded, warnings);
    }

    private static IReadOnlyList<string> ExtractVlessLinks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return VlessLinkRegex.Matches(content)
            .Select(match => match.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string DecodeBase64IfNeeded(string content, out bool wasDecoded)
    {
        wasDecoded = false;
        var trimmed = content.Trim('\uFEFF', ' ', '\r', '\n', '\t');

        if (trimmed.Contains("vless://", StringComparison.OrdinalIgnoreCase) || !LooksLikeBase64(trimmed))
            return content;

        try
        {
            var normalized = NormalizeBase64(trimmed);
            var decodedBytes = Convert.FromBase64String(normalized);
            var decoded = Encoding.UTF8.GetString(decodedBytes);

            if (!string.IsNullOrWhiteSpace(decoded))
            {
                wasDecoded = true;
                return decoded;
            }
        }
        catch (FormatException)
        {
            return content;
        }

        return content;
    }

    private static bool LooksLikeBase64(string value)
    {
        var compact = RemoveWhitespace(value);
        if (compact.Length < 16)
            return false;

        return compact.All(ch => char.IsLetterOrDigit(ch) || ch is '+' or '/' or '-' or '_' or '=');
    }

    private static string NormalizeBase64(string value)
    {
        var compact = RemoveWhitespace(value).Replace('-', '+').Replace('_', '/');
        var padding = compact.Length % 4;

        if (padding > 0)
            compact = compact.PadRight(compact.Length + 4 - padding, '=');

        return compact;
    }

    private static string RemoveWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }
}

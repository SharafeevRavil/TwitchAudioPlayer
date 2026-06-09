using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace TwitchAudioPlayer.WPF.Services.Proxy;

public sealed record ProxyHealthResult(
    bool IsHealthy,
    string Message,
    TimeSpan Elapsed,
    HttpStatusCode? StatusCode);

public sealed class ProxyHealthChecker
{
    public const string DefaultHealthCheckUrl = "https://www.youtube.com/generate_204";

    public async Task<ProxyHealthResult> CheckAsync(
        Uri proxyUri,
        TimeSpan timeout,
        string healthCheckUrl = DefaultHealthCheckUrl,
        CancellationToken cancellationToken = default)
    {
        if (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps)
            return new ProxyHealthResult(false, "Only HTTP proxy endpoints can be health-checked.", TimeSpan.Zero, null);

        if (!Uri.TryCreate(healthCheckUrl, UriKind.Absolute, out var targetUri))
            return new ProxyHealthResult(false, "Health check URL is invalid.", TimeSpan.Zero, null);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = timeout
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            if (response.StatusCode == HttpStatusCode.NoContent)
                return new ProxyHealthResult(true, "Proxy is working.", stopwatch.Elapsed, response.StatusCode);

            if (response.IsSuccessStatusCode)
                return new ProxyHealthResult(true, "Proxy returned a successful response.", stopwatch.Elapsed, response.StatusCode);

            return new ProxyHealthResult(false, "YouTube is not reachable through the proxy.", stopwatch.Elapsed, response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            return new ProxyHealthResult(false, "Proxy health check timed out.", stopwatch.Elapsed, null);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return new ProxyHealthResult(false, "Proxy health check failed.", stopwatch.Elapsed, null);
        }
    }
}

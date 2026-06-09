using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace TwitchAudioPlayer.WPF.Services.Proxy;

public sealed class XrayProcessManager : IAsyncDisposable
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(
        string xrayExecutablePath,
        string configPath,
        int localPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xrayExecutablePath))
            throw new ProxyException("xray.exe path is empty.");

        if (!File.Exists(xrayExecutablePath))
            throw new ProxyException("xray.exe was not found.");

        if (!File.Exists(configPath))
            throw new ProxyException("Xray config file was not created.");

        await StopAsync(cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = xrayExecutablePath,
            Arguments = $"run -config \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(xrayExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new ProxyException("Could not start xray.exe.", ex);
        }

        if (_process == null)
            throw new ProxyException("Could not start xray.exe.");

        _ = DrainProcessOutputAsync(_process, cancellationToken);

        bool portReady;
        try
        {
            portReady = await WaitForLocalPortAsync(localPort, timeout, cancellationToken);
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }

        if (!portReady)
        {
            var exited = _process.HasExited;
            await StopAsync(cancellationToken);
            throw new ProxyException(exited
                ? "xray.exe stopped before the local proxy port became ready."
                : "Local proxy port did not become ready in time.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        _process = null;

        if (process == null)
            return;

        try
        {
            if (process.HasExited)
                return;

            if (process.CloseMainWindow())
            {
                using var gracefulCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulCts.CancelAfter(TimeSpan.FromSeconds(3));

                try
                {
                    await process.WaitForExitAsync(gracefulCts.Token);
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static async Task<bool> WaitForLocalPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await CanConnectAsync(port, cancellationToken))
                return true;

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DrainProcessOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch
        {
            // Output is intentionally discarded; full server details may appear there.
        }
    }
}

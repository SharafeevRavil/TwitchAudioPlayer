using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Serilog;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

/// <summary>
/// Fast, metadata-only YouTube search through yt-dlp. The process is always bounded and
/// cancellation kills the whole process tree, so a stale prefetch cannot block a real track.
/// </summary>
public sealed class YouTubeYtDlpSearchService
{
    private const int ResultLimit = 5;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] DefaultUserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:136.0) Gecko/20100101 Firefox/136.0"
    ];

    private readonly ILogger _logger = Log.ForContext<YouTubeYtDlpSearchService>();
    private readonly IUserSettingsManager _settingsManager;
    private readonly string _userAgent = DefaultUserAgents[Random.Shared.Next(DefaultUserAgents.Length)];

    public YouTubeYtDlpSearchService(IUserSettingsManager settingsManager) =>
        _settingsManager = settingsManager;

    public bool IsAvailable => ResolveExecutable() is not null;

    public async Task<IReadOnlyList<YouTubeSearchItem>> SearchAsync(
        string query,
        YouTubeSearchSort sort,
        CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            _logger.Debug("yt-dlp is not available");
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArguments(startInfo, query);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return [];

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProcessTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                    throw;

                _logger.Warning("yt-dlp search timed out after {Elapsed} ms for {Query}",
                    stopwatch.ElapsedMilliseconds, query);
                return [];
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                _logger.Warning("yt-dlp search exited with code {ExitCode} for {Query}: {Error}",
                    process.ExitCode, query, Truncate(error));
                return [];
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.Warning("yt-dlp returned empty output for {Query}: {Error}", query, Truncate(error));
                return [];
            }

            var items = Parse(output);
            _logger.Information("YouTube search backend yt-dlp returned {Count} results in {Elapsed} ms for {Query}",
                items.Count, stopwatch.ElapsedMilliseconds, query);
            return items;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "yt-dlp search failed after {Elapsed} ms for {Query}",
                stopwatch.ElapsedMilliseconds, query);
            return [];
        }
    }

    private void AddArguments(ProcessStartInfo startInfo, string query)
    {
        startInfo.ArgumentList.Add("--flat-playlist");
        startInfo.ArgumentList.Add("--dump-single-json");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--playlist-end");
        startInfo.ArgumentList.Add(ResultLimit.ToString());
        startInfo.ArgumentList.Add("--socket-timeout");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("--retries");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--extractor-retries");
        startInfo.ArgumentList.Add("1");

        if (_settingsManager.Settings.UseYouTubeProxy &&
            _settingsManager.Settings.YouTubeProxyList is { Count: > 0 } proxies)
        {
            startInfo.ArgumentList.Add("--proxy");
            startInfo.ArgumentList.Add(proxies[Random.Shared.Next(proxies.Count)]);
        }

        if (_settingsManager.Settings.RotateYouTubeUserAgent)
        {
            startInfo.ArgumentList.Add("--user-agent");
            startInfo.ArgumentList.Add(_userAgent);
        }

        startInfo.ArgumentList.Add($"ytsearch{ResultLimit}:{query}");
    }

    private static IReadOnlyList<YouTubeSearchItem> Parse(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var results = new List<YouTubeSearchItem>(ResultLimit);
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (results.Count == ResultLimit)
                    break;
                if (ConvertEntry(entry, results.Count + 1) is { } item)
                    results.Add(item);
            }
        }
        else if (ConvertEntry(root, 1) is { } item)
        {
            results.Add(item);
        }

        return results;
    }

    private static YouTubeSearchItem? ConvertEntry(JsonElement entry, int rank)
    {
        var id = GetString(entry, "id");
        var title = GetString(entry, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var channel = GetString(entry, "channel");
        if (string.IsNullOrWhiteSpace(channel))
            channel = GetString(entry, "uploader");
        var duration = entry.TryGetProperty("duration", out var durationElement) &&
                       durationElement.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(durationElement.GetDouble())
            : TimeSpan.Zero;
        long? views = entry.TryGetProperty("view_count", out var viewsElement) &&
                      viewsElement.TryGetInt64(out var viewCount)
            ? viewCount
            : null;
        var verified = entry.TryGetProperty("channel_is_verified", out var verifiedElement) &&
                       verifiedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                       verifiedElement.GetBoolean();

        return new YouTubeSearchItem(
            rank,
            id,
            title,
            channel,
            GetString(entry, "channel_id"),
            duration,
            GetThumbnail(entry),
            views,
            verified);
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetThumbnail(JsonElement entry)
    {
        var direct = GetString(entry, "thumbnail");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;
        if (!entry.TryGetProperty("thumbnails", out var thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var result = string.Empty;
        foreach (var thumbnail in thumbnails.EnumerateArray())
        {
            var url = GetString(thumbnail, "url");
            if (!string.IsNullOrWhiteSpace(url))
                result = url;
        }
        return result;
    }

    private static string? ResolveExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        var local = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(local))
            return local;

        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return path.Select(folder => Path.Combine(folder, executableName)).FirstOrDefault(File.Exists);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // Best effort cleanup during cancellation/timeout.
        }
    }

    private static string Truncate(string value) =>
        string.IsNullOrWhiteSpace(value) ? "no details" : value.Trim()[..Math.Min(value.Trim().Length, 500)];
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using YoutubeExplode;
using YoutubeExplode.Search;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

using System.Net.Http;

using System.IO;

public enum YouTubeSearchSort
{
    Relevance,
    ViewCount
}

public sealed record YouTubeSearchItem(
    int Rank,
    string VideoId,
    string Title,
    string ChannelTitle,
    string? ChannelId,
    TimeSpan Duration,
    string ThumbnailUrl,
    long? ViewCount,
    bool ChannelVerified);

public sealed class YouTubeSearchService : IDisposable
{
    private const int ResultLimit = 5;
    private static readonly TimeSpan RawSearchTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan YtDlpSearchTimeout = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan YoutubeExplodeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BackendFailureCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(350);
    private readonly ILogger _logger = Log.ForContext<YouTubeSearchService>();
    private readonly HttpClient _httpClient;
    private readonly YoutubeClient _youtube = new();
    private readonly IUserSettingsManager _settingsManager;
    private readonly YouTubeYtDlpSearchService _ytDlpSearchService;
    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<SearchOperation>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _rawSearchBlockedUntil;
    private DateTimeOffset _lastPipelineStartedAt = DateTimeOffset.MinValue;

    public YouTubeSearchService(IUserSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _ytDlpSearchService = new YouTubeYtDlpSearchService(settingsManager);
    }

    public async Task<IReadOnlyList<YouTubeSearchItem>> SearchAsync(string query, YouTubeSearchSort sort,
        CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];

        var key = $"{sort}:{query}";
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<SearchOperation>(
            () => new SearchOperation(token => SearchPipelineAsync(query, sort, token)),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = lazy.Value;
        operation.AddWaiter();
        _logger.Debug(operation.Results.IsCompleted
                ? "Using completed coalesced YouTube search for {Query}"
                : "Awaiting coalesced YouTube search for {Query}",
            query);
        try
        {
            return await operation.Results.WaitAsync(cancellationToken);
        }
        finally
        {
            if (operation.ReleaseWaiter() == 0)
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<SearchOperation>>(key, lazy));
                operation.Cancel();
            }
        }
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> SearchPipelineAsync(
        string query,
        YouTubeSearchSort sort,
        CancellationToken cancellationToken)
    {
        await _pipelineGate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var wait = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastPipelineStartedAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
            _lastPipelineStartedAt = DateTimeOffset.UtcNow;

            var preferYtDlp = _settingsManager.Settings.UseYtDlpForSearch;

            if (preferYtDlp && await TryYtDlpAsync(query, sort, cancellationToken) is { Count: > 0 } ytDlpFirst)
                return Complete("yt-dlp", ytDlpFirst, query, stopwatch);

            if (await TryRawAsync(query, sort, cancellationToken) is { Count: > 0 } raw)
                return Complete("raw HTML", raw, query, stopwatch);

            if (!preferYtDlp && await TryYtDlpAsync(query, sort, cancellationToken) is { Count: > 0 } ytDlpFallback)
                return Complete("yt-dlp fallback", ytDlpFallback, query, stopwatch);

            var explode = await TryBackendAsync(
                "YoutubeExplode",
                YoutubeExplodeTimeout,
                cancellationToken,
                token => SearchWithYoutubeExplodeAsync(query, token));
            return Complete("YoutubeExplode fallback", explode, query, stopwatch);
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> TryRawAsync(
        string query,
        YouTubeSearchSort sort,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _rawSearchBlockedUntil)
            return [];

        try
        {
            var result = await TryBackendAsync(
                "raw HTML",
                RawSearchTimeout,
                cancellationToken,
                token => SearchRawAsync(query, sort, token));
            if (result.Count == 0)
                _rawSearchBlockedUntil = DateTimeOffset.UtcNow + BackendFailureCooldown;
            return result;
        }
        catch (YouTubeSearchRateLimitedException exception)
        {
            _rawSearchBlockedUntil = DateTimeOffset.UtcNow + BackendFailureCooldown;
            _logger.Warning(exception, "Raw YouTube search is rate limited; other backends remain available");
            return [];
        }
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> TryYtDlpAsync(
        string query,
        YouTubeSearchSort sort,
        CancellationToken cancellationToken)
    {
        if (!_ytDlpSearchService.IsAvailable)
            return [];

        return await TryBackendAsync(
            "yt-dlp",
            YtDlpSearchTimeout,
            cancellationToken,
            token => _ytDlpSearchService.SearchAsync(query, sort, token));
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> TryBackendAsync(
        string backend,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<YouTubeSearchItem>>> action)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            _logger.Warning("YouTube search backend {Backend} timed out after {Elapsed} ms",
                backend, stopwatch.ElapsedMilliseconds);
            return [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (YouTubeSearchRateLimitedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "YouTube search backend {Backend} failed after {Elapsed} ms",
                backend, stopwatch.ElapsedMilliseconds);
            return [];
        }
    }

    private IReadOnlyList<YouTubeSearchItem> Complete(
        string backend,
        IReadOnlyList<YouTubeSearchItem> results,
        string query,
        Stopwatch stopwatch)
    {
        _logger.Information(
            "YouTube search completed via {Backend}: {Count} results in {Elapsed} ms for {Query}",
            backend, results.Count, stopwatch.ElapsedMilliseconds, query);
        return results;
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> SearchRawAsync(string query, YouTubeSearchSort sort,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sortParameter = sort == YouTubeSearchSort.ViewCount ? "&sp=CAMSAhAB" : "";
        var url = "https://www.youtube.com/results?hl=en&gl=US&search_query=" +
                  Uri.EscapeDataString(query) + sortParameter;

        _logger.Debug("SearchRawAsync fetching from {Url}", url);

        using var response = await _httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
        {
            _logger.Warning("YouTube raw search rate limit/forbidden for query {Query}, status {Status}", query, response.StatusCode);
            throw new YouTubeSearchRateLimitedException((int)response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.Debug("SearchRawAsync received {Length} bytes, parsing", html.Length);

        using var initialData = ExtractInitialData(html);
        var results = new List<YouTubeSearchItem>(ResultLimit);
        var videoIds = new HashSet<string>(StringComparer.Ordinal);

        if (!CollectPrimarySearchVideoRenderers(initialData.RootElement, results, videoIds))
            CollectVideoRenderers(initialData.RootElement, results, videoIds);

        _logger.Debug("SearchRawAsync completed in {Elapsed}ms, returned {Count} results for {Query}", sw.ElapsedMilliseconds, results.Count, query);
        return results;
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> SearchWithYoutubeExplodeAsync(string query,
        CancellationToken cancellationToken)
    {
        var results = new List<YouTubeSearchItem>(ResultLimit);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var perVideoTimeout = TimeSpan.FromSeconds(5);
        var totalTimeout = TimeSpan.FromSeconds(20);

        try
        {
            await foreach (var result in _youtube.Search.GetVideosAsync(query, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                // Check total timeout
                if (sw.Elapsed > totalTimeout)
                {
                    _logger.Debug("YoutubeExplode search timeout after {Elapsed}ms for query {Query}", sw.ElapsedMilliseconds, query);
                    break;
                }

                // Timeout protection per video
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(perVideoTimeout);

                try
                {
                    results.Add(new YouTubeSearchItem(
                        results.Count + 1,
                        result.Id.Value,
                        result.Title,
                        result.Author.ChannelTitle,
                        result.Author.ChannelId.Value,
                        result.Duration ?? TimeSpan.Zero,
                        result.Thumbnails.LastOrDefault()?.Url ?? "",
                        null,
                        false));

                    if (results.Count == ResultLimit)
                        break;
                }
                catch (OperationCanceledException)
                {
                    _logger.Debug("Skipped video due to timeout in YoutubeExplode search");
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "YoutubeExplode search error after {Elapsed}ms for {Query}, returning {Count} results so far",
                sw.ElapsedMilliseconds, query, results.Count);
        }

        _logger.Debug("YoutubeExplode search completed in {Elapsed}ms, returned {Count} results", sw.ElapsedMilliseconds, results.Count);
        return results;
    }

    private static JsonDocument ExtractInitialData(string html)
    {
        var markerIndex = html.IndexOf("ytInitialData", StringComparison.Ordinal);
        if (markerIndex < 0)
            throw new InvalidDataException("ytInitialData was not found in the YouTube search response.");

        var jsonStart = html.IndexOf('{', markerIndex);
        if (jsonStart < 0)
            throw new InvalidDataException("ytInitialData JSON was not found in the YouTube search response.");

        var utf8 = Encoding.UTF8.GetBytes(html.AsSpan(jsonStart).ToString());
        var reader = new Utf8JsonReader(utf8);
        return JsonDocument.ParseValue(ref reader);
    }

    private static bool CollectPrimarySearchVideoRenderers(JsonElement root,
        ICollection<YouTubeSearchItem> results, ISet<string> videoIds)
    {
        if (!TryGetProperty(root, out var sectionList,
                "contents", "twoColumnSearchResultsRenderer", "primaryContents",
                "sectionListRenderer", "contents") ||
            sectionList.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var section in sectionList.EnumerateArray())
        {
            if (!section.TryGetProperty("itemSectionRenderer", out var itemSection) ||
                !itemSection.TryGetProperty("contents", out var contents) ||
                contents.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in contents.EnumerateArray())
            {
                if (!item.TryGetProperty("videoRenderer", out var renderer) ||
                    !TryParseVideoRenderer(renderer, results.Count + 1, out var parsed) ||
                    !videoIds.Add(parsed.VideoId))
                    continue;

                results.Add(parsed);
                if (results.Count >= ResultLimit)
                    return true;
            }
        }

        return true;
    }

    private static void CollectVideoRenderers(JsonElement element, ICollection<YouTubeSearchItem> results,
        ISet<string> videoIds)
    {
        if (results.Count >= ResultLimit)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("videoRenderer", out var renderer) &&
                TryParseVideoRenderer(renderer, results.Count + 1, out var item) &&
                videoIds.Add(item.VideoId))
            {
                results.Add(item);
                if (results.Count >= ResultLimit)
                    return;
            }

            foreach (var property in element.EnumerateObject())
                CollectVideoRenderers(property.Value, results, videoIds);
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return;

        foreach (var child in element.EnumerateArray())
            CollectVideoRenderers(child, results, videoIds);
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var part in path)
            if (!value.TryGetProperty(part, out value))
                return false;

        return true;
    }

    private static bool TryParseVideoRenderer(JsonElement renderer, int rank, out YouTubeSearchItem item)
    {
        item = null!;
        if (!renderer.TryGetProperty("videoId", out var videoIdElement))
            return false;

        var videoId = videoIdElement.GetString();
        var title = GetText(renderer, "title");
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
            return false;

        var channelTitle = GetText(renderer, "ownerText");
        var channelId = TryGetNestedString(renderer, "ownerText", "runs", 0,
            "navigationEndpoint", "browseEndpoint", "browseId");
        var duration = ParseDuration(GetText(renderer, "lengthText"));
        var thumbnailUrl = GetLastThumbnailUrl(renderer);
        var viewsText = GetText(renderer, "viewCountText");
        if (string.IsNullOrWhiteSpace(viewsText))
            viewsText = GetText(renderer, "shortViewCountText");

        item = new YouTubeSearchItem(
            rank,
            videoId,
            title,
            channelTitle,
            channelId,
            duration,
            thumbnailUrl,
            ParseViewCount(viewsText),
            HasVerifiedOwnerBadge(renderer));
        return true;
    }

    private static string GetText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var text))
            return "";
        if (text.TryGetProperty("simpleText", out var simpleText))
            return simpleText.GetString() ?? "";
        if (!text.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return "";

        return string.Concat(runs.EnumerateArray()
            .Select(run => run.TryGetProperty("text", out var value) ? value.GetString() : ""));
    }

    private static string? TryGetNestedString(JsonElement element, string property, string arrayProperty,
        int index, params string[] path)
    {
        if (!element.TryGetProperty(property, out var current) ||
            !current.TryGetProperty(arrayProperty, out current) ||
            current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index)
            return null;

        current = current[index];
        foreach (var part in path)
            if (!current.TryGetProperty(part, out current))
                return null;

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string GetLastThumbnailUrl(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("thumbnail", out var thumbnail) ||
            !thumbnail.TryGetProperty("thumbnails", out var thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
            return "";

        var url = "";
        foreach (var item in thumbnails.EnumerateArray())
            if (item.TryGetProperty("url", out var value))
                url = value.GetString() ?? url;
        return url;
    }

    private static bool HasVerifiedOwnerBadge(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("ownerBadges", out var badges) || badges.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var badge in badges.EnumerateArray())
        {
            if (!badge.TryGetProperty("metadataBadgeRenderer", out var metadata) ||
                !metadata.TryGetProperty("style", out var style))
                continue;

            if (style.GetString()?.Contains("VERIFIED", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    private static TimeSpan ParseDuration(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _)))
            return TimeSpan.Zero;

        var numbers = parts.Select(int.Parse).ToArray();
        return numbers.Length == 3
            ? new TimeSpan(numbers[0], numbers[1], numbers[2])
            : new TimeSpan(0, numbers[0], numbers[1]);
    }

    internal static long? ParseViewCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"(?<number>[\d.,]+)\s*(?<suffix>[KMB])?", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var numberText = match.Groups["number"].Value;
        var suffix = match.Groups["suffix"].Value.ToUpperInvariant();
        if (suffix.Length == 0)
        {
            var digits = new string(numberText.Where(char.IsDigit).ToArray());
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var exact)
                ? exact
                : null;
        }

        if (!double.TryParse(numberText.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
            return null;

        var multiplier = suffix switch
        {
            "K" => 1_000d,
            "M" => 1_000_000d,
            "B" => 1_000_000_000d,
            _ => 1d
        };
        return checked((long)Math.Round(number * multiplier));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _pipelineGate.Dispose();
    }

    private sealed class SearchOperation
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _waiters;

        public SearchOperation(Func<CancellationToken, Task<IReadOnlyList<YouTubeSearchItem>>> action)
        {
            Results = action(_cancellation.Token);
            _ = Results.ContinueWith(
                _ => _cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<IReadOnlyList<YouTubeSearchItem>> Results { get; }

        public void AddWaiter() => Interlocked.Increment(ref _waiters);

        public int ReleaseWaiter() => Interlocked.Decrement(ref _waiters);

        public void Cancel()
        {
            try
            {
                if (!Results.IsCompleted)
                    _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation completed between the check and cancellation.
            }
        }
    }
}

public sealed class YouTubeSearchRateLimitedException(int statusCode)
    : Exception($"YouTube search returned HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
}

public sealed class YouTubeSearchCircuitOpenException(DateTimeOffset blockedUntil)
    : Exception("YouTube search circuit breaker is open.")
{
    public DateTimeOffset BlockedUntil { get; } = blockedUntil;
}

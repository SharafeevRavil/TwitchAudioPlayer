using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace TwitchAudioPlayer.WPF.Services.ChatGpt;

public sealed record ChatGptYouTubeCandidate(
    string VideoId,
    int YouTubeRank,
    string Title,
    string Channel,
    long? Views,
    TimeSpan Duration);

public sealed record ChatGptDecision(string? SelectedVideoId, string Reason, bool FromCache);

public sealed class ChatGptResolverService : IDisposable
{
    private const string ChatGptJsVersion = "4.14.2";
    private static readonly TimeSpan DecisionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);
    private static readonly HttpClient HttpClient = new();

    private readonly ILogger _logger = Log.ForContext<ChatGptResolverService>();
    private readonly IUserSettingsManager _settingsManager;
    private readonly ConcurrentDictionary<Guid, ChatGptBrowserSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, DeepSeekBrowserSession> _deepSeekSessions = new();
    private readonly Dictionary<string, CachedDecision> _decisionCache = new(StringComparer.Ordinal);
    private readonly ChatGptAccountSettings _anonymousAccount = new()
    {
        Id = Guid.Empty,
        Name = "No account"
    };
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _activeRequestLock = new();
    private readonly object _cacheLock = new();
    private readonly string _decisionCachePath;
    private readonly string _profilesFolder;
    private readonly string _deepSeekProfilesFolder;
    private CancellationTokenSource? _activeRequest;
    private Task<string>? _chatGptJsTask;
    private DateTimeOffset _blockedUntil;
    private bool _disposed;

    public ChatGptResolverService(IUserSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        Status = $"{ActiveProviderName} resolver is idle";
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchAudioPlayer", "ChatGPT");
        _profilesFolder = Path.Combine(appData, "Profiles");
        _decisionCachePath = Path.Combine(appData, "youtube-decisions.json");
        _deepSeekProfilesFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchAudioPlayer", "DeepSeek", "Profiles");
        LoadDecisionCache();
    }

    public event EventHandler? StatusChanged;

    public string Status { get; private set; } = "AI resolver is idle";

    public string ActiveProviderName => GetProviderName(_settingsManager.Settings.ChatGptResolver.Provider);

    public bool IsEnabled => _settingsManager.Settings.ChatGptResolver.Enabled &&
                             GetActiveAccount() is not null;

    public async Task<ChatGptDecision?> ResolveAsync(
        string artist,
        string title,
        IReadOnlyList<ChatGptYouTubeCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        var account = GetActiveAccount();
        if (!settings.Enabled || account is null || candidates.Count == 0)
            return null;
        var provider = settings.Provider;
        var projectName = string.Empty;
        var providerOptionsKey = provider == AiResolverProvider.DeepSeekWeb
            ? $"search={settings.DeepSeekUseSearch};deepthink={settings.DeepSeekUseDeepThink}"
            : string.Empty;

        var topCandidates = candidates.Take(5).ToArray();
        var cacheKey = CreateDecisionKey(provider, providerOptionsKey, account.Id, projectName, artist, title, topCandidates);
        lock (_cacheLock)
        {
            if (_decisionCache.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.CreatedAt < DecisionLifetime)
            {
                return new ChatGptDecision(cached.SelectedVideoId, cached.Reason, true);
            }
        }

        if (DateTimeOffset.UtcNow < _blockedUntil)
            return null;

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(RequestTimeout);
        var started = false;

        try
        {
            await _requestGate.WaitAsync(requestCts.Token);
            try
            {
                // A prefetch for this exact track may have completed while this request was
                // waiting for the single browser chat slot. Re-check here to avoid sending the
                // same prompt twice and wasting a free-account message.
                lock (_cacheLock)
                {
                    if (_decisionCache.TryGetValue(cacheKey, out var queuedCached) &&
                        DateTimeOffset.UtcNow - queuedCached.CreatedAt < DecisionLifetime)
                    {
                        return new ChatGptDecision(
                            queuedCached.SelectedVideoId,
                            queuedCached.Reason,
                            true);
                    }
                }

                lock (_activeRequestLock)
                    _activeRequest = requestCts;
                started = true;
                requestCts.Token.ThrowIfCancellationRequested();
                var requestId = Guid.NewGuid().ToString("N");
                var prompt = BuildPrompt(requestId, artist, title, topCandidates);
                var providerName = GetProviderName(provider);
                SetStatus($"Starting {providerName} WebView and choosing a video for {artist} — {title}…");
                string response;
                if (provider == AiResolverProvider.DeepSeekWeb)
                {
                    response = await GetDeepSeekSession(account).SendAsync(
                        prompt,
                        requestId,
                        settings.DeepSeekUseSearch,
                        settings.DeepSeekUseDeepThink,
                        requestCts.Token);
                }
                else
                {
                    var session = GetSession(account);
                    response = await session.SendAsync(prompt, requestId, projectName,
                        GetChatGptJsAsync, requestCts.Token);
                }

                var decision = ParseDecision(response, requestId, topCandidates);

                lock (_cacheLock)
                    _decisionCache[cacheKey] = new CachedDecision(
                        DateTimeOffset.UtcNow, decision.SelectedVideoId, decision.Reason);
                _ = SaveDecisionCacheAsync();
                SetStatus(decision.SelectedVideoId is null
                    ? $"{providerName} kept VK audio: {decision.Reason}"
                    : $"{providerName} selected YouTube: {decision.Reason}");
                return decision;
            }
            finally
            {
                if (started)
                {
                    lock (_activeRequestLock)
                    {
                        if (ReferenceEquals(_activeRequest, requestCts))
                            _activeRequest = null;
                    }
                }

                _requestGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (started)
                await StopActiveGenerationAsync();
            SetStatus("Obsolete AI request stopped");
            throw;
        }
        catch (OperationCanceledException)
        {
            if (started)
            {
                await StopActiveGenerationAsync();
                _blockedUntil = DateTimeOffset.UtcNow + FailureCooldown;
                SetStatus("AI resolver did not answer in time; using local ranking");
            }
            else
            {
                SetStatus("Queued AI resolver request expired; using local ranking");
            }

            return null;
        }
        catch (Exception exception)
        {
            _blockedUntil = DateTimeOffset.UtcNow + FailureCooldown;
            _logger.Warning(exception, "AI resolver failed");
            SetStatus($"AI resolver unavailable: {exception.Message}");
            return null;
        }
    }

    public Task ShowAccountAsync(Guid accountId)
    {
        var account = GetAccount(accountId);
        return _settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb
            ? GetDeepSeekSession(account).ShowInteractiveAsync()
            : GetSession(account).ShowInteractiveAsync();
    }

    public async Task<bool> IsLoggedInAsync(Guid accountId)
    {
        try
        {
            var account = GetAccount(accountId);
            return _settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb
                ? await GetDeepSeekSession(account).IsLoggedInAsync()
                : await GetSession(account).IsLoggedInAsync();
        }
        catch (Exception exception)
        {
            _logger.Debug(exception, "Unable to check AI resolver login state");
            return false;
        }
    }

    public async Task LogoutAsync(Guid accountId)
    {
        var account = GetAccount(accountId);
        if (_settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb)
            account.DeepSeekConversationUrl = null;
        else
        {
            account.ConversationUrl = null;
            account.ConversationProjectName = null;
        }

        await _settingsManager.SaveSettingsSilentlyAsync();
        if (_settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb)
            await GetDeepSeekSession(account).LogoutAsync();
        else
            await GetSession(account).LogoutAsync();
        SetStatus($"Logged out from {account.Name} in {GetProviderName(_settingsManager.Settings.ChatGptResolver.Provider)}");
    }

    public async Task ReloginAsync(Guid accountId)
    {
        await LogoutAsync(accountId);
        await ShowAccountAsync(accountId);
        SetStatus($"Complete the {GetProviderName(_settingsManager.Settings.ChatGptResolver.Provider)} login in the opened window");
    }

    public async Task OpenProjectAsync(Guid accountId, string projectName)
    {
        var account = GetAccount(accountId);
        if (_settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb)
            account.DeepSeekConversationUrl = null;
        else
        {
            account.ConversationUrl = null;
            account.ConversationProjectName = null;
        }

        await _settingsManager.SaveSettingsSilentlyAsync();
        if (_settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb)
        {
            await GetDeepSeekSession(account).ShowInteractiveAsync();
            SetStatus("Opened DeepSeek");
            return;
        }

        var session = GetSession(account);
        await session.ShowInteractiveAsync();
        var effectiveProjectName = string.Empty;
        await session.OpenProjectAsync(effectiveProjectName, CancellationToken.None);
        SetStatus(string.IsNullOrWhiteSpace(effectiveProjectName)
            ? "Opened ChatGPT"
            : $"Opened project “{effectiveProjectName}”");
    }

    public async Task ResetConversationAsync(Guid accountId)
    {
        var account = GetAccount(accountId);
        if (_settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb)
            account.DeepSeekConversationUrl = null;
        else
        {
            account.ConversationUrl = null;
            account.ConversationProjectName = null;
        }

        await _settingsManager.SaveSettingsSilentlyAsync();
        SetStatus("The resolver will create a new chat on the next request");
    }

    public async Task ClearDecisionCacheAsync()
    {
        lock (_cacheLock)
            _decisionCache.Clear();

        try
        {
            if (File.Exists(_decisionCachePath))
                File.Delete(_decisionCachePath);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Failed to delete AI decision cache file");
        }

        SetStatus("AI decision cache was cleared");
        await Task.CompletedTask;
    }

    public void CloseAccount(Guid accountId)
    {
        if (_sessions.TryRemove(accountId, out var session))
            session.Dispose();
        if (_deepSeekSessions.TryRemove(accountId, out var deepSeekSession))
            deepSeekSession.Dispose();
    }

    public async Task StopActiveGenerationAsync()
    {
        var account = GetActiveAccount();
        if (account is null)
            return;
        if (_settingsManager.Settings.ChatGptResolver.Provider == AiResolverProvider.DeepSeekWeb)
        {
            if (_deepSeekSessions.TryGetValue(account.Id, out var deepSeekSession))
                await deepSeekSession.StopAsync();
        }
        else if (_sessions.TryGetValue(account.Id, out var session))
        {
            await session.StopAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_activeRequestLock)
        {
            _activeRequest?.Cancel();
            _activeRequest?.Dispose();
            _activeRequest = null;
        }

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        foreach (var session in _deepSeekSessions.Values)
            session.Dispose();
        _deepSeekSessions.Clear();
        _requestGate.Dispose();
    }

    private ChatGptAccountSettings? GetActiveAccount()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        if (settings.UseAnonymous)
            return _anonymousAccount;

        return settings.ActiveAccountId is { } id
            ? settings.Accounts.FirstOrDefault(account => account.Id == id)
            : null;
    }

    private ChatGptAccountSettings GetAccount(Guid accountId) =>
        accountId == Guid.Empty
            ? _anonymousAccount
            : _settingsManager.Settings.ChatGptResolver.Accounts.FirstOrDefault(account => account.Id == accountId) ??
        throw new InvalidOperationException("ChatGPT account is no longer configured");

    private ChatGptBrowserSession GetSession(ChatGptAccountSettings account) =>
        _sessions.GetOrAdd(account.Id, _ => new ChatGptBrowserSession(
            account,
            Path.Combine(_profilesFolder, account.Id.ToString("N")),
            _settingsManager));

    private DeepSeekBrowserSession GetDeepSeekSession(ChatGptAccountSettings account) =>
        _deepSeekSessions.GetOrAdd(account.Id, _ => new DeepSeekBrowserSession(
            account,
            Path.Combine(_deepSeekProfilesFolder, account.Id.ToString("N")),
            _settingsManager));

    private static string GetProviderName(AiResolverProvider provider) =>
        provider == AiResolverProvider.DeepSeekWeb ? "DeepSeek" : "ChatGPT";

    private async Task<string> GetChatGptJsAsync()
    {
        Task<string> download;
        lock (_activeRequestLock)
            download = _chatGptJsTask ??= DownloadChatGptJsAsync();
        try
        {
            return await download;
        }
        catch
        {
            lock (_activeRequestLock)
            {
                if (ReferenceEquals(_chatGptJsTask, download))
                    _chatGptJsTask = null;
            }

            throw;
        }
    }

    private static async Task<string> DownloadChatGptJsAsync()
    {
        var url = $"https://cdn.jsdelivr.net/npm/@kudoai/chatgpt.js@{ChatGptJsVersion}/dist/chatgpt.min.js";
        return await HttpClient.GetStringAsync(url);
    }

    private static string BuildPrompt(
        string requestId,
        string artist,
        string title,
        IReadOnlyList<ChatGptYouTubeCandidate> candidates)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You select a YouTube video to replace a VK audio track.");
        builder.AppendLine("Goal: avoid a static VK audio screen by choosing the best visual YouTube replacement, but only when it is the same track/version as the VK track.");
        builder.AppendLine("Use only the supplied candidates. YouTube relevance order, title, channel, views, and duration are all evidence to weigh together; none of them is absolute proof alone.");
        builder.AppendLine("A candidate title does not need to include the artist name. Exact song-title match plus the correct artist/official channel is strong evidence of the same track.");
        builder.AppendLine("VK metadata can be incomplete or shortened. If the VK title looks like a prefix/short form of an obvious official candidate title by the same artist (for example 'Figure' vs 'Figure 8'), do not reject it just because the YouTube title has an extra word/number. Treat it as likely same-track evidence when the top results consistently point to that fuller title.");
        builder.AppendLine("If your current chat/provider has web search enabled and you are unsure whether the VK title is shortened or whether the candidate is the same release, use search/knowledge to verify before returning null.");
        builder.AppendLine("Hard rule: never choose a different song, unrelated famous-artist bait, cover, live version, instrumental, remix, mashup, nightcore/slowed/sped-up edit, or dub unless the VK title explicitly asks for that exact version.");
        builder.AppendLine("If no candidate is likely to contain the same recording/version of the VK track, return null. Null is correct for different songs even if they are from the same artist/channel.");
        builder.AppendLine("If the same track/version is present, prefer candidates in this order:");
        builder.AppendLine("1) official music video / official video clip from artist, VEVO, label, or verified/obvious official channel;");
        builder.AppendLine("2) official lyric video, official visualizer, official audio with a real visualizer, or official live-action/animated clip;");
        builder.AppendLine("3) strong unofficial music video, fan video, AMV, GMV, or visual edit that appears to use the same original recording;");
        builder.AppendLine("4) official audio / official album-track upload with static or mostly static artwork;");
        builder.AppendLine("5) plain lyrics/audio upload only as a last resort, and only if title/artist/version and duration make it likely to be the same recording.");
        builder.AppendLine("Hard visual preference: if at least one candidate is a proper video (music video, official video, lyric video, visualizer, fan video, AMV, or other real-motion visual edit), prefer any such visual candidate over any 'official audio' or static album-art upload, even when the audio upload is on the artist's channel. Only choose an audio-only upload when there are no plausible visual replacements or when metadata strongly proves the audio upload is the exact same release and no visual exists.");
        builder.AppendLine("Treat uploads labeled 'official audio', 'album', or 'audio' as lower priority than any candidate that clearly has video content unless there is no reasonable visual candidate.");
        builder.AppendLine("If an audio upload is the only candidate from an official/verified channel, require very close duration match (within 10 seconds or less for short tracks) and strong title/artist evidence before picking it.");
        builder.AppendLine("Tie-breaker: an official music video or title containing 'official music video'/'official video' MUST beat an official audio/plain artist upload of the same song, even if the audio upload is from the artist channel or has a slightly closer duration.");
        builder.AppendLine("Tie-breaker: an official lyric video/visualizer MUST beat an official audio/plain artist upload of the same song unless there is strong evidence that the lyric/visualizer candidate is a different version.");
        builder.AppendLine("Penalty rule: if a candidate title or channel suggests 'audio', 'album', 'spotify', 'soundcloud', or explicitly 'audio' in metadata, subtract preference unless no visual candidate exists.");
        builder.AppendLine("Important visual rule: when two candidates are similarly likely to be the same recording, prefer the one that is clearly a real video/clip/AMV/visual edit over a static official audio or album-art upload. Official channel is strong evidence, but it does not automatically beat a good same-track video.");
        builder.AppendLine("Lyric video is acceptable when it is the same song/version, especially from the artist/label channel. Unofficial/fan uploads are acceptable when there is no better official visual candidate and the recording match is still likely.");
        builder.AppendLine("Use duration as a sanity signal, not a hard rule: music videos often have intros/outros; huge duration mismatch should make you suspicious unless the title clearly explains it.");
        builder.AppendLine("Views and channel authority are supporting signals: high views and official/artist/label channels increase confidence; tiny views or random channels reduce confidence but do not automatically reject a good same-track candidate.");
        builder.AppendLine("Return null only when the best candidate is probably the wrong track/version or all candidates are too weak to trust.");
        builder.AppendLine("Reply with one JSON object and nothing else:");
        builder.AppendLine("{\"requestId\":\"...\",\"selectedVideoId\":\"... or null\",\"reason\":\"short reason\"}");
        builder.AppendLine($"requestId: {requestId}");
        builder.AppendLine($"VK track: {artist} — {title}");
        builder.AppendLine("Candidates:");
        foreach (var candidate in candidates)
        {
            builder.Append(candidate.YouTubeRank).Append(". videoId=").Append(candidate.VideoId)
                .Append(" | ").Append(candidate.Title)
                .Append(" | channel=").Append(candidate.Channel)
                .Append(" | views=").Append(candidate.Views?.ToString() ?? "unknown")
                .Append(" | duration=").Append(candidate.Duration.ToString(@"m\:ss"))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static ChatGptDecision ParseDecision(
        string response,
        string requestId,
        IReadOnlyList<ChatGptYouTubeCandidate> candidates)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidDataException("AI resolver returned no JSON decision");

        var json = response[start..(end + 1)];
        string? selectedVideoId;
        string? reason;
        try
        {
            using var document = JsonDocument.Parse(EscapeControlCharactersInJsonStrings(json));
            var root = document.RootElement;
            if (!root.TryGetProperty("requestId", out var requestElement) ||
                requestElement.GetString() != requestId)
                throw new InvalidDataException("AI resolver returned an obsolete decision");
            if (!root.TryGetProperty("selectedVideoId", out var selectedElement))
                throw new InvalidDataException("AI resolver decision has no selectedVideoId");

            selectedVideoId = selectedElement.ValueKind == JsonValueKind.Null
                ? null
                : selectedElement.GetString();
            reason = root.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            (selectedVideoId, reason) = ParseLooseDecision(json, requestId);
        }

        if (selectedVideoId is not null && candidates.All(candidate => candidate.VideoId != selectedVideoId))
            throw new InvalidDataException("AI resolver selected a video outside the supplied list");

        return new ChatGptDecision(selectedVideoId, reason ?? "no explanation", false);
    }

    private static (string? SelectedVideoId, string? Reason) ParseLooseDecision(
        string json,
        string expectedRequestId)
    {
        var request = Regex.Match(json,
            "\\\"requestId\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"",
            RegexOptions.IgnoreCase);
        if (!request.Success || request.Groups["value"].Value != expectedRequestId)
            throw new InvalidDataException("AI resolver returned an obsolete or malformed decision");

        var selected = Regex.Match(json,
            "\\\"selectedVideoId\\\"\\s*:\\s*(?<value>null|\\\"[^\\\"]*\\\")",
            RegexOptions.IgnoreCase);
        if (!selected.Success)
            throw new InvalidDataException("AI resolver decision has no selectedVideoId");

        var selectedValue = selected.Groups["value"].Value;
        var selectedVideoId = selectedValue.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? null
            : selectedValue.Trim('"');

        string? reason = null;
        var reasonMatch = Regex.Match(json,
            "\\\"reason\\\"\\s*:\\s*(?<value>[\\s\\S]*)",
            RegexOptions.IgnoreCase);
        if (reasonMatch.Success)
        {
            reason = reasonMatch.Groups["value"].Value.Trim();
            reason = reason.TrimEnd('}').Trim().TrimEnd(',').Trim();
            if (reason.Length >= 2 && reason[0] == '"' && reason[^1] == '"')
                reason = reason[1..^1];
            reason = reason
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
                .Trim();
        }

        return (selectedVideoId, reason);
    }

    private static string EscapeControlCharactersInJsonStrings(string json)
    {
        var builder = new StringBuilder(json.Length + 16);
        var insideString = false;
        var escaped = false;
        foreach (var character in json)
        {
            if (insideString)
            {
                if (escaped)
                {
                    escaped = false;
                    builder.Append(character);
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    builder.Append(character);
                    continue;
                }

                if (character == '"')
                {
                    insideString = false;
                    builder.Append(character);
                    continue;
                }

                switch (character)
                {
                    case '\n': builder.Append("\\n"); continue;
                    case '\r': builder.Append("\\r"); continue;
                    case '\t': builder.Append("\\t"); continue;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                            continue;
                        }
                        break;
                }
            }
            else if (character == '"')
            {
                insideString = true;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string CreateDecisionKey(
        AiResolverProvider provider,
        string providerOptionsKey,
        Guid accountId,
        string projectName,
        string artist,
        string title,
        IEnumerable<ChatGptYouTubeCandidate> candidates)
    {
        var value = $"{provider}|{providerOptionsKey}|{accountId:N}|{projectName}|{artist}|{title}|" +
                    string.Join(',', candidates.Select(candidate => candidate.VideoId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void LoadDecisionCache()
    {
        try
        {
            if (!File.Exists(_decisionCachePath))
                return;
            var state = JsonSerializer.Deserialize<Dictionary<string, CachedDecision>>(
                File.ReadAllText(_decisionCachePath));
            if (state is null)
                return;
            foreach (var pair in state)
                _decisionCache[pair.Key] = pair.Value;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Failed to load ChatGPT decision cache");
        }
    }

    private async Task SaveDecisionCacheAsync()
    {
        try
        {
            Dictionary<string, CachedDecision> snapshot;
            lock (_cacheLock)
                snapshot = new Dictionary<string, CachedDecision>(_decisionCache, StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(_decisionCachePath)!);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_decisionCachePath, json);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Failed to save ChatGPT decision cache");
        }
    }

    private void SetStatus(string value)
    {
        Status = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record CachedDecision(DateTimeOffset CreatedAt, string? SelectedVideoId, string Reason);

    private sealed class DeepSeekBrowserSession : IDisposable
    {
        private const string DeepSeekUrl = "https://chat.deepseek.com/";
        private readonly ChatGptAccountSettings _account;
        private readonly string _profileFolder;
        private readonly IUserSettingsManager _settingsManager;
        private readonly ILogger _logger = Log.ForContext<DeepSeekBrowserSession>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserResult>> _pending = new();
        private Window? _window;
        private WebView2? _webView;
        private Task? _initializationTask;
        private bool _interactive;
        private bool _disposing;

        public DeepSeekBrowserSession(
            ChatGptAccountSettings account,
            string profileFolder,
            IUserSettingsManager settingsManager)
        {
            _account = account;
            _profileFolder = profileFolder;
            _settingsManager = settingsManager;
        }

        public async Task ShowInteractiveAsync()
        {
            await EnsureInitializedAsync();
            await OnUiAsync(() =>
            {
                if (_window is null)
                    return;
                _interactive = true;
                _window.Title = $"DeepSeek — {_account.Name}";
                _window.ShowInTaskbar = true;
                _window.Opacity = 1;
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.WindowState = WindowState.Normal;
                MoveWindowToWorkAreaCenter(_window);
                _window.Show();
                _window.Topmost = true;
                _window.Topmost = false;
                _window.Focus();
                _window.Activate();
            });
        }

        public async Task<bool> IsLoggedInAsync()
        {
            await EnsureInitializedAsync();
            return await HasComposerAsync();
        }

        public async Task LogoutAsync()
        {
            await EnsureInitializedAsync();
            await NavigateAsync(new Uri("https://chat.deepseek.com/sign_out"), CancellationToken.None);
            await ShowInteractiveAsync();
        }

        public async Task<string> SendAsync(
            string prompt,
            string requestId,
            bool useSearch,
            bool useDeepThink,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync();
            var hideAfterRequest = await EnsureBackgroundActiveAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(_account.DeepSeekConversationUrl) &&
                    Uri.TryCreate(_account.DeepSeekConversationUrl, UriKind.Absolute, out var conversationUri))
                {
                    if (!string.Equals(await GetLocationAsync(), conversationUri.AbsoluteUri,
                            StringComparison.OrdinalIgnoreCase))
                        await NavigateAsync(conversationUri, cancellationToken);
                }
                else
                {
                    await NavigateAsync(new Uri(DeepSeekUrl), cancellationToken);
                }

                await WaitForComposerAsync(cancellationToken);
                await EnsureBridgeAsync();

                var completion = new TaskCompletionSource<BrowserResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pending.TryAdd(requestId, completion))
                    throw new InvalidOperationException("Duplicate DeepSeek request id");

                try
                {
                    var idJson = JsonSerializer.Serialize(requestId);
                    var promptJson = JsonSerializer.Serialize(prompt);
                    var optionsJson = JsonSerializer.Serialize(new
                    {
                        search = useSearch,
                        deepThink = useDeepThink
                    });
                    await ExecuteScriptAsync($"window.__tapDeepSeekResolver.run({idJson}, {promptJson}, {optionsJson})");
                    using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                    var result = await completion.Task;
                    if (!result.Ok)
                        throw new InvalidOperationException(result.Error ?? "DeepSeek browser request failed");

                    if (Uri.TryCreate(result.Url, UriKind.Absolute, out var resultUri) &&
                        resultUri.Host.Equals("chat.deepseek.com", StringComparison.OrdinalIgnoreCase))
                    {
                        _account.DeepSeekConversationUrl = resultUri.AbsoluteUri;
                        await _settingsManager.SaveSettingsSilentlyAsync();
                    }

                    return result.Response ?? string.Empty;
                }
                finally
                {
                    _pending.TryRemove(requestId, out _);
                }
            }
            finally
            {
                if (hideAfterRequest)
                    await HideBackgroundWindowAsync();
            }
        }

        public async Task StopAsync()
        {
            if (_webView?.CoreWebView2 is null)
                return;
            try
            {
                await ExecuteScriptAsync("window.__tapDeepSeekResolver?.stop?.(); true");
            }
            catch
            {
                // The page may be navigating while an obsolete request is being stopped.
            }
        }

        public void Dispose()
        {
            _disposing = true;
            foreach (var completion in _pending.Values)
                completion.TrySetCanceled();
            _pending.Clear();
            if (Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                return;

            void CloseWindow()
            {
                _webView?.Dispose();
                _window?.Close();
                _webView = null;
                _window = null;
            }

            if (dispatcher.CheckAccess())
                CloseWindow();
            else
                dispatcher.Invoke(CloseWindow);
        }

        private Task EnsureInitializedAsync()
        {
            _initializationTask ??= InitializeAsync();
            return _initializationTask;
        }

        private async Task InitializeAsync()
        {
            await OnUiAsync(async () =>
            {
                Directory.CreateDirectory(_profileFolder);
                var window = new Window
                {
                    Title = $"DeepSeek — {_account.Name}",
                    Width = 1100,
                    Height = 780,
                    MinWidth = 760,
                    MinHeight = 520,
                    Background = System.Windows.Media.Brushes.Black,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    Opacity = 0
                };
                var webView = new WebView2();
                window.Content = webView;
                window.Closing += (_, args) =>
                {
                    if (_disposing || Application.Current.Dispatcher.HasShutdownStarted)
                        return;
                    args.Cancel = true;
                    _interactive = false;
                    window.Hide();
                };

                _window = window;
                _webView = webView;
                window.Show();
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                await NavigateAsync(new Uri(DeepSeekUrl), CancellationToken.None);
                window.Hide();
            });
        }

        private async Task<bool> EnsureBackgroundActiveAsync()
        {
            return await OnUiAsync(() =>
            {
                if (_window is null || _interactive)
                    return false;
                _window.ShowInTaskbar = false;
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Left = -20000;
                _window.Top = -20000;
                _window.Opacity = 0;
                _window.Show();
                return true;
            });
        }

        private Task HideBackgroundWindowAsync() => OnUiAsync(() =>
        {
            if (!_interactive)
                _window?.Hide();
        });

        private async Task EnsureBridgeAsync()
        {
            await ExecuteScriptAsync("""
                (() => {
                    if (window.__tapDeepSeekResolver) return true;
                    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
                    const visible = element => {
                        if (!element) return false;
                        const style = getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
                    };
                    const textOf = element => [
                        element?.getAttribute?.('aria-label'),
                        element?.getAttribute?.('title'),
                        element?.innerText,
                        element?.textContent
                    ].filter(Boolean).join(' ').toLowerCase();
                    const isActiveToggle = element => {
                        const attributes = [
                            element?.getAttribute?.('aria-pressed'),
                            element?.getAttribute?.('aria-checked'),
                            element?.getAttribute?.('data-state'),
                            element?.getAttribute?.('data-active')
                        ].filter(Boolean).join(' ').toLowerCase();
                        const classes = String(element?.className || '').toLowerCase();
                        return /\btrue\b|checked|selected|active|on/.test(attributes) ||
                            /\b(active|selected|checked)\b/.test(classes);
                    };
                    const findToggleButton = patterns => {
                        const nodes = [...document.querySelectorAll('button, [role="button"], label')].filter(visible);
                        return nodes.find(node => patterns.some(pattern => pattern.test(textOf(node)))) || null;
                    };
                    const setToggle = async (patterns, desired) => {
                        const button = findToggleButton(patterns);
                        if (!button) return false;
                        const active = isActiveToggle(button);
                        if ((desired && !active) || (!desired && active)) {
                            button.click();
                            await sleep(350);
                        }
                        return true;
                    };
                    const configureOptions = async options => {
                        const value = options || {};
                        await setToggle([/search/, /web\s*search/, /internet/], Boolean(value.search));
                        await setToggle([/deep\s*think/, /deepthink/, /\br1\b/, /reason/], Boolean(value.deepThink));
                    };
                    const findComposer = () => {
                        const nodes = [...document.querySelectorAll('textarea, input[type="text"], [contenteditable="true"], div[role="textbox"]')];
                        return nodes
                            .filter(node => visible(node) && !node.disabled && !node.readOnly)
                            .map(node => ({ node, rect: node.getBoundingClientRect() }))
                            .sort((a, b) => b.rect.bottom - a.rect.bottom || b.rect.width * b.rect.height - a.rect.width * a.rect.height)[0]?.node || null;
                    };
                    const setComposerText = (composer, value) => {
                        composer.focus();
                        if (composer instanceof HTMLTextAreaElement || composer instanceof HTMLInputElement) {
                            const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(composer), 'value')?.set;
                            if (setter) setter.call(composer, value);
                            else composer.value = value;
                        } else {
                            composer.textContent = value;
                        }
                        composer.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: value }));
                        composer.dispatchEvent(new Event('change', { bubbles: true }));
                    };
                    let runGeneration = 0;
                    const composerText = composer => {
                        if (!composer) return '';
                        if (composer instanceof HTMLTextAreaElement || composer instanceof HTMLInputElement)
                            return String(composer.value || '');
                        return String(composer.innerText || composer.textContent || '');
                    };
                    const reportProgress = (id, stage, details) => chrome.webview.postMessage({
                        source: 'tap-deepseek', kind: 'progress', id, stage,
                        details: String(details || ''), url: location.href
                    });
                    const findSendButton = composer => {
                        const composerRect = composer.getBoundingClientRect();
                        let container = composer.parentElement;
                        for (let depth = 0; depth < 9 && container; depth++, container = container.parentElement) {
                            const buttons = [...container.querySelectorAll('button, [role="button"]')]
                                .filter(button => {
                                    if (!visible(button) || button.disabled || button.getAttribute('aria-disabled') === 'true')
                                        return false;
                                    const rect = button.getBoundingClientRect();
                                    return rect.left >= composerRect.right - 240 &&
                                        rect.right <= composerRect.right + 100 &&
                                        rect.top >= composerRect.top - 24 &&
                                        rect.bottom <= composerRect.bottom + 64;
                                });
                            if (buttons.length > 0) {
                                return buttons.sort((left, right) => {
                                    const a = left.getBoundingClientRect();
                                    const b = right.getBoundingClientRect();
                                    return b.right - a.right || b.bottom - a.bottom;
                                })[0];
                            }
                        }
                        return null;
                    };
                    const clickSend = composer => {
                        const button = findSendButton(composer);
                        if (button) {
                            button.focus();
                            for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup'])
                                button.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
                            button.click();
                            const rect = button.getBoundingClientRect();
                            return `local-button:x=${Math.round(rect.left)},y=${Math.round(rect.top)},label=${textOf(button).slice(0, 80)}`;
                        }
                        return 'no-button';
                    };
                    const pressEnter = composer => {
                        for (const type of ['keydown', 'keypress', 'keyup'])
                            composer.dispatchEvent(new KeyboardEvent(type, {
                                key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
                                bubbles: true, cancelable: true
                            }));
                    };
                    const wasSubmitted = (id, composer) => {
                        if (composer.isConnected && visible(composer))
                            return !composerText(composer).includes(id);
                        const replacement = findComposer();
                        return Boolean(replacement) && !composerText(replacement).includes(id);
                    };
                    const submitPrompt = async (id, composer, generation) => {
                        for (let attempt = 1; attempt <= 3; attempt++) {
                            if (generation !== runGeneration) throw new Error('DeepSeek request was superseded');
                            const current = findComposer() || composer;
                            let method;
                            if (attempt === 1) {
                                method = clickSend(current);
                            } else if (attempt === 2) {
                                const form = current.closest('form');
                                if (form?.requestSubmit) {
                                    form.requestSubmit();
                                    method = 'requestSubmit';
                                } else method = clickSend(current);
                            } else if (attempt === 3) {
                                pressEnter(current);
                                method = 'enter';
                            }
                            reportProgress(id, `submit-attempt-${attempt}`, method);
                            for (let check = 0; check < 8; check++) {
                                await sleep(250);
                                if (wasSubmitted(id, current)) {
                                    reportProgress(id, 'submitted', method);
                                    return;
                                }
                            }
                        }
                        throw new Error('DeepSeek composer kept the prompt after 3 send attempts');
                    };
                    const findDecision = id => {
                        const text = document.body?.innerText || '';
                        const matches = text.match(/\{[\s\S]*?\}/g) || [];
                        for (let index = matches.length - 1; index >= 0; index--) {
                            const candidate = matches[index];
                            if (candidate.includes(id) && candidate.includes('selectedVideoId')) return candidate;
                        }
                        return '';
                    };
                    const waitForDecision = async (id, generation) => {
                        for (let attempt = 0; attempt < 90; attempt++) {
                            if (generation !== runGeneration) throw new Error('DeepSeek request was superseded');
                            const decision = findDecision(id);
                            if (decision) return decision;
                            await sleep(1000);
                        }
                        throw new Error('DeepSeek did not return a JSON decision');
                    };
                    window.__tapDeepSeekResolver = {
                        stop: () => {
                            runGeneration++;
                            const buttons = [...document.querySelectorAll('button')].filter(button => visible(button));
                            const stop = buttons.find(button => /stop|cancel|停止|останов/i.test(textOf(button)));
                            if (stop) stop.click();
                            const composer = findComposer();
                            if (composer && /requestId\s*:/i.test(composerText(composer)))
                                setComposerText(composer, '');
                        },
                        run: async (id, prompt, options) => {
                            const generation = ++runGeneration;
                            try {
                                const composer = findComposer();
                                if (!composer) throw new Error('DeepSeek composer was not found');
                                await configureOptions(options);
                                setComposerText(composer, prompt);
                                reportProgress(id, 'composer-filled', '');
                                await sleep(400);
                                await submitPrompt(id, composer, generation);
                                const response = await waitForDecision(id, generation);
                                reportProgress(id, 'decision-received', '');
                                chrome.webview.postMessage({ source: 'tap-deepseek', id, ok: true, response: String(response || ''), url: location.href });
                            } catch (error) {
                                chrome.webview.postMessage({ source: 'tap-deepseek', id, ok: false, error: String(error?.message || error), url: location.href });
                            }
                        }
                    };
                    return true;
                })()
                """);
        }

        private async Task WaitForComposerAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await HasComposerAsync())
                    return;
                await Task.Delay(400, cancellationToken);
            }

            throw new InvalidOperationException("DeepSeek is not logged in or has no chat composer");
        }

        private async Task<bool> HasComposerAsync() =>
            await ExecuteScriptAsync("""
                (() => {
                    const visible = element => {
                        if (!element) return false;
                        const style = getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
                    };
                    const nodes = [...document.querySelectorAll('textarea, input[type="text"], [contenteditable="true"], div[role="textbox"]')];
                    return nodes.some(node => visible(node) && !node.disabled && !node.readOnly);
                })()
                """) == "true";

        private async Task<string> GetLocationAsync()
        {
            var raw = await ExecuteScriptAsync("location.href");
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }

        private async Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
        {
            await OnUiAsync(async () =>
            {
                if (_webView is null)
                    throw new InvalidOperationException("DeepSeek WebView2 is not initialized");
                if (string.Equals(_webView.Source?.AbsoluteUri, uri.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    _webView.NavigationCompleted -= OnNavigationCompleted;
                    if (args.IsSuccess)
                        completion.TrySetResult();
                    else
                        completion.TrySetException(new InvalidOperationException(
                            $"DeepSeek navigation failed: {args.WebErrorStatus}"));
                }

                _webView.NavigationCompleted += OnNavigationCompleted;
                _webView.Source = uri;
                using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                try
                {
                    await completion.Task;
                }
                finally
                {
                    _webView.NavigationCompleted -= OnNavigationCompleted;
                }
            });
        }

        private Task<string> ExecuteScriptAsync(string script) => OnUiAsync(async () =>
        {
            if (_webView?.CoreWebView2 is null)
                throw new InvalidOperationException("DeepSeek WebView2 is not initialized");
            return await _webView.ExecuteScriptAsync(script);
        });

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("source", out var source) || source.GetString() != "tap-deepseek" ||
                    !root.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id)
                    return;

                if (root.TryGetProperty("kind", out var kind) && kind.GetString() == "progress")
                {
                    _logger.Information("DeepSeek request {RequestId}: {Stage} {Details}",
                        id,
                        root.TryGetProperty("stage", out var stage) ? stage.GetString() : "progress",
                        root.TryGetProperty("details", out var details) ? details.GetString() : null);
                    return;
                }

                if (!_pending.TryGetValue(id, out var completion))
                    return;

                completion.TrySetResult(new BrowserResult(
                    root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                    root.TryGetProperty("response", out var response) ? response.GetString() : null,
                    root.TryGetProperty("error", out var error) ? error.GetString() : null,
                    root.TryGetProperty("url", out var url) ? url.GetString() : null));
            }
            catch (Exception exception)
            {
                foreach (var completion in _pending.Values)
                    completion.TrySetException(exception);
            }
        }

        private static Task OnUiAsync(Action action)
        {
            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private static Task<T> OnUiAsync<T>(Func<T> action)
        {
            var dispatcher = Application.Current.Dispatcher;
            return dispatcher.CheckAccess()
                ? Task.FromResult(action())
                : dispatcher.InvokeAsync(action).Task;
        }

        private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
        {
            var dispatcher = Application.Current.Dispatcher;
            return dispatcher.CheckAccess()
                ? action()
                : dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private static Task OnUiAsync(Func<Task> action)
        {
            var dispatcher = Application.Current.Dispatcher;
            return dispatcher.CheckAccess()
                ? action()
                : dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private static void MoveWindowToWorkAreaCenter(Window window)
        {
            var workArea = SystemParameters.WorkArea;
            var width = double.IsNaN(window.Width) || window.Width <= 0 ? 1100 : window.Width;
            var height = double.IsNaN(window.Height) || window.Height <= 0 ? 780 : window.Height;
            window.Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
            window.Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        }

        private sealed record BrowserResult(bool Ok, string? Response, string? Error, string? Url);
    }

    private sealed class ChatGptBrowserSession : IDisposable
    {
        private const string ChatGptUrl = "https://chatgpt.com/";
        private readonly ChatGptAccountSettings _account;
        private readonly string _profileFolder;
        private readonly IUserSettingsManager _settingsManager;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserResult>> _pending = new();
        private Window? _window;
        private WebView2? _webView;
        private Task? _initializationTask;
        private bool _interactive;
        private bool _disposing;

        public ChatGptBrowserSession(
            ChatGptAccountSettings account,
            string profileFolder,
            IUserSettingsManager settingsManager)
        {
            _account = account;
            _profileFolder = profileFolder;
            _settingsManager = settingsManager;
        }

        public async Task ShowInteractiveAsync()
        {
            await EnsureInitializedAsync();
            await OnUiAsync(() =>
            {
                if (_window is null)
                    return;
                _interactive = true;
                _window.Title = $"ChatGPT — {_account.Name}";
                _window.ShowInTaskbar = true;
                _window.Opacity = 1;
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.WindowState = WindowState.Normal;
                MoveWindowToWorkAreaCenter(_window);
                _window.Show();
                _window.Topmost = true;
                _window.Topmost = false;
                _window.Focus();
                _window.Activate();
            });
        }

        public async Task<bool> IsLoggedInAsync()
        {
            await EnsureInitializedAsync();
            var raw = await ExecuteScriptAsync(
                "Boolean(document.getElementById('prompt-textarea') || document.querySelector('[data-testid=\\\"composer\\\"]'))");
            return raw == "true";
        }

        public async Task LogoutAsync()
        {
            await EnsureInitializedAsync();
            await NavigateAsync(new Uri("https://chatgpt.com/auth/logout"), CancellationToken.None);
            await ShowInteractiveAsync();
        }

        public async Task OpenProjectAsync(string projectName, CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync();
            await NavigateAsync(new Uri(ChatGptUrl), cancellationToken);
            if (!string.IsNullOrWhiteSpace(projectName))
                await SelectProjectAsync(projectName, cancellationToken);
        }

        public async Task<string> SendAsync(
            string prompt,
            string requestId,
            string projectName,
            Func<Task<string>> getLibraryAsync,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync();
            var hideAfterRequest = await EnsureBackgroundActiveAsync();
            try
            {
                if (string.Equals(_account.ConversationProjectName ?? string.Empty,
                        projectName.Trim(), StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(_account.ConversationUrl) &&
                    Uri.TryCreate(_account.ConversationUrl, UriKind.Absolute, out var conversationUri))
                {
                    if (!string.Equals(await GetLocationAsync(), conversationUri.AbsoluteUri,
                            StringComparison.OrdinalIgnoreCase))
                        await NavigateAsync(conversationUri, cancellationToken);
                }
                else
                {
                    await NavigateAsync(new Uri(ChatGptUrl), cancellationToken);
                    if (!string.IsNullOrWhiteSpace(projectName))
                        await SelectProjectAsync(projectName, cancellationToken);
                }

                await WaitForComposerAsync(cancellationToken);
                await EnsureChatGptJsAsync(await getLibraryAsync());
                await EnsureBridgeAsync();

                var completion = new TaskCompletionSource<BrowserResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pending.TryAdd(requestId, completion))
                    throw new InvalidOperationException("Duplicate ChatGPT request id");

                try
                {
                    var idJson = JsonSerializer.Serialize(requestId);
                    var promptJson = JsonSerializer.Serialize(prompt);
                    await ExecuteScriptAsync($"window.__tapChatGptResolver.run({idJson}, {promptJson})");
                    using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                    var result = await completion.Task;
                    if (!result.Ok)
                        throw new InvalidOperationException(result.Error ?? "ChatGPT browser request failed");

                    if (Uri.TryCreate(result.Url, UriKind.Absolute, out var resultUri) &&
                        resultUri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
                        resultUri.AbsolutePath.Contains("/c/", StringComparison.OrdinalIgnoreCase))
                    {
                        _account.ConversationUrl = resultUri.AbsoluteUri;
                        _account.ConversationProjectName = projectName.Trim();
                        await _settingsManager.SaveSettingsSilentlyAsync();
                    }

                    return result.Response ?? string.Empty;
                }
                finally
                {
                    _pending.TryRemove(requestId, out _);
                }
            }
            finally
            {
                if (hideAfterRequest)
                    await HideBackgroundWindowAsync();
            }
        }

        public async Task StopAsync()
        {
            if (_webView?.CoreWebView2 is null)
                return;
            try
            {
                await ExecuteScriptAsync(
                    "window.__tapChatGptResolver?.stop?.(); try { chatgpt?.stop?.(); } catch {} true");
            }
            catch
            {
                // The page may be navigating while an obsolete request is being stopped.
            }
        }

        public void Dispose()
        {
            _disposing = true;
            foreach (var completion in _pending.Values)
                completion.TrySetCanceled();
            _pending.Clear();
            if (Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                return;
            void CloseWindow()
            {
                _webView?.Dispose();
                _window?.Close();
                _webView = null;
                _window = null;
            }

            if (dispatcher.CheckAccess())
                CloseWindow();
            else
                dispatcher.Invoke(CloseWindow);
        }

        private Task EnsureInitializedAsync()
        {
            _initializationTask ??= InitializeAsync();
            return _initializationTask;
        }

        private async Task InitializeAsync()
        {
            await OnUiAsync(async () =>
            {
                Directory.CreateDirectory(_profileFolder);
                var window = new Window
                {
                    Title = $"ChatGPT — {_account.Name}",
                    Width = 1100,
                    Height = 780,
                    MinWidth = 760,
                    MinHeight = 520,
                    Background = System.Windows.Media.Brushes.Black,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    Opacity = 0
                };
                var webView = new WebView2();
                window.Content = webView;
                window.Closing += (_, args) =>
                {
                    if (_disposing || Application.Current.Dispatcher.HasShutdownStarted)
                        return;
                    args.Cancel = true;
                    _interactive = false;
                    window.Hide();
                };

                _window = window;
                _webView = webView;
                window.Show();
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                await NavigateAsync(new Uri(ChatGptUrl), CancellationToken.None);
                window.Hide();
            });
        }

        private async Task<bool> EnsureBackgroundActiveAsync()
        {
            return await OnUiAsync(() =>
            {
                if (_window is null || _interactive)
                    return false;
                _window.ShowInTaskbar = false;
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Left = -20000;
                _window.Top = -20000;
                _window.Opacity = 0;
                _window.Show();
                return true;
            });
        }

        private Task HideBackgroundWindowAsync() => OnUiAsync(() =>
        {
            if (!_interactive)
                _window?.Hide();
        });

        private async Task SelectProjectAsync(string projectName, CancellationToken cancellationToken)
        {
            await WaitForPageAsync(cancellationToken);
            var projectJson = JsonSerializer.Serialize(projectName.Trim());
            var raw = await ExecuteScriptAsync($$"""
                (() => {
                    const wanted = {{projectJson}}.trim().toLocaleLowerCase();
                    const nodes = [...document.querySelectorAll('a, button')];
                    const project = nodes.find(node => (node.innerText || node.textContent || '').trim().toLocaleLowerCase() === wanted);
                    if (!project) return JSON.stringify({ ok: false, error: 'Project not found in the sidebar' });
                    setTimeout(() => project.click(), 0);
                    return JSON.stringify({ ok: true });
                })()
                """);
            var result = DeserializeScriptJson<ProjectNavigationResult>(raw);
            if (result?.Ok != true)
                throw new InvalidOperationException(result?.Error ?? "ChatGPT project was not found");

            await Task.Delay(1500, cancellationToken);
            if (!await HasComposerAsync())
            {
                await ExecuteScriptAsync("""
                    (() => {
                        const labels = ['new chat', 'new chat in', 'новый чат', 'новый чат в проекте'];
                        const nodes = [...document.querySelectorAll('main a, main button, [role=main] a, [role=main] button')];
                        const button = nodes.find(node => labels.some(label => (node.innerText || node.getAttribute('aria-label') || '').trim().toLocaleLowerCase().includes(label)));
                        if (button) { setTimeout(() => button.click(), 0); return true; }
                        return false;
                    })()
                    """);
                await Task.Delay(1000, cancellationToken);
            }

            await WaitForComposerAsync(cancellationToken);
        }

        private async Task EnsureChatGptJsAsync(string library)
        {
            if (await ExecuteScriptAsync("typeof chatgpt !== 'undefined'") == "true")
                return;
            await ExecuteScriptAsync(library);
            if (await ExecuteScriptAsync("typeof chatgpt !== 'undefined'") != "true")
                throw new InvalidOperationException("chatgpt.js failed to initialize");
        }

        private async Task EnsureBridgeAsync()
        {
            await ExecuteScriptAsync("""
                (() => {
                    if (window.__tapChatGptResolver) return true;
                    window.__tapChatGptResolver = {
                        stop: () => { try { chatgpt.stop(); } catch {} },
                        run: async (id, prompt) => {
                            try {
                                chatgpt.send(prompt);
                                await new Promise(resolve => setTimeout(resolve, 600));
                                await chatgpt.isIdle();
                                let response = '';
                                try {
                                    response = await chatgpt.getChatData('active', 'msg', 'chatgpt', 'latest');
                                } catch {
                                    const messages = [...document.querySelectorAll('[data-message-author-role="assistant"]')];
                                    response = messages.at(-1)?.innerText || '';
                                }
                                chrome.webview.postMessage({ source: 'tap-chatgpt', id, ok: true, response: String(response || ''), url: location.href });
                            } catch (error) {
                                chrome.webview.postMessage({ source: 'tap-chatgpt', id, ok: false, error: String(error?.message || error), url: location.href });
                            }
                        }
                    };
                    return true;
                })()
                """);
        }

        private async Task WaitForComposerAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await HasComposerAsync())
                    return;
                await Task.Delay(400, cancellationToken);
            }

            throw new InvalidOperationException("ChatGPT is not logged in or the project has no chat composer");
        }

        private async Task WaitForPageAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ExecuteScriptAsync("document.readyState === 'complete'") == "true")
                    return;
                await Task.Delay(250, cancellationToken);
            }
        }

        private async Task<bool> HasComposerAsync() =>
            await ExecuteScriptAsync(
                "Boolean(document.getElementById('prompt-textarea') || document.querySelector('[data-testid=\\\"composer\\\"]'))") == "true";

        private async Task<string> GetLocationAsync()
        {
            var raw = await ExecuteScriptAsync("location.href");
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }

        private async Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
        {
            await OnUiAsync(async () =>
            {
                if (_webView is null)
                    throw new InvalidOperationException("ChatGPT WebView2 is not initialized");
                if (string.Equals(_webView.Source?.AbsoluteUri, uri.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    _webView.NavigationCompleted -= OnNavigationCompleted;
                    if (args.IsSuccess)
                        completion.TrySetResult();
                    else
                        completion.TrySetException(new InvalidOperationException(
                            $"ChatGPT navigation failed: {args.WebErrorStatus}"));
                }

                _webView.NavigationCompleted += OnNavigationCompleted;
                _webView.Source = uri;
                using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                try
                {
                    await completion.Task;
                }
                finally
                {
                    _webView.NavigationCompleted -= OnNavigationCompleted;
                }
            });
        }

        private Task<string> ExecuteScriptAsync(string script) => OnUiAsync(async () =>
        {
            if (_webView?.CoreWebView2 is null)
                throw new InvalidOperationException("ChatGPT WebView2 is not initialized");
            return await _webView.ExecuteScriptAsync(script);
        });

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("source", out var source) || source.GetString() != "tap-chatgpt" ||
                    !root.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id ||
                    !_pending.TryGetValue(id, out var completion))
                    return;

                completion.TrySetResult(new BrowserResult(
                    root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                    root.TryGetProperty("response", out var response) ? response.GetString() : null,
                    root.TryGetProperty("error", out var error) ? error.GetString() : null,
                    root.TryGetProperty("url", out var url) ? url.GetString() : null));
            }
            catch (Exception exception)
            {
                foreach (var completion in _pending.Values)
                    completion.TrySetException(exception);
            }
        }

        private static T? DeserializeScriptJson<T>(string raw)
        {
            var json = JsonSerializer.Deserialize<string>(raw);
            return json is null ? default : JsonSerializer.Deserialize<T>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        private static Task OnUiAsync(Action action)
        {
            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private static Task<T> OnUiAsync<T>(Func<T> action)
        {
            var dispatcher = Application.Current.Dispatcher;
            return dispatcher.CheckAccess()
                ? Task.FromResult(action())
                : dispatcher.InvokeAsync(action).Task;
        }

        private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
        {
            var dispatcher = Application.Current.Dispatcher;
            return dispatcher.CheckAccess()
                ? action()
                : dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private static Task OnUiAsync(Func<Task> action)
        {
            var dispatcher = Application.Current.Dispatcher;
            return dispatcher.CheckAccess()
                ? action()
                : dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private static void MoveWindowToWorkAreaCenter(Window window)
        {
            var workArea = SystemParameters.WorkArea;
            var width = double.IsNaN(window.Width) || window.Width <= 0 ? 1100 : window.Width;
            var height = double.IsNaN(window.Height) || window.Height <= 0 ? 780 : window.Height;
            window.Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
            window.Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        }

        private sealed record BrowserResult(bool Ok, string? Response, string? Error, string? Url);
        private sealed class ProjectNavigationResult
        {
            public bool Ok { get; set; }
            public string? Error { get; set; }
        }
    }
}

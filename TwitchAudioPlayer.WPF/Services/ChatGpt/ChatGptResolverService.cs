using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private CancellationTokenSource? _activeRequest;
    private Task<string>? _chatGptJsTask;
    private DateTimeOffset _blockedUntil;
    private bool _disposed;

    public ChatGptResolverService(IUserSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchAudioPlayer", "ChatGPT");
        _profilesFolder = Path.Combine(appData, "Profiles");
        _decisionCachePath = Path.Combine(appData, "youtube-decisions.json");
        LoadDecisionCache();
    }

    public event EventHandler? StatusChanged;

    public string Status { get; private set; } = "ChatGPT resolver is idle";

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
        var projectName = string.Empty;

        var topCandidates = candidates.Take(5).ToArray();
        var cacheKey = CreateDecisionKey(account.Id, projectName, artist, title, topCandidates);
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

        CancellationTokenSource requestCts;
        CancellationTokenSource? previous;
        lock (_activeRequestLock)
        {
            previous = _activeRequest;
            requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(RequestTimeout);
            _activeRequest = requestCts;
        }

        previous?.Cancel();
        if (previous is not null)
            _ = StopActiveGenerationAsync();

        try
        {
            await _requestGate.WaitAsync(requestCts.Token);
            try
            {
                requestCts.Token.ThrowIfCancellationRequested();
                var requestId = Guid.NewGuid().ToString("N");
                var prompt = BuildPrompt(requestId, artist, title, topCandidates);
                var session = GetSession(account);
                SetStatus($"ChatGPT is choosing a video for {artist} — {title}…");
                var response = await session.SendAsync(prompt, requestId, projectName,
                    GetChatGptJsAsync, requestCts.Token);
                var decision = ParseDecision(response, requestId, topCandidates);

                lock (_cacheLock)
                    _decisionCache[cacheKey] = new CachedDecision(
                        DateTimeOffset.UtcNow, decision.SelectedVideoId, decision.Reason);
                _ = SaveDecisionCacheAsync();
                SetStatus(decision.SelectedVideoId is null
                    ? $"ChatGPT kept VK audio: {decision.Reason}"
                    : $"ChatGPT selected YouTube: {decision.Reason}");
                return decision;
            }
            finally
            {
                _requestGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopActiveGenerationAsync();
            SetStatus("Obsolete ChatGPT request stopped");
            throw;
        }
        catch (OperationCanceledException)
        {
            await StopActiveGenerationAsync();
            _blockedUntil = DateTimeOffset.UtcNow + FailureCooldown;
            SetStatus("ChatGPT did not answer in time; using local ranking");
            return null;
        }
        catch (Exception exception)
        {
            _blockedUntil = DateTimeOffset.UtcNow + FailureCooldown;
            _logger.Warning(exception, "ChatGPT resolver failed");
            SetStatus($"ChatGPT unavailable: {exception.Message}");
            return null;
        }
        finally
        {
            lock (_activeRequestLock)
            {
                if (ReferenceEquals(_activeRequest, requestCts))
                    _activeRequest = null;
            }

            requestCts.Dispose();
        }
    }

    public Task ShowAccountAsync(Guid accountId) => GetSession(GetAccount(accountId)).ShowInteractiveAsync();

    public async Task<bool> IsLoggedInAsync(Guid accountId)
    {
        try
        {
            return await GetSession(GetAccount(accountId)).IsLoggedInAsync();
        }
        catch (Exception exception)
        {
            _logger.Debug(exception, "Unable to check ChatGPT login state");
            return false;
        }
    }

    public async Task LogoutAsync(Guid accountId)
    {
        var account = GetAccount(accountId);
        account.ConversationUrl = null;
        account.ConversationProjectName = null;
        await _settingsManager.SaveSettingsSilentlyAsync();
        await GetSession(account).LogoutAsync();
        SetStatus($"Logged out from {account.Name}");
    }

    public async Task ReloginAsync(Guid accountId)
    {
        await LogoutAsync(accountId);
        await GetSession(GetAccount(accountId)).ShowInteractiveAsync();
        SetStatus("Complete the ChatGPT login in the opened window");
    }

    public async Task OpenProjectAsync(Guid accountId, string projectName)
    {
        var account = GetAccount(accountId);
        account.ConversationUrl = null;
        account.ConversationProjectName = null;
        await _settingsManager.SaveSettingsSilentlyAsync();
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
        account.ConversationUrl = null;
        account.ConversationProjectName = null;
        await _settingsManager.SaveSettingsSilentlyAsync();
        SetStatus("The resolver will create a new chat on the next request");
    }

    public void CloseAccount(Guid accountId)
    {
        if (_sessions.TryRemove(accountId, out var session))
            session.Dispose();
    }

    public async Task StopActiveGenerationAsync()
    {
        var account = GetActiveAccount();
        if (account is not null && _sessions.TryGetValue(account.Id, out var session))
            await session.StopAsync();
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
        builder.AppendLine("Use only the supplied candidates. YouTube relevance order is strong evidence.");
        builder.AppendLine("The musical recording/version must match. Prefer a real music video, official artist upload, lyric/visual video, or a strong fan clip. Intros/outros and different video duration are normal. Do not choose a live, cover, remix or alternate version unless the VK title asks for it. Views and channel identity are supporting evidence, not hard requirements.");
        builder.AppendLine("If none is sufficiently likely to be the same recording, select null so VK keeps playing.");
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
            throw new InvalidDataException("ChatGPT returned no JSON decision");

        using var document = JsonDocument.Parse(response[start..(end + 1)]);
        var root = document.RootElement;
        if (!root.TryGetProperty("requestId", out var requestElement) ||
            requestElement.GetString() != requestId)
            throw new InvalidDataException("ChatGPT returned an obsolete decision");
        if (!root.TryGetProperty("selectedVideoId", out var selectedElement))
            throw new InvalidDataException("ChatGPT decision has no selectedVideoId");

        string? selectedVideoId = selectedElement.ValueKind == JsonValueKind.Null
            ? null
            : selectedElement.GetString();
        if (selectedVideoId is not null && candidates.All(candidate => candidate.VideoId != selectedVideoId))
            throw new InvalidDataException("ChatGPT selected a video outside the supplied list");

        var reason = root.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString()
            : null;
        return new ChatGptDecision(selectedVideoId, reason ?? "no explanation", false);
    }

    private static string CreateDecisionKey(
        Guid accountId,
        string projectName,
        string artist,
        string title,
        IEnumerable<ChatGptYouTubeCandidate> candidates)
    {
        var value = $"{accountId:N}|{projectName}|{artist}|{title}|" +
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
        Status = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record CachedDecision(DateTimeOffset CreatedAt, string? SelectedVideoId, string Reason);

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

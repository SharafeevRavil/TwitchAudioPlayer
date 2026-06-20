using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Shared.Player;
using Serilog;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;
using TwitchAudioPlayer.WPF.Services.ChatGpt;
using YoutubeExplode;
using YoutubeExplode.Search;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

using System.Text.Json;
using System.IO;
public enum VkPlaybackSource
{
    Vk,
    YouTube
}

public enum YouTubeMatchConfidence
{
    Low,
    Medium,
    High
}

public enum YouTubeCandidateKind
{
    OfficialVideo,
    Video,
    Visual,
    Audio,
    Alternate,
    Rejected
}

public sealed record YouTubeMatchCandidate(
    int Rank,
    string VideoId,
    string Title,
    string ChannelTitle,
    TimeSpan Duration,
    string ThumbnailUrl,
    long? ViewCount,
    YouTubeCandidateKind Kind,
    double Score,
    YouTubeMatchConfidence Confidence,
    string Reason,
    bool ChannelVerified)
{
    public bool IsConfident => Confidence == YouTubeMatchConfidence.High;

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string MetricsText => $"{FormatCount(ViewCount)} views · {DurationText} · YouTube #{Rank}";

    public string ConfidenceText => $"{Score:0} pts · {Reason}";

    private static string FormatCount(long? value)
    {
        if (value is null)
            return "—";

        return value.Value switch
        {
            >= 1_000_000_000 => $"{value.Value / 1_000_000_000d:0.#}B",
            >= 1_000_000 => $"{value.Value / 1_000_000d:0.#}M",
            >= 1_000 => $"{value.Value / 1_000d:0.#}K",
            _ => value.Value.ToString(CultureInfo.InvariantCulture)
        };
    }
}

public sealed partial class VkYouTubePlaybackService : ObservableObject
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan PrefetchDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PrefetchRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
    private static readonly string[] Decorations =
    [
        "official music video", "official video", "official audio", "music video", "lyric video",
        "lyrics", "lyric", "visualizer", "animation video", "animation", "video", "audio", "clip",
        "клип", "текст песни", "official", "hd", "4k"
    ];
    private static readonly string[] AlternateMarkers =
    [
        "cover", "кавер", "karaoke", "nightcore", "slowed", "sped up", "8d", "remix", "mix", "ремикс",
        "reaction", "tutorial", "instrumental", "live", "concert", "концерт"
    ];

    private readonly ILogger _logger = Log.ForContext<VkYouTubePlaybackService>();
    private readonly IUserSettingsManager _settingsManager;
    private readonly BrowserPlayerService _browserPlayer;
    private readonly YouTubeSearchService _searchService;
    private readonly ChatGptResolverService _chatGptResolver;
    private readonly PlayerService _player;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly Dictionary<string, YouTubeMatchCandidate> _manual = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedVideos = new(StringComparer.Ordinal);
    private readonly string _cachePath;
    private CancellationTokenSource? _currentCts;
    private CancellationTokenSource? _prefetchCts;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private YouTubeMatchCandidate? _lastAppliedCandidate;
    private int _consecutiveFailures;
    private int _fallbackAttempts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVkSourceSelected))]
    [NotifyPropertyChangedFor(nameof(IsYouTubeSourceSelected))]
    private VkPlaybackSource _activeSource;
    [ObservableProperty] private bool _autoPlayEnabled;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string _statusText = "Select a VK track";
    [ObservableProperty] private YouTubeMatchCandidate? _selectedCandidate;

    private const int CacheFormatVersion = 15;

    public VkYouTubePlaybackService(IUserSettingsManager settingsManager, BrowserPlayerService browserPlayer,
        YouTubeSearchService searchService, ChatGptResolverService chatGptResolver)
    {
        _settingsManager = settingsManager;
        _browserPlayer = browserPlayer;
        _searchService = searchService;
        _chatGptResolver = chatGptResolver;
        _player = StaticService.Container.GetRequiredService<PlayerService>();
        _autoPlayEnabled = settingsManager.Settings.AutoPlayYouTubeForVk;
        _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TwitchAudioPlayer", "vkYoutubeMatches.json");
        LoadState();

        var dispatcher = Application.Current.Dispatcher;
        _player.TrackChangedEvent += (_, _) =>
            dispatcher.InvokeAsync(() => HandleTrackChangedAsync(_player.CurrentTrack));
        _player.NextTrackChanged += (_, _) =>
            dispatcher.InvokeAsync(() => SchedulePrefetch(_player.CurrentTrack));
        _browserPlayer.PlaybackEnded += (_, _) => dispatcher.InvokeAsync(HandleEndedAsync);
        _browserPlayer.PlaybackFailed += (_, message) =>
            dispatcher.InvokeAsync(() => HandleFailureAsync(message));
        _browserPlayer.SkipRequested += (_, _) => dispatcher.InvokeAsync(HandleEndedAsync);
        _browserPlayer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BrowserPlayerService.CurrentOwner))
                dispatcher.Invoke(HandleOwnerChanged);
        };
        if (_player.CurrentTrack is not null)
            _ = HandleTrackChangedAsync(_player.CurrentTrack);
    }

    public ObservableCollection<YouTubeMatchCandidate> Candidates { get; } = [];
    public bool IsVkSourceSelected => ActiveSource == VkPlaybackSource.Vk;
    public bool IsYouTubeSourceSelected => ActiveSource == VkPlaybackSource.YouTube;

    partial void OnAutoPlayEnabledChanged(bool value)
    {
        _settingsManager.Settings.AutoPlayYouTubeForVk = value;
        _ = _settingsManager.SaveSettingsSilentlyAsync();
        if (!value && ActiveSource == VkPlaybackSource.YouTube)
            _ = SwitchToVkAsync();
        else if (value && IsAvailable)
            _ = ResolveAsync(_player.CurrentTrack, true);
    }

    [RelayCommand]
    private async Task SwitchToVkAsync()
    {
        RefreshSourceButtons();
        if (ActiveSource != VkPlaybackSource.YouTube ||
            _browserPlayer.CurrentOwner != BrowserPlaybackOwner.VkReplacement)
            return;

        var position = _browserPlayer.Position;
        var wasPlaying = _browserPlayer.IsPlaying;
        _browserPlayer.Stop();
        ActiveSource = VkPlaybackSource.Vk;
        _player.IsPlaybackSuppressed = false;
        _player.Seek(position);
        if (wasPlaying)
            _player.Play();

        StatusText = "Playing VK audio";
        RefreshSourceButtons();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SwitchToYouTubeAsync()
    {
        RefreshSourceButtons();
        if (!IsAvailable) return;
        if (SelectedCandidate is null)
            await ResolveAsync(_player.CurrentTrack, false, true);
        if (SelectedCandidate is not null)
            await PlayAsync(SelectedCandidate);
        RefreshSourceButtons();
    }

    [RelayCommand]
    private async Task SelectCandidateAsync()
    {
        var track = _player.CurrentTrack;
        if (track is null || SelectedCandidate is null ||
            ReferenceEquals(SelectedCandidate, _lastAppliedCandidate))
            return;
        var saved = SelectedCandidate with
        {
            Confidence = YouTubeMatchConfidence.High,
            Reason = "saved manual choice"
        };
        _manual[TrackKey(track)] = saved;
        _lastAppliedCandidate = saved;
        StatusText = $"Saved choice: {saved.Title}";
        await SaveStateAsync();
        await PlayAsync(saved);
    }

    private async Task HandleTrackChangedAsync(PlaylistTrack? track)
    {
        _currentCts?.Cancel();
        _prefetchCts?.Cancel();
        if (_browserPlayer.CurrentOwner == BrowserPlaybackOwner.VkReplacement)
        {
            _browserPlayer.Stop();
            _player.IsPlaybackSuppressed = false;
        }

        ActiveSource = VkPlaybackSource.Vk;
        IsAvailable = IsVkTrack(track);
        Candidates.Clear();
        SelectedCandidate = null;
        _lastAppliedCandidate = null;
        _failedVideos.Clear();
        _fallbackAttempts = 0;
        if (!IsAvailable || track is null)
        {
            StatusText = "YouTube replacement is available for VK tracks";
            return;
        }

        StatusText = "Waiting to search YouTube…";
        await ResolveAsync(track, AutoPlayEnabled);
    }

    private async Task ResolveAsync(PlaylistTrack? track, bool autoPlay, bool skipDebounce = false)
    {
        if (!IsVkTrack(track) || track is null) return;
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        var cts = new CancellationTokenSource();
        _currentCts = cts;
        try
        {
            if (!skipDebounce && !HasCache(track))
                await Task.Delay(SearchDebounce, cts.Token);
            IsResolving = true;
            StatusText = "Searching YouTube…";
            var candidates = await GetCandidatesAsync(track, cts.Token);
            if (cts.IsCancellationRequested || _player.CurrentTrack != track) return;

            Candidates.Clear();
            foreach (var candidate in candidates.Take(5)) Candidates.Add(candidate);
            var best = BestPlaybackCandidate(candidates);
            _lastAppliedCandidate = best;
            SelectedCandidate = best;
            StatusText = best switch
            {
                null => "No suitable YouTube videos found — playing VK",
                { Confidence: YouTubeMatchConfidence.High } => $"High confidence: {best.Reason}",
                { Confidence: YouTubeMatchConfidence.Medium } => $"Uncertain: {best.Reason} — playing VK",
                _ => "No reliable YouTube match — playing VK"
            };
            if (autoPlay && best?.Confidence == YouTubeMatchConfidence.High)
                await PlayAsync(best);
            SchedulePrefetch(track);
        }
        catch (OperationCanceledException) { }
        catch (YouTubeSearchCircuitOpenException e)
        {
            StatusText = $"YouTube search paused until {e.BlockedUntil.LocalDateTime:t} — playing VK";
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Failed to resolve YouTube replacement for {Track}", track.Title);
            StatusText = "YouTube search failed — playing VK";
        }
        finally
        {
            if (ReferenceEquals(_currentCts, cts))
            {
                _currentCts = null;
                IsResolving = false;
            }
            cts.Dispose();
        }
    }

    private void SchedulePrefetch(PlaylistTrack? track)
    {
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        if (!AutoPlayEnabled || !IsVkTrack(track) || track is null) return;

        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        _ = PrefetchAsync(track, cts);
    }

    private async Task PrefetchAsync(PlaylistTrack current, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(PrefetchDelay, cts.Token);
            while (!cts.IsCancellationRequested && _player.CurrentTrack == current)
            {
                var next = _player.NextPlayTrack;
                if (IsVkTrack(next))
                {
                    if (!HasCache(next!))
                        await GetCandidatesAsync(next!, cts.Token);
                    return;
                }

                await Task.Delay(PrefetchRetryDelay, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (YouTubeSearchCircuitOpenException) { }
        catch (Exception e) { _logger.Debug(e, "YouTube prefetch failed"); }
        finally
        {
            if (ReferenceEquals(_prefetchCts, cts)) _prefetchCts = null;
            cts.Dispose();
        }
    }

    private async Task<IReadOnlyList<YouTubeMatchCandidate>> GetCandidatesAsync(
        PlaylistTrack track, CancellationToken cancellationToken)
    {
        var key = TrackKey(track);
        _manual.TryGetValue(key, out var manual);
        if (manual is not null)
            return [manual with
            {
                Confidence = YouTubeMatchConfidence.High,
                Reason = "saved manual choice"
            }];

        IReadOnlyList<YouTubeMatchCandidate> candidates;
        if (_cache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < CacheLifetime)
        {
            candidates = cached.Candidates;
        }
        else
        {
            var query = $"{track.GetArtistsString()} {track.Title}".Trim();
            var results = await SearchAsync(query, YouTubeSearchSort.Relevance, cancellationToken);
            candidates = Rank(track, results);
            _cache[key] = new CacheEntry(DateTimeOffset.UtcNow, candidates);
            _ = SaveStateAsync();
        }

        return await ApplyChatGptDecisionAsync(track, candidates, cancellationToken);
    }

    private async Task<IReadOnlyList<YouTubeMatchCandidate>> ApplyChatGptDecisionAsync(
        PlaylistTrack track,
        IReadOnlyList<YouTubeMatchCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (!_chatGptResolver.IsEnabled || candidates.Count == 0)
            return candidates;

        var decision = await _chatGptResolver.ResolveAsync(
            track.GetArtistsString(),
            track.Title,
            candidates.Take(5).Select(candidate => new ChatGptYouTubeCandidate(
                candidate.VideoId,
                candidate.Rank,
                candidate.Title,
                candidate.ChannelTitle,
                candidate.ViewCount,
                candidate.Duration)).ToArray(),
            cancellationToken);
        if (decision is null)
            return candidates;

        if (decision.SelectedVideoId is null)
        {
            return candidates.Select(candidate => candidate with
            {
                Confidence = candidate.Confidence == YouTubeMatchConfidence.High
                    ? YouTubeMatchConfidence.Medium
                    : candidate.Confidence,
                Reason = $"ChatGPT kept VK: {decision.Reason}"
            }).ToArray();
        }

        var promotedScore = candidates.Max(candidate => candidate.Score) + 100;
        return candidates.Select(candidate => candidate.VideoId == decision.SelectedVideoId
            ? candidate with
            {
                Kind = candidate.Kind is YouTubeCandidateKind.OfficialVideo or YouTubeCandidateKind.Visual
                    ? candidate.Kind
                    : YouTubeCandidateKind.Video,
                Score = promotedScore,
                Confidence = YouTubeMatchConfidence.High,
                Reason = $"ChatGPT: {decision.Reason}"
            }
            : candidate).ToArray();
    }

    private IReadOnlyList<YouTubeMatchCandidate> Rank(
        PlaylistTrack track, IReadOnlyList<YouTubeSearchItem> results)
    {
        if (results.Count == 0)
            return [];

        var evaluations = results.Select(result => Evaluate(track, result)).ToArray();
        var maxViews = evaluations
            .Where(evaluation => evaluation.Kind != YouTubeCandidateKind.Rejected)
            .Select(evaluation => evaluation.Result.ViewCount ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var candidates = evaluations
            .Select(evaluation => CreateCandidate(evaluation, maxViews))
            .ToArray();
        return candidates
            .OrderBy(candidate => candidate.Rank)
            .ToArray();
    }

    private Evaluation Evaluate(PlaylistTrack track, YouTubeSearchItem result)
    {
        var artist = Normalize(track.GetArtistsString());
        var targetTitle = Normalize(track.Title);
        var fullTitle = Normalize(result.Title);
        var titleSimilarity = GetCandidateTitleVariants(result.Title, artist)
            .Select(variant => TrackTitleSimilarity(targetTitle, variant))
            .DefaultIfEmpty(0)
            .Max();

        var channel = NormalizeChannel(result.ChannelTitle);
        var artistInTitle = ContainsTokens(fullTitle, artist);
        var channelSimilarity = NameSimilarity(artist, channel);
        var exactArtistChannel = artist.Length > 0 && Compact(artist) == Compact(channel);
        var trustedChannel = exactArtistChannel ||
                             result.ChannelVerified && channelSimilarity >= 0.42;
        var artistEvidence = artist.Length == 0 || artistInTitle || channelSimilarity >= 0.35;

        var differentVersion = HasDifferentRemixIdentity(track.Title, result.Title);
        var explicitVideo = HasAny(fullTitle,
            "official music video", "official video", "music video", "video",
            "clip", "клип", "animation", "amv", "lyric", "lyrics", "visualizer");

        var kind = Classify(Normalize(track.Title), fullTitle, result.ChannelTitle,
            trustedChannel, result.ChannelVerified);
        var durationOk = IsDurationOk(track.Data.Duration, result.Duration, kind, explicitVideo);
        var durationFarOff = IsDurationFarOff(track.Data.Duration, result.Duration, kind, explicitVideo);

        return new Evaluation(result, kind, titleSimilarity, artistEvidence, artistInTitle,
            trustedChannel, channelSimilarity, explicitVideo, durationOk, durationFarOff,
            differentVersion);
    }

    private static YouTubeCandidateKind Classify(string trackTitle, string title,
        string channel, bool trusted, bool verified)
    {
        if (AlternateMarkers.Any(marker =>
                HasPhrase(title, marker) && !HasPhrase(trackTitle, marker)))
            return YouTubeCandidateKind.Alternate;

        var audio = HasAny(title, "official audio", "audio only", "full audio") ||
                    Normalize(channel).EndsWith(" topic", StringComparison.Ordinal) &&
                    !HasAny(title, "video", "clip", "клип", "animation", "amv");
        if (audio)
            return YouTubeCandidateKind.Audio;

        var visual = HasAny(title, "lyrics", "lyric", "текст песни", "visualizer");
        if (visual)
            return YouTubeCandidateKind.Visual;

        if (HasAny(title, "official music video", "official video") && (trusted || verified))
            return YouTubeCandidateKind.OfficialVideo;

        return YouTubeCandidateKind.Video;
    }

    private static bool IsDurationOk(TimeSpan audioDuration, TimeSpan videoDuration,
        YouTubeCandidateKind kind, bool explicitVideo)
    {
        if (videoDuration == TimeSpan.Zero)
            return true;
        if (IsDurationFarOff(audioDuration, videoDuration, kind, explicitVideo))
            return false;
        if (IsClipLike(kind, explicitVideo))
            return true;

        var delta = Math.Abs((videoDuration - audioDuration).TotalSeconds);
        return delta <= Math.Max(90, audioDuration.TotalSeconds * 0.45);
    }

    private static bool IsDurationFarOff(TimeSpan audioDuration, TimeSpan videoDuration,
        YouTubeCandidateKind kind, bool explicitVideo)
    {
        if (videoDuration == TimeSpan.Zero)
            return false;

        if (videoDuration.TotalSeconds < Math.Min(75, audioDuration.TotalSeconds * 0.45))
            return true;

        if (IsClipLike(kind, explicitVideo))
            return videoDuration.TotalMinutes > 12 &&
                   videoDuration.TotalSeconds > audioDuration.TotalSeconds + 420;

        var delta = Math.Abs((videoDuration - audioDuration).TotalSeconds);
        return delta > Math.Max(180, audioDuration.TotalSeconds);
    }

    private static bool IsClipLike(YouTubeCandidateKind kind, bool explicitVideo) =>
        kind is YouTubeCandidateKind.OfficialVideo or YouTubeCandidateKind.Visual ||
        kind == YouTubeCandidateKind.Video && explicitVideo;

    private static double CalculateScore(Evaluation evaluation, long maxViews)
    {
        var relevanceScore = 36 * Math.Exp(-0.22 * (evaluation.Result.Rank - 1));
        var viewScore = evaluation.Result.ViewCount is > 0 && maxViews > 0
            ? 18 * Math.Log10(evaluation.Result.ViewCount.Value + 1) /
              Math.Log10(maxViews + 1)
            : 0;
        var artistScore = (evaluation.ArtistInTitle ? 8 : 0) +
                          evaluation.ChannelSimilarity * 10 +
                          (evaluation.TrustedChannel ? 10 : 0) +
                          (evaluation.Result.ChannelVerified ? 4 : 0);
        var contentScore = evaluation.Kind switch
        {
            YouTubeCandidateKind.OfficialVideo => 25,
            YouTubeCandidateKind.Video when evaluation.ExplicitVideo => 14,
            YouTubeCandidateKind.Video => 6,
            YouTubeCandidateKind.Visual => 8,
            YouTubeCandidateKind.Audio => -15,
            YouTubeCandidateKind.Alternate => -20,
            _ => 0
        };
        var durationScore = evaluation.DurationOk ? 3 : evaluation.DurationFarOff ? -16 : 0;
        var versionPenalty = evaluation.DifferentVersion ? -28 : 0;
        var artistPenalty = evaluation.ArtistEvidence ? 0 : -36;

        return Math.Round(relevanceScore + viewScore + artistScore +
                          contentScore + durationScore + versionPenalty + artistPenalty, 1);
    }

    private static YouTubeMatchCandidate CreateCandidate(Evaluation evaluation, long maxViews)
    {
        var score = CalculateScore(evaluation, maxViews);
        return new YouTubeMatchCandidate(
            evaluation.Result.Rank,
            evaluation.Result.VideoId,
            evaluation.Result.Title,
            evaluation.Result.ChannelTitle,
            evaluation.Result.Duration,
            evaluation.Result.ThumbnailUrl,
            evaluation.Result.ViewCount,
            evaluation.Kind,
            score,
            Confidence(evaluation, score),
            Reason(evaluation),
            evaluation.Result.ChannelVerified);
    }

    private static YouTubeMatchConfidence Confidence(Evaluation evaluation, double score)
    {
        if (!IsPlaybackKind(evaluation.Kind) ||
            !evaluation.ArtistEvidence ||
            evaluation.DurationFarOff ||
            evaluation.DifferentVersion)
            return YouTubeMatchConfidence.Low;

        return score switch
        {
            >= 70 => YouTubeMatchConfidence.High,
            >= 50 => YouTubeMatchConfidence.Medium,
            _ => YouTubeMatchConfidence.Low
        };
    }

    private static string Reason(Evaluation x)
    {
        var type = x.Kind switch
        {
            YouTubeCandidateKind.OfficialVideo => "official artist video",
            YouTubeCandidateKind.Video when x.TrustedChannel => "artist channel video",
            YouTubeCandidateKind.Video => "clean video candidate",
            YouTubeCandidateKind.Visual => "lyrics/visualizer",
            YouTubeCandidateKind.Audio => "audio upload",
            YouTubeCandidateKind.Alternate => "alternate version",
            _ => "weak match"
        };
        var issues = new List<string>();
        if (!x.ArtistEvidence) issues.Add("no artist match");
        if (x.DifferentVersion) issues.Add("different remix/version");
        if (x.DurationFarOff) issues.Add("extreme duration");
        return issues.Count == 0
            ? type
            : $"{type}, {string.Join(", ", issues)}";
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> SearchAsync(
        string query, YouTubeSearchSort sort, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _blockedUntil)
                throw new YouTubeSearchCircuitOpenException(_blockedUntil);

            var wait = RequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
            _lastRequestAt = DateTimeOffset.UtcNow;

            try
            {
                var results = await _searchService.SearchAsync(query, sort, cancellationToken);
                _consecutiveFailures = 0;
                return results;
            }
            catch (YouTubeSearchRateLimitedException)
            {
                _blockedUntil = DateTimeOffset.UtcNow + RateLimitCooldown;
                throw new YouTubeSearchCircuitOpenException(_blockedUntil);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (++_consecutiveFailures >= 3)
                    _blockedUntil = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task PlayAsync(YouTubeMatchCandidate candidate)
    {
        var track = _player.CurrentTrack;
        if (!IsVkTrack(track) || track is null ||
            _browserPlayer.CurrentOwner == BrowserPlaybackOwner.MusicOrder)
            return;

        var position = ActiveSource == VkPlaybackSource.YouTube
            ? _browserPlayer.Position
            : _player.Position;

        // Suppression closes the race where PlayerService finishes opening the VK source
        // after this pause and calls MediaPlayer.Play() underneath the iframe.
        _player.IsPlaybackSuppressed = true;
        _player.Pause();
        _lastAppliedCandidate = candidate;
        SelectedCandidate = candidate;
        ActiveSource = VkPlaybackSource.YouTube;
        StatusText = $"YouTube #{candidate.Rank}: {candidate.Title}";
        if (!_settingsManager.Settings.UseSeparateSourceVolumes)
        {
            _browserPlayer.SetVolume(_player.Volume);
            _browserPlayer.SetMuted(_player.IsMuted);
        }

        _browserPlayer.Load(new YouTubeBrowserPlaybackRequest(candidate.VideoId, position, track,
            _browserPlayer.CreateRequestId(), BrowserPlaybackOwner.VkReplacement));
        await Task.CompletedTask;
    }

    private async Task HandleEndedAsync()
    {
        if (_browserPlayer.CurrentOwner != BrowserPlaybackOwner.VkReplacement)
            return;

        _browserPlayer.Stop();
        ActiveSource = VkPlaybackSource.Vk;
        _player.IsPlaybackSuppressed = false;
        await _player.NextTrack();
    }

    private async Task HandleFailureAsync(string message)
    {
        if (_browserPlayer.CurrentOwner != BrowserPlaybackOwner.VkReplacement)
            return;

        var position = _browserPlayer.Position;
        if (SelectedCandidate is not null)
            _failedVideos.Add(SelectedCandidate.VideoId);

        _browserPlayer.Stop();
        ActiveSource = VkPlaybackSource.Vk;
        _player.Seek(position);

        var fallback = _fallbackAttempts == 0
            ? BestPlaybackCandidate(Candidates.Where(candidate =>
                candidate.Confidence == YouTubeMatchConfidence.High &&
                !_failedVideos.Contains(candidate.VideoId)))
            : null;
        if (fallback is not null)
        {
            _fallbackAttempts++;
            StatusText = $"Trying fallback YouTube #{fallback.Rank}";
            await PlayAsync(fallback);
            return;
        }

        _player.IsPlaybackSuppressed = false;
        _player.Play();
        StatusText = $"YouTube failed ({message}) — playing VK";
    }

    private void HandleOwnerChanged()
    {
        if (ActiveSource == VkPlaybackSource.YouTube &&
            _browserPlayer.CurrentOwner != BrowserPlaybackOwner.VkReplacement)
            ActiveSource = VkPlaybackSource.Vk;
    }

    private bool HasCache(PlaylistTrack track)
    {
        var key = TrackKey(track);
        return _manual.ContainsKey(key) || _cache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < CacheLifetime;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_cachePath));
            if (state is null || state.Version != CacheFormatVersion)
                return;

            foreach (var (key, search) in state.Searches)
                _cache[key] = new CacheEntry(search.CreatedAt, search.Candidates);
            foreach (var (key, candidate) in state.ManualSelections)
                _manual[key] = candidate;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Failed to load VK YouTube cache");
        }
    }

    private async Task SaveStateAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var state = new PersistedState
            {
                Searches = _cache.ToDictionary(p => p.Key, p => new PersistedSearch
                {
                    CreatedAt = p.Value.CreatedAt, Candidates = p.Value.Candidates.ToList()
                }),
                ManualSelections = new Dictionary<string, YouTubeMatchCandidate>(_manual,
                    StringComparer.Ordinal)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var temp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, _cachePath, true);
        }
        catch (Exception e) { _logger.Warning(e, "Failed to save VK YouTube cache"); }
        finally { _saveGate.Release(); }
    }

    private void RefreshSourceButtons()
    {
        OnPropertyChanged(nameof(IsVkSourceSelected));
        OnPropertyChanged(nameof(IsYouTubeSourceSelected));
    }

    private static bool IsVkTrack(PlaylistTrack? track) =>
        track?.Data is VkTrackData and not YtTrackData;
    private static string TrackKey(PlaylistTrack track) =>
        $"{Normalize(track.GetArtistsString())}|{Normalize(track.Title)}|{Math.Round(track.Data.Duration.TotalSeconds)}";

    

    private static bool IsPlaybackCandidate(YouTubeMatchCandidate candidate) =>
        IsPlaybackKind(candidate.Kind) && candidate.Confidence != YouTubeMatchConfidence.Low;

    private static YouTubeMatchCandidate? BestPlaybackCandidate(IEnumerable<YouTubeMatchCandidate> candidates) =>
        candidates
            .Where(IsPlaybackCandidate)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Rank)
            .FirstOrDefault();

    private static bool IsPlaybackKind(YouTubeCandidateKind kind) =>
        kind is YouTubeCandidateKind.OfficialVideo
            or YouTubeCandidateKind.Video
            or YouTubeCandidateKind.Visual;

    private static double TrackTitleSimilarity(string trackTitle, string candidateTitle)
    {
        if (trackTitle.Length == 0 || candidateTitle.Length == 0)
            return 0;

        if ($" {candidateTitle} ".Contains($" {trackTitle} ", StringComparison.Ordinal))
        {
            var trackTokenCount = TokenCount(trackTitle);
            var candidateTokenCount = TokenCount(candidateTitle);
            if (trackTokenCount <= 3 && candidateTokenCount >= trackTokenCount + 4)
                return 0.78;

            return 1;
        }

        var primaryCompact = Compact(trackTitle);
        var candidateCompact = Compact(candidateTitle);
        if (primaryCompact.Length >= 4 &&
            candidateCompact.Contains(primaryCompact, StringComparison.Ordinal))
            return 0.95;

        return Similarity(trackTitle, candidateTitle);
    }

    private static IReadOnlyList<string> GetCandidateTitleVariants(string rawTitle, string artist)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal)
        {
            CanonicalTitle(rawTitle, artist)
        };

        foreach (var segment in rawTitle.Split(['|', '•'], StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = CanonicalTitle(segment, artist);
            if (canonical.Length > 0)
                variants.Add(canonical);
        }

        return variants.ToArray();
    }

    private static string CanonicalTitle(string rawTitle, string artist)
    {
        var withoutMetadataBlocks = Regex.Replace(rawTitle, @"\[(?<tag>[^\]]+)\]", match =>
            IsMetadataBlock(match.Groups["tag"].Value) ? " " : match.Value,
            RegexOptions.CultureInvariant);
        return CleanTitle(Normalize(withoutMetadataBlocks), artist);
    }

    private static bool IsMetadataBlock(string value)
    {
        var normalized = Normalize(value);
        return HasAny(normalized,
            "ncs", "ncs release", "official video", "official audio", "music video",
            "copyright free", "free download", "hd", "4k");
    }

    private static bool HasDifferentRemixIdentity(string trackTitle, string candidateTitle)
    {
        var match = Regex.Match(trackTitle,
            @"[\(\[](?<variant>[^\)\]]*\b(?:remix|mix|edit)\b[^\)\]]*)[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var identity = Normalize(match.Groups["variant"].Value);
        foreach (var genericMarker in new[] { "remix", "mix", "edit", "version", "official" })
            identity = RemovePhrase(identity, genericMarker);

        var identityTokens = identity.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2)
            .ToArray();
        if (identityTokens.Length == 0)
            return false;

        var candidateTokens = Normalize(candidateTitle)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        return identityTokens.Any(token => !candidateTokens.Contains(token));
    }

    private static string CleanTitle(string title, string artist)
    {
        var clean = artist.Length > 0 ? RemovePhrase(title, artist) : title;
        foreach (var marker in Decorations) clean = RemovePhrase(clean, marker);
        return Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private static string NormalizeChannel(string channel)
    {
        var result = Normalize(channel);
        foreach (var marker in new[] { "vevo", "official artist channel", "official", "topic" })
            result = RemovePhrase(result, marker);
        return result;
    }

    private static double NameSimilarity(string artist, string channel)
    {
        if (artist.Length == 0 || channel.Length == 0) return 0;
        var a = Compact(artist);
        var c = Compact(channel);
        if (a == c) return 1;
        if (a.Length >= 4 && c.Contains(a, StringComparison.Ordinal) ||
            c.Length >= 4 && a.Contains(c, StringComparison.Ordinal)) return 0.85;
        return Similarity(artist, channel);
    }

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = Normalize(right).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (a.Count == 0 || b.Count == 0) return 0;
        var common = a.Count(b.Contains);
        return common / (double)(a.Count + b.Count - common) * 0.35 +
               common / (double)a.Count * 0.65;
    }

    private static int TokenCount(string value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool ContainsTokens(string text, string phrase)
    {
        var wanted = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 2).ToArray();
        if (wanted.Length == 0) return false;
        var actual = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return wanted.Count(actual.Contains) >= Math.Ceiling(wanted.Length * 0.75);
    }

    private static bool HasAny(string text, params string[] phrases) =>
        phrases.Any(p => HasPhrase(text, p));
    private static bool HasPhrase(string text, string phrase) =>
        $" {text} ".Contains($" {Normalize(phrase)} ", StringComparison.Ordinal);
    private static string RemovePhrase(string text, string phrase) =>
        Regex.Replace($" {text} ", $@"\s{Regex.Escape(Normalize(phrase))}\s", " ").Trim();
    private static string Compact(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    

    

    

    private sealed record CacheEntry(DateTimeOffset CreatedAt,
        IReadOnlyList<YouTubeMatchCandidate> Candidates);
    private sealed record Evaluation(
        YouTubeSearchItem Result,
        YouTubeCandidateKind Kind,
        double TitleSimilarity,
        bool ArtistEvidence,
        bool ArtistInTitle,
        bool TrustedChannel,
        double ChannelSimilarity,
        bool ExplicitVideo,
        bool DurationOk,
        bool DurationFarOff,
        bool DifferentVersion);
    private sealed class PersistedState
    {
        public int Version { get; set; } = CacheFormatVersion;
        public Dictionary<string, PersistedSearch> Searches { get; set; } = [];
        public Dictionary<string, YouTubeMatchCandidate> ManualSelections { get; set; } = [];
    }
    private sealed class PersistedSearch
    {
        public DateTimeOffset CreatedAt { get; set; }
        public List<YouTubeMatchCandidate> Candidates { get; set; } = [];
    }
}

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services.ChatGpt;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.Services;

public sealed partial class ApplicationStatusService : ObservableObject
{
    private const int HistoryLimit = 500;
    private readonly ConcurrentQueue<PendingStatus> _pending = new();
    private readonly List<StatusEntry> _history = [];
    private readonly Dispatcher _dispatcher;
    private int _drainScheduled;
    private int _currentIndex = -1;

    [ObservableProperty] private string _currentText = "Application is ready";
    [ObservableProperty] private string _currentSource = "System";
    [ObservableProperty] private bool _isCurrentError;
    [ObservableProperty] private string _positionText = "1 / 1";

    public ApplicationStatusService(
        VkYouTubePlaybackService resolver,
        BrowserPlayerService browserPlayer,
        ChatGptResolverService aiResolver)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        resolver.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(VkYouTubePlaybackService.StatusText))
                Publish("Resolver", resolver.StatusText);
        };
        browserPlayer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BrowserPlayerService.StatusText) &&
                !IsPositionOnlyBrowserStatus(browserPlayer.StatusText))
                Publish("YouTube player", browserPlayer.StatusText);
        };
        aiResolver.StatusChanged += (_, _) => Publish(aiResolver.ActiveProviderName, aiResolver.Status);

        AddEntry(new StatusEntry(DateTimeOffset.Now, "System", "Application is ready", false));
        Publish("Resolver", resolver.StatusText);
        Publish("YouTube player", browserPlayer.StatusText);
        Publish(aiResolver.ActiveProviderName, aiResolver.Status);
    }

    public void Publish(string source, string? message, bool? isError = null)
    {
        var normalized = Normalize(message);
        if (normalized.Length == 0)
            return;

        _pending.Enqueue(new PendingStatus(
            source,
            normalized,
            isError ?? LooksLikeError(normalized)));
        ScheduleDrain();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous()
    {
        if (!CanGoPrevious()) return;
        _currentIndex--;
        ApplyCurrent();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (!CanGoNext()) return;
        _currentIndex++;
        ApplyCurrent();
    }

    [RelayCommand(CanExecute = nameof(CanGoLatest))]
    private void Latest()
    {
        if (!CanGoLatest()) return;
        _currentIndex = _history.Count - 1;
        ApplyCurrent();
    }

    private bool CanGoPrevious() => _currentIndex > 0;
    private bool CanGoNext() => _currentIndex >= 0 && _currentIndex < _history.Count - 1;
    private bool CanGoLatest() => CanGoNext();

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0)
            return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
    }

    private void Drain()
    {
        try
        {
            while (_pending.TryDequeue(out var pending))
                AddEntry(new StatusEntry(DateTimeOffset.Now, pending.Source, pending.Message, pending.IsError));
        }
        finally
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
            if (!_pending.IsEmpty)
                ScheduleDrain();
        }
    }

    private void AddEntry(StatusEntry entry)
    {
        if (_history.LastOrDefault() is { } last &&
            last.Source == entry.Source && last.Message == entry.Message && last.IsError == entry.IsError)
            return;

        var followLatest = _currentIndex < 0 || _currentIndex == _history.Count - 1;
        _history.Add(entry);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(0);
            if (_currentIndex > 0) _currentIndex--;
        }

        if (followLatest)
            _currentIndex = _history.Count - 1;
        ApplyCurrent();
    }

    private void ApplyCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _history.Count)
            return;
        var entry = _history[_currentIndex];
        CurrentSource = entry.Source;
        CurrentText = entry.Message;
        IsCurrentError = entry.IsError;
        PositionText = $"{_currentIndex + 1} / {_history.Count}";
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        LatestCommand.NotifyCanExecuteChanged();
    }

    private static bool LooksLikeError(string value) =>
        value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("ошиб", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("не удалось", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("сбой", StringComparison.OrdinalIgnoreCase);

    private static bool IsPositionOnlyBrowserStatus(string value) =>
        value.StartsWith("Playing in browser:", StringComparison.OrdinalIgnoreCase) &&
        value.Contains(" / ", StringComparison.Ordinal);

    private static string Normalize(string? value) =>
        string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record PendingStatus(string Source, string Message, bool IsError);
    private sealed record StatusEntry(DateTimeOffset CreatedAt, string Source, string Message, bool IsError);
}

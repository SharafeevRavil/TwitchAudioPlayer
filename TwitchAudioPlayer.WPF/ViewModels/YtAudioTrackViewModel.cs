using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class YtAudioTrackViewModel : ObservableObject
{
    public MusicOrderWithTrack AudioTrack { get; }

    [ObservableProperty] private string _sourceImage = "";
    [ObservableProperty] private AudioTrackViewModel? _audioTrackViewModel;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isRetrying;
    [ObservableProperty] private string _failedTitle = "";
    [ObservableProperty] private string _failedMessage = "";
    [ObservableProperty] private string _playbackErrorMessage = "";
    [ObservableProperty] private string _orderDateText = "";

    /// <inheritdoc/>
    public YtAudioTrackViewModel(MusicOrderWithTrack audioTrack)
    {
        AudioTrack = audioTrack;
        IsFailed = !audioTrack.IsAvailable;
        FailedTitle = BuildFailedTitle(audioTrack.MusicOrder.Uri);
        FailedMessage = audioTrack.ErrorMessage ?? "Could not load YouTube track.";
        OrderDateText = audioTrack.MusicOrder.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        if (AudioTrack.PlaylistTrack != null)
            AudioTrackViewModel = new AudioTrackViewModel(AudioTrack.PlaylistTrack, true);

        SourceImage = audioTrack.MusicOrder.Type switch
        {
            OrderType.Twitch => "pack://application:,,,/Assets/icons/twitch.png",
            OrderType.DonationAlerts => "pack://application:,,,/Assets/icons/da.png",
            _ => ""
        };
    }

    public bool CanRetry => AudioTrack.CanRetry && !IsRetrying;

    public bool HasPlaybackError => !string.IsNullOrWhiteSpace(PlaybackErrorMessage);

    public event EventHandler<YtAudioTrackViewModel>? RetryRequested;

    public void SetPlaybackError(string message)
    {
        PlaybackErrorMessage = message;
    }

    public void ClearPlaybackError()
    {
        PlaybackErrorMessage = "";
    }

    public void ReplaceAudioTrack(MusicOrderWithTrack audioTrack)
    {
        AudioTrack.PlaylistTrack = audioTrack.PlaylistTrack;
        AudioTrack.Error = audioTrack.Error;
        AudioTrack.ErrorMessage = audioTrack.ErrorMessage;

        IsFailed = !AudioTrack.IsAvailable;
        FailedMessage = AudioTrack.ErrorMessage ?? "Could not load YouTube track.";
        AudioTrackViewModel = AudioTrack.PlaylistTrack != null
            ? new AudioTrackViewModel(AudioTrack.PlaylistTrack, true)
            : null;
        ClearPlaybackError();
        OnPropertyChanged(nameof(CanRetry));
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        RetryRequested?.Invoke(this, this);
    }

    partial void OnIsRetryingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRetry));
        RetryCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlaybackErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasPlaybackError));
    }

    private static string BuildFailedTitle(string uri)
    {
        var id = TryExtractYouTubeId(uri);
        return string.IsNullOrWhiteSpace(id) ? "YouTube order" : $"YouTube order: {id}";
    }

    private static string? TryExtractYouTubeId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return null;

        if (parsed.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return parsed.AbsolutePath.Trim('/').Split('/').FirstOrDefault();

        if (parsed.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = parsed.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "v");
            if (query != null)
                return Uri.UnescapeDataString(query[1]);

            var segments = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed")
                return segments[1];
        }

        return null;
    }
}

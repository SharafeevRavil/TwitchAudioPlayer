using System.Net;
using System.Net.Http;
using System.Text;
using MihaZupan;
using MusicX.Shared.Player;
using Serilog;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public enum YtTrackError
{
    YtNotFound = 10,
    TrackZeroDuration = 20,
    FailedToGetStream = 30,
}

// fixme: костыль, см. подробнее в MediaSourceBase
public record YtTrackData(
    string Url,
    bool IsLiked,
    bool IsExplicit,
    bool? HasLyrics,
    TimeSpan Duration,
    IdInfo Info,
    string TrackCode,
    string? ParentBlockId,
    IdInfo? Playlist,
    string? MainColor) : VkTrackData(Url, IsLiked, IsExplicit, HasLyrics, Duration, Info, TrackCode, ParentBlockId,
    Playlist, MainColor);

public class YouTubeService
{
    private readonly YoutubeClient _youtube = new(new HttpClient(new HttpClientHandler()
    {
        // Proxy = new HttpToSocks5Proxy("127.0.0.1",  10808)
        Proxy = new WebProxy("127.0.0.1",  10808)
    }));
    
    private readonly ILogger _logger = Log.ForContext<YouTubeService>();

    public async Task<(PlaylistTrack? Track, YtTrackError? Error)> GetPlaylistTrack(string url,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("Начало обработки трека с URL: {Url}", url);
        var video = await GetTrackInfo(url, cancellationToken);
        if (video == null)
        {
            _logger.Warning("Не удалось получить информацию о видео для URL: {Url}", url);
            return (null, YtTrackError.YtNotFound);
        }
        _logger.Information("Получена информация о видео: {Title}", video.Title);
        if (video.Duration == TimeSpan.Zero)
        {
            _logger.Warning("Длительность видео равна нулю для видео: {Title}", video.Title);
            return (null, YtTrackError.TrackZeroDuration);
        }
        
        _logger.Information("Попытка получения потока для видео: {Title}", video.Title);
        var stream = await GetTrackStreamWithRetryAsync(video, cancellationToken);
        if (stream == null)
        {
            _logger.Error("Не удалось получить поток аудио для видео: {Title}", video.Title);
            return (null, YtTrackError.FailedToGetStream);
        }
        _logger.Information("Поток аудио получен для видео: {Title}", video.Title);
        var fakeVkAlbum =
            new VkAlbumId(-1, -1, "", "", video.Thumbnails.Count > 0 ? video.Thumbnails[0].Url : "", null);
        var artists = new List<TrackArtist> { new(video.Author.ChannelTitle, new ArtistId("", ArtistIdType.None)) };
        var fakeVkData = new YtTrackData(stream.Url, false, false, null, video.Duration ?? TimeSpan.MaxValue,
            GetFakeId(), "", null, null, null);
        var playlistTrack = new PlaylistTrack(video.Title, video.Description, fakeVkAlbum, artists, null, fakeVkData);
        _logger.Information("PlaylistTrack успешно создан для видео: {Title}", video.Title);
        return (playlistTrack, null);
    }

    public async Task<IStreamInfo?> GetTrackStreamWithRetryAsync(Video video, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int delay = 2000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var stream = await GetTrackStream(video, cancellationToken);
            if (stream != null)
                return stream;

            if (attempt < maxRetries) await Task.Delay(delay, cancellationToken);
        }

        return null; // Возврат null после всех попыток
    }


    private readonly Random _random = new();
    private IdInfo GetFakeId() => new(NextLong(_random), NextLong(_random), Guid.NewGuid().ToString());

    private static long NextLong(Random random)
    {
        var buffer = new byte[8];
        random.NextBytes(buffer);
        return BitConverter.ToInt64(buffer, 0);
    }

    private async Task<Video?> GetTrackInfo(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _youtube.Videos.GetAsync(url, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while getting track info for URL: {Url}", url);
            return null;
        }
    }

    private async Task<IStreamInfo?> GetTrackStream(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var video = await GetTrackInfo(url, cancellationToken);
            if (video == null) return null;
            return await GetTrackStream(video, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while getting track stream for URL: {Url}", url);
            return null;
        }
    }

    private static readonly SemaphoreSlim Semaphore = new(5);

    private async Task<IStreamInfo?> GetTrackStream(Video video, CancellationToken cancellationToken)
    {
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
            return streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while getting stream manifest for video ID: {VideoId}", video.Id);
            return null;
        }
        finally
        {
            Semaphore.Release();
        }
    }
}

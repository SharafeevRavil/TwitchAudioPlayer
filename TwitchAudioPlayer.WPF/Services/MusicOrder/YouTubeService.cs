using MusicX.Shared.Player;
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

public class YouTubeService
{
    private readonly YoutubeClient _youtube = new();

    public async Task<(PlaylistTrack? Track, YtTrackError? Error)> GetPlaylistTrack(string url, CancellationToken cancellationToken = default)
    {
        var video = await GetTrackInfo(url, cancellationToken);
        if (video == null) return (null, YtTrackError.YtNotFound);
        if (video.Duration == TimeSpan.Zero) return (null, YtTrackError.TrackZeroDuration); // stream
        var stream = await GetTrackStreamWithRetryAsync(video, cancellationToken);
        if (stream == null) return (null, YtTrackError.FailedToGetStream);
        var fakeVkAlbum = new VkAlbumId(-1, -1, "", "", video.Thumbnails.Count > 0 ? video.Thumbnails[0].Url : "", null);
        var artists = new List<TrackArtist> { new(video.Author.ChannelTitle, new ArtistId("", ArtistIdType.None)) };
        var fakeVkData = new VkTrackData(stream.Url, false, false, null, video.Duration ?? TimeSpan.MaxValue, 
            GetFakeId(), "", null, null, null);
        return (new PlaylistTrack(video.Title, video.Description, fakeVkAlbum, artists, null, fakeVkData), null);
    }

    public async Task<IStreamInfo?> GetTrackStreamWithRetryAsync(Video video, CancellationToken cancellationToken)
    {
        const int maxRetries = 8;
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
            Console.WriteLine(e);
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
            Console.WriteLine(e);
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
            Console.WriteLine(e);
            return null;
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
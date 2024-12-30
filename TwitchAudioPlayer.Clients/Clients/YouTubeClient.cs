using System.IO;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace TwitchAudioPlayer.Clients.Clients;

public class YouTubeClient : Client
{
    private readonly YoutubeClient _youtube = new();

    public override async Task<string> DownloadTrack(string url)
    {
        // var url = "https://www.youtube.com/watch?v=liPu1_aPH5k&list=RDbv2yNre7saU&index=17&ab_channel=RiotGamesMusic";
        var video = await _youtube.Videos.GetAsync(url);
        var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id);
        var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        await using var stream = await _youtube.Videos.Streams.GetAsync(audioStreamInfo);
        await using var fileStream = new FileStream("audio.mp3", FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(fileStream);

        return "audio.mp3";
    }
}
using TwitchAudioPlayer.Clients.Clients;
using TwitchAudioPlayer.Domain.Models;

namespace TwitchAudioPlayer.Domain.Services;

public class AudioService(VkClient vkClient)
{
    public async Task<List<AudioTrack>> GetAudioTracksAsync(string url)
    {
        var audioList = await vkClient.GetAudioListAsync(url);
        var audioTracks = new List<AudioTrack>();

        if (audioList == null) return audioTracks;
        audioTracks.AddRange(audioList.Select(audio => new AudioTrack(
            audio.Artist,
            audio.Title,
            audio.Url?.ToString(),
            audio.Album?.Thumb?.Photo68,
            TimeSpan.FromSeconds(audio.Duration)
        )));

        return audioTracks;
    }
}
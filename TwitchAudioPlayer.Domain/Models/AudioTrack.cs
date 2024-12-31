namespace TwitchAudioPlayer.Domain.Models;

public class AudioTrack
{
    public AudioTrack(string artist, string title, string? downloadLink, string? imageLink, TimeSpan duration)
    {
        Artist = artist;
        Title = title;
        DownloadLink = downloadLink;
        ImageLink = imageLink;
        Duration = duration;
    }


    public string Artist { get; set; }
    public string Title { get; set; }
    public string? DownloadLink { get; set; }
    public string? ImageLink { get; set; }
    public TimeSpan Duration { get; set; }
}
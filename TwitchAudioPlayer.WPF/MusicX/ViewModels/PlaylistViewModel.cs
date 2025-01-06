namespace TwitchAudioPlayer.WPF.MusicX.ViewModels;

[Serializable]
public record PlaylistData(long? PlaylistId, long OwnerId, string AccessKey, int? Count = null)
{
    public static PlaylistData Parse(string str)
    {
        var parts = str.Split('_');
        ArgumentOutOfRangeException.ThrowIfNotEqual(parts.Length, 3, nameof(str));

        return new PlaylistData(long.Parse(parts[1]), long.Parse(parts[0]), parts[2]);
    }
}

public record UserPlaylistData(long OwnerId, int? Count = null)
{
// я хуй знает зачем нужна эта поебота
    public static UserPlaylistData Parse(string str)
    {
        var parts = str.Split('_');
        ArgumentOutOfRangeException.ThrowIfNotEqual(parts.Length, 1, nameof(str));

        return new UserPlaylistData(long.Parse(parts[0]));
    }
}
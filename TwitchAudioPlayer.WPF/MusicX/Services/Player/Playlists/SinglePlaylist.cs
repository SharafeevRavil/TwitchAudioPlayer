using System.Text.Json.Serialization;
using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

[JsonConverter(typeof(PlaylistJsonConverter<SinglePlaylist, PlaylistTrack>))]
public class SinglePlaylist : PlaylistBase<PlaylistTrack>, IRandomAccessPlaylist, IShufflePlaylist
{
    private bool _canLoad = true;

    public SinglePlaylist(PlaylistTrack data)
    {
        Data = data;
    }

    public override PlaylistTrack Data { get; }

    public override bool CanLoad => _canLoad;

    public override IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null)
    {
        _canLoad = false;
        return new List<PlaylistTrack> { Data }.ToAsyncEnumerable();
    }

    public ValueTask<int> GetCountAsync()
    {
        return new ValueTask<int>(1);
    }

    public ValueTask<IEnumerable<PlaylistTrack>> GetRangeAsync(Range range, object? initiator = null)
    {
        return new ValueTask<IEnumerable<PlaylistTrack>>([Data]);
    }

    public IPlaylist ShuffleWithSeed(int seed)
    {
        return this;
    }
}
using System.Text.Json.Serialization;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

[JsonConverter(typeof(PlaylistJsonConverter<YouTubeQueuePlaylist, EquatableList<PlaylistTrack>>))]
public class YouTubeQueuePlaylist : PlaylistBase<EquatableList<PlaylistTrack>>, IRandomAccessPlaylist /*, IShufflePlaylist*/
{
    public YouTubeQueuePlaylist(IEnumerable<PlaylistTrack> data)
    {
        Data = new EquatableList<PlaylistTrack>(data);
    }

    public override EquatableList<PlaylistTrack> Data { get; }

    private int _offset;
    private const int loadCount = 1;
    public override bool CanLoad => _offset < Data.Count;

    public override IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null)
    {
        var dataToLoad = Data.Skip(_offset).Take(loadCount).ToList();
        _offset += dataToLoad.Count;
        OnTrackAdded((initiator, dataToLoad));
        return dataToLoad.ToAsyncEnumerable();
    }

    public ValueTask<int> GetCountAsync()
    {
        return new ValueTask<int>(Data.Count);
    }

    public ValueTask<IEnumerable<PlaylistTrack>> GetRangeAsync(Range range, object? initiator = null)
    {
        return new ValueTask<IEnumerable<PlaylistTrack>>(Data[range]);
    }

    public int AddTrack(PlaylistTrack track)
    {
        Data.Add(track);
        return Data.IndexOf(track);
    }

    public void AddTracks(IEnumerable<PlaylistTrack> tracks)
    {
        foreach (var track in tracks) Data.Add(track);
    }

    // public IPlaylist ShuffleWithSeed(int seed)
    // {
    //     var data = Data.ToList();
    //
    //     var random = new Random(seed);
    //     random.Shuffle(CollectionsMarshal.AsSpan(data));
    //
    //     return new ListPlaylist(data);
    // }
}
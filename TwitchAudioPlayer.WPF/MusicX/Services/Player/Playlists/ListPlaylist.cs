using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

[JsonConverter(typeof(PlaylistJsonConverter<ListPlaylist, EquatableList<PlaylistTrack>>))]
public class ListPlaylist : PlaylistBase<EquatableList<PlaylistTrack>>, IRandomAccessPlaylist, IShufflePlaylist
{
    private bool _canLoad = true;
    private PlaylistBase<EquatableList<PlaylistTrack>> _playlistBaseImplementation;

    public ListPlaylist(IEnumerable<PlaylistTrack> data)
    {
        Data = new EquatableList<PlaylistTrack>(data);
    }

    public override EquatableList<PlaylistTrack> Data { get; }

    public override bool CanLoad => _canLoad;

    public override IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null)
    {
        _canLoad = false;
        return Data.ToAsyncEnumerable();
    }

    public ValueTask<int> GetCountAsync()
    {
        return new ValueTask<int>(Data.Count);
    }

    public ValueTask<IEnumerable<PlaylistTrack>> GetRangeAsync(Range range, object? initiator = null)
    {
        return new ValueTask<IEnumerable<PlaylistTrack>>(Data[range]);
    }

    public IPlaylist ShuffleWithSeed(int seed)
    {
        var data = Data.ToList();

        var random = new Random(seed);
        random.Shuffle(CollectionsMarshal.AsSpan(data));

        return new ListPlaylist(data);
    }
}

public class EquatableList<T> : List<T>, IEquatable<EquatableList<T>> where T : IEquatable<T>
{
    public EquatableList(IEnumerable<T> data) : base(data)
    {
    }

    public EquatableList()
    {
    }

    public bool Equals(EquatableList<T>? other)
    {
        return other is not null && Count == other.Count && other.SequenceEqual(this);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as EquatableList<T>);
    }

    public override int GetHashCode()
    {
        return this.Select(b => b.GetHashCode()).Aggregate(HashCode.Combine);
    }
}
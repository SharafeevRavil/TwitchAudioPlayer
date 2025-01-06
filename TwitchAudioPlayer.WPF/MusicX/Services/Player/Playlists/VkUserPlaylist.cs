using System.Text.Json.Serialization;
using MusicX.Core.Models;
using MusicX.Core.Services;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.ViewModels;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

[JsonConverter(typeof(PlaylistJsonConverter<VkUserPlaylist, UserPlaylistData>))]
public class VkUserPlaylist : PlaylistBase<UserPlaylistData>, IRandomAccessPlaylist, IShufflePlaylist
{
    private readonly List<PlaylistTrack> _tracks = [];
    private readonly VkService _vkService;
    private int _count;

    private bool _firstLoad = true;
    private int _offset;

    private int? _seed;

    public VkUserPlaylist(VkService vkService, UserPlaylistData data)
    {
        _vkService = vkService;
        Data = data;
    }

    public int LoadCount { get; set; } = 40;
    public override UserPlaylistData Data { get; }

    public override IEnumerable<PlaylistTrack> GetCached()
    {
        return _tracks;
    }

    public override async IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null)
    {
        var (ownerId, _) = Data;
        var trackPlaylist = new Playlist
        {
            OwnerId = Data.OwnerId
        };

        await PerformFirstLoadAsync();

        var response = await _vkService.AudioGetAsync(null, ownerId, null, _offset, LoadCount, _seed);

        _offset += response.Items.Count;

        var tracks = response.Items.Select(audio => audio.ToTrack(trackPlaylist)).ToList();
        _tracks.AddRange(tracks);
        OnTrackAdded((initiator, tracks));

        foreach (var track in tracks) yield return track;
    }

    public override bool Equals(IPlaylist? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (other is not VkUserPlaylist playlist)
            return false;

        return Data.Equals(playlist.Data) && _seed == playlist._seed;
    }

    public override bool CanLoad => _firstLoad || _offset < _count;

    public async ValueTask<int> GetCountAsync()
    {
        if (Data.Count.HasValue)
            return Data.Count.Value;

        await PerformFirstLoadAsync();

        return _count;
    }

    public async ValueTask<IEnumerable<PlaylistTrack>> GetRangeAsync(Range range, object? initiator = null)
    {
        var (ownerId, _) = Data;
        var trackPlaylist = new Playlist
        {
            OwnerId = Data.OwnerId
        };

        await PerformFirstLoadAsync();

        var (offset, length) = range.GetOffsetAndLength(_count);
        var response = await _vkService.AudioGetAsync(null, ownerId, null, offset, length, _seed);

        var tracks = response.Items.Select(audio => audio.ToTrack(trackPlaylist)).ToList();
        _tracks.AddRange(tracks);
        OnTrackAdded((initiator, tracks));

        return tracks;
    }

    public IPlaylist ShuffleWithSeed(int seed)
    {
        return new VkUserPlaylist(_vkService, Data)
        {
            _seed = seed
        };
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Data, _seed);
    }

    private async ValueTask PerformFirstLoadAsync()
    {
        var (ownerId, _) = Data;

        if (_firstLoad)
        {
            // previous implementation (VkPlaylistPlaylist):
            // var playlist = await _vkService.GetPlaylistAsync(0, id, accessKey, ownerId);
            // _count = (int)playlist.Playlist.Count;

            _count = await _vkService.AudioGetCountAsync(null, ownerId);
            _offset = 0;

            _firstLoad = false;
        }
    }
}
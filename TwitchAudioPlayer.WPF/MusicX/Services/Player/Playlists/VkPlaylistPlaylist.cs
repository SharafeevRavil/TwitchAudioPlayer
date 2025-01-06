using System.Text.Json.Serialization;
using MusicX.Core.Models;
using MusicX.Core.Services;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.ViewModels;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

[JsonConverter(typeof(PlaylistJsonConverter<VkPlaylistPlaylist, PlaylistData>))]
public class VkPlaylistPlaylist : PlaylistBase<PlaylistData>, IRandomAccessPlaylist, IShufflePlaylist
{
    private const int LoadCount = 40;
    private readonly VkService _vkService;
    private int _count;

    private bool _firstLoad = true;
    private int _offset;

    private int? _seed;

    public VkPlaylistPlaylist(VkService vkService, PlaylistData data)
    {
        _vkService = vkService;
        Data = data;
    }

    public override PlaylistData Data { get; }

    public override async IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null)
    {
        var (id, ownerId, accessKey, _) = Data;
        var trackPlaylist = new Playlist
        {
            Id = Data.PlaylistId,
            OwnerId = Data.OwnerId,
            AccessKey = Data.AccessKey
        };

        await PerformFirstLoadAsync();

        var response = await _vkService.AudioGetAsync(id, ownerId, accessKey, _offset, LoadCount, _seed);

        _offset += response.Items.Count;

        foreach (var audio in response.Items) yield return audio.ToTrack(trackPlaylist);
    }

    public override bool Equals(IPlaylist? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (other is not VkPlaylistPlaylist playlist)
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
        var (id, ownerId, accessKey, _) = Data;
        var trackPlaylist = new Playlist
        {
            Id = Data.PlaylistId,
            OwnerId = Data.OwnerId,
            AccessKey = Data.AccessKey
        };

        await PerformFirstLoadAsync();

        var (offset, length) = range.GetOffsetAndLength(_count);
        var response = await _vkService.AudioGetAsync(id, ownerId, accessKey, offset, length, _seed);
        return response.Items.Select(audio => audio.ToTrack(trackPlaylist));
    }

    public IPlaylist ShuffleWithSeed(int seed)
    {
        return new VkPlaylistPlaylist(_vkService, Data)
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
        var (id, ownerId, accessKey, _) = Data;

        if (_firstLoad)
        {
            // notfixme: я не помню нахуй я это закомментил и пометил как fixm. я вообще этот класс не юзаю вроде бы
            // var playlist = await _vkService.GetPlaylistAsync(0, id, accessKey, ownerId);
            // _count = (int)playlist.Playlist.Count;
            _offset = 0;

            _firstLoad = false;
        }
    }
}
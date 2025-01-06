using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

public interface IPlaylist<out TData> : IPlaylist where TData : class, IEquatable<TData>
{
    TData Data { get; }
}

[JsonConverter(typeof(PlaylistJsonConverter))]
public interface IPlaylist : IEquatable<IPlaylist>
{
    bool CanLoad { get; }
    IEnumerable<PlaylistTrack> GetCached();
    IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null);

    event EventHandler<(object? initiator, IEnumerable<PlaylistTrack> newTracks)>? TrackAdded
    {
        add => throw new NotImplementedException();
        remove => throw new NotImplementedException();
    }
}

public interface IRandomAccessPlaylist : IPlaylist
{
    ValueTask<int> GetCountAsync();
    ValueTask<IEnumerable<PlaylistTrack>> GetRangeAsync(Range range, object? initiator = null);
}

public interface IShufflePlaylist : IPlaylist
{
    IPlaylist ShuffleWithSeed(int seed);
}

public abstract class PlaylistBase<TData> : IPlaylist<TData> where TData : class, IEquatable<TData>
{
    public virtual IEnumerable<PlaylistTrack> GetCached()
    {
        return new List<PlaylistTrack>();
    }

    public abstract IAsyncEnumerable<PlaylistTrack> LoadAsync(object? initiator = null);
    public abstract bool CanLoad { get; }
    public abstract TData Data { get; }

    public virtual bool Equals(IPlaylist? other)
    {
        return other is PlaylistBase<TData> { Data: { } otherData } && GetType() == other.GetType() &&
               Data.Equals(otherData);
    }

    public event EventHandler<(object? initiator, IEnumerable<PlaylistTrack> newTracks)>? TrackAdded;

    public override bool Equals(object? obj)
    {
        return Equals((IPlaylist?)obj);
    }

    public override int GetHashCode()
    {
        return Data.GetHashCode();
    }

    protected virtual void OnTrackAdded((object? initiator, IEnumerable<PlaylistTrack> newTracks) e)
    {
        TrackAdded?.Invoke(this, e);
    }
}

public class PlaylistJsonConverter<TPlaylist, TData> : JsonConverter<TPlaylist>
    where TPlaylist : class, IPlaylist<TData> where TData : class, IEquatable<TData>
{
    public override TPlaylist? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var data = JsonSerializer.Deserialize<TData>(ref reader, options);

        return data is null ? null : ActivatorUtilities.CreateInstance<TPlaylist>(StaticService.Container, data);
    }

    public override void Write(Utf8JsonWriter writer, TPlaylist value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.Data, options);
    }
}

public class PlaylistJsonConverter : JsonConverter<IPlaylist>
{
    public override IPlaylist? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject || !reader.Read())
            throw new JsonException("Unexpected end when reading playlist.");

        reader.Read();
        var type = reader.GetString();
        reader.Read();

        IPlaylist? playlist = type switch
        {
            "single" => JsonSerializer.Deserialize<SinglePlaylist>(ref reader, options),
            "list" => JsonSerializer.Deserialize<ListPlaylist>(ref reader, options),
            "radio" => JsonSerializer.Deserialize<RadioPlaylist>(ref reader, options),
            "vkBlock" => JsonSerializer.Deserialize<VkBlockPlaylist>(ref reader, options),
            "vkPlaylist" => JsonSerializer.Deserialize<VkPlaylistPlaylist>(ref reader, options),
            "mix" => JsonSerializer.Deserialize<MixPlaylist>(ref reader, options),
            _ => throw new JsonException("Unsupported playlist type.")
        };

        if (playlist is null || !reader.Read())
            throw new JsonException("Unexpected end when reading playlist.");

        return playlist;
    }

    public override void Write(Utf8JsonWriter writer, IPlaylist value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        void WriteObject<T>(string type, T data)
        {
            writer.WriteString("$type", type);
            writer.WritePropertyName("Data");
            JsonSerializer.Serialize(writer, data, options);
        }

        switch (value)
        {
            case SinglePlaylist playlist:
                WriteObject("single", playlist);
                break;
            case ListPlaylist playlist:
                WriteObject("list", playlist);
                break;
            case RadioPlaylist playlist:
                WriteObject("radio", playlist);
                break;
            case VkBlockPlaylist playlist:
                WriteObject("vkBlock", playlist);
                break;
            case VkPlaylistPlaylist playlist:
                WriteObject("vkPlaylist", playlist);
                break;
            case MixPlaylist playlist:
                WriteObject("mix", playlist);
                break;
            default:
                throw new JsonException("Unsupported playlist type.");
        }

        writer.WriteEndObject();
    }
}
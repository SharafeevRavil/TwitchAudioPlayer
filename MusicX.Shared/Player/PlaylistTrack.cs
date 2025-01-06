using System.Text.Json.Serialization;

namespace MusicX.Shared.Player;

public sealed record PlaylistTrack(
    string Title,
    string Subtitle,
    AlbumId? AlbumId,
    ICollection<TrackArtist> MainArtists,
    ICollection<TrackArtist>? FeaturedArtists,
    TrackData Data)
{
    public bool Equals(PlaylistTrack? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return AlbumId == other.AlbumId && Data == other.Data;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(AlbumId?.GetHashCode(), Data.GetHashCode());
    }
}

[JsonDerivedType(typeof(VkTrackData), "vk")]
[JsonDerivedType(typeof(BoomTrackData), "boom")]
[JsonDerivedType(typeof(DownloaderData), "downloader")]
public abstract record TrackData(string Url, bool IsLiked, bool IsExplicit, TimeSpan Duration);

public sealed record BoomTrackData(string Url, bool IsLiked, bool IsExplicit, TimeSpan Duration, string Id) : TrackData(
    Url, IsLiked, IsExplicit, Duration)
{
    public bool Equals(BoomTrackData? other)
    {
        return Id == other?.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
}

public record IdInfo(long? Id, long OwnerId, string AccessKey)
{
    public string ToOwnerIdString()
    {
        return $"{OwnerId}_{Id}";
    }

    public override string ToString()
    {
        return $"{OwnerId}_{Id}_{AccessKey}";
    }
}

public sealed record VkTrackData(
    string Url,
    bool IsLiked,
    bool IsExplicit,
    bool? HasLyrics,
    TimeSpan Duration,
    IdInfo Info,
    string TrackCode,
    string? ParentBlockId,
    IdInfo? Playlist,
    string? MainColor) : TrackData(Url, IsLiked, IsExplicit, Duration)
{
    public bool Equals(VkTrackData? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return Info == other.Info;
    }

    public override int GetHashCode()
    {
        return Info.GetHashCode();
    }
}

public record DownloaderData(string Url, bool IsLiked, bool IsExplicit, TimeSpan Duration, string PlaylistName)
    : TrackData(
        Url, IsLiked, IsExplicit, Duration);
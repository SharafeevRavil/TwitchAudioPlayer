using System.Text.Json.Serialization;

namespace MusicX.Shared.Player;

[JsonDerivedType(typeof(VkAlbumId), "vk")]
[JsonDerivedType(typeof(BoomAlbumId), "boom")]
public abstract record AlbumId(string? Name, string? CoverUrl, string? BigCoverUrl);

public sealed record UnknownAlbumId(string? CoverUrl, string? BigCoverUrl) : AlbumId(null, CoverUrl, BigCoverUrl);

public sealed record VkAlbumId(
    long Id,
    long OwnerId,
    string AccessKey,
    string Name,
    string CoverUrl,
    string? BigCoverUrl) : AlbumId(Name, CoverUrl, BigCoverUrl)
{
    public bool Equals(VkAlbumId? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return Id == other.Id && OwnerId == other.OwnerId && AccessKey == other.AccessKey;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id.GetHashCode(), OwnerId.GetHashCode(), AccessKey.GetHashCode());
    }
}

public sealed record BoomAlbumId(string Id, string Name, string CoverUrl, string? BigCoverUrl)
    : AlbumId(Name, CoverUrl, BigCoverUrl)
{
    public bool Equals(BoomAlbumId? other)
    {
        return Id == other?.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
}
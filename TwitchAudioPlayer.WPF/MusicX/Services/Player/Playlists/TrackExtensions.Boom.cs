using MusicX.Core.Models.Boom;
using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

public static partial class TrackExtensions
{
    public static PlaylistTrack ToTrack(this Track track)
    {
        var mainArtists = track.Artists?.Any() == false
            ? new[]
            {
                track.Artist?.ToTrackArtist() ??
                new TrackArtist(track.ArtistDisplayName, new ArtistId(track.ArtistDisplayName, ArtistIdType.None))
            }
            : track.Artists!.Select(ToTrackArtist).ToArray();

        return new PlaylistTrack(track.Name, string.Empty, track.Album?.ToAlbumId(), mainArtists,
            Array.Empty<TrackArtist>(),
            new BoomTrackData(track.File, track.IsLiked, track.IsExplicit, TimeSpan.FromSeconds(track.Duration),
                track.ApiId));
    }

    public static TrackArtist ToTrackArtist(this Artist artist)
    {
        return new TrackArtist(artist.Name, new ArtistId(artist.ApiId, ArtistIdType.Boom));
    }

    public static BoomAlbumId ToAlbumId(this Album album)
    {
        return new BoomAlbumId(album.ApiId, album.Name, album.Cover.Url, null);
    }
}
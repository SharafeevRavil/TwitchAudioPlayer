using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public class MusicOrderWithTrack
{
    public MusicOrder MusicOrder { get; set; }
    public PlaylistTrack PlaylistTrack { get; set; }
}

public class MusicOrder
{
    public string Uri { get; }
    public DateTimeOffset Date { get; }
    public OrderType Type { get; }
    public Played Played { get; set; }

    public MusicOrder(string uri, DateTimeOffset date, OrderType type, Played played = Played.NotPlayed)
    {
        Uri = uri;
        Date = date;
        Type = type;
        Played = played;
    }
}

public enum Played
{
    NotPlayed = 10,
    Played = 20,
    Invalid = 30
}

public class PlayedComparer : IComparer<Played>
{
    public int Compare(Played x, Played y) => ToInt(x) - ToInt(y);

    private static int ToInt(Played x) =>
        x switch
        {
            Played.Played => 0,
            Played.NotPlayed => 1,
            Played.Invalid => 2,
            _ => throw new ArgumentException($"Invalid Played value: {x}")
        };
}

public enum OrderType
{
    Twitch = 10,
    DonationAlerts = 20
}
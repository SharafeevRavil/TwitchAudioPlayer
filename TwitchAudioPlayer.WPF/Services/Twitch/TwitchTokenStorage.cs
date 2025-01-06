using System.IO;

namespace TwitchAudioPlayer.WPF.Services.Twitch;

public class TwitchTokenStorage : AbstractTokenStorage<string>
{
    protected override string Target { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TwitchAudioPlayer", "TwitchToken");
}
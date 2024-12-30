namespace TwitchAudioPlayer.Clients.Clients;

public interface IVkAuthStorage
{
    public void SaveToken(string token);
    public string? GetToken();
}
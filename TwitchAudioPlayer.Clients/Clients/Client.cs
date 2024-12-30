using System;
using System.Threading.Tasks;

namespace TwitchAudioPlayer.Clients.Clients;

public abstract class Client
{
    public virtual Task<string> DownloadTrack(string url)
    {
        throw new NotImplementedException();
    }
}
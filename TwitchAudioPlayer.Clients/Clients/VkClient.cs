using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VkNet;
using VkNet.AudioBypassService.Extensions;
using VkNet.Model;
using VkNet.Enums.Filters;

namespace TwitchAudioPlayer.Clients.Clients;

public partial class VkClient /* : Client*/
{
    private readonly IVkAuthStorage _vkAuthStorage;
    private readonly VkApi _api;

    public VkClient(IVkAuthStorage vkAuthStorage)
    {
        _vkAuthStorage = vkAuthStorage;
        
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAudioBypass();
        _api = new VkApi(services);
        LoadToken();
    }

    private void LoadToken()
    {
        var token = _vkAuthStorage.GetToken();
        if (token == null)
            return;

        _api.Authorize(new ApiAuthParams
        {
            IsTokenUpdateAutomatically = true,
            AccessToken = token
        });
    }

    public bool IsAuthorized => _api.IsAuthorized;

    public void Authorize(string login, string password)
    {
        _api.Authorize(new ApiAuthParams
        {
            IsTokenUpdateAutomatically = true,
            // ApplicationId = 1980660, // idk app id from the habr guide
            ApplicationId = 2274003,
            Login = login,
            Password = password,
            Settings = Settings.All
        });
        _vkAuthStorage.SaveToken(_api.Token);
    }
    
    [GeneratedRegex(@"vk.com/audios(-?\d+)(\?.*)?")]
    private static partial Regex VkAudioRegex1();
    
    public async Task<List<Audio>?> GetAudioListAsync(string url)
    {
        var match = VkAudioRegex1().Match(url);
        if (!match.Success)
        {
            Console.WriteLine("Неверная ссылка на аудиозаписи.");
            return null;
        }

        var id = long.Parse(match.Groups[1].Value);
        
        var offset = 0;
        const int fetchCount = 6000;
        List<Audio> audioList = [];
        while (true)
        {
            var audio = await _api.Audio.GetAsync(new AudioGetParams
            {
                OwnerId = id,
                Count = fetchCount,
                Offset = offset
            });
            offset += audio.Count;
            audioList.AddRange(audio);
            
            if (audio.Count < fetchCount)
                break;
        }
        
        return audioList;
    }

    public IReadOnlyCollection<string> GetAvailableAudioLinkFormats() =>
    [
        "https://vk.com/audios123456789"
    ];
}
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.Services;

public class UserSettings
{
    //player
    public Key PrevKey { get; set; } = Key.F7; 
    public ModifierKeys PrevModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Shift;
    public Key PauseKey { get; set; } = Key.F8; 
    public ModifierKeys PauseModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Shift;
    public Key NextKey { get; set; } = Key.F9; 
    public ModifierKeys NextModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Shift;
    public Key VolMuteKey { get; set; } = Key.F10; 
    public ModifierKeys VolMuteModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Shift; 
    public Key VolDownKey { get; set; } = Key.F11; 
    public ModifierKeys VolDownModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Shift;
    public Key VolUpKey { get; set; } = Key.F12;
    public ModifierKeys VolUpModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Shift; 
    
    //vk
    public long? VkUserId { get; set; }

    //yt
    public double MaxMinutesLength { get; set; } = 6;
    public YouTubePlaybackMode YouTubePlaybackMode { get; set; } = YouTubePlaybackMode.Browser;
    public bool BrowserPlayerTopmost { get; set; } = true;
    //twitch
    public string? TwitchRewardTitle { get; set; } = "Заказ музыки";
    public string? TwitchRewardPrompt { get; set; } = "Укажите ссылку на YouTube видео. Максимальная длительность - 6 минут.";

    public uint? TwitchRewardCost { get; set; } = 5000;
    //da
    public long? DaAppId { get; set; }
    public string? DaAppKey { get; set; }
    public string? DaWidgetToken { get; set; }
}

public class UserSettingsManager : IUserSettingsManager
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public UserSettingsManager(string filePath)
    {
        _filePath = filePath;
        Settings = LoadSettingsInternal();
        _jsonSerializerOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    public event EventHandler<UserSettings>? SettingsChanged;

    public UserSettings Settings { get; }

    // похуй на потокобезопасность, в 99% случаев не пригодится
    public async Task SaveSettingsAsync()
    {
        await Task.Run(() =>
        {
            var json = JsonSerializer.Serialize(Settings, _jsonSerializerOptions);

            EnsureDirectoryExists(_filePath);
            File.WriteAllText(_filePath, json);
            SettingsChanged?.Invoke(this, Settings);
        });
    }

    private UserSettings LoadSettingsInternal()
    {
        if (!File.Exists(_filePath)) return new UserSettings();
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (Directory.Exists(directory)) return;
        Directory.CreateDirectory(directory!);
    }
}

public interface IUserSettingsManager
{
    UserSettings Settings { get; }

    public event EventHandler<UserSettings>? SettingsChanged;

    Task SaveSettingsAsync();
}

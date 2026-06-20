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
    public bool UseSeparateSourceVolumes { get; set; } = true;
    public double VkVolume { get; set; } = 0.01;
    public double YouTubeVolume { get; set; } = 1;
    public bool BrowserPlayerTopmost { get; set; } = true;
    public bool AutoPlayYouTubeForVk { get; set; }
    public ChatGptResolverSettings ChatGptResolver { get; set; } = new();
    public WindowBoundsSettings MainWindowBounds { get; set; } = new();
    public WindowBoundsSettings BrowserPlayerWindowBounds { get; set; } = new();
    //twitch
    public string? TwitchRewardTitle { get; set; } = "Заказ музыки";
    public string? TwitchRewardPrompt { get; set; } = "Укажите ссылку на YouTube видео. Максимальная длительность - 6 минут.";

    public uint? TwitchRewardCost { get; set; } = 5000;
    //da
    public long? DaAppId { get; set; }
    public string? DaAppKey { get; set; }
    public string? DaWidgetToken { get; set; }
}

public class ChatGptResolverSettings
{
    public bool Enabled { get; set; }
    public string ProjectName { get; set; } = "TwitchAudioPlayer";
    public Guid? ActiveAccountId { get; set; }
    public List<ChatGptAccountSettings> Accounts { get; set; } = [];
}

public class ChatGptAccountSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "ChatGPT account";
    public string? ConversationUrl { get; set; }
    public string? ConversationProjectName { get; set; }
}

public class WindowBoundsSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool IsMaximized { get; set; }
}

public class UserSettingsManager : IUserSettingsManager
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly object _saveLock = new();

    public UserSettingsManager(string filePath)
    {
        _filePath = filePath;
        Settings = LoadSettingsInternal();
        _jsonSerializerOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    public event EventHandler<UserSettings>? SettingsChanged;

    public UserSettings Settings { get; }

    public async Task SaveSettingsAsync()
    {
        await Task.Run(WriteSettingsFile);
        SettingsChanged?.Invoke(this, Settings);
    }

    public Task SaveSettingsSilentlyAsync() => Task.Run(WriteSettingsFile);

    public void SaveSettingsSilently() => WriteSettingsFile();

    private void WriteSettingsFile()
    {
        lock (_saveLock)
        {
            var json = JsonSerializer.Serialize(Settings, _jsonSerializerOptions);

            EnsureDirectoryExists(_filePath);
            File.WriteAllText(_filePath, json);
        }
    }

    private UserSettings LoadSettingsInternal()
    {
        if (!File.Exists(_filePath)) return new UserSettings();
        var json = File.ReadAllText(_filePath);
        var settings = JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        settings.ChatGptResolver ??= new ChatGptResolverSettings();
        settings.ChatGptResolver.Accounts ??= [];
        return settings;
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
    Task SaveSettingsSilentlyAsync();
    void SaveSettingsSilently();
}

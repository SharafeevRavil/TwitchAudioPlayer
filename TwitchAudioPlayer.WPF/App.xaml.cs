using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicX.Core.Services;
using NLog;
using Serilog;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Sources;
using TwitchAudioPlayer.WPF.MusicX.Services.Stores;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.ChatGpt;
using TwitchAudioPlayer.WPF.Services.DonationAlerts;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.Services.Twitch;
using TwitchAudioPlayer.WPF.ViewModels;
using TwitchAudioPlayer.WPF.ViewModels.Modals;
using TwitchAudioPlayer.WPF.Views;
using TwitchAudioPlayer.WPF.Views.Modals;
using VkNet.AudioBypassService.Abstractions;
using VkNet.AudioBypassService.Extensions;
using VkNet.Extensions.DependencyInjection;
using CustomSectionsService = TwitchAudioPlayer.WPF.MusicX.Services.CustomSectionsService;


namespace TwitchAudioPlayer.WPF;

using CustomSectionsService = MusicX.Services.CustomSectionsService;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IServiceProvider _serviceProvider = null!; // Initialized in OnStartup

    protected override void OnStartup(StartupEventArgs e)
    {
        Environment.SetEnvironmentVariable("SLAVA_UKRAINI", "1", EnvironmentVariableTarget.Process);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        // var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        var startingWindow = _serviceProvider.GetRequiredService<StartingWindow>();
        startingWindow.Show();

        base.OnStartup(e);
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        var logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TwitchAudioPlayer", "logs", $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog();
        });

        var settingsFilePathFolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TwitchAudioPlayer");
        var settingsFilePath = Path.Combine(new DirectoryInfo(settingsFilePathFolder).FullName, "userSettings.json");
        var settingsManager = new UserSettingsManager(settingsFilePath);
        services.AddSingleton<IUserSettingsManager>(settingsManager);

        services.AddTransient<IWindowService, WindowService>();
        services.AddSingleton<BrowserPlayerService>();
        services.AddSingleton<BrowserPlayerWindowService>();
        services.AddSingleton<MusicOrderRepository>();

        services.AddSingleton<DonationAlertsService>();
        services.AddTransient<DonationAlertsOrdersNotifier>();

        services.AddSingleton<TwitchService>();
        services.AddSingleton<TwitchTokenStorage>();
        services.AddTransient<TwitchOrdersNotifier>();

        services.AddSingleton<MusicOrderService>();
        services.AddTransient<YouTubeService>();
        services.AddSingleton<YouTubeSearchService>();
        services.AddSingleton<ChatGptResolverService>();
        services.AddSingleton<VkYouTubePlaybackService>();
        services.AddSingleton<ApplicationStatusService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<VkAudioViewModel>();
        services.AddTransient<AudioPlayerViewModel>();
        services.AddSingleton<BrowserPlayerViewModel>();
        services.AddTransient<VkSettingsViewModel>();
        services.AddTransient<YtAudioViewModel>();
        services.AddTransient<YtSettingsViewModel>();
        services.AddTransient<HotkeySettingsViewModel>();
        services.AddTransient<ChatGptSettingsViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<VkAudioView>();
        services.AddTransient<AudioTrackControl>();
        services.AddTransient<AudioPlayerView>();
        services.AddSingleton<BrowserPlayerWindow>();
        services.AddSingleton<StartingWindow>();
        services.AddTransient<VkSettingsView>();
        services.AddTransient<YtAudioView>();
        services.AddTransient<YtSettingsView>();
        services.AddTransient<HotkeySettingsView>();
        services.AddTransient<ChatGptSettingsView>();

        InitMusicX();

        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Приложение запущено");
    }

    private static void InitMusicX()
    {
        var collection = new ServiceCollection();

        // collection.AddSingleton<IAsyncCaptchaSolver, CaptchaSolverService>();

        collection.AddSingleton<IVkTokenStore, TokenStore>();
        collection.AddSingleton<IDeviceIdStore, DeviceIdStore>();
        collection.AddSingleton<IExchangeTokenStore, ExchangeTokenStore>();

        collection.AddAudioBypass();
        collection.AddVkNet();

        collection.AddSingleton<VkService>();
        collection.AddSingleton<GithubService>();
        collection.AddSingleton<BoomService>();
        collection.AddSingleton(LogManager.GetLogger("Common"));
        collection.AddSingleton<GeniusService>();

        collection.AddSingleton<ITrackMediaSource, BoomMediaSource>();
        collection.AddSingleton<ITrackMediaSource, VkMediaSource>();

        // collection.AddSingleton<ITrackStatsListener, VkTrackStats>();

        collection.AddSingleton<ConfigService>();
        collection.AddSingleton<PlayerService, PlayerService>(); // свой плеер
        collection.AddSingleton<ICustomSectionsService, CustomSectionsService>();

        collection.AddSingleton(
            s => new BackendConnectionService(s.GetRequiredService<Logger>(), StaticService.Version));

        StaticService.Container = collection.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs exitEventArgs)
    {
        if (StaticService.Container is IDisposable musicXContainer)
            musicXContainer.Dispose();

        // Dispose of services if needed
        if (_serviceProvider is IDisposable disposable) disposable.Dispose();

        base.OnExit(exitEventArgs);
    }
}

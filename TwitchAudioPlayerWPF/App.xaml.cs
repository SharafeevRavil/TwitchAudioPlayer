using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TwitchAudioPlayer.Clients.Clients;
using TwitchAudioPlayerWPF.Utils;
using TwitchAudioPlayerWPF.ViewModels;
using TwitchAudioPlayerWPF.Views;

namespace TwitchAudioPlayerWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IServiceProvider _serviceProvider = null!; // Initialized in OnStartup

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        // Configure Logging
        services.AddLogging();
        
        // Register Services
        services.AddTransient<IVkAuthStorage, VkAuthStorage>();
        services.AddSingleton<VkClient, VkClient>();

        // Register ViewModels
        services.AddSingleton<MainViewModel, MainViewModel>();

        // Register Views
        services.AddSingleton<MainWindow>();
    }
    
    protected override void OnExit(ExitEventArgs exitEventArgs)
    {
        // Dispose of services if needed
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
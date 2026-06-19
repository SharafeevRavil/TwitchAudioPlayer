using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MusicX.Core.Services;
using NLog;
using TwitchAudioPlayer.WPF.MusicX.Models;
using TwitchAudioPlayer.WPF.MusicX.Services;
using TwitchAudioPlayer.WPF.MusicX.Services.Player;
using TwitchAudioPlayer.WPF.Services;
using VkNet.Abstractions;
using VkNet.AudioBypassService.Abstractions;
using VkNet.AudioBypassService.Models.Auth;
using VkNet.Exception;
using IAuthCategory = VkNet.AudioBypassService.Abstractions.Categories.IAuthCategory;

namespace TwitchAudioPlayer.WPF.Views;

public partial class StartingWindow : Window
{
    private readonly IServiceProvider _normalContainer;

    public StartingWindow(IServiceProvider normalContainer, IUserSettingsManager userSettingsManager)
    {
        _normalContainer = normalContainer;
        InitializeComponent();
        WindowBoundsHelper.CenterOver(this, userSettingsManager.Settings.MainWindowBounds);
    }

    // долбоебская хуйня
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.Run(async () =>
        {
            var container = StaticService.Container;

            var vkService = container.GetRequiredService<VkService>();
            var logger = container.GetRequiredService<Logger>();

            var configService = container.GetRequiredService<ConfigService>();
            var config = await configService.GetConfig();

            await Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(config.AccessToken))
                    {
                        if (string.IsNullOrEmpty(config.AnonToken))
                            await container.GetRequiredService<IVkApiAuthAsync>()
                                .AuthorizeAsync(new AndroidApiAuthParams());

                        //fixme:
                        await Login();

                        // ActivatorUtilities.CreateInstance<AccountsWindow>(container).Show();
                    }
                    else
                    {
                        try
                        {
                            await vkService.SetTokenAsync(config.AccessToken);

                            var mainWindow = ActivatorUtilities.CreateInstance<MainWindow>(_normalContainer);
                            Application.Current.MainWindow = mainWindow;
                            mainWindow.Show();
                            Close();
                        }
                        catch (VkApiException ex) when (ex.Message.Contains("has expired"))
                        {
                            await Logout(config, container, _normalContainer);
                        }
                        catch (VkApiMethodInvokeException ex) when (ex.ErrorCode is 5 or 1117)
                        {
                            await Logout(config, container, _normalContainer);
                        }
                    }
                }
                catch (HttpRequestException ex)
                {
                    logger.Error(ex, ex.Message);

                    // var error = new NoInternetWindow();
                    //
                    // error.Show();
                    // this.Close();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.Message);

                    // var error = new FatalErrorView(ex);
                    //
                    // error.Show();
                    // this.Close();
                }
            });
        });
    }

    private async Task Login()
    {
        var player = StaticService.Container.GetRequiredService<PlayerService>();
        var vkApi = StaticService.Container.GetRequiredService<IVkApi>();
        var configService = StaticService.Container.GetRequiredService<ConfigService>();
        var exchangeTokenStore = StaticService.Container.GetRequiredService<IExchangeTokenStore>();
        var vkApiAuth = StaticService.Container.GetRequiredService<IVkApiAuthAsync>();
        var authCategory = StaticService.Container
            .GetRequiredService<IAuthCategory>();

        var login = "";
        var password = "";
        // login
        var (_, isPhone, authFlow, flowNames, sid, nextStep) = await authCategory.ValidateAccountAsync(login,
            passkeySupported: true, loginWays:
            new[]
            {
                LoginWay.Password, LoginWay.Push, LoginWay.Sms, LoginWay.CallReset, LoginWay.ReserveCode,
                LoginWay.Codegen, LoginWay.Email, LoginWay.Passkey
            });
        // password
        await vkApiAuth.AuthorizeAsync(new AndroidApiAuthParams(login, sid, null,
            new[] { LoginWay.Push, LoginWay.Email }, password)
        {
            AndroidGrantType = AndroidGrantType.Password
        });
        // after login
        var (token, profile) = await authCategory.GetExchangeToken();

        vkApi.UserId = profile.Id;

        configService.Config.UserId = profile.Id;
        configService.Config.UserName = $"{profile.FirstName} {profile.LastName}";

        await exchangeTokenStore.SetExchangeTokenAsync(token);

        // LoggedIn?.Invoke(this, EventArgs.Empty);
    }

    private async Task Logout(ConfigModel config, IServiceProvider container, IServiceProvider normalContainer)
    {
        var configService = container.GetRequiredService<ConfigService>();

        config.AccessToken = null;
        config.UserName = null!;
        config.UserId = 0;
        config.AccessTokenTtl = default;
        config.ExchangeToken = null;

        if (string.IsNullOrEmpty(config.AnonToken))
            await container.GetRequiredService<IVkApiAuthAsync>()
                .AuthorizeAsync(new AndroidApiAuthParams());

        await configService.SetConfig(config);

        // ActivatorUtilities.CreateInstance<AccountsWindow>(normalContainer).Show();

        Close();
    }
}

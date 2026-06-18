using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.DonationAlerts;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.Services.Proxy;
using TwitchAudioPlayer.WPF.Services.Twitch;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class YtSettingsViewModel : ModalViewModelBase
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly DonationAlertsService _donationAlertsService;
    private readonly TwitchService _twitchService;
    private readonly IProxyService _proxyService;

    [ObservableProperty] private double? _maxMinutesLength;
    [ObservableProperty] private YouTubePlaybackMode _youTubePlaybackMode;

    public YtSettingsViewModel(IUserSettingsManager userSettingsManager, DonationAlertsService donationAlertsService,
        TwitchService twitchService, IProxyService proxyService)
    {
        _userSettingsManager = userSettingsManager;
        _donationAlertsService = donationAlertsService;
        _twitchService = twitchService;
        _proxyService = proxyService;

        var dispatcher = Dispatcher.CurrentDispatcher;
        userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(NotifySettingsChanged);

        // YT
        MaxMinutesLength = _userSettingsManager.Settings.MaxMinutesLength;
        YouTubePlaybackMode = _userSettingsManager.Settings.YouTubePlaybackMode;
        LoadProxySettings();

        // Twitch
        TwitchRewardTitle = _userSettingsManager.Settings.TwitchRewardTitle;
        TwitchRewardPrompt = _userSettingsManager.Settings.TwitchRewardPrompt;
        TwitchRewardCost = _userSettingsManager.Settings.TwitchRewardCost;
        dispatcher.Invoke(async () => await CheckTwitchServiceStatus());

        // DA
        _daAppId = _userSettingsManager.Settings.DaAppId;
        _daAppKey = _userSettingsManager.Settings.DaAppKey;
        _daWidgetToken = _userSettingsManager.Settings.DaWidgetToken;

        IsDaAuth = donationAlertsService.IsAuth;
        IsDaAuth = true; // спрятал настройки, пока не юзается public DA API

        IsDaWidgetTokenValid = false;
        dispatcher.Invoke(async () => IsDaWidgetTokenValid = await _donationAlertsService.CheckWidgetTokenValid());
    }

    private async Task NotifySettingsChanged()
    {
        DonationAuthCommand.NotifyCanExecuteChanged();

        IsDaWidgetTokenValid = await _donationAlertsService.CheckWidgetTokenValid();
    }

    protected override async Task SaveAsync()
    {
        // Yt
        if (MaxMinutesLength.HasValue) _userSettingsManager.Settings.MaxMinutesLength = MaxMinutesLength.Value;
        _userSettingsManager.Settings.YouTubePlaybackMode = YouTubePlaybackMode;
        // Twitch
        _userSettingsManager.Settings.TwitchRewardTitle = TwitchRewardTitle;
        _userSettingsManager.Settings.TwitchRewardPrompt = TwitchRewardPrompt;
        _userSettingsManager.Settings.TwitchRewardCost = TwitchRewardCost;
        // Da
        _userSettingsManager.Settings.DaAppId = DaAppId;
        _userSettingsManager.Settings.DaAppKey = DaAppKey;
        _userSettingsManager.Settings.DaWidgetToken = DaWidgetToken;
        ApplyProxySettings();

        await _userSettingsManager.SaveSettingsAsync();
        await base.SaveAsync();
    }

    #region Proxy

    public IReadOnlyList<YouTubePlaybackMode> YouTubePlaybackModes { get; } = Enum.GetValues<YouTubePlaybackMode>();

    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues<ProxyMode>();

    [ObservableProperty] private ProxyMode _proxyMode;
    [ObservableProperty] private string? _externalProxyUrl;
    [ObservableProperty] private string? _singleNodeUri;
    [ObservableProperty] private string? _subscriptionUrl;
    [ObservableProperty] private int _localHttpPort;
    [ObservableProperty] private string _proxyTestStatusText = "";
    [ObservableProperty] private bool _isProxyTesting;
    [ObservableProperty] private bool _isProxyTestEnabled = true;
    [ObservableProperty] private bool _isProxyExternalMode;
    [ObservableProperty] private bool _isProxySingleMode;
    [ObservableProperty] private bool _isProxySubscriptionMode;

    partial void OnProxyModeChanged(ProxyMode value)
    {
        IsProxyExternalMode = value == ProxyMode.External;
        IsProxySingleMode = value == ProxyMode.Single;
        IsProxySubscriptionMode = value == ProxyMode.Subscription;
    }

    partial void OnIsProxyTestingChanged(bool value)
    {
        IsProxyTestEnabled = !value;
    }

    [RelayCommand]
    private async Task ProxyTestAsync()
    {
        IsProxyTesting = true;
        ProxyTestStatusText = "Testing proxy...";

        try
        {
            ApplyProxySettings();
            await _userSettingsManager.SaveSettingsAsync();
            var result = await _proxyService.TestProxyAsync();
            ProxyTestStatusText = result.Success ? "Proxy test succeeded" : result.Message;
        }
        finally
        {
            IsProxyTesting = false;
        }
    }

    private void LoadProxySettings()
    {
        var settings = _userSettingsManager.Settings.ProxySettings;
        ProxyMode = settings.Mode;
        ExternalProxyUrl = settings.ExternalProxyUrl;
        SingleNodeUri = settings.SingleNodeUri;
        SubscriptionUrl = settings.SubscriptionUrl;
        LocalHttpPort = settings.LocalHttpPort;
        ProxyTestStatusText = _proxyService.CurrentStatus.Message;
    }

    private void ApplyProxySettings()
    {
        var settings = _userSettingsManager.Settings.ProxySettings;
        settings.Mode = ProxyMode;
        settings.ExternalProxyUrl = ExternalProxyUrl;
        settings.SingleNodeUri = SingleNodeUri;
        settings.SubscriptionUrl = SubscriptionUrl;
        settings.LocalHttpPort = LocalHttpPort;
    }

    #endregion

    #region Twitch

    [ObservableProperty] private string? _twitchRewardTitle;
    [ObservableProperty] private string? _twitchRewardPrompt;
    [ObservableProperty] private uint? _twitchRewardCost;

    [ObservableProperty] private bool _isTwitchAuth;
    [ObservableProperty] private bool _isTwitchAuthAndTokenValid;
    [ObservableProperty] private bool _isTwitchAuthAndTokenInvalid;
    [ObservableProperty] private bool _isTwitchNeedToAuth;


    [RelayCommand]
    private async Task TwitchAuthAsync()
    {
        await _twitchService.SignIn();
        await CheckTwitchServiceStatus();
    }

    [RelayCommand]
    private async Task TwitchCreateAsync() =>
        await _twitchService.CreateRewardAsync(_userSettingsManager.Settings.TwitchRewardTitle!,
            _userSettingsManager.Settings.TwitchRewardPrompt!, _userSettingsManager.Settings.TwitchRewardCost ?? 0);

    [RelayCommand]
    private async Task TwitchUpdateAsync() =>
        await _twitchService.UpdateRewardIfChanged(_userSettingsManager.Settings.TwitchRewardTitle!,
            _userSettingsManager.Settings.TwitchRewardPrompt!, _userSettingsManager.Settings.TwitchRewardCost ?? 0);

    [RelayCommand]
    private async Task TwitchDeleteAsync() =>
        await _twitchService.DeleteRewardsAsync(_userSettingsManager.Settings.TwitchRewardTitle!);

    private async Task CheckTwitchServiceStatus()
    {
        IsTwitchAuth = _twitchService.IsAuth;
        await CheckTwitchTokenValidAsync();
    }

    private async Task CheckTwitchTokenValidAsync()
    {
        var isValid = await _twitchService.CheckTokenValidAsync();
        IsTwitchAuthAndTokenValid = IsTwitchAuth && isValid;
        IsTwitchAuthAndTokenInvalid = IsTwitchAuth && !isValid;
        IsTwitchNeedToAuth = !IsTwitchAuth || IsTwitchAuthAndTokenInvalid;
    }

    #endregion

    #region Da

    [ObservableProperty] private bool _isDaAuth;
    [ObservableProperty] private bool _isDaWidgetTokenValid;


    private string? _daWidgetToken;

    public string? DaWidgetToken
    {
        get => _daWidgetToken;
        set
        {
            SetProperty(ref _daWidgetToken, value);

            _userSettingsManager.Settings.DaWidgetToken = DaWidgetToken;
            _userSettingsManager.SaveSettingsAsync();
        }
    }

    private long? _daAppId;
    private string? _daAppKey;

    public long? DaAppId
    {
        get => _daAppId;
        set
        {
            SetProperty(ref _daAppId, value);
            _userSettingsManager.Settings.DaAppId = DaAppId;
            _userSettingsManager.SaveSettingsAsync();
        }
    }

    public string? DaAppKey
    {
        get => _daAppKey;
        set
        {
            SetProperty(ref _daAppKey, value);
            _userSettingsManager.Settings.DaAppKey = DaAppKey;
            _userSettingsManager.SaveSettingsAsync();
        }
    }

    private bool CheckDaSettings() => _userSettingsManager.Settings is { DaAppKey: not null, DaAppId: not null };

    [RelayCommand(CanExecute = nameof(CheckDaSettings))]
    private async Task DonationAuthAsync()
    {
        await _donationAlertsService.SignIn();
        IsDaAuth = _donationAlertsService.IsAuth;
    }

    #endregion


    // [RelayCommand]
    // private async Task TestAsync()
    // {
    //     // var media = await _donationAlertsService.GetMediaAsync();
    //     // _musicOrderService.Start();
    //     var rewardId = await _twitchService.GetRewardId(_userSettingsManager.Settings.TwitchRewardTitle!,
    //         true, null, null, null);
    //     // rewardId = "3e1193cb-a0ed-4b76-8659-d128347955ca";
    //     var a = await _twitchService.GetUnfulfilledRewardsAfter(rewardId, DateTime.Now.AddMinutes(-30));
    // }
}

using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.DonationAlerts;
using TwitchAudioPlayer.WPF.Services.Twitch;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class YtSettingsViewModel : ModalViewModelBase
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly DonationAlertsService _donationAlertsService;
    private readonly TwitchService _twitchService;

    [ObservableProperty] private double? _maxMinutesLength;

    public YtSettingsViewModel(IUserSettingsManager userSettingsManager, DonationAlertsService donationAlertsService,
        TwitchService twitchService)
    {
        _userSettingsManager = userSettingsManager;
        _donationAlertsService = donationAlertsService;
        _twitchService = twitchService;

        var dispatcher = Dispatcher.CurrentDispatcher;
        userSettingsManager.SettingsChanged += (_, _) => dispatcher.Invoke(NotifySettingsChanged);

        // YT
        MaxMinutesLength = _userSettingsManager.Settings.MaxMinutesLength;

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
        // Twitch
        _userSettingsManager.Settings.TwitchRewardTitle = TwitchRewardTitle;
        _userSettingsManager.Settings.TwitchRewardPrompt = TwitchRewardPrompt;
        _userSettingsManager.Settings.TwitchRewardCost = TwitchRewardCost;
        // Da
        _userSettingsManager.Settings.DaAppId = DaAppId;
        _userSettingsManager.Settings.DaAppKey = DaAppKey;
        _userSettingsManager.Settings.DaWidgetToken = DaWidgetToken;

        await _userSettingsManager.SaveSettingsAsync();
        await base.SaveAsync();
    }

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
        await _twitchService.UpdateRewardAsync(_userSettingsManager.Settings.TwitchRewardTitle!,
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
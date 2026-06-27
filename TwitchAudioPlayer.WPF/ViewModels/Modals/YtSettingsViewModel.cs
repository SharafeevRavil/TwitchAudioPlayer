using System;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.DonationAlerts;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.Services.Twitch;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class YtSettingsViewModel : ModalViewModelBase
{
    private readonly IUserSettingsManager _userSettingsManager;
    private readonly DonationAlertsService _donationAlertsService;
    private readonly TwitchService _twitchService;
    private bool _isInitialized;

    [ObservableProperty] private double? _maxMinutesLength;
    [ObservableProperty] private bool _useSeparateSourceVolumes;
    [ObservableProperty] private bool _useYtDlpForSearch;
    [ObservableProperty] private bool _rotateYouTubeUserAgent;
    [ObservableProperty] private bool _useYouTubeProxy;
    [ObservableProperty] private string? _youTubeProxyListText;
    [ObservableProperty] private double _obsYouTubeAudioGain;

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
        UseSeparateSourceVolumes = _userSettingsManager.Settings.UseSeparateSourceVolumes;
        UseYtDlpForSearch = _userSettingsManager.Settings.UseYtDlpForSearch;
        RotateYouTubeUserAgent = _userSettingsManager.Settings.RotateYouTubeUserAgent;
        UseYouTubeProxy = _userSettingsManager.Settings.UseYouTubeProxy;
        YouTubeProxyListText = string.Join("\n", _userSettingsManager.Settings.YouTubeProxyList ?? new List<string>());
        ObsYouTubeAudioGain = _userSettingsManager.Settings.ObsYouTubeAudioGain;

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
        _isInitialized = true;
    }

    partial void OnObsYouTubeAudioGainChanged(double value)
    {
        if (!_isInitialized)
            return;

        _userSettingsManager.Settings.ObsYouTubeAudioGain = Math.Clamp(value, 0, 64);
        _ = _userSettingsManager.SaveSettingsAsync();
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
        _userSettingsManager.Settings.UseSeparateSourceVolumes = UseSeparateSourceVolumes;
        _userSettingsManager.Settings.UseYtDlpForSearch = UseYtDlpForSearch;
        _userSettingsManager.Settings.RotateYouTubeUserAgent = RotateYouTubeUserAgent;
        _userSettingsManager.Settings.UseYouTubeProxy = UseYouTubeProxy;
        _userSettingsManager.Settings.YouTubeProxyList = (YouTubeProxyListText ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        _userSettingsManager.Settings.ObsYouTubeAudioGain = Math.Clamp(ObsYouTubeAudioGain, 0, 64);
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

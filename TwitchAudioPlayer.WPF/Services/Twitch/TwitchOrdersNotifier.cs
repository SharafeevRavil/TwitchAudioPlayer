using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchLib.Api.Core.Enums;

namespace TwitchAudioPlayer.WPF.Services.Twitch;

public class TwitchOrdersNotifier : NewOrdersNotifier
{
    private readonly TwitchService _twitchService;
    private readonly IUserSettingsManager _userSettingsManager;
    private string? _rewardId;

    public TwitchOrdersNotifier(TwitchService twitchService, IUserSettingsManager userSettingsManager)
    {
        _twitchService = twitchService;
        _userSettingsManager = userSettingsManager;

        _userSettingsManager.SettingsChanged += UserSettingsManagerOnSettingsChanged;
    }

    protected override async Task<List<MusicOrder.MusicOrder>?> MakeApiRequest(DateTimeOffset lastOrderDate)
    {
        var redemptions = await _twitchService.GetUnfulfilledRewardsAfter(_rewardId!, lastOrderDate);
        return redemptions
            .Select(x => new MusicOrder.MusicOrder(x.UserInput, new DateTimeOffset(x.RedeemedAt), OrderType.Twitch))
            .ToList();
    }

    protected override async Task<bool> BeforeTimerStartCheck()
    {
        if (!await EnsureRewardExist()) return false;
        return _twitchService.IsAuth && await _twitchService.CheckTokenValidAsync();
    }

    private async Task<bool> EnsureRewardExist()
    {
        // Settings reward title set
        var rewardTitle = _userSettingsManager.Settings.TwitchRewardTitle;
        if (string.IsNullOrEmpty(rewardTitle)) return false;
        // Settings reward prompt set
        var rewardPrompt = _userSettingsManager.Settings.TwitchRewardPrompt;
        if (string.IsNullOrEmpty(rewardPrompt)) return false;
        // Settings reward cost set
        var rewardCost = _userSettingsManager.Settings.TwitchRewardCost;
        if (rewardCost == null) return false;

        var rewardId = await _twitchService.UpdateRewardAsync(rewardTitle, rewardPrompt, rewardCost.Value);
        if (rewardId == null)
        {
            rewardId = await _twitchService.CreateRewardAsync(rewardTitle, rewardPrompt, rewardCost.Value);
            if (rewardId == null) return false;
        }

        _rewardId = rewardId;
        return true;
    }

    private void UserSettingsManagerOnSettingsChanged(object? sender, UserSettings e) => RestartTimer();

    public override async Task OrdersAccepted(List<MusicOrder.MusicOrder> orders, bool isAccepted = true)
    {
        if (_rewardId == null) return;
        await _twitchService.AcceptRewards(_rewardId, orders.Select(x => (x.Uri, x.Date)).ToList(),
            isAccepted ? CustomRewardRedemptionStatus.FULFILLED : CustomRewardRedemptionStatus.CANCELED);
    }
}
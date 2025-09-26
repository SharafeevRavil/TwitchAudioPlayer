using Serilog;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.Services.DonationAlerts;

public class DonationAlertsOrdersNotifier(DonationAlertsService donationAlertsService) : NewOrdersNotifier
{
    protected override async Task<List<MusicOrder.MusicOrder>?> MakeApiRequest(DateTimeOffset lastOrderDate)
    {
        try
        {
            var media = await donationAlertsService.GetMediaAfterAsync(lastOrderDate);
            return media?
                .Select(x => new MusicOrder.MusicOrder(x.AdditionalData.Url, x.DateCreated, OrderType.DonationAlerts))
                .ToList();
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при выполнении API-запроса.");
            return null;
        }
    }

    protected override async Task<bool> BeforeTimerStartCheck() => await donationAlertsService.CheckWidgetTokenValid();
}

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Helix.Models.ChannelPoints;
using TwitchLib.Api.Helix.Models.ChannelPoints.CreateCustomReward;
using TwitchLib.Api.Helix.Models.ChannelPoints.GetCustomRewardRedemption;
using TwitchLib.Api.Helix.Models.ChannelPoints.UpdateCustomReward;
using TwitchLib.Api.Helix.Models.ChannelPoints.UpdateCustomRewardRedemptionStatus;
using TwitchLib.Api.Helix.Models.Users.GetUsers;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.PubSub;
using TwitchLib.PubSub.Events;
using Serilog;

namespace TwitchAudioPlayer.WPF.Services.Twitch;

public class TwitchService
{
    private readonly TwitchTokenStorage _tokenStorage;
    private const string redirectUri = "http://localhost:5002";
    private const string redirectUri2 = "http://localhost:5003";
    private const string clientId = "fnrcsgcxi68xndaooiu8p5ixhcu28g";
    private const string scope = "channel:read:redemptions channel:manage:redemptions";

    private TwitchAPI _api;

    public TwitchService(TwitchTokenStorage tokenStorage)
    {
        _tokenStorage = tokenStorage;
        _api = new TwitchAPI
        {
            Settings =
            {
                ClientId = clientId,
                AccessToken = _tokenStorage.GetToken()
            }
        };
    }

    #region Auth

    public async Task SignIn()
    {
        Log.Information("Начало авторизации Twitch. Открытие окна авторизации...");
        var authorizationRequest =
            $"https://id.twitch.tv/oauth2/authorize?client_id={clientId}&redirect_uri={redirectUri}&response_type=token&scope={scope}";

        Process.Start(new ProcessStartInfo
        {
            FileName = authorizationRequest,
            UseShellExecute = true
        });

        await WaitForRedirectAsync();
    }

    private async Task WaitForRedirectAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri + "/");
        listener.Start();
        Log.Information("Ожидание первого редиректа...");

        var secondListener = Task.Run(WaitForSecondRedirectAsync);

        var context = await listener.GetContextAsync();
        Log.Information("Первый редирект получен.");

        const string responseString = $$"""
                                        <!DOCTYPE html>
                                        <html>
                                        <body>
                                            <script type='text/javascript'>
                                                (async function() {
                                                    try {
                                                        var fragment = window.location.hash.substring(1);
                                                        await fetch(`{{redirectUri2}}/?${fragment}`, { method: 'GET' });
                                                    } catch (e) {} finally {
                                                        document.body.innerHTML = '<h1>Authorization successful. You can close this window.</h1>';
                                                    }
                                                })();
                                            </script>
                                        </body>
                                        </html>
                                        """;
        var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
        context.Response.ContentLength64 = buffer.Length;
        var output = context.Response.OutputStream;
        await output.WriteAsync(buffer);
        output.Close();
        listener.Stop();

        Log.Information("Ответ отправлен пользователю после первого редиректа.");
        await secondListener;
    }

    private async Task WaitForSecondRedirectAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri2 + "/");
        listener.Start();
        Log.Information("Ожидание второго редиректа...");

        var context = await listener.GetContextAsync();
        var query = context.Request.QueryString;
        var accessToken = query["access_token"];
        listener.Stop();

        if (!string.IsNullOrEmpty(accessToken))
        {
            Log.Information("Второй редирект получен. Access token получен.");
            _api.Settings.AccessToken = accessToken;
            _tokenStorage.SaveToken(_api.Settings.AccessToken);
        }
        else
        {
            Log.Warning("Второй редирект завершился без получения access token.");
        }
    }

    public bool IsAuth => _api.Settings.AccessToken != null;
    public async Task<bool> CheckTokenValidAsync() => await _api.Auth.ValidateAccessTokenAsync() != null;

    #endregion

    public async Task<CustomReward?> GetReward(string title, bool onlyManageableRewards = true, bool? isEnabled = true,
        bool? isUserInputRequired = true, bool? shouldRedemptionsSkipQueue = false, string? currentUser = null)
    {
        Log.Information("Получение награды с заголовком: {Title}", title);
        try
        {
            currentUser ??= await GetCurrentUserId();
            var rewards = await _api.Helix.ChannelPoints.GetCustomRewardAsync(currentUser, null, onlyManageableRewards);
            IEnumerable<CustomReward> rewardsEnumerable = rewards.Data.Where(x => x.Title == title);
            if (isEnabled != null)
                rewardsEnumerable = rewardsEnumerable
                    .Where(x => x.IsEnabled == isEnabled);
            if (isUserInputRequired != null)
                rewardsEnumerable = rewardsEnumerable
                    .Where(x => x.IsUserInputRequired == isUserInputRequired);
            if (shouldRedemptionsSkipQueue != null)
                rewardsEnumerable = rewardsEnumerable
                    .Where(x => x.ShouldRedemptionsSkipQueue == shouldRedemptionsSkipQueue);
            var matchingReward = rewardsEnumerable.FirstOrDefault();
            return matchingReward;
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при получении награды с заголовком: {Title}", title);
            return null;
        }
    }

    public async Task<string?> CreateRewardAsync(string title, string prompt, uint cost, bool isUserInputRequired = true,
        bool isEnabled = true, string? currentUser = null)
    {
        Log.Information("Создание награды. Заголовок: {Title}, Стоимость: {Cost}", title, cost);
        try
        {
            currentUser ??= await GetCurrentUserId();
            var response = await _api.Helix.ChannelPoints.CreateCustomRewardsAsync(currentUser,
                new CreateCustomRewardsRequest
                {
                    IsEnabled = isEnabled,
                    Title = title,
                    Prompt = prompt,
                    Cost = (int)cost,
                    IsUserInputRequired = isUserInputRequired
                });
            return response.Data.Length == 0 ? null : response.Data[0].Id;
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при создании награды с заголовком: {Title}", title);
            return null;
        }
    }

    public async Task<string?> UpdateRewardIfChanged(string title, string prompt, uint cost, bool isUserInputRequired = true,
        bool isEnabled = true, string? currentUser = null)
    {
        Log.Information("Обновление награды, если имеются изменения. Заголовок: {Title}", title);
        try
        {
            var reward = await GetReward(title, true, null, null, null);
            if (reward == null) return null;
            var isChanged = reward.Title != title || reward.Prompt != prompt || reward.Cost != cost ||
                            reward.IsUserInputRequired != isUserInputRequired || reward.IsEnabled != isEnabled;
            if (!isChanged) return reward.Id;
            return await UpdateRewardAsync(reward.Id, title, prompt, (int)cost, isUserInputRequired, isEnabled, currentUser);
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при обновлении награды с заголовком: {Title}", title);
            return null;
        }
    }

    private async Task<string?> UpdateRewardAsync(string rewardId, string title, string prompt, int cost,
        bool isUserInputRequired = true, bool isEnabled = true, string? currentUser = null)
    {
        Log.Information("Обновление награды с ID: {RewardId}", rewardId);
        try
        {
            currentUser ??= await GetCurrentUserId();
            var response = await _api.Helix.ChannelPoints.UpdateCustomRewardAsync(currentUser, rewardId,
                new UpdateCustomRewardRequest
                {
                    IsEnabled = isEnabled,
                    Title = title,
                    Prompt = prompt,
                    Cost = cost,
                    IsUserInputRequired = isUserInputRequired
                });
            return response.Data.Length == 0 ? null : response.Data[0].Id;
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при обновлении награды с ID: {RewardId}", rewardId);
            return null;
        }
    }

    public async Task DeleteRewardsAsync(string title, string? currentUser = null)
    {
        Log.Information("Удаление награды с заголовком: {Title}", title);
        try
        {
            currentUser ??= await GetCurrentUserId();
            var reward = await GetReward(title, true, null, null, null, currentUser);
            await _api.Helix.ChannelPoints.DeleteCustomRewardAsync(currentUser, reward?.Id);
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при удалении награды с заголовком: {Title}", title);
        }
    }

    public async Task<List<RewardRedemption>> GetUnfulfilledRewardsAfter(string rewardId, DateTimeOffset after,
        string? currentUser = null)
    {
        try
        {
            var rewards = await GetUnfulfilledRewards(rewardId);
            return rewards.Where(x => new DateTimeOffset(x.RedeemedAt) > after).ToList();
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при получении невыполненных наград.");
            return [];
        }
    }

    public async Task<List<RewardRedemption>> GetUnfulfilledRewards(string rewardId, string? currentUser = null)
    {
        currentUser ??= await GetCurrentUserId();
        var redemptions =
            await _api.Helix.ChannelPoints.GetCustomRewardRedemptionAsync(currentUser, rewardId, null,
                "UNFULFILLED");
        return redemptions.Data.ToList();
    }

    private string? _currentUserId;
    public async Task<string?> GetCurrentUserId() => _currentUserId ??= await RequestCurrentUserIdAsync();

    public async Task<string?> RequestCurrentUserIdAsync()
    {
        var response = await _api.Helix.Users.GetUsersAsync();
        return response.Users.Length == 0 ? null : response.Users[0].Id;
    }

    public async Task AcceptRewards(string rewardId, List<(string Uri, DateTimeOffset Date)> data, 
        CustomRewardRedemptionStatus redemptionStatus, string? currentUser = null)
    {
        Log.Information("Обработка принятия наград для rewardId: {RewardId}", rewardId);
        currentUser ??= await GetCurrentUserId();
        var rewards = await GetUnfulfilledRewards(rewardId, currentUser);
        var toFulfill = rewards
            .Where(x => data.Contains((x.UserInput, new DateTimeOffset(x.RedeemedAt))))
            .Select(x => x.Id)
            .ToList();
        await _api.Helix.ChannelPoints.UpdateRedemptionStatusAsync(currentUser, rewardId, toFulfill,
            new UpdateCustomRewardRedemptionStatusRequest { Status = redemptionStatus });
        Log.Information("Награды с rewardId: {RewardId} обновлены до статуса: {Status}", rewardId, redemptionStatus);
    }
}

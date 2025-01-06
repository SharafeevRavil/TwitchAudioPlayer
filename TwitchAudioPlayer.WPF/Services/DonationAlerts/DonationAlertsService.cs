using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TwitchAudioPlayer.WPF.Services.DonationAlerts;

public class DonationAlertsService
{
    private readonly IUserSettingsManager _userSettingsManager;

    private const string redirectUri = "http://localhost:5000";
    private const string authUrl = "https://www.donationalerts.com/oauth/authorize";
    private const string tokenUrl = "https://www.donationalerts.com/oauth/token";
    private const string apiBaseUrl = "https://www.donationalerts.com/api/v1/";
    private const string apiWidgetBaseUrl = "https://www.donationalerts.com/api/";

    private long? _clientId;
    private string? _clientSecret;

    private DonationAlertsToken? _tokens;
    private string? _widgetToken;
    private readonly HttpClient _httpClient;


    public DonationAlertsService(IUserSettingsManager userSettingsManager)
    {
        _userSettingsManager = userSettingsManager;

        userSettingsManager.SettingsChanged += (_, _) => SetSettings();

        SetSettings();

        _httpClient = new HttpClient();
    }

    private void SetSettings()
    {
        _clientId = _userSettingsManager.Settings.DaAppId;
        _clientSecret = _userSettingsManager.Settings.DaAppKey;
        _widgetToken = _userSettingsManager.Settings.DaWidgetToken;
    }

    #region Auth

    public void SetAccessToken(string accessToken)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // никуда нахуй не сохраняю, т.к. вообще не нужно пока публичное апи. долбоебы на DA
    }

    public async Task SignIn()
    {
        const string scope = "oauth-donation-index";
        var authorizationRequest =
            $"{authUrl}?response_type=code&client_id={_clientId}&redirect_uri={redirectUri}&scope={scope}";
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
        Console.WriteLine("Listening for redirect...");
        var context = await listener.GetContextAsync();
        var query = context.Request.QueryString;
        var code = query["code"];
        const string responseString = "<html><body><h1>Authorization successful. You can close this window.</h1></body></html>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
        context.Response.ContentLength64 = buffer.Length;

        var output = context.Response.OutputStream;
        await output.WriteAsync(buffer);
        output.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(code))
        {
            var tokens = await GetTokenAsync(code);
            if (tokens != null)
            {
                _tokens = tokens;
                SetAccessToken(tokens.AccessToken);
            }
        }
    }

    private async Task<DonationAlertsToken?> GetTokenAsync(string code)
    {
        if (_clientId == null || _clientSecret == null)
            return null;

        var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        request.Content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("client_id", _clientId.Value.ToString()),
            new KeyValuePair<string, string?>("client_secret", _clientSecret),
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", redirectUri)
        ]);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var responseData = await response.Content.ReadAsStringAsync();
        var now = DateTimeOffset.UtcNow;
        var obj = JsonSerializer.Deserialize<OAuthTokenResponse>(responseData);
        return obj == null
            ? null
            : new DonationAlertsToken(obj.AccessToken, obj.RefreshToken, now.AddSeconds(obj.ExpiresIn));
    }

    public bool IsAuth => _tokens != null;

    #endregion

    public async Task<string> GetDonationsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{apiBaseUrl}alerts/donations");
            response.EnsureSuccessStatusCode();
            var respData = await response.Content.ReadAsStringAsync();
            return respData;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return null;
        }
    }

    public async Task<bool> CheckWidgetTokenValid()
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{apiWidgetBaseUrl}getmediadata?callback=nothing&token={_widgetToken}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }
    
    public async Task<List<Media>?> GetMediaAsync()
    {
        try
        {
            if (_widgetToken == null) return null;

            var response = await _httpClient.GetAsync(
                $"{apiWidgetBaseUrl}getmediadata?callback=nothing&token={_widgetToken}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            response.EnsureSuccessStatusCode();
            var respData = await response.Content.ReadAsStringAsync();

            var media = Media.ParseResponse(respData);
            if (media != null) return media;
            
            Console.WriteLine($"Error parsing media: {respData}");
            return [];
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return null;
        }
    }

    public async Task<List<Media>?> GetMediaAfterAsync(DateTimeOffset after)
    {
        var media = await GetMediaAsync();
        return media?.Where(x => x.DateCreated > after).ToList();
    }
}
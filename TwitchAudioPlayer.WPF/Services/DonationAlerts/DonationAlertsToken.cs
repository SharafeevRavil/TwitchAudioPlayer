using System.Text.Json.Serialization;

namespace TwitchAudioPlayer.WPF.Services;

public record DonationAlertsToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public class OAuthTokenResponse
{
    [JsonPropertyName("token_type")] public string TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("access_token")] public string AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; }
}
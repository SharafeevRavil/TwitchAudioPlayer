using System.Security.Cryptography;
using System.Text;
using TwitchAudioPlayer.Clients.Clients;

namespace TwitchAudioPlayerWPF.Utils;

// DPAPI-based token storage
// WINDOWS ONLY (it's a wpf app, so...)
public class VkAuthStorage : IVkAuthStorage
{
    private const string target = "VkAccessToken";

    public void SaveToken(string token)
    {
        var encryptedToken = Encrypt(token);
        System.IO.File.WriteAllText(target, encryptedToken);
    }

    public string? GetToken()
    {
        if (!System.IO.File.Exists(target))
            return null;

        var encryptedToken = System.IO.File.ReadAllText(target);
        return Decrypt(encryptedToken);
    }

    private static string Encrypt(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    private static string Decrypt(string encryptedText)
    {
        var encryptedBytes = Convert.FromBase64String(encryptedText);
        var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
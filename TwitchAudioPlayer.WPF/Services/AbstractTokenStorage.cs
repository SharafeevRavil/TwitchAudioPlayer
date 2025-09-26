using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace TwitchAudioPlayer.WPF.Services;

// DPAPI-based token storage
// WINDOWS ONLY (it's a wpf app, so...)
public abstract class AbstractTokenStorage<T> where T: class?
{
    protected abstract string Target { get; }

    public void SaveToken(T token)
    {
        try
        {
            var jsonToken = JsonSerializer.Serialize(token);
            var encryptedToken = Encrypt(jsonToken);
            File.WriteAllText(Target, encryptedToken);
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при сохранении токена.");
            throw;
        }
    }

    public T? GetToken()
    {
        try
        {
            if (!File.Exists(Target)) return null;

            var encryptedToken = File.ReadAllText(Target);
            var jsonToken = Decrypt(encryptedToken);
            return JsonSerializer.Deserialize<T>(jsonToken);
        }
        catch (Exception e)
        {
            Log.Error(e, "Ошибка при получении токена.");
            return null;
        }
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

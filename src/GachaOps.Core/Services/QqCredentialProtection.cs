using System.Security.Cryptography;
using System.Text;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

internal static class QqCredentialProtection
{
    public static QqNotificationSettings Load(QqNotificationSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedAppSecret))
            return settings with { AppSecret = string.Empty, CredentialUnavailable = false };
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedAppSecret),
                null, DataProtectionScope.CurrentUser);
            try { return settings with { AppSecret = Encoding.UTF8.GetString(bytes), CredentialUnavailable = false }; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            return settings with { AppSecret = string.Empty, CredentialUnavailable = true };
        }
    }

    public static QqNotificationSettings PrepareForSave(QqNotificationSettings settings)
    {
        // Preserve unreadable ciphertext until the user replaces it or removes the channel.
        if (settings.CredentialUnavailable && string.IsNullOrEmpty(settings.AppSecret)) return settings;
        if (string.IsNullOrEmpty(settings.AppSecret))
            return settings with { ProtectedAppSecret = string.Empty, CredentialUnavailable = false };
        var bytes = Encoding.UTF8.GetBytes(settings.AppSecret);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return settings with
            {
                ProtectedAppSecret = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)),
                CredentialUnavailable = false
            };
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            throw new IOException("QQ AppSecret 加密失败，设置未保存");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

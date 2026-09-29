using System.Security.Cryptography;
using System.Text;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

internal static class EmailCredentialProtection
{
    public static QqEmailNotificationSettings Load(QqEmailNotificationSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedAuthorizationCode))
            return settings with { AuthorizationCode = string.Empty, CredentialUnavailable = false };
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedAuthorizationCode),
                null, DataProtectionScope.CurrentUser);
            try { return settings with { AuthorizationCode = Encoding.UTF8.GetString(bytes), CredentialUnavailable = false }; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            return settings with { AuthorizationCode = string.Empty, CredentialUnavailable = true };
        }
    }

    public static QqEmailNotificationSettings PrepareForSave(QqEmailNotificationSettings settings)
    {
        // Keep unreadable ciphertext until the user replaces the credential or removes the channel.
        if (settings.CredentialUnavailable && string.IsNullOrEmpty(settings.AuthorizationCode)) return settings;
        if (string.IsNullOrEmpty(settings.AuthorizationCode))
            return settings with { ProtectedAuthorizationCode = string.Empty, CredentialUnavailable = false };
        var bytes = Encoding.UTF8.GetBytes(settings.AuthorizationCode);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return settings with
            {
                ProtectedAuthorizationCode = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)),
                CredentialUnavailable = false
            };
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            // Existing settings-save failure handling applies; never persist plaintext or raw crypto errors.
            throw new IOException("邮件授权码加密失败，设置未保存");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

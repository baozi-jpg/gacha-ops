using System.Net.Sockets;
using System.Text;
using GachaOps.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GachaOps.Core.Services;

internal static class QqEmailNotificationService
{
    public static async Task<NotificationDeliveryResult> SendAsync(QqEmailNotificationSettings settings,
        string title, string body, CancellationToken cancellationToken, SmtpClient? client = null)
    {
        if (!TryAddress(settings.SenderAddress, out var sender) || !sender!.Address.EndsWith("@qq.com", StringComparison.OrdinalIgnoreCase))
            return Failure("请填写有效的 QQ 发件邮箱");
        if (!TryAddress(settings.RecipientAddress, out var recipient)) return Failure("请填写有效的收件邮箱");
        if (settings.CredentialUnavailable) return Failure("授权码无法解密，请重新填写");
        if (string.IsNullOrWhiteSpace(settings.AuthorizationCode)) return Failure("请填写 QQ 邮箱授权码");
        if (title.Any(char.IsControl)) return Failure("邮件主题格式无效");

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("GachaOps", sender.Address));
        message.To.Add(recipient!);
        message.Subject = title;
        message.Body = new TextPart("plain") { Text = body };
        using var smtp = client ?? new SmtpClient();
        smtp.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NotificationService.SendTimeout);
        try
        {
            // Fixed endpoint, mandatory TLS, and MailKit's default certificate validation.
            await smtp.ConnectAsync("smtp.qq.com", 465, SecureSocketOptions.SslOnConnect, timeout.Token).ConfigureAwait(false);
            await smtp.AuthenticateAsync(Encoding.UTF8, new System.Net.NetworkCredential(sender.Address, settings.AuthorizationCode),
                timeout.Token).ConfigureAwait(false);
            await smtp.SendAsync(message, timeout.Token).ConfigureAwait(false);
            // Disposal closes the connection without a second network wait after SMTP acceptance.
            return new(true, Channel: NotificationChannel.QqEmail);
        }
        catch (OperationCanceledException)
        {
            return Failure(cancellationToken.IsCancellationRequested ? "通知发送已取消" : "通知发送超时");
        }
        catch (SslHandshakeException) { return Failure("邮件安全连接失败，请检查网络和证书"); }
        catch (AuthenticationException) { return Failure("邮件认证失败，请检查邮箱和授权码"); }
        catch (SmtpCommandException) { return Failure("邮件服务器拒绝请求，请检查邮箱设置"); }
        catch (SmtpProtocolException) { return Failure("邮件服务器响应异常"); }
        catch (SocketException) { return Failure("邮件服务器连接失败"); }
        catch (IOException) { return Failure("邮件连接读写失败"); }
    }

    private static NotificationDeliveryResult Failure(string error) => new(false, error, Channel: NotificationChannel.QqEmail);

    private static bool TryAddress(string? value, out MailboxAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        // Reject display names, address lists, groups and header injection rather than accepting a prefix.
        return MailboxAddress.TryParse(value, out address) && string.IsNullOrEmpty(address.Name)
            && address.Address == value && value.Count(c => c == '@') == 1
            && value[(value.IndexOf('@') + 1)..].Contains('.');
    }
}

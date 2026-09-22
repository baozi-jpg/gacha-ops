using System.Net.Http.Json;
using System.Text.Json;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public enum RunNotificationKind { Reminder, Started, Result, Test }

public sealed record NotificationDeliveryResult(bool Sent, string? Error = null);

public sealed class BarkNotificationService(HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 16 * 1024
    };
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReminderLeadTime = TimeSpan.FromMinutes(5);

    public async Task<NotificationDeliveryResult> SendAsync(
        AppSettings settings, RunNotificationKind kind, string title, string body,
        CancellationToken cancellationToken = default)
    {
        if (!settings.NotificationsEnabled || !(kind switch
            {
                RunNotificationKind.Reminder => settings.NotifyBeforeScheduledRun,
                RunNotificationKind.Started => settings.NotifyRunStarted,
                RunNotificationKind.Result => settings.NotifyRunResult,
                RunNotificationKind.Test => true,
                _ => false
            }))
            return new(false);

        if (!TryGetEndpoint(settings.BarkAddress, out var endpoint, out var key))
            return new(false, "Bark 地址无效，请填写包含设备密钥的 HTTP(S) 地址");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            using var response = await (client ?? DefaultClient).PostAsJsonAsync(endpoint,
                new { device_key = key, title, body, group = "GachaOps" }, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, $"Bark 服务返回 HTTP {(int)response.StatusCode}");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 200
                ? new(true)
                : new(false, "Bark 服务未确认接收通知");
        }
        catch (OperationCanceledException)
        {
            return new(false, cancellationToken.IsCancellationRequested ? "通知发送已取消" : "通知发送超时");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            // Never log exception text or response bodies: either can contain the device key.
            return new(false, "通知发送失败，请检查网络和 Bark 服务");
        }
    }

    private static bool TryGetEndpoint(string? address, out Uri? endpoint, out string key)
    {
        endpoint = null;
        key = string.Empty;
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        var separator = path.LastIndexOf('/');
        if (separator < 0 || separator == path.Length - 1)
            return false;
        key = Uri.UnescapeDataString(path[(separator + 1)..]);
        if (string.IsNullOrWhiteSpace(key) || key.Contains('/') || key.Contains('\\'))
            return false;
        endpoint = new UriBuilder(uri) { Path = path[..(separator + 1)] + "push" }.Uri;
        return true;
    }
}

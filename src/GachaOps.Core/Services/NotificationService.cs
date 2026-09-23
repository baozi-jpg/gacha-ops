using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public enum RunNotificationKind { Reminder, Started, Result, Test }
public enum NotificationChannel { Bark, Ntfy }

public sealed record NotificationDeliveryResult(bool Sent, string? Error = null, string? SkippedReason = null,
    NotificationChannel? Channel = null, IReadOnlyList<NotificationDeliveryResult>? Deliveries = null);

public sealed class NotificationService(HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 16 * 1024
    };
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReminderLeadTime = TimeSpan.FromMinutes(5);

    public async Task<NotificationDeliveryResult> SendAsync(
        AppSettings settings, RunNotificationKind kind, string title, string body,
        CancellationToken cancellationToken = default, NotificationChannel? channel = null)
    {
        if (!settings.NotificationsEnabled)
            return new(false, SkippedReason: "通知总开关关闭", Channel: channel);
        if (!(kind switch
            {
                RunNotificationKind.Reminder => settings.NotifyBeforeScheduledRun,
                RunNotificationKind.Started => settings.NotifyRunStarted,
                RunNotificationKind.Result => settings.NotifyRunResult,
                RunNotificationKind.Test => true,
                _ => false
            }))
            return new(false, SkippedReason: "该类通知已关闭", Channel: channel);

        var bark = settings.Bark ?? (string.IsNullOrWhiteSpace(settings.BarkAddress)
            ? null : MigrateBarkAddress(settings.BarkAddress));
        var sends = new List<Task<NotificationDeliveryResult>>(2);
        if ((channel is null or NotificationChannel.Bark) && bark is { IsEnabled: true })
            sends.Add(SendBarkAsync(bark, title, body, cancellationToken));
        if ((channel is null or NotificationChannel.Ntfy) && settings.Ntfy is { IsEnabled: true } ntfy)
            sends.Add(SendNtfyAsync(ntfy, title, body, cancellationToken));
        if (sends.Count == 0)
            return new(false, SkippedReason: "尚无已启用的通知渠道", Channel: channel);
        var results = await Task.WhenAll(sends).ConfigureAwait(false);
        if (results.Length == 1) return results[0];
        var errors = results.Where(result => result.Error is not null).Select(result => $"{result.Channel}: {result.Error}").ToArray();
        return new(results.All(result => result.Sent), errors.Length == 0 ? null : string.Join("；", errors), Deliveries: results);
    }

    private Task<NotificationDeliveryResult> SendBarkAsync(BarkNotificationSettings bark,
        string title, string body, CancellationToken cancellationToken)
    {
        if (!TryGetServer(bark.ServerAddress, out var server) || string.IsNullOrWhiteSpace(bark.DeviceKey)
            || bark.DeviceKey.Contains('/') || bark.DeviceKey.Contains('\\'))
            return Task.FromResult(new NotificationDeliveryResult(false, "Bark 地址无效或设备密钥为空", Channel: NotificationChannel.Bark));
        var endpoint = new Uri(server!, "push");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { device_key = bark.DeviceKey, title, body, group = "GachaOps" })
        };
        return SendRequestAsync(request, NotificationChannel.Bark, null, cancellationToken);
    }

    private Task<NotificationDeliveryResult> SendNtfyAsync(NtfyNotificationSettings ntfy,
        string title, string body, CancellationToken cancellationToken)
    {
        if (!TryGetServer(ntfy.ServerAddress, out var server))
            return Task.FromResult(new NotificationDeliveryResult(false, "ntfy 服务地址无效，请填写 HTTP(S) 地址", Channel: NotificationChannel.Ntfy));
        if (string.IsNullOrWhiteSpace(ntfy.Topic) || ntfy.Topic.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            return Task.FromResult(new NotificationDeliveryResult(false, "ntfy 订阅主题仅支持英文字母、数字、下划线和连字符", Channel: NotificationChannel.Ntfy));
        var token = ntfy.AccessToken?.Trim() ?? string.Empty;
        if (token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '_' and not '~' and not '+' and not '/' and not '='))
            return Task.FromResult(new NotificationDeliveryResult(false, "ntfy 访问令牌格式无效", Channel: NotificationChannel.Ntfy));
        var request = new HttpRequestMessage(HttpMethod.Post, server)
        {
            Content = JsonContent.Create(new { topic = ntfy.Topic, title, message = body })
        };
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return SendRequestAsync(request, NotificationChannel.Ntfy, ntfy.Topic, cancellationToken);
    }

    private async Task<NotificationDeliveryResult> SendRequestAsync(HttpRequestMessage request,
        NotificationChannel channel, string? topic, CancellationToken cancellationToken)
    {
        using var ownedRequest = request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        var name = channel == NotificationChannel.Bark ? "Bark" : "ntfy";
        try
        {
            using var response = await (client ?? DefaultClient).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, $"{name} 服务返回 HTTP {(int)response.StatusCode}", Channel: channel);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var root = payload.RootElement;
            var accepted = root.ValueKind == JsonValueKind.Object && (channel == NotificationChannel.Bark
                ? root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out var value) && value == 200
                : HasString(root, "event", "message") && HasString(root, "topic", topic)
                    && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()));
            return accepted ? new(true, Channel: channel) : new(false, $"{name} 服务未确认接收通知", Channel: channel);
        }
        catch (OperationCanceledException)
        {
            return new(false, cancellationToken.IsCancellationRequested ? "通知发送已取消" : "通知发送超时", Channel: channel);
        }
        catch (HttpRequestException exception)
        {
            // Only framework error categories are safe: messages and response bodies can contain the device key.
            return new(false, $"通知 HTTP 请求失败（{exception.HttpRequestError}）", Channel: channel);
        }
        catch (IOException)
        {
            return new(false, "通知连接读写失败", Channel: channel);
        }
        catch (JsonException)
        {
            return new(false, $"{name} 响应不是有效的 JSON", Channel: channel);
        }
    }

    private static bool HasString(JsonElement root, string property, string? value) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() == value;

    public static void RecordDelivery(RunNotificationKind kind, NotificationDeliveryResult result,
        string? root = null, string? scheduledTime = null)
    {
        if (result.Deliveries is { } deliveries)
        {
            foreach (var delivery in deliveries) RecordDelivery(kind, delivery, root, scheduledTime);
            return;
        }
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GachaOps");
        try
        {
            Directory.CreateDirectory(root);
            // Never persist the address, payload, or raw exception. Accepted does not mean delivered to the phone.
            File.AppendAllText(Path.Combine(root, "notifications.jsonl"), JsonSerializer.Serialize(new
            {
                At = DateTimeOffset.Now, Kind = kind.ToString(), ScheduledTime = scheduledTime, Channel = result.Channel?.ToString(),
                Outcome = result.Sent ? "服务已接收" : result.Error is not null ? "发送失败" : "未发送",
                Reason = result.Error ?? result.SkippedReason
            }) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            new CrashLogStore(root).TryWrite("NotificationLog", new IOException("通知结果记录失败"));
        }
        if (result.Error is { } error)
            new CrashLogStore(root).TryWrite($"{result.Channel}Notification", new InvalidOperationException(error));
    }

    public static BarkNotificationSettings MigrateBarkAddress(string address)
    {
        // Keep malformed legacy input masked and editable without guessing a destination.
        var invalid = new BarkNotificationSettings { ServerAddress = string.Empty, DeviceKey = address.Trim() };
        if (!TryGetServer(address, out var uri)) return invalid;
        var path = uri!.AbsolutePath.TrimEnd('/');
        var separator = path.LastIndexOf('/');
        if (separator < 0 || separator == path.Length - 1) return invalid;
        var key = Uri.UnescapeDataString(path[(separator + 1)..]);
        if (string.IsNullOrWhiteSpace(key) || key.Contains('/') || key.Contains('\\')) return invalid;
        return new() { ServerAddress = new UriBuilder(uri) { Path = path[..(separator + 1)] }.Uri.AbsoluteUri.TrimEnd('/'), DeviceKey = key };
    }

    private static bool TryGetServer(string? address, out Uri? uri)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        uri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        return true;
    }
}

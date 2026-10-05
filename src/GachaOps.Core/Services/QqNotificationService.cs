using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed record QqBindingResult(string? OpenId = null, string? Error = null);

public sealed class QqNotificationService(HttpClient? client = null)
{
    private const string ApiBase = "https://api.bot.qq.com";
    private const int MaximumPayloadBytes = 16 * 1024;
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = MaximumPayloadBytes
    };
    public static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(60);

    public async Task<NotificationDeliveryResult> SendAsync(QqNotificationSettings settings,
        string title, string body, CancellationToken cancellationToken = default)
    {
        if (Validate(settings, requireOpenId: true) is { } error)
            return new(false, error, Channel: NotificationChannel.Qq);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NotificationService.SendTimeout);
        try
        {
            // The platform returns the same token within its lifetime. Fetch on demand so no expired token is reused.
            var token = await GetAccessTokenAsync(settings, timeout.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiBase}/v2/users/{Uri.EscapeDataString(settings.UserOpenId)}/messages")
            {
                Content = JsonContent.Create(new { msg_type = 0, content = title + Environment.NewLine + body })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
            using var payload = await RequestAsync(request, timeout.Token).ConfigureAwait(false);
            if (!TryString(payload.RootElement, "id", out var id) || string.IsNullOrWhiteSpace(id))
                throw new QqApiException("QQ 服务未确认接收通知");
            return new(true, Channel: NotificationChannel.Qq);
        }
        catch (Exception exception) when (IsOperationalError(exception))
        {
            return new(false, SafeError(exception, cancellationToken, binding: false), Channel: NotificationChannel.Qq);
        }
    }

    public async Task<QqBindingResult> BindAsync(QqNotificationSettings settings, Action<string> onVerifyCode,
        CancellationToken cancellationToken = default)
    {
        if (Validate(settings, requireOpenId: false) is { } error) return new(Error: error);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BindTimeout);
        try
        {
            var token = await GetAccessTokenAsync(settings, timeout.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/gateway");
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
            using var gateway = await RequestAsync(request, timeout.Token).ConfigureAwait(false);
            if (!TryString(gateway.RootElement, "url", out var address) || !IsOfficialGateway(address, out var uri))
                throw new QqApiException("QQ 网关地址无效");
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri!, timeout.Token).ConfigureAwait(false);
            var openId = await BindOnSocketAsync(socket, token, onVerifyCode, timeout.Token).ConfigureAwait(false);
            return new(OpenId: openId);
        }
        catch (Exception exception) when (IsOperationalError(exception))
        {
            return new(Error: SafeError(exception, cancellationToken, binding: true));
        }
    }

    internal static string? Validate(QqNotificationSettings settings, bool requireOpenId)
    {
        if (settings.CredentialUnavailable) return "AppSecret 无法解密，请重新填写";
        if (string.IsNullOrEmpty(settings.AppId) || settings.AppId.Length > 20
            || settings.AppId.Any(c => !char.IsAsciiDigit(c))) return "QQ AppID 无效，请填写机器人 AppID";
        if (string.IsNullOrEmpty(settings.AppSecret) || settings.AppSecret.Length > 512
            || settings.AppSecret.Any(c => c < '!' || c > '~')) return "QQ AppSecret 为空或格式无效";
        if (requireOpenId && !IsOpenId(settings.UserOpenId)) return "用户 OpenID 无效，请先绑定机器人";
        return null;
    }

    private static bool IsOpenId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static bool IsOfficialGateway(string? address, out Uri? uri) =>
        Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == "wss" && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "api.bot.qq.com" or "api.sgroup.qq.com" or "sandbox.api.sgroup.qq.com";

    private async Task<string> GetAccessTokenAsync(QqNotificationSettings settings, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/app/getAppAccessToken")
        {
            Content = JsonContent.Create(new { appId = settings.AppId, clientSecret = settings.AppSecret })
        };
        using var payload = await RequestAsync(request, ct).ConfigureAwait(false);
        if (!TryString(payload.RootElement, "access_token", out var token) || string.IsNullOrEmpty(token)
            || token.Length > 4096 || token.Any(c => c < '!' || c > '~'))
            throw new QqApiException("QQ 服务未返回有效的访问令牌");
        return token;
    }

    private async Task<JsonDocument> RequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await (client ?? DefaultClient).SendAsync(request, ct).ConfigureAwait(false);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        try
        {
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new QqApiException("QQ 响应格式无效");
            foreach (var property in new[] { "code", "err_code" })
            {
                if (!root.TryGetProperty(property, out var code)) continue;
                if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value))
                    throw new QqApiException("QQ 响应格式无效");
                if (value != 0) throw new QqApiException(ApiError(value));
            }
            if (!response.IsSuccessStatusCode)
                throw new QqApiException($"QQ 服务返回 HTTP {(int)response.StatusCode}");
            return payload;
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    private static string ApiError(int code) => code switch
    {
        100007 or 10004 => "QQ AppID 无效或机器人不可用",
        100016 => "QQ AppID 或 AppSecret 错误",
        100001 or 40034100 => "QQ 发送过于频繁，请稍后再试",
        40034105 => "QQ 主动消息无权限，请检查机器人权限及手机 QQ 的允许主动发送设置",
        40054004 => "请先用手机 QQ 添加机器人好友",
        40054013 => "QQ 用户已拒收机器人消息，请检查手机 QQ 设置",
        40054016 => "QQ 机器人已下线，请检查开放平台状态",
        40054007 or 40054018 => "QQ 消息过长",
        _ => $"QQ 服务返回错误码 {code}"
    };

    private static bool IsOperationalError(Exception exception) => exception is
        QqApiException or OperationCanceledException or HttpRequestException or IOException or JsonException or WebSocketException;

    private static string SafeError(Exception exception, CancellationToken cancellationToken, bool binding) => exception switch
    {
        QqApiException api => api.Message,
        OperationCanceledException => cancellationToken.IsCancellationRequested
            ? binding ? "已取消绑定" : "通知发送已取消"
            : binding ? "绑定超时，请重新绑定并发送验证码" : "通知发送超时",
        HttpRequestException http => $"QQ HTTP 请求失败（{http.HttpRequestError}）",
        JsonException => "QQ 响应不是有效的 JSON",
        _ => binding ? "QQ 绑定连接中断，请重新绑定" : "QQ 通知连接读写失败"
    };

    // Kept at the WebSocket boundary so the real handshake, fragmentation and event filtering can be tested offline.
    internal static async Task<string> BindOnSocketAsync(WebSocket socket, string token,
        Action<string> onVerifyCode, CancellationToken ct)
    {
        using var hello = await ReceiveAsync(socket, ct).ConfigureAwait(false);
        if (Opcode(hello.RootElement) != 10 || !hello.RootElement.TryGetProperty("d", out var data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("heartbeat_interval", out var interval)
            || interval.ValueKind != JsonValueKind.Number || !interval.TryGetInt32(out var milliseconds)
            || milliseconds is < 100 or > 120000)
            throw new QqApiException("QQ 网关握手失败");
        await SendSocketAsync(socket, new { op = 2, d = new { token = "QQBot " + token, intents = 1 << 25, shard = new[] { 0, 1 } } }, ct)
            .ConfigureAwait(false);
        long sequence = -1;
        while (true)
        {
            using var ready = await ReceiveAsync(socket, ct).ConfigureAwait(false);
            CheckGatewayState(ready.RootElement);
            UpdateSequence(ready.RootElement, value => sequence = value);
            if (Opcode(ready.RootElement) == 0 && TryString(ready.RootElement, "t", out var type) && type == "READY") break;
        }
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        ct.ThrowIfCancellationRequested();
        onVerifyCode(code);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(socket, milliseconds, () => Interlocked.Read(ref sequence), session.Token);
        var receive = ReceiveOpenIdAsync(socket, code, value => Interlocked.Exchange(ref sequence, value), session.Token);
        try
        {
            var completed = await Task.WhenAny(receive, heartbeat).ConfigureAwait(false);
            if (completed == heartbeat)
            {
                await heartbeat.ConfigureAwait(false);
                throw new QqApiException("QQ 绑定连接中断，请重新绑定");
            }
            return await receive.ConfigureAwait(false);
        }
        finally
        {
            session.Cancel();
            socket.Abort();
            try { await Task.WhenAll(receive, heartbeat).ConfigureAwait(false); }
            catch (Exception exception) when (IsOperationalError(exception)) { /* Observe both tasks during shutdown. */ }
        }
    }

    private static async Task HeartbeatAsync(WebSocket socket, int milliseconds, Func<long> sequence, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var value = sequence();
            await SendSocketAsync(socket, new { op = 1, d = value < 0 ? (long?)null : value }, ct).ConfigureAwait(false);
            await Task.Delay(milliseconds, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReceiveOpenIdAsync(WebSocket socket, string code, Action<long> setSequence, CancellationToken ct)
    {
        while (true)
        {
            using var message = await ReceiveAsync(socket, ct).ConfigureAwait(false);
            var root = message.RootElement;
            CheckGatewayState(root);
            UpdateSequence(root, setSequence);
            if (Opcode(root) != 0 || !TryString(root, "t", out var type) || type != "C2C_MESSAGE_CREATE"
                || !root.TryGetProperty("d", out var data) || data.ValueKind != JsonValueKind.Object
                || !TryString(data, "content", out var content) || content?.Trim() != code
                || !data.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object) continue;
            if (author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True) continue;
            if (!TryString(author, "user_openid", out var openId) || string.IsNullOrEmpty(openId))
                TryString(author, "id", out openId);
            if (IsOpenId(openId)) return openId!;
        }
    }

    private static int Opcode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var op)
            || op.ValueKind != JsonValueKind.Number || !op.TryGetInt32(out var value))
            throw new QqApiException("QQ 网关响应格式无效");
        return value;
    }

    private static void CheckGatewayState(JsonElement root)
    {
        if (Opcode(root) is 7 or 9) throw new QqApiException("QQ 网关要求重新连接，请重新绑定");
    }

    private static void UpdateSequence(JsonElement root, Action<long> update)
    {
        if (root.TryGetProperty("s", out var sequence) && sequence.ValueKind == JsonValueKind.Number
            && sequence.TryGetInt64(out var value)) update(value);
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return true;
    }

    private static Task SendSocketAsync(WebSocket socket, object payload, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(payload)), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (frame.MessageType != WebSocketMessageType.Text)
                throw new QqApiException("QQ 绑定连接中断，请重新绑定");
            if (message.Length + frame.Count > MaximumPayloadBytes) throw new QqApiException("QQ 网关响应过大");
            message.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage) return JsonDocument.Parse(message.ToArray());
        }
    }

    private sealed class QqApiException(string message) : Exception(message);
}

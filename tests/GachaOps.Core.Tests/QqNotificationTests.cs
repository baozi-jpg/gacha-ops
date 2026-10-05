using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

internal static class QqNotificationTests
{
    private const string Secret = "offline-qq-app-secret";
    private const string Token = "offline-qq-access-token";
    private const string OpenId = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4";
    private static QqNotificationSettings Qq => new() { AppId = "102123456", AppSecret = Secret, UserOpenId = OpenId };
    private static AppSettings Settings => new() { NotificationsEnabled = true, Qq = Qq };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static async Task CredentialsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "GachaOps.Tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        var settings = Settings;
        Check(!JsonSerializer.Serialize(settings).Contains(Secret) && !Qq.ToString().Contains(Secret), "序列化和字符串不泄漏密钥");
        await store.SaveAsync(settings);
        var text = await File.ReadAllTextAsync(store.SettingsPath);
        Check(!text.Contains(Secret) && !text.Contains("\"AppSecret\""), "设置只保存密文");
        settings = (await store.LoadAsync()).Settings;
        Check(settings.Qq is { AppSecret: Secret, UserOpenId: OpenId, CredentialUnavailable: false }, "凭据与 OpenID 正常往返");
        settings.Qq = settings.Qq! with { IsEnabled = false };
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.Qq is { IsEnabled: false, AppSecret: Secret }, "停用保留密钥");
        settings.Qq = Qq with { ProtectedAppSecret = "invalid-ciphertext", AppSecret = "", CredentialUnavailable = true };
        await store.SaveAsync(settings);
        settings = (await store.LoadAsync()).Settings;
        Check(settings.Qq is { CredentialUnavailable: true, AppSecret: "" }, "损坏密文不回退明文");
        using var handler = new FakeHttp((_, _) => Task.FromResult(Json("{}")));
        using var client = new HttpClient(handler);
        var result = await new NotificationService(client).SendAsync(settings, RunNotificationKind.Test, "测试", "正文");
        Check(result.Error == "AppSecret 无法解密，请重新填写" && handler.Calls == 0, "解密失败不联网");
        await store.SaveAsync(settings);
        Check((await File.ReadAllTextAsync(store.SettingsPath)).Contains("invalid-ciphertext"), "无关保存保留损坏密文");
        settings.Qq = settings.Qq! with { AppSecret = Secret, CredentialUnavailable = false };
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.Qq is { AppSecret: Secret, CredentialUnavailable: false }, "重新填写恢复");
        settings.Qq = null;
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.Qq is null
            && !(await File.ReadAllTextAsync(store.SettingsPath)).Contains("ProtectedAppSecret"), "移除清除配置与凭据");
        await File.WriteAllTextAsync(store.SettingsPath, "{\"Qq\":{\"AppSecret\":\"" + Secret + "\"}}");
        Check((await store.LoadAsync()).Settings.Qq?.AppSecret == "", "不接受设置里的明文密钥");
    }

    public static async Task DeliveryAsync()
    {
        foreach (var expiry in new[] { "7200", "\"7200\"" })
        {
            using var handler = new FakeHttp(async (request, ct) =>
            {
                Check(request.RequestUri!.Host == "api.bot.qq.com" && request.Method == HttpMethod.Post, "固定官方 HTTPS POST");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (request.RequestUri.AbsolutePath == "/app/getAppAccessToken")
                {
                    Check(request.Headers.Authorization is null && body.RootElement.GetProperty("appId").GetString() == Qq.AppId
                        && body.RootElement.GetProperty("clientSecret").GetString() == Secret, "正确的 token 请求字段");
                    return Json("{\"access_token\":\"" + Token + "\",\"expires_in\":" + expiry + "}");
                }
                Check(request.RequestUri.AbsolutePath == $"/v2/users/{OpenId}/messages", "只调用私聊消息接口");
                Check(request.Headers.Authorization?.Scheme == "QQBot" && request.Headers.Authorization.Parameter == Token, "使用访问令牌鉴权");
                Check(body.RootElement.GetProperty("msg_type").GetInt32() == 0
                    && body.RootElement.GetProperty("content").GetString() == "GachaOps · 测试通知" + Environment.NewLine + NotificationService.QqTestBody,
                    "完整中文标题和测试正文");
                Check(!body.RootElement.TryGetProperty("msg_id", out _) && !body.RootElement.TryGetProperty("is_wakeup", out _), "定时通知使用主动消息");
                return Json("{\"id\":\"message-id\",\"timestamp\":\"2026-10-05T21:30:00+08:00\"}");
            });
            using var client = new HttpClient(handler);
            var result = await new NotificationService(client).SendAsync(Settings, RunNotificationKind.Test,
                "GachaOps · 测试通知", NotificationService.QqTestBody, channel: NotificationChannel.Qq);
            Check(result.Sent && result.Channel == NotificationChannel.Qq && handler.Calls == 2, "一次取 token 和发送，无重试");
        }
    }

    public static async Task ValidationAndSwitchesAsync()
    {
        using var handler = new FakeHttp((_, _) => Task.FromResult(Json("{}")));
        using var client = new HttpClient(handler);
        foreach (var qq in new[]
        {
            Qq with { AppId = "" }, Qq with { AppId = "123\r\nX-Header: value" },
            Qq with { AppSecret = "" }, Qq with { AppSecret = "value\r\n" },
            Qq with { UserOpenId = "" }, Qq with { UserOpenId = "../groups/123" },
            Qq with { CredentialUnavailable = true }
        })
        {
            var result = await new QqNotificationService(client).SendAsync(qq, "测试", "正文");
            Check(!result.Sent && result.Error is not null, "无效配置拒绝联网");
        }
        var settings = Settings;
        var service = new NotificationService(client);
        settings.NotificationsEnabled = false;
        await service.SendAsync(settings, RunNotificationKind.Test, "测试", "正文");
        settings.NotificationsEnabled = true;
        settings.NotifyBeforeScheduledRun = settings.NotifyRunStarted = settings.NotifyRunResult = false;
        foreach (var kind in new[] { RunNotificationKind.Reminder, RunNotificationKind.Started, RunNotificationKind.Result })
            await service.SendAsync(settings, kind, "测试", "正文");
        await service.SendAsync(settings, RunNotificationKind.Test, "测试", "正文", channel: NotificationChannel.Ntfy);
        settings.Qq = Qq with { IsEnabled = false };
        await service.SendAsync(settings, RunNotificationKind.Test, "测试", "正文");
        Check(handler.Calls == 0, "总开关、子开关、渠道开关和测试目标有效");
        foreach (var address in new[] { "ws://api.bot.qq.com/websocket", "wss://evil.example/websocket",
            "wss://api.bot.qq.com.evil.example/websocket", "wss://user@api.bot.qq.com/websocket", "wss://api.bot.qq.com:444/websocket" })
            Check(!QqNotificationService.IsOfficialGateway(address, out _), "拒绝外部或不安全网关");
        Check(QqNotificationService.IsOfficialGateway("wss://api.bot.qq.com/websocket/", out _), "允许官方网关");
    }

    public static async Task FailuresAsync()
    {
        foreach (var response in new[]
        {
            "{\"code\":100016,\"message\":\"" + Secret + "\"}",
            "{\"err_code\":40034105,\"message\":\"" + Token + "\"}",
            "{\"code\":40054004}", "{\"code\":40054013}", "{\"code\":987654,\"message\":\"" + Secret + "\"}",
            "{\"code\":\"bad\"}", "{}", "[]", "null", "not-json"
        })
        {
            using var handler = new FakeHttp((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/app/getAppAccessToken"
                    ? Json("{\"access_token\":\"" + Token + "\"}") : Json(response)));
            using var client = new HttpClient(handler);
            var result = await new QqNotificationService(client).SendAsync(Qq, "测试", "正文");
            Check(!result.Sent && result.Error is not null && handler.Calls == 2, "HTTP 200 中的失败和缺失消息 ID 不能误报成功或重试");
            var resultText = JsonSerializer.Serialize(result);
            Check(!resultText.Contains(Secret) && !resultText.Contains(Token), "响应原文不进入发送结果");
            var root = Path.Combine(Path.GetTempPath(), "GachaOps.Tests", Guid.NewGuid().ToString("N"));
            NotificationService.RecordDelivery(RunNotificationKind.Test, result, root);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Check(!(await File.ReadAllTextAsync(path)).Contains(Secret) && !(await File.ReadAllTextAsync(path)).Contains(Token), "发送和错误日志不泄漏凭据");
        }
        using var channels = new FakeHttp((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "api.day.app" => Json("{\"code\":200}"),
            "ntfy.sh" => Json("{\"id\":\"id\",\"event\":\"message\",\"topic\":\"test-topic\"}"),
            _ => Json("{\"code\":100016}")
        }));
        using var http = new HttpClient(channels);
        var settings = Settings;
        settings.Bark = new() { DeviceKey = "test-key" };
        settings.Ntfy = new() { Topic = "test-topic" };
        var combined = await new NotificationService(http).SendAsync(settings, RunNotificationKind.Result, "测试", "正文");
        Check(combined.Deliveries is { Count: 3 } && combined.Deliveries.Count(item => item.Sent) == 2, "QQ 失败不影响其他渠道");
        using var gatewayHandler = new FakeHttp((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/app/getAppAccessToken"
                ? Json("{\"access_token\":\"" + Token + "\"}") : Json("{\"url\":\"wss://evil.example/websocket\"}")));
        using var gatewayClient = new HttpClient(gatewayHandler);
        var binding = await new QqNotificationService(gatewayClient).BindAsync(Qq, _ => throw new InvalidOperationException("不应显示验证码"));
        Check(binding.Error == "QQ 网关地址无效" && gatewayHandler.Calls == 2, "外部网关在连接和显示验证码前拒绝");
        using var tokenError = new FakeHttp((_, _) => Task.FromResult(Json("{\"code\":100016,\"message\":\"" + Secret + "\"}")));
        using var tokenClient = new HttpClient(tokenError);
        binding = await new QqNotificationService(tokenClient).BindAsync(Qq, _ => throw new InvalidOperationException("不应显示验证码"));
        Check(binding.Error == "QQ AppID 或 AppSecret 错误" && tokenError.Calls == 1, "HTTP 200 token 业务错误不泄漏且不重试");
        using var broken = new FakeHttp((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, Secret));
        using var brokenClient = new HttpClient(broken);
        var failed = await new QqNotificationService(brokenClient).SendAsync(Qq, "测试", "正文");
        binding = await new QqNotificationService(brokenClient).BindAsync(Qq, _ => { });
        Check(failed.Error == "QQ HTTP 请求失败（ConnectionError）" && binding.Error == failed.Error,
            "网络异常只保留框架分类，不泄漏原始异常");
        using var redirected = new FakeHttp((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Redirect)));
        using var redirectedClient = new HttpClient(redirected);
        failed = await new QqNotificationService(redirectedClient).SendAsync(Qq, "测试", "正文");
        Check(failed.Error == "QQ 服务返回 HTTP 302" && redirected.Calls == 1, "重定向不是成功且不继续发送");
    }

    public static async Task TimeoutAndCancellationAsync()
    {
        using var handler = new FakeHttp(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var client = new HttpClient(handler);
        var watch = Stopwatch.StartNew();
        var result = await new QqNotificationService(client).SendAsync(Qq, "测试", "正文");
        Check(result.Error == "通知发送超时" && watch.Elapsed < TimeSpan.FromSeconds(8), "整个发送过程限时五秒");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        result = await new QqNotificationService(client).SendAsync(Qq, "测试", "正文", cancellation.Token);
        Check(result.Error == "通知发送已取消", "用户取消与超时区分");
        using var bindCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var binding = await new QqNotificationService(client).BindAsync(Qq, _ => throw new InvalidOperationException("不应显示验证码"), bindCancellation.Token);
        Check(binding.Error == "已取消绑定", "取 token 时可取消绑定");
        using var messageHandler = new FakeHttp(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/app/getAppAccessToken") return Json("{\"access_token\":\"" + Token + "\"}");
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var messageClient = new HttpClient(messageHandler);
        using var messageCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        result = await new QqNotificationService(messageClient).SendAsync(Qq, "测试", "正文", messageCancellation.Token);
        Check(result.Error == "通知发送已取消" && messageHandler.Calls == 2, "发送消息时取消且不重试");
    }

    public static async Task BindingProtocolAsync()
    {
        using var socket = new ScriptedSocket();
        string? codeSeen = null;
        var result = await QqNotificationService.BindOnSocketAsync(socket, Token, code =>
        {
            codeSeen = code;
            Check(socket.ReadyReceived && code.Length == 6 && code.All(char.IsAsciiDigit), "READY 后才显示六位验证码");
            socket.Message(new { op = 0, s = 2, t = "GROUP_AT_MESSAGE_CREATE", d = new { content = code, author = new { user_openid = "wrong-group" } } });
            socket.Message(new { op = 0, s = 3, t = "C2C_MESSAGE_CREATE", d = new { content = "wrong-code", author = new { user_openid = "wrong-user" } } });
            socket.Message(new { op = 0, s = 4, t = "C2C_MESSAGE_CREATE", d = new { content = code, author = new { user_openid = "wrong-bot", bot = true } } });
            socket.Message(new { op = 0, s = 5, t = "C2C_MESSAGE_CREATE", d = new { content = " " + code + " ", author = new { user_openid = OpenId } } }, fragmented: true);
        }, CancellationToken.None);
        Check(result == OpenId && codeSeen is not null && socket.State == WebSocketState.Aborted && socket.PendingReceives == 0,
            "只绑定正确私聊发送者，支持分片并收尾连接");
        using var identify = JsonDocument.Parse(socket.Sent.First());
        Check(identify.RootElement.GetProperty("op").GetInt32() == 2
            && identify.RootElement.GetProperty("d").GetProperty("token").GetString() == "QQBot " + Token
            && identify.RootElement.GetProperty("d").GetProperty("intents").GetInt32() == 1 << 25, "网关鉴权和订阅正确");
        using var heartbeat = JsonDocument.Parse(socket.Sent[1]);
        Check(heartbeat.RootElement.GetProperty("op").GetInt32() == 1 && heartbeat.RootElement.GetProperty("d").GetInt64() == 1,
            "首次心跳使用 READY 序列号");
        var count = socket.Sent.Count;
        await Task.Delay(150);
        Check(socket.Sent.Count == count, "绑定结束不再发送心跳");
        using var legacy = new ScriptedSocket();
        result = await QqNotificationService.BindOnSocketAsync(legacy, Token, code =>
            legacy.Message(new { op = 0, t = "C2C_MESSAGE_CREATE", d = new { content = code, author = new { id = OpenId } } }), CancellationToken.None);
        Check(result == OpenId, "兼容旧事件的 author.id");
    }

    public static async Task BindingShutdownAsync()
    {
        using var socket = new ScriptedSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        try
        {
            await QqNotificationService.BindOnSocketAsync(socket, Token, _ => { }, cancellation.Token);
            throw new InvalidOperationException("取消不应成功绑定");
        }
        catch (OperationCanceledException) { }
        Check(socket.PendingReceives == 0 && socket.State == WebSocketState.Aborted, "取消后没有遗留接收和心跳");
        using var disconnected = new ScriptedSocket { FailHeartbeat = true };
        var bind = QqNotificationService.BindOnSocketAsync(disconnected, Token, _ => { }, CancellationToken.None);
        Check(await Task.WhenAny(bind, Task.Delay(1000)) == bind, "心跳故障立即结束，不等到绑定超时");
        try { await bind; throw new InvalidOperationException("心跳失败不应绑定成功"); }
        catch (WebSocketException) { }
        Check(disconnected.PendingReceives == 0, "心跳失败清理并观察接收任务");
        using var oversized = new ScriptedSocket();
        try
        {
            await QqNotificationService.BindOnSocketAsync(oversized, Token, _ =>
            {
                for (var i = 0; i < 5; i++) oversized.Frame(new byte[4096], i == 4);
            }, CancellationToken.None);
            throw new InvalidOperationException("过大消息不能接收");
        }
        catch (Exception exception) when (exception.Message == "QQ 网关响应过大") { }
        Check(oversized.PendingReceives == 0 && oversized.State == WebSocketState.Aborted, "过大响应有界并清理连接");
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request, ct);
        }
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, bool End)> _frames = Channel.CreateUnbounded<(byte[], bool)>();
        private WebSocketState _state = WebSocketState.Open;
        public List<string> Sent { get; } = [];
        public bool ReadyReceived { get; private set; }
        public bool FailHeartbeat { get; init; }
        public int PendingReceives { get; private set; }
        public ScriptedSocket() => Message(new { op = 10, d = new { heartbeat_interval = 100 } });
        public void Frame(byte[] bytes, bool end) => _frames.Writer.TryWrite((bytes, end));
        public void Message(object value, bool fragmented = false)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (fragmented)
            {
                Frame(bytes[..(bytes.Length / 2)], false);
                Frame(bytes[(bytes.Length / 2)..], true);
            }
            else Frame(bytes, true);
        }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var value = Encoding.UTF8.GetString(buffer);
            Sent.Add(value);
            using var json = JsonDocument.Parse(value);
            var op = json.RootElement.GetProperty("op").GetInt32();
            if (op == 2) Message(new { op = 0, s = 1, t = "READY", d = new { session_id = "offline-session" } });
            if (op == 1 && FailHeartbeat) throw new WebSocketException(Secret);
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            PendingReceives++;
            try
            {
                var (bytes, end) = await _frames.Reader.ReadAsync(ct);
                Check(bytes.Length <= buffer.Count, "测试帧不得超出接收缓冲区");
                bytes.AsSpan().CopyTo(buffer.AsSpan());
                if (end && Encoding.UTF8.GetString(bytes).Contains("READY")) ReadyReceived = true;
                return new(bytes.Length, WebSocketMessageType.Text, end);
            }
            finally { PendingReceives--; }
        }
    }
}

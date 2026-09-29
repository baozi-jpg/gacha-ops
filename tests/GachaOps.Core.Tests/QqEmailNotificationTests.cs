using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GachaOps.Core.Models;
using GachaOps.Core.Services;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

internal static class QqEmailNotificationTests
{
    private const string Secret = "test-authorization-code";
    private static QqEmailNotificationSettings Email => new()
    {
        SenderAddress = "123456@qq.com", RecipientAddress = "654321@qq.com", AuthorizationCode = Secret
    };
    private static AppSettings Settings => new() { NotificationsEnabled = true, QqEmail = Email };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task CredentialsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "GachaOps.Tests", Guid.NewGuid().ToString("N"));
        // Retain the isolated test files, avoiding recursive cleanup or real user settings.
        var store = new SettingsStore(root);
        var settings = Settings;
        Check(!JsonSerializer.Serialize(settings).Contains(Secret), "普通序列化也不能输出授权码");
        await store.SaveAsync(settings);
        var json = await File.ReadAllTextAsync(store.SettingsPath);
        Check(!json.Contains(Secret) && !json.Contains("\"AuthorizationCode\""), "设置中只保存密文");
        Check(!Email.ToString().Contains(Secret), "记录字符串不能泄漏授权码");
        settings = (await store.LoadAsync()).Settings;
        Check(settings.QqEmail?.AuthorizationCode == Secret && !settings.QqEmail.CredentialUnavailable, "当前用户解密往返");
        settings.QqEmail = settings.QqEmail! with { IsEnabled = false };
        await store.SaveAsync(settings);
        settings = (await store.LoadAsync()).Settings;
        Check(settings.QqEmail is { IsEnabled: false, AuthorizationCode: Secret }, "停用保留授权码");
        settings.QqEmail = Email with { ProtectedAuthorizationCode = "invalid-ciphertext", AuthorizationCode = "", CredentialUnavailable = true };
        await store.SaveAsync(settings);
        settings = (await store.LoadAsync()).Settings;
        Check(settings.QqEmail is { CredentialUnavailable: true, AuthorizationCode: "" }, "损坏密文不能回退明文");
        using var smtp = new FakeSmtp();
        var result = await new NotificationService(emailClient: smtp).SendAsync(settings, RunNotificationKind.Test, "测试", "内容");
        Check(result.Error == "授权码无法解密，请重新填写" && smtp.Connects == 0, "无法解密不连接服务器");
        await store.SaveAsync(settings);
        Check((await File.ReadAllTextAsync(store.SettingsPath)).Contains("invalid-ciphertext"), "无关保存保留不可解密的密文");
        settings.QqEmail = settings.QqEmail! with { AuthorizationCode = Secret, CredentialUnavailable = false };
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.QqEmail is { CredentialUnavailable: false, AuthorizationCode: Secret }, "重新填写恢复");
        settings.QqEmail = null;
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.QqEmail is null, "移除后不恢复邮件渠道");
        Check(!(await File.ReadAllTextAsync(store.SettingsPath)).Contains("ProtectedAuthorizationCode"), "移除清除保存凭据");
    }

    public static async Task DeliveryAsync()
    {
        using var smtp = new FakeSmtp();
        var result = await new NotificationService(emailClient: smtp).SendAsync(Settings,
            RunNotificationKind.Test, "GachaOps · 测试通知", NotificationService.EmailTestBody, channel: NotificationChannel.QqEmail);
        Check(result.Sent && smtp.Connects == 1 && smtp.Authentications == 1 && smtp.Sends == 1, "一次连接认证发送");
        Check(smtp.Host == "smtp.qq.com" && smtp.Port == 465 && smtp.Security == SecureSocketOptions.SslOnConnect,
            "固定 QQ 服务器且强制 TLS");
        Check(smtp.DefaultCertificateValidation, "没有自定义绕过证书检查");
        Check(smtp.Username == Email.SenderAddress && smtp.PasswordMatched, "使用发件账号和授权码认证");
        using var stream = new MemoryStream(smtp.Message!);
        using var message = await MimeMessage.LoadAsync(stream);
        Check(message.From.Mailboxes.Single().Address == Email.SenderAddress && message.From.Mailboxes.Single().Name == "GachaOps", "真实发件身份");
        Check(message.To.Mailboxes.Single().Address == Email.RecipientAddress && message.Cc.Count == 0 && message.Bcc.Count == 0, "只有明确收件人");
        Check(message.Subject == "GachaOps · 测试通知" && message.TextBody!.TrimEnd('\r', '\n') == "这是一封邮件通知测试。", "中文邮件编码及测试样稿");
        Check(message.Body is TextPart { IsPlain: true } && !message.Attachments.Any() && message.HtmlBody is null, "仅纯文本无附件");
        Check(!Encoding.UTF8.GetString(smtp.Message!).Contains(Secret), "授权码不在邮件中");
    }

    public static async Task ValidationAndSwitchesAsync()
    {
        foreach (var email in new[]
        {
            Email with { SenderAddress = "other@example.com" },
            Email with { SenderAddress = "Name <123456@qq.com>" },
            Email with { RecipientAddress = "one@example.com,two@example.com" },
            Email with { RecipientAddress = "one@example.com\r\nBcc: two@example.com" },
            Email with { RecipientAddress = "Name <one@example.com>" },
            Email with { RecipientAddress = "" },
            Email with { AuthorizationCode = "" }
        })
        {
            using var smtp = new FakeSmtp();
            var settings = Settings;
            settings.QqEmail = email;
            var result = await new NotificationService(emailClient: smtp).SendAsync(settings, RunNotificationKind.Test, "测试", "内容");
            Check(!result.Sent && result.Error is not null && smtp.Connects == 0, "非法配置必须在联网前拒绝");
        }
        using var blocked = new FakeSmtp();
        var service = new NotificationService(emailClient: blocked);
        var off = Settings;
        off.NotificationsEnabled = false;
        await service.SendAsync(off, RunNotificationKind.Test, "测试", "内容");
        off.NotificationsEnabled = true;
        off.NotifyBeforeScheduledRun = off.NotifyRunStarted = off.NotifyRunResult = false;
        foreach (var kind in new[] { RunNotificationKind.Reminder, RunNotificationKind.Started, RunNotificationKind.Result })
            await service.SendAsync(off, kind, "测试", "内容");
        await service.SendAsync(off, RunNotificationKind.Test, "测试", "内容", channel: NotificationChannel.Ntfy);
        off.QqEmail = Email with { IsEnabled = false };
        await service.SendAsync(off, RunNotificationKind.Test, "测试", "内容");
        Check(blocked.Connects == 0, "总开关子开关及渠道选择阻止 SMTP");
        using var injected = new FakeSmtp();
        var rejected = await new NotificationService(emailClient: injected).SendAsync(Settings, RunNotificationKind.Test, "主题\r\nBcc: evil@example.com", "内容");
        Check(rejected.Error == "邮件主题格式无效" && injected.Connects == 0, "拒绝邮件头注入");
    }

    public static async Task FailuresAsync()
    {
        foreach (var failure in new Exception[]
        {
            new AuthenticationException(Secret), new SslHandshakeException(Secret),
            new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, Secret),
            new SmtpProtocolException(Secret), new IOException(Secret)
        })
        {
            using var smtp = new FakeSmtp { Failure = failure };
            using var http = new HttpClient(new AcceptedHttp());
            var settings = Settings;
            settings.Bark = new() { DeviceKey = "test-key" };
            settings.Ntfy = new() { Topic = "test-topic" };
            var result = await new NotificationService(http, smtp).SendAsync(settings, RunNotificationKind.Result, "测试", "内容");
            Check(!result.Sent && result.Deliveries is { Count: 3 }
                && result.Deliveries.Count(item => item.Sent) == 2, "邮件失败不影响两个 HTTP 渠道");
            Check(!JsonSerializer.Serialize(result).Contains(Secret), "原始异常不泄漏");
            var root = Path.Combine(Path.GetTempPath(), "GachaOps.Tests", Guid.NewGuid().ToString("N"));
            NotificationService.RecordDelivery(RunNotificationKind.Result, result, root);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Check(!(await File.ReadAllTextAsync(path)).Contains(Secret), "通知与崩溃日志均脱敏");
            Check(smtp.Connects == 1 && smtp.Sends == 0, "失败不重试");
        }
        using var slow = new FakeSmtp { Slow = true };
        using var client = new HttpClient(new AcceptedHttp());
        var concurrent = Settings;
        concurrent.Bark = new() { DeviceKey = "test-key" };
        var sending = new NotificationService(client, slow).SendAsync(concurrent, RunNotificationKind.Result, "测试", "内容");
        var timed = await sending.WaitAsync(TimeSpan.FromSeconds(8));
        Check(timed.Deliveries!.Any(item => item.Error == "通知发送超时") && timed.Deliveries!.Any(item => item.Sent), "SMTP 超时有界且其他渠道成功");
        using var cancelled = new FakeSmtp { Slow = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resultCancelled = await new NotificationService(emailClient: cancelled).SendAsync(Settings, RunNotificationKind.Test, "测试", "内容", cancellation.Token);
        Check(resultCancelled.Error == "通知发送已取消", "取消区别于超时");
    }

    public static async Task SamplesAsync()
    {
        Check(NotificationService.ReminderBody("21:30") == "计划于 21:30 开始运行。", "提醒样稿");
        Check(NotificationService.StartedBody(["BetterGI", "MAA", "MaaEnd"]) == "本轮任务：BetterGI、MAA、MaaEnd", "开始样稿");
        var start = new DateTimeOffset(2026, 9, 29, 21, 30, 0, TimeSpan.FromHours(8));
        var id = Guid.NewGuid();
        var tasks = new[] { ToolId.BetterGi, ToolId.Maa, ToolId.MaaEnd }
            .Select(tool => new WorkflowTaskSetting { ToolId = tool, IsEnabled = true, Channel = 1 }).ToArray();
        var records = tasks.Select(task => new RunRecord
        {
            WorkflowRunId = id, ToolId = task.ToolId, ToolName = ToolCatalog.Get(task.ToolId).Name,
            State = RunState.Succeeded, Message = "完成", StartedAt = start, EndedAt = start.AddSeconds(1112)
        }).ToArray();
        WorkflowRunSummary Summary(QueueRunResult result, IEnumerable<RunRecord> runs, bool persisted = true,
            string? reason = null, bool started = true, string? time = null, IReadOnlyList<ToolPreparationWarning>? warnings = null) =>
            WorkflowRunSummary.Create(id, start, start.AddSeconds(1112), tasks, runs, result, persisted,
                reason, warnings, started, time);
        var success = Summary(QueueRunResult.AllPlannedTasksCompleted, records);
        var failure = Summary(QueueRunResult.NotAllPlannedTasksCompleted,
            [records[0], records[1] with { State = RunState.TimedOut, Message = "private diagnostic" }, records[2] with { State = RunState.Skipped }]);
        var unstarted = Summary(QueueRunResult.NotAllPlannedTasksCompleted,
            [records[1] with { State = RunState.Failed, Message = "MAA 配置不存在：private-profile" }], started: false);
        var skipped = Summary(QueueRunResult.NotAllPlannedTasksCompleted, [], reason: "其他应用处于前台或前台状态无法确认", started: false, time: "21:30");
        var warning = Summary(QueueRunResult.AllPlannedTasksCompleted, records,
            warnings: [new(ToolId.Maa, "更新失败，已使用当前版本", "private diagnostic")]);
        var history = Summary(QueueRunResult.AllPlannedTasksCompleted, records, persisted: false);
        foreach (var (summary, title, body) in new[]
        {
            (success, "GachaOps · 任务已完成", "BetterGI、MAA、MaaEnd · 18分32秒"),
            (failure, "GachaOps · 运行异常", "MAA：超时\n未运行：MaaEnd"),
            (unstarted, "GachaOps · 未启动", "MAA：配置不存在"),
            (skipped, "GachaOps · 定时已跳过", "21:30：前台占用或状态不明"),
            (warning, "GachaOps · 任务已完成", "BetterGI、MAA、MaaEnd · 18分32秒\nMAA：更新失败，使用原版本"),
            (history, "GachaOps · 运行异常", "历史保存失败")
        })
        {
            Check(summary.Title == title && summary.Body.Replace("\r\n", "\n") == body, "审核样稿逐字匹配");
            using var smtp = new FakeSmtp();
            var sent = await new NotificationService(emailClient: smtp).SendAsync(Settings, RunNotificationKind.Result, summary.Title, summary.Body);
            Check(sent.Sent, "摘要交付邮件渠道");
            using var stream = new MemoryStream(smtp.Message!);
            using var message = await MimeMessage.LoadAsync(stream);
            Check(message.Subject == title && message.TextBody!.Replace("\r\n", "\n").TrimEnd('\n') == body, "MIME 中文与换行无损");
            Check(!message.TextBody!.Contains("private"), "本地诊断不进入邮件");
        }
    }

    public static async Task CertificateAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=invalid.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var ssl = new SslStream(socket.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation.Token);
            }
            catch (Exception exception) when (exception is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException) { }
        });
        using var client = new LocalTlsClient((IPEndPoint)listener.LocalEndpoint);
        var result = await new NotificationService(emailClient: client).SendAsync(Settings, RunNotificationKind.Test, "测试", "内容", cancellation.Token);
        await server;
        Check(result.Error == "邮件安全连接失败，请检查网络和证书" && !client.AuthenticationAttempted,
            "真实 TLS 握手拒绝不可信证书且不提交授权码");
    }

    private sealed class LocalTlsClient(IPEndPoint endpoint) : SmtpClient
    {
        public bool AuthenticationAttempted;
        public override async Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(endpoint, cancellationToken);
            await base.ConnectAsync(socket.GetStream(), host, port, options, cancellationToken);
        }
        public override Task AuthenticateAsync(Encoding encoding, ICredentials credentials, CancellationToken cancellationToken = default)
        {
            AuthenticationAttempted = true;
            throw new InvalidOperationException("不可信证书后不应认证");
        }
    }

    private sealed class AcceptedHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host == "api.day.app" ? "{\"code\":200}" : "{\"event\":\"message\",\"id\":\"1\",\"topic\":\"test-topic\"}")
            });
    }

    private sealed class FakeSmtp : SmtpClient
    {
        public int Connects, Authentications, Sends, Port;
        public string? Host, Username;
        public bool PasswordMatched, DefaultCertificateValidation, Slow;
        public SecureSocketOptions Security;
        public Exception? Failure;
        public byte[]? Message;
        public override async Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            Connects++;
            Host = host; Port = port; Security = options;
            DefaultCertificateValidation = ServerCertificateValidationCallback is null;
            if (Failure is not null) throw Failure;
            if (Slow) await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        public override Task AuthenticateAsync(Encoding encoding, ICredentials credentials, CancellationToken cancellationToken = default)
        {
            Authentications++;
            var credential = credentials.GetCredential(new Uri("smtp://smtp.qq.com"), "PLAIN");
            Username = credential?.UserName;
            PasswordMatched = credential?.Password == Secret;
            return Task.CompletedTask;
        }
        public override async Task<string> SendAsync(FormatOptions options, MimeMessage message, CancellationToken cancellationToken = default, ITransferProgress? progress = null)
        {
            Sends++;
            using var stream = new MemoryStream();
            await message.WriteToAsync(options, stream, cancellationToken);
            Message = stream.ToArray();
            return "accepted";
        }
    }
}

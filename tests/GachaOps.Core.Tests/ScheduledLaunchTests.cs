using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

internal static class ScheduledLaunchTests
{
    private static void Check(bool value, string reason)
    { if (!value) throw new InvalidOperationException(reason); }

    public static async Task SettingsAsync()
    {
        var root = NewRoot();
        var store = new SettingsStore(root);
        var old = (await store.LoadAsync()).Settings;
        Check(!old.ScheduledLaunchEnabled && old.DailySchedules.Count == 0, "旧设置必须默认关闭");
        old.ScheduledLaunchEnabled = true;
        old.DailySchedules = [new("8:17"), new("08:17"), new("24:00"), new("10:31", false)];
        await store.SaveAsync(old);
        var loaded = (await store.LoadAsync()).Settings;
        Check(loaded.DailySchedules.SequenceEqual(new[] { new DailySchedule("08:17"), new DailySchedule("10:31", false) }), "时刻归一化/停用往返失败");
        Check(ScheduledLaunch.TimeChoices.Count == 96 && ScheduledLaunch.TimeChoices[1] == "00:15", "15 分钟仅用于下拉选择");
        foreach (var invalid in new[] { "8", "08:60", "-1:00", "08:17:00", "garbage" })
            Check(!ScheduledLaunch.TryTime(invalid, out _), "无效时间被接受");
        Check(ScheduledLaunch.TryTime("08:17", out _), "必须允许任意分钟");
        foreach (var text in new[] { "8:5", "08:5", "8:05", "08:05" })
            Check(ScheduledLaunch.TryTime(text, out var time) && time == new TimeOnly(8, 5), "小时和分钟均支持一位或两位");
        foreach (var text in new[] { "8:", ":5", "008:05", "08:005", "24:00", "1:60", "a:05" })
            Check(!ScheduledLaunch.TryTime(text, out _), "空值及非法分段不能接受");
    }

    public static async Task SpacingAsync()
    {
        var now = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings { ScheduledLaunchEnabled = true,
            NotificationsEnabled = true, DailySchedules = [new("08:00"), new("08:59")] };
        var request = new ScheduledRequest("08:00", false, now);
        Check(ScheduledLaunch.Validate(settings, request, now, TimeZoneInfo.Utc, out _) is not null,
            "相差 59 分钟的已启用时刻必须阻止运行");
        settings.DailySchedules[1] = new("09:00");
        Check(ScheduledLaunch.Validate(settings, request, now, TimeZoneInfo.Utc, out _) is null,
            "恰好一小时可以运行");
        settings.DailySchedules[1] = new("08:05", false);
        Check(ScheduledLaunch.Validate(settings, request, now, TimeZoneInfo.Utc, out _) is null,
            "未勾选的冲突时刻可以保留");
        Check(ScheduledLaunch.FindConflictingTime(settings.DailySchedules, "8:5") == "08:00",
            "新增和勾选冲突项共享规范化校验");
        Check(ScheduledLaunch.FindConflictingTime(settings.DailySchedules, "09:00") is null,
            "新增恰好一小时的时刻可以启用");
        settings.DailySchedules = [new("23:30"), new("00:15")];
        now = new DateTimeOffset(2026, 9, 24, 23, 30, 0, TimeSpan.Zero);
        request = new("23:30", false, now);
        Check(ScheduledLaunch.Validate(settings, request, now, TimeZoneInfo.Utc, out _) is not null,
            "跨午夜 45 分钟也必须拒绝");
        settings.DailySchedules[1] = new("00:30");
        Check(ScheduledLaunch.Validate(settings, request, now, TimeZoneInfo.Utc, out _) is null,
            "跨午夜恰好一小时可启用");

        settings.DailySchedules = [new("08:00"), new("08:05"), new("12:00")];
        var store = new SettingsStore(NewRoot());
        await store.SaveAsync(settings);
        settings = (await store.LoadAsync()).Settings;
        Check(settings.DailySchedules.All(item => item.IsEnabled), "旧冲突配置不得悄悄取消勾选");
        var disabled = new List<string>();
        var registered = new List<string>();
        const string owned = "GachaOps-test-Daily-0800-Run";
        ScheduledTaskRegistration.Reconcile(settings, Path.Combine(NewRoot(), "GachaOps.exe"), "test",
            new Dictionary<string, string?> { [owned] = ScheduledTaskRegistration.Owner }, disabled.Add,
            (name, _) => registered.Add(name));
        Check(disabled.SequenceEqual(new[] { owned }) && registered.Count == 0,
            "旧配置有冲突时停用已有任务，不能注册运行或提醒");
        now = new DateTimeOffset(2026, 9, 24, 11, 55, 0, TimeSpan.Zero);
        Check(ScheduledLaunch.Validate(settings, new("12:00", true, now), now, TimeZoneInfo.Utc, out _) is not null,
            "冲突配置的遗留提醒入口也应拒绝");
        settings.DailySchedules[1] = settings.DailySchedules[1] with { IsEnabled = false };
        ScheduledTaskRegistration.Reconcile(settings, Path.Combine(NewRoot(), "GachaOps.exe"), "test",
            new Dictionary<string, string?>(), disabled.Add, (name, _) => registered.Add(name));
        Check(registered.Count == 4, "解决冲突后恢复两个运行和两个提醒");
    }

    public static Task TimingAsync()
    {
        var now = new DateTimeOffset(2026, 9, 22, 8, 17, 0, TimeSpan.Zero);
        var settings = new AppSettings { ScheduledLaunchEnabled = true, DailySchedules = [new("08:17"), new("00:02")] };
        var request = ScheduledLaunch.Parse(["--scheduled-run", "08:17"], now)!;
        Check(request is not null && !request.Reminder, "专用运行入口");
        Check(ScheduledLaunch.Parse(["--scheduled-run", "bad"], now) is null, "拒绝无效入口");
        Check(ScheduledLaunch.Parse(["--scheduled-run", "08:17", "extra"], now) is null, "拒绝额外参数");
        foreach (var seconds in new[] { 0, 1, 30 })
            Check(ScheduledLaunch.Validate(settings, request!, now.AddSeconds(seconds), TimeZoneInfo.Utc, out _) is null, "窗口内应接受");
        foreach (var seconds in new[] { -1, 31, 300, 3600 })
            Check(ScheduledLaunch.Validate(settings, request!, now.AddSeconds(seconds), TimeZoneInfo.Utc, out _) is not null, "过期/未来请求不得补跑");
        settings.ScheduledLaunchEnabled = false;
        Check(ScheduledLaunch.Validate(settings, request!, now, TimeZoneInfo.Utc, out _) is not null, "总开关关闭");
        settings.ScheduledLaunchEnabled = true;
        settings.DailySchedules[0] = new("08:17", false);
        Check(ScheduledLaunch.Validate(settings, request!, now, TimeZoneInfo.Utc, out _) is not null, "单项停用");
        var midnight = now.Date.AddHours(23).AddMinutes(57);
        var reminder = new ScheduledRequest("00:02", true, new DateTimeOffset(midnight, TimeSpan.Zero));
        Check(ScheduledLaunch.Validate(settings, reminder, reminder.ReceivedAt, TimeZoneInfo.Utc, out var date) is null
            && date == now.Date.AddDays(1).AddMinutes(2), "跨午夜提前五分钟应属于次日轮次");
        settings.DailySchedules = [new("08:20")];
        var lateReminder = new ScheduledRequest("08:20", true, now);
        Check(ScheduledLaunch.Validate(settings, lateReminder, now, TimeZoneInfo.Utc, out _) is not null,
            "新增三分钟后运行的时刻不补发已经错过的提醒");
        var upcomingRun = new ScheduledRequest("08:20", false, now.AddMinutes(3));
        Check(ScheduledLaunch.Validate(settings, upcomingRun, upcomingRun.ReceivedAt, TimeZoneInfo.Utc, out _) is null,
            "错过提醒不影响到点运行");
        return Task.CompletedTask;
    }

    public static Task ClaimsAsync()
    {
        var root = NewRoot();
        var now = DateTimeOffset.UtcNow;
        var request = new ScheduledRequest("08:17", false, now);
        var store = new ScheduledLaunchStore(root);
        Check(store.TryClaim(request, now.Date), "首次认领");
        Check(!new ScheduledLaunchStore(root).TryClaim(request, now.Date), "新进程不得重复认领");
        Check(!store.TryClaim(request, now.Date.AddDays(-1)), "时钟回拨不得重跑");
        Check(store.TryClaim(request with { Reminder = true }, now.Date), "提醒与运行独立");
        Check(store.TryClaim(request, now.Date.AddDays(1)), "下一天应能运行");
        var path = Path.Combine(root, "scheduled-claims.json");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { store.TryClaim(request, now.Date.AddDays(2)); throw new InvalidOperationException("锁定时应拒绝"); }
            catch (IOException) { }
        }
        File.WriteAllText(path, "broken");
        try { store.TryClaim(request, now.Date.AddDays(2)); throw new InvalidOperationException("损坏记录不可忽略"); }
        catch (JsonException) { }
        return Task.CompletedTask;
    }

    public static Task InterruptedClaimAsync()
    {
        var root = NewRoot();
        var date = new DateTime(2026, 9, 27);
        var request = new ScheduledRequest("08:17", false, new DateTimeOffset(date, TimeSpan.Zero));
        var store = new ScheduledLaunchStore(root);
        Check(store.TryClaim(request, date), "首次认领");
        Check(store.TryClaim(request with { Reminder = true }, date), "保留已有提醒认领");
        var path = Path.Combine(root, "scheduled-claims.json");
        var previous = File.ReadAllBytes(path);
        try
        {
            store.TryClaim(request, date.AddDays(1), (stream, _) =>
            {
                stream.Write("{\"08:17/False\":\"interrupted"u8);
                stream.Flush();
                throw new IOException("模拟写入中断");
            });
            throw new InvalidOperationException("保存失败不得返回认领成功");
        }
        catch (IOException) { }
        Check(File.ReadAllBytes(path).SequenceEqual(previous), "写入中断必须保留原有有效认领记录");
        Check(!new ScheduledLaunchStore(root).TryClaim(request, date), "失败后旧认领仍防重");
        Check(!store.TryClaim(request, date.AddDays(-1)), "失败后仍防时钟回拨");
        Check(!store.TryClaim(request with { Reminder = true }, date), "失败后提醒防重仍保留");
        Check(new ScheduledLaunchStore(root).TryClaim(request, date.AddDays(1)), "下次保存应覆盖中断的临时文件");
        return Task.CompletedTask;
    }

    public static Task InvalidClaimsAsync()
    {
        foreach (var content in new[] { "", "null", "broken" })
        {
            var root = NewRoot();
            var path = Path.Combine(root, "scheduled-claims.json");
            File.WriteAllText(path, content);
            try
            {
                new ScheduledLaunchStore(root).TryClaim(new("08:17", false, DateTimeOffset.UtcNow), new(2026, 9, 27));
                throw new InvalidOperationException("已有空记录或损坏记录不得重置后认领");
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
            Check(File.ReadAllText(path) == content, "无效记录必须保留原文");
        }
        return Task.CompletedTask;
    }

    public static Task ClaimReplaceFailureAsync()
    {
        var root = NewRoot();
        var date = new DateTime(2026, 9, 27);
        var request = new ScheduledRequest("08:17", false, new DateTimeOffset(date, TimeSpan.Zero));
        var store = new ScheduledLaunchStore(root);
        Check(store.TryClaim(request, date), "首次认领");
        var path = Path.Combine(root, "scheduled-claims.json");
        var previous = File.ReadAllBytes(path);
        // Allow the old record to be read, but deny its replacement after the scratch file is flushed.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                store.TryClaim(request, date.AddDays(1));
                throw new InvalidOperationException("替换失败不得返回认领成功");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(previous), "替换失败必须保留旧记录");
        }
        Check(!store.TryClaim(request, date), "替换失败后仍防重");
        Check(store.TryClaim(request, date.AddDays(1)), "解除占用后可保存下一轮");
        return Task.CompletedTask;
    }

    public static async Task ConcurrentClaimsAsync()
    {
        var root = NewRoot();
        var results = await ClaimProcessesAsync(root, 6);
        Check(results.Count(result => result == "claimed") == 1, "独立进程并发认领必须恰好成功一次");
        Check(results.All(result => result is "claimed" or "duplicate" or "busy"), "并发认领结果无效");
        Check((await ClaimProcessesAsync(root, 1)).Single() == "duplicate", "替换文件后新进程仍须防重");

        var store = new ScheduledLaunchStore(root);
        var date = new DateTime(2026, 9, 27);
        var request = new ScheduledRequest("08:17", true, new DateTimeOffset(date, TimeSpan.Zero));
        Check(store.TryClaim(request, date, (stream, claims) =>
        {
            JsonSerializer.Serialize(stream, claims);
            Check(ClaimProcessesAsync(root, 1).GetAwaiter().GetResult().Single() == "busy",
                "提醒保存期间运行认领也必须被同一把锁排除");
        }), "提醒与运行独立认领");
        using (var held = new FileStream(Path.Combine(root, "scheduled-claims.json.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check((await ClaimProcessesAsync(root, 1)).Single() == "busy", "替换后锁身份不能改变");
        Check(!store.TryClaim(request, date), "提醒不得重复认领");
    }

    public static int ClaimProcess(string root)
    {
        Console.WriteLine("ready");
        if (Console.ReadLine() != "claim") return 1;
        try
        {
            var date = new DateTime(2026, 9, 27);
            var claimed = new ScheduledLaunchStore(root).TryClaim(
                new("08:17", false, new DateTimeOffset(date, TimeSpan.Zero)), date);
            Console.WriteLine(claimed ? "claimed" : "duplicate");
        }
        catch (IOException) { Console.WriteLine("busy"); }
        return 0;
    }

    private static async Task<string[]> ClaimProcessesAsync(string root, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var children = new List<Process>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true
                };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(ScheduledLaunchTests).Assembly.Location);
                start.ArgumentList.Add("--scheduled-claim-helper");
                start.ArgumentList.Add(root);
                children.Add(Process.Start(start) ?? throw new InvalidOperationException("测试子进程未启动"));
            }
            foreach (var child in children)
                Check(await child.StandardOutput.ReadLineAsync(timeout.Token) == "ready", "测试子进程未就绪");
            foreach (var child in children) await child.StandardInput.WriteLineAsync("claim");
            var results = new List<string>();
            foreach (var child in children)
            {
                results.Add(await child.StandardOutput.ReadLineAsync(timeout.Token) ?? "missing");
                await child.WaitForExitAsync(timeout.Token);
                Check(child.ExitCode == 0, "测试子进程异常退出");
            }
            return results.ToArray();
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
        }
    }

    public static Task DesktopAsync()
    {
        Check(ScheduledLaunch.ForegroundBlock(true, true, "Default", true, false, false, "Window") is null, "自身窗口");
        foreach (var name in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            Check(ScheduledLaunch.ForegroundBlock(true, true, "Default", false, false, true, name) is null, "桌面或任务栏");
        foreach (var name in new[] { "CabinetWClass", "Chrome_WidgetWin_1", "PetWindow", "" })
            Check(ScheduledLaunch.ForegroundBlock(true, true, "Default", false, false, true, name) is not null, "文件管理器/其他前台不能因 explorer 放行");
        Check(ScheduledLaunch.ForegroundBlock(true, true, "Default", false, false, false, "WorkerW") is not null, "伪装类名不可放行");
        Check(ScheduledLaunch.ForegroundBlock(false, true, "Default", true, false, false, "") is not null, "断开会话");
        Check(ScheduledLaunch.ForegroundBlock(true, false, "Default", true, false, false, "") is not null, "无法读桌面");
        Check(ScheduledLaunch.ForegroundBlock(true, true, "Winlogon", true, false, false, "") is not null, "锁屏");
        Check(ScheduledLaunch.AdmissionBlock(true, false, true, null) is null, "空闲可运行");
        Check(ScheduledLaunch.AdmissionBlock(true, true, true, null) is not null, "运行/准备中不排队");
        Check(ScheduledLaunch.AdmissionBlock(false, false, true, null) is not null, "初始化中不运行");
        Check(ScheduledLaunch.AdmissionBlock(true, false, false, null) is not null, "注册失败不伪装可用");
        Check(ScheduledLaunch.AdmissionBlock(true, false, true, "前台已变化") is not null, "准备后前台变化需阻止");
        return Task.CompletedTask;
    }

    public static Task TaskXmlAsync()
    {
        var root = NewRoot();
        var executable = Path.Combine(root, "folder & spaced", "GachaOps.exe");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        foreach (var reminder in new[] { false, true })
        {
            var xml = XDocument.Parse(ScheduledTaskRegistration.CreateXml(executable, "S-1-5-21-test", "00:02", reminder, DateTime.Today));
            string? Value(string name) => xml.Descendants(ns + name).SingleOrDefault()?.Value;
            Check(Value("Command") == executable, "带空格路径需原样保存");
            Check(Value("LogonType") == "InteractiveToken" && Value("RunLevel") == "HighestAvailable", "沿用管理员清单且不保存密码");
            foreach (var name in new[] { "StartWhenAvailable", "WakeToRun", "AllowStartOnDemand" }) Check(Value(name) == "false", name);
            Check(Value("MultipleInstancesPolicy") == "Parallel", "由实例入口决定忙碌拒绝，不排队");
            Check(Value("DaysInterval") == "1" && !xml.Descendants(ns + "Repetition").Any(), "每日时刻不是 15 分钟循环");
            Check(Value("Arguments") == (reminder ? "--scheduled-reminder 00:02" : "--scheduled-run 00:02"), "专用参数");
            Check(Value("StartBoundary")!.EndsWith(reminder ? "23:57:00" : "00:02:00"), "提前五分钟");
            Check(Value("Description") == ScheduledTaskRegistration.Owner, "明确所有权标记");
            var moved = XDocument.Parse(ScheduledTaskRegistration.CreateXml(Path.Combine(root, "moved", "GachaOps.exe"), "S-1-5-21-test", "00:02", reminder, DateTime.Today));
            Check(moved.Descendants(ns + "Command").Single().Value != executable, "迁移后重新生成路径");
        }
        return Task.CompletedTask;
    }

    public static async Task PipeAsync()
    {
        var name = "GachaOps-Test-" + Guid.NewGuid().ToString("N");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<ScheduledRequest>();
        var store = new ScheduledLaunchStore(NewRoot());
        var admitted = 0;
        var listener = ScheduledInstancePipe.ListenAsync(name, request =>
        {
            received.Add(request);
            if (store.TryClaim(request, request.ReceivedAt.Date)) admitted++;
        }, cancel.Token);
        var request = new ScheduledRequest("08:17", false, DateTimeOffset.Now);
        Check(await ScheduledInstancePipe.ForwardAsync(name, request, cancel.Token), "已有实例必须收到请求并确认");
        Check(await ScheduledInstancePipe.ForwardAsync(name, request, cancel.Token), "监听器继续接收，由认领策略去重");
        Check(received.Count == 2 && received.All(item => item == request), "转交内容不丢失");
        Check(admitted == 1, "重复转交只能准入一次");
        cancel.Cancel();
        await listener;
        using var disconnected = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Check(!await ScheduledInstancePipe.ForwardAsync(name, request, disconnected.Token), "实例不存在时不能假报成功");
    }

    public static Task RegistrationAsync()
    {
        var executable = Path.Combine(NewRoot(), "GachaOps.exe");
        const string prefix = "GachaOps-test-Daily-";
        var existing = new Dictionary<string, string?> { [prefix + "0800-Run"] = ScheduledTaskRegistration.Owner, ["OtherApp"] = "other" };
        var disabled = new List<string>();
        var registered = new List<string>();
        for (var flags = 0; flags < 8; flags++)
        {
            disabled.Clear(); registered.Clear();
            var settings = new AppSettings { ScheduledLaunchEnabled = (flags & 1) != 0,
                NotificationsEnabled = (flags & 2) != 0, NotifyBeforeScheduledRun = (flags & 4) != 0,
                DailySchedules = [new("08:17"), new("09:31"), new("10:17", false)] };
            ScheduledTaskRegistration.Reconcile(settings, executable, "test", existing, disabled.Add,
                (name, _) => registered.Add(name));
            Check(disabled.SequenceEqual(new[] { prefix + "0800-Run" }), "只停用自己的旧任务");
            Check(registered.Count == (!settings.ScheduledLaunchEnabled ? 0 : flags == 7 ? 4 : 2), "提醒总开关与子开关组合");
            Check(registered.All(name => name.StartsWith(prefix) && !name.Contains("1017")), "单项停用不注册");
        }
        existing[prefix + "0817-Run"] = "someone else";
        disabled.Clear();
        try
        {
            ScheduledTaskRegistration.Reconcile(new(), executable, "test", existing, disabled.Add, (_, _) => { });
            throw new IOException("所有权冲突应拒绝");
        }
        catch (InvalidOperationException) { Check(disabled.Count == 0, "冲突前不能修改任何任务"); }
        existing.Remove(prefix + "0817-Run");
        try
        {
            ScheduledTaskRegistration.Reconcile(new() { ScheduledLaunchEnabled = true, DailySchedules = [new("08:17")] },
                executable, "test", existing, _ => { }, (_, _) => throw new UnauthorizedAccessException());
            throw new IOException("注册失败不能声称可用");
        }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    public static async Task SkipReportAsync()
    {
        var root = NewRoot();
        var history = new HistoryStore(root);
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var notifications = new NotificationService(client);
        var settings = new AppSettings { NotificationsEnabled = true, NotifyRunResult = true,
            BarkAddress = "https://example.invalid/key", WorkflowTasks = [new() { ToolId = ToolId.Maa, IsEnabled = true, Channel = 1 }] };
        var request = new ScheduledRequest("08:17", false, DateTimeOffset.Now);
        var report = await ScheduledRunReport.SkipAsync(settings, request, "已有一轮运行或准备中", root, history, notifications);
        Check(report.Persisted && (await history.ReadAllAsync()).Single().State == RunState.Skipped, "跳过记录准确");
        Check(handler.Calls == 1 && report.Summary.Title == "GachaOps · 定时已跳过"
            && report.Summary.Body == "08:17：已有任务进行中", "跳过推送只保留时刻和一次原因");
        Check(handler.LastBody == report.Summary.Body, "精简正文实际发送给渠道");
        var journalPath = Path.Combine(root, "notifications.jsonl");
        using (var entry = JsonDocument.Parse(File.ReadLines(journalPath).Last()))
        {
            Check(entry.RootElement.GetProperty("Outcome").GetString() == "服务已接收", "记录服务接收而非手机送达");
            Check(entry.RootElement.GetProperty("ScheduledTime").GetString() == request.Time, "通知记录可关联定时时刻");
        }
        report = await ScheduledRunReport.SkipAsync(settings, request, "其他应用处于前台", root, history, notifications);
        Check(handler.Calls == 2 && report.Summary.Body.Contains("其他应用处于前台"), "前台占用也须发送跳过结果");
        settings.NotifyRunResult = false;
        report = await ScheduledRunReport.SkipAsync(settings, request, "已锁屏", root, history, notifications);
        Check(handler.Calls == 2, "结果开关独立");
        Check(report.Persisted && report.Summary.Body.Contains("桌面不可用") && report.Delivery.SkippedReason == "该类通知已关闭",
            "通知时机关闭仍提供完整跳过结果供窗口显示");
        using (var entry = JsonDocument.Parse(File.ReadLines(journalPath).Last()))
            Check(entry.RootElement.GetProperty("Reason").GetString() == "该类通知已关闭", "关闭开关的静默结果必须可区分");
        settings.NotificationsEnabled = false;
        report = await ScheduledRunReport.SkipAsync(settings, request, "其他应用处于前台", root, history, notifications);
        Check(handler.Calls == 2 && report.Persisted && report.Summary.Body.Contains("其他应用处于前台")
            && report.Delivery.SkippedReason == "通知总开关关闭", "通知总开关关闭仍保留可显示的跳过原因");
        settings.NotificationsEnabled = settings.NotifyRunResult = true;
        using var failedClient = new HttpClient(new CaptureHandler(HttpStatusCode.ServiceUnavailable));
        report = await ScheduledRunReport.SkipAsync(settings, request, "已有一轮运行或准备中", root, history,
            new NotificationService(failedClient));
        Check(report.Persisted && report.Summary.Body.Contains("已有任务进行中") && report.Delivery.Error is not null,
            "发送失败不得丢失跳过结果，窗口可同时显示发送失败");
        var blockedRoot = Path.Combine(NewRoot(), "file");
        File.WriteAllText(blockedRoot, "cannot create directory here");
        report = await ScheduledRunReport.SkipAsync(settings, request, "前台变化", blockedRoot, new HistoryStore(blockedRoot), notifications);
        Check(!report.Persisted && report.Summary.Body.Contains("历史保存失败"), "历史失败必须告知窗口保留");
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "GachaOps-ScheduledTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CaptureHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastBody = payload.RootElement.GetProperty("body").GetString();
            return new HttpResponseMessage(statusCode) { Content = new StringContent("{\"code\":200}", Encoding.UTF8, "application/json") };
        }
    }
}

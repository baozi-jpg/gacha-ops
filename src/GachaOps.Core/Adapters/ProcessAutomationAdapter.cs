using System.Diagnostics;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public abstract class ProcessAutomationAdapter : IAutomationAdapter
{
    private readonly LogMonitor _logMonitor;

    protected ProcessAutomationAdapter(LogMonitor? logMonitor = null)
    {
        _logMonitor = logMonitor ?? new LogMonitor();
    }

    public abstract ToolId Id { get; }

    public abstract string DisplayName { get; }

    public ValidationResult Validate(AppSettings settings)
    {
        var issues = new List<string>();
        var executablePath = GetExecutablePath(settings);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            issues.Add($"找不到 {DisplayName} 程序：{executablePath}");
        }

        var source = GetLogSource(settings);
        if (!Directory.Exists(source.DirectoryPath))
        {
            issues.Add($"找不到 {DisplayName} 日志目录：{source.DirectoryPath}");
        }

        if (File.Exists(executablePath) && IsProcessAlreadyRunning(executablePath))
        {
            issues.Add($"{DisplayName} 已经在运行，为避免重复任务不会再次启动。");
        }

        ValidateAdditional(settings, issues);
        return issues.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, issues);
    }

    public bool CanContinueAfterUnstartedFailure(AppSettings settings)
    {
        var executablePath = GetExecutablePath(settings);
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        try
        {
            // No launch was attempted by this run. Reject any existing tool, including
            // another installation with the same name; absence is not game-exit evidence.
            return !IsProcessAlreadyRunning(executablePath);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public ProcessStartInfo BuildStartInfo(AppSettings settings)
    {
        var executablePath = GetExecutablePath(settings);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        AddArguments(startInfo, settings);
        return startInfo;
    }

    public Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = Validate(settings);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Issues));
        }

        var source = GetLogSource(settings);
        var checkpoint = _logMonitor.Capture(source);
        var startedAt = DateTimeOffset.Now;
        var process = Process.Start(BuildStartInfo(settings))
            ?? throw new InvalidOperationException($"Windows 未能启动 {DisplayName}。");
        return Task.FromResult(new AutomationRunHandle(process, startedAt, source, checkpoint));
    }

    public Task<RunResult> MonitorAsync(
        AutomationRunHandle handle,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var observeLogLine = CreateLogObserver(settings);
        return _logMonitor.MonitorAsync(
            handle.Process,
            handle.StartedAt,
            handle.LogSource,
            handle.Checkpoint,
            observeLogLine,
            settings.NoLogTimeout,
            settings.HardTimeout,
            progress,
            cancellationToken,
            GetCompletionFinalizationPolicy(settings));
    }

    protected abstract string GetExecutablePath(AppSettings settings);

    protected abstract LogSource GetLogSource(AppSettings settings);

    protected abstract void AddArguments(ProcessStartInfo startInfo, AppSettings settings);

    protected abstract Func<string, LogObservation> CreateLogObserver(AppSettings settings);

    protected abstract CompletionFinalizationPolicy GetCompletionFinalizationPolicy(AppSettings settings);

    protected virtual void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
    }

    private static bool IsProcessAlreadyRunning(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited during the check.
                }
            }
        }

        return false;
    }
}

using System.Diagnostics;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public sealed class BetterGiUpdateProvider : ToolUpdateProviderBase
{
    public BetterGiUpdateProvider(
        IGitHubReleaseClient? releaseClient = null,
        TimeSpan? updateTimeout = null)
        : base(releaseClient, updateTimeout)
    {
    }

    public override ToolId Id => ToolId.BetterGi;

    public override string DisplayName => ToolCatalog.Get(Id).DisplayName;

    protected override string Repository => "babalae/better-genshin-impact";

    protected override string GetExecutablePath(AppSettings settings) => settings.BetterGiPath;

    protected override string ReadVersion(AppSettings settings)
    {
        var info = FileVersionInfo.GetVersionInfo(settings.BetterGiPath);
        return info.ProductVersion ?? info.FileVersion
            ?? throw new InvalidOperationException("无法读取 BetterGI 版本。");
    }

    protected override IReadOnlyList<string> GetFingerprintPaths(AppSettings settings) =>
        [settings.BetterGiPath];

    protected override string? InferLegacyInstallationPath(
        AppSettings settings,
        ToolUpdatePendingState pending) =>
        InferInstallationPathFromProcessDirectory(settings.BetterGiPath, pending.ProcessPath);

    public override ProcessStartInfo BuildUpdateStartInfo(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.BetterGiPath) ?? string.Empty;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(root, "BetterGI.update.exe"),
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add("cnb");
        return startInfo;
    }
}

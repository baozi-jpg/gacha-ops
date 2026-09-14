using System.Text.Json;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed class ToolUpdateStateStore
{
    private readonly string _statePath;
    private readonly JsonSerializerOptions _options = SettingsStore.CreateJsonOptions();

    public ToolUpdateStateStore(string? appDataRoot = null)
    {
        var root = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GachaOps");
        _statePath = Path.Combine(root, "tool-update-state.json");
    }

    public string StatePath => _statePath;

    public async Task<ToolUpdatePersistentState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                _statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<ToolUpdatePersistentState>(
                stream,
                _options,
                cancellationToken).ConfigureAwait(false);
            if (state is null || state.SchemaVersion != 1)
            {
                throw new InvalidDataException("工具更新状态文件版本无效。");
            }

            if (state.PendingUpdates is null)
                throw new InvalidDataException("工具更新状态缺少有效恢复集合。");
            Validate(state);
            return state;
        }
        catch (FileNotFoundException)
        {
            return new ToolUpdatePersistentState();
        }
        catch (DirectoryNotFoundException)
        {
            return new ToolUpdatePersistentState();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("工具更新状态文件无法解析。", exception);
        }
    }

    public async Task SaveAsync(
        ToolUpdatePersistentState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(_statePath)
            ?? throw new InvalidOperationException("工具更新状态路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = string.Concat(_statePath, ".tmp");

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, state, _options, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    private static void Validate(ToolUpdatePersistentState state)
    {
        foreach (var (toolId, pending) in state.PendingUpdates)
        {
            if (!Enum.IsDefined(toolId)
                || pending is null
                || pending.ToolId != toolId
                || string.IsNullOrWhiteSpace(pending.TargetVersion)
                || !IsValidFingerprint(pending.BeforeFingerprint)
                || pending.ProcessId is <= 0)
            {
                throw new InvalidDataException("工具更新状态包含无效的恢复记录。");
            }
        }
    }

    private static bool IsValidFingerprint(ToolVersionFingerprint? fingerprint) =>
        fingerprint is not null
        && !string.IsNullOrWhiteSpace(fingerprint.Version)
        && !string.IsNullOrWhiteSpace(fingerprint.Sha256)
        && fingerprint.TotalLength >= 0;
}

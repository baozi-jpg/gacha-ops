using System.Text.Json;
using System.Text.Json.Serialization;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed record SettingsLoadResult(AppSettings Settings, bool RecoveredFromCorruptSettings);

public sealed class SettingsStore
{
    private readonly string _settingsPath;
    private readonly string _corruptSettingsPath;
    private readonly JsonSerializerOptions _options;

    public SettingsStore(string? appDataRoot = null)
    {
        var root = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GachaOps");
        _settingsPath = Path.Combine(root, "settings.json");
        _corruptSettingsPath = Path.Combine(root, "settings.corrupt.json");
        _options = CreateJsonOptions();
    }

    public string SettingsPath => _settingsPath;

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AppSettings? settings;
            await using (var stream = File.OpenRead(_settingsPath))
            {
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (settings is null || !HasValidStructure(settings))
            {
                return RecoverFromCorruptSettings();
            }

            settings.Normalize();
            return new SettingsLoadResult(settings, RecoveredFromCorruptSettings: false);
        }
        catch (FileNotFoundException)
        {
            return new SettingsLoadResult(CreateDefaultSettings(), RecoveredFromCorruptSettings: false);
        }
        catch (DirectoryNotFoundException)
        {
            return new SettingsLoadResult(CreateDefaultSettings(), RecoveredFromCorruptSettings: false);
        }
        catch (JsonException)
        {
            return RecoverFromCorruptSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("设置路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = string.Concat(_settingsPath, ".tmp");

        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static AppSettings CreateDefaultSettings()
    {
        var settings = new AppSettings { WorkflowTasks = [] };
        settings.Normalize();
        return settings;
    }

    private static bool HasValidStructure(AppSettings settings) =>
        settings is
        {
            BetterGiPath: not null,
            BetterGiMode: not null,
            BetterGiProfile: not null,
            MaaPath: not null,
            MaaProfile: not null,
            MaaEndPath: not null,
            MaaEndInstance: not null
        };

    private SettingsLoadResult RecoverFromCorruptSettings()
    {
        File.Move(_settingsPath, _corruptSettingsPath, overwrite: true);
        return new SettingsLoadResult(CreateDefaultSettings(), RecoveredFromCorruptSettings: true);
    }
}

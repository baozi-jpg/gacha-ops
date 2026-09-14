using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GachaOps.Core.Services;

public sealed class CrashLogStore
{
    private const int SourceLengthLimit = 32;
    private const int VersionLengthLimit = 128;
    private const int ExceptionTypeLengthLimit = 512;
    private const int MessageLengthLimit = 2_048;
    private const int StackTraceLengthLimit = 16_384;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _crashPath;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = false };
    private readonly object _writeGate = new();

    public CrashLogStore(string? appDataRoot = null, TimeProvider? timeProvider = null)
    {
        var root = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GachaOps");
        _crashPath = Path.Combine(root, "crashes", "crash.jsonl");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryWrite(string source, Exception exception)
    {
        try
        {
            lock (_writeGate)
            {
                var directory = Path.GetDirectoryName(_crashPath)
                    ?? throw new InvalidOperationException("崩溃记录路径没有父目录。");
                Directory.CreateDirectory(directory);
                PruneCore();

                var record = new CrashLogRecord(
                    _timeProvider.GetUtcNow(),
                    Limit(source, SourceLengthLimit),
                    Limit(GetApplicationVersion(), VersionLengthLimit),
                    Limit(exception.GetType().FullName ?? exception.GetType().Name, ExceptionTypeLengthLimit),
                    Limit(exception.Message, MessageLengthLimit),
                    Limit(exception.StackTrace, StackTraceLengthLimit));
                var line = JsonSerializer.Serialize(record, _options) + Environment.NewLine;
                File.AppendAllText(_crashPath, line, Utf8WithoutBom);
                return true;
            }
        }
        catch (Exception)
        {
            // Best-effort crash evidence must never replace the original application failure.
            return false;
        }
    }

    public bool TryPrune()
    {
        try
        {
            lock (_writeGate)
            {
                PruneCore();
                return true;
            }
        }
        catch (Exception)
        {
            // Startup and crash handling must continue when local storage is unavailable.
            return false;
        }
    }

    private void PruneCore()
    {
        if (!File.Exists(_crashPath))
        {
            return;
        }

        var cutoff = _timeProvider.GetUtcNow().AddDays(-7);
        var retainedRecords = new List<CrashLogRecord>();
        var rewriteRequired = !EndsWithLineBreak(_crashPath);
        using (var stream = new FileStream(
                   _crashPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 16 * 1024,
                   FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    rewriteRequired = true;
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<CrashLogRecord>(line, _options);
                    if (record is null || record.Timestamp < cutoff)
                    {
                        rewriteRequired = true;
                    }
                    else
                    {
                        retainedRecords.Add(record);
                    }
                }
                catch (JsonException)
                {
                    rewriteRequired = true;
                }
            }
        }

        if (rewriteRequired)
        {
            Rewrite(retainedRecords);
        }
    }

    private void Rewrite(IReadOnlyList<CrashLogRecord> records)
    {
        var temporaryPath = string.Concat(_crashPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                foreach (var record in records)
                {
                    writer.WriteLine(JsonSerializer.Serialize(record, _options));
                }
            }

            File.Move(temporaryPath, _crashPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool EndsWithLineBreak(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        var finalByte = stream.ReadByte();
        return finalByte is '\r' or '\n';
    }

    private static string GetApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashLogStore).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }

    private static string Limit(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private sealed record CrashLogRecord(
        DateTimeOffset Timestamp,
        string Source,
        string Version,
        string ExceptionType,
        string Message,
        string StackTrace);
}

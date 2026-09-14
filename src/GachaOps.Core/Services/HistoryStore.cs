using System.Text.Json;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed class HistoryStore
{
    public static IComparer<RunRecord> LatestFirstComparer { get; } =
        Comparer<RunRecord>.Create(static (left, right) =>
        {
            var comparison = right.StartedAt.CompareTo(left.StartedAt);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.EndedAt.CompareTo(left.EndedAt);
            return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
        });

    private readonly string _historyPath;
    private readonly JsonSerializerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public HistoryStore(string? appDataRoot = null, TimeProvider? timeProvider = null)
    {
        _options = SettingsStore.CreateJsonOptions();
        _options.WriteIndented = false;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var root = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GachaOps");
        _historyPath = Path.Combine(root, "runs", "history.jsonl");
    }

    public string HistoryPath => _historyPath;

    public async Task AppendAsync(RunRecord record, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_historyPath)
            ?? throw new InvalidOperationException("历史路径没有父目录。");
        Directory.CreateDirectory(directory);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = _timeProvider.GetUtcNow().AddDays(-7);
            var (retainedRecords, rewriteRequired) = await ReadRetainedRecordsAsync(
                cutoff,
                cancellationToken).ConfigureAwait(false);
            if (record.EndedAt < cutoff)
            {
                if (rewriteRequired)
                {
                    await RewriteAsync(retainedRecords, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var persistedRecord = WithoutLogExcerpt(record);
            if (rewriteRequired)
            {
                var snapshot = new List<RunRecord>(retainedRecords.Count + 1);
                snapshot.AddRange(retainedRecords);
                snapshot.Add(persistedRecord);
                await RewriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                return;
            }

            var line = JsonSerializer.Serialize(persistedRecord, _options) + Environment.NewLine;
            await File.AppendAllTextAsync(_historyPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<RunRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return await ReadCoreAsync(maximumCount: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunRecord>> ReadLatestAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        return await ReadCoreAsync(maximumCount, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RunRecord>> ReadCoreAsync(
        int? maximumCount,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = _timeProvider.GetUtcNow().AddDays(-7);
            var (retainedRecords, rewriteRequired) = await ReadRetainedRecordsAsync(
                cutoff,
                cancellationToken).ConfigureAwait(false);
            if (rewriteRequired)
            {
                await RewriteAsync(retainedRecords, cancellationToken).ConfigureAwait(false);
            }

            var orderedRecords = retainedRecords.Order(LatestFirstComparer);
            return maximumCount is { } recentLimit
                ? orderedRecords.Take(recentLimit).ToArray()
                : orderedRecords.ToArray();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(List<RunRecord> RetainedRecords, bool RewriteRequired)> ReadRetainedRecordsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var retainedRecords = new List<RunRecord>();
        var rewriteRequired = false;
        FileStream stream;
        try
        {
            stream = new FileStream(
                _historyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return (retainedRecords, rewriteRequired);
        }
        catch (DirectoryNotFoundException)
        {
            return (retainedRecords, rewriteRequired);
        }

        await using var ownedStream = stream;
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                rewriteRequired = true;
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<RunRecord>(line, _options);
                if (record is not null)
                {
                    if (record.EndedAt < cutoff)
                    {
                        rewriteRequired = true;
                    }
                    else
                    {
                        rewriteRequired |= record.LogExcerpt is not { Count: 0 };
                        retainedRecords.Add(WithoutLogExcerpt(record));
                    }
                }
                else
                {
                    rewriteRequired = true;
                }
            }
            catch (JsonException)
            {
                rewriteRequired = true;
            }
        }

        return (retainedRecords, rewriteRequired);
    }

    private async Task RewriteAsync(
        IReadOnlyList<RunRecord> records,
        CancellationToken cancellationToken)
    {
        var temporaryPath = string.Concat(_historyPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream))
            {
                foreach (var record in records)
                {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(WithoutLogExcerpt(record), _options).AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static RunRecord WithoutLogExcerpt(RunRecord record)
    {
        return record.LogExcerpt is { Count: 0 }
            ? record
            : record with { LogExcerpt = Array.Empty<string>() };
    }
}

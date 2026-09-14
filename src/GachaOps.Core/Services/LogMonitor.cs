using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GachaOps.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace GachaOps.Core.Services;

public sealed class LogMonitor
{
    private const int ReadBufferCharacters = 8 * 1024;
    private const int MaxBufferedLineCharacters = 64 * 1024;
    private const int LongLineOverlapCharacters = 512;
    private const int MaxExcerptLines = 20;
    private const int MaxInternalErrorDetails = 8;
    private const int MaxInternalErrorDetailCharacters = 240;
    private const int ContinuityWindowBytes = 64;
    private readonly TimeSpan _pollInterval;

    public LogMonitor(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(750);
    }

    public LogCheckpoint Capture(LogSource source)
    {
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var fileStates = new Dictionary<string, LogCheckpoint.FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateLogFiles(source, out _))
        {
            try
            {
                var offset = new FileInfo(file).Length;
                offsets[file] = offset;

                using var stream = OpenLogFile(file);
                offset = stream.Length;
                offsets[file] = offset;
                fileStates[file] = CaptureFileState(stream, offset);
            }
            catch (IOException)
            {
                // The writer may rotate the file while the snapshot is taken.
            }
        }

        return new LogCheckpoint(offsets) { FileStates = fileStates };
    }

    public async Task<RunResult> MonitorAsync(
        Process process,
        DateTimeOffset startedAt,
        LogSource source,
        LogCheckpoint checkpoint,
        Func<string, LogObservation> observeLine,
        TimeSpan noLogTimeout,
        TimeSpan hardTimeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        CompletionFinalizationPolicy completionFinalizationPolicy = CompletionFinalizationPolicy.CompleteOnEvidence)
    {
        return await MonitorCoreAsync(
            () => SafeHasExited(process),
            () => SafeExitCode(process),
            startedAt,
            source,
            checkpoint,
            observeLine,
            noLogTimeout,
            hardTimeout,
            progress,
            cancellationToken,
            completionFinalizationPolicy).ConfigureAwait(false);
    }

    public async Task<RunResult> MonitorCoreAsync(
        Func<bool> hasExited,
        Func<int?> getExitCode,
        DateTimeOffset startedAt,
        LogSource source,
        LogCheckpoint checkpoint,
        Func<string, LogObservation> observeLine,
        TimeSpan noLogTimeout,
        TimeSpan hardTimeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        CompletionFinalizationPolicy completionFinalizationPolicy = CompletionFinalizationPolicy.CompleteOnEvidence)
    {
        var offsets = new Dictionary<string, long>(checkpoint.Offsets, StringComparer.OrdinalIgnoreCase);
        var carries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var decodeStates = new Dictionary<string, Utf8FileDecodeState>(StringComparer.OrdinalIgnoreCase);
        var fileStates = new Dictionary<string, LogCheckpoint.FileState>(
            checkpoint.FileStates,
            StringComparer.OrdinalIgnoreCase);
        var recentExcerpt = new Queue<LogExcerptEntry>();
        var evidenceExcerpt = new Queue<LogExcerptEntry>();
        var criticalExcerpt = new Queue<LogExcerptEntry>();
        var startedWorkItems = new HashSet<string>(StringComparer.Ordinal);
        var succeededWorkItems = new HashSet<string>(StringComparer.Ordinal);
        var failedWorkItems = new HashSet<string>(StringComparer.Ordinal);
        var internalErrorDetails = new List<string>();
        var internalErrorDetailSet = new HashSet<string>(StringComparer.Ordinal);
        var completionEvidenceObserved = false;
        var internalErrorObserved = false;
        var blockingFailureObserved = false;
        var logReadFailed = false;
        var lastActivity = startedAt;
        long nextLogLineSequence = 0;

        void ProcessLine(string line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = observeLine(line);
            cancellationToken.ThrowIfCancellationRequested();
            var excerptEntry = CreateExcerptEntry(nextLogLineSequence++, line);
            AddExcerpt(recentExcerpt, excerptEntry);
            if (observation.HasEvidence)
            {
                AddExcerpt(evidenceExcerpt, excerptEntry);
            }
            if (observation.HasCriticalEvidence)
            {
                AddExcerpt(criticalExcerpt, excerptEntry);
            }

            progress?.Report(line);
            cancellationToken.ThrowIfCancellationRequested();
            completionEvidenceObserved |= observation.RunCompleted;
            internalErrorObserved |= observation.InternalError;
            blockingFailureObserved |= observation.BlockingFailure;
            AddInternalErrorDetail(observation.InternalErrorDetail);
            AddWorkItem(startedWorkItems, observation.StartedWorkItemId);
            AddWorkItem(succeededWorkItems, observation.SucceededWorkItemId);
            AddWorkItem(failedWorkItems, observation.FailedWorkItemId);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scan = ReadNewLines(
                source,
                offsets,
                carries,
                decodeStates,
                fileStates,
                cancellationToken,
                ProcessLine);
            cancellationToken.ThrowIfCancellationRequested();
            logReadFailed = scan.ReadFailed;
            if (scan.BytesRead > 0)
            {
                lastActivity = DateTimeOffset.Now;
            }

            if (blockingFailureObserved)
            {
                return new RunResult(
                    RunState.Failed,
                    "日志报告无法继续的运行失败。",
                    getExitCode(),
                    CreateExcerpt());
            }

            if (hasExited())
            {
                var finalScan = ReadNewLines(
                    source,
                    offsets,
                    carries,
                    decodeStates,
                    fileStates,
                    cancellationToken,
                    ProcessLine,
                    flushCarries: true);
                cancellationToken.ThrowIfCancellationRequested();
                logReadFailed = finalScan.ReadFailed;

                if (blockingFailureObserved)
                {
                    return new RunResult(
                        RunState.Failed,
                        "进程退出前报告无法继续的运行失败。",
                        getExitCode(),
                        CreateExcerpt());
                }

                var exitCode = getExitCode();
                if (exitCode != 0)
                {
                    return new RunResult(
                        RunState.Failed,
                        exitCode is null
                            ? "程序已经退出，但无法确认正常退出。"
                            : $"程序异常退出（退出码 {exitCode}）。",
                        exitCode,
                        CreateExcerpt());
                }

                if (completionEvidenceObserved || AllStartedWorkItemsHaveTerminalState())
                {
                    return CreateCompletedResult(exitCode, CompletionMessage());
                }

                return new RunResult(
                    RunState.Failed,
                    LogReadFailureOr("程序已经退出，但没有检测到本次任务的完成标记。"),
                    exitCode,
                    CreateExcerpt());
            }

            if (completionEvidenceObserved
                && completionFinalizationPolicy == CompletionFinalizationPolicy.CompleteOnEvidence)
            {
                return CreateCompletedResult(getExitCode(), CompletionMessage());
            }

            var now = DateTimeOffset.Now;
            if (now - startedAt >= hardTimeout)
            {
                return new RunResult(RunState.TimedOut, $"运行超过 {hardTimeout.TotalMinutes:0} 分钟。", getExitCode(), CreateExcerpt());
            }

            if (now - lastActivity >= noLogTimeout)
            {
                return new RunResult(
                    RunState.TimedOut,
                    LogReadFailureOr($"连续 {noLogTimeout.TotalMinutes:0} 分钟没有新日志。"),
                    getExitCode(),
                    CreateExcerpt());
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        bool AllStartedWorkItemsHaveTerminalState() => startedWorkItems.Count > 0
            && startedWorkItems.All(workItemId =>
                succeededWorkItems.Contains(workItemId) || failedWorkItems.Contains(workItemId));

        string CompletionMessage() =>
            completionFinalizationPolicy == CompletionFinalizationPolicy.RequireProcessExit
                ? "任务已完成，且程序已正常退出。"
                : "任务已完成。";

        string LogReadFailureOr(string fallback) =>
            logReadFailed ? "日志读取失败，无法确认任务状态。" : fallback;

        RunResult CreateCompletedResult(int? exitCode, string successMessage)
        {
            var completedWithErrors = internalErrorObserved || failedWorkItems.Count > 0;
            return new RunResult(
                completedWithErrors ? RunState.CompletedWithErrors : RunState.Succeeded,
                completedWithErrors
                    ? CreateInternalErrorMessage()
                    : successMessage,
                exitCode,
                CreateExcerpt());
        }

        IReadOnlyList<string> CreateExcerpt() =>
            BuildExcerpt(recentExcerpt, evidenceExcerpt, criticalExcerpt);

        void AddInternalErrorDetail(string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return;
            }

            var compact = detail.Trim();
            if (compact.Length > MaxInternalErrorDetailCharacters)
            {
                compact = string.Concat(
                    compact.AsSpan(0, MaxInternalErrorDetailCharacters - 3),
                    "...");
            }

            if (internalErrorDetailSet.Add(compact)
                && internalErrorDetails.Count < MaxInternalErrorDetails)
            {
                internalErrorDetails.Add(compact);
            }
        }

        string CreateInternalErrorMessage()
        {
            var lines = internalErrorDetails
                .Select(detail => $"• {detail}")
                .ToList();
            var overflowCount = internalErrorDetailSet.Count - internalErrorDetails.Count;
            if (overflowCount > 0)
            {
                lines.Add($"• 另有 {overflowCount} 项执行异常");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    private static LogReadResult ReadNewLines(
        LogSource source,
        IDictionary<string, long> offsets,
        IDictionary<string, string> carries,
        IDictionary<string, Utf8FileDecodeState> decodeStates,
        IDictionary<string, LogCheckpoint.FileState> fileStates,
        CancellationToken cancellationToken,
        Action<string> processLine,
        bool flushCarries = false)
    {
        long totalBytes = 0;
        var files = EnumerateLogFiles(source, out var readFailed);
        var activeFiles = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byteBuffer = new byte[ReadBufferCharacters];
        var characterBuffer = new char[ReadBufferCharacters];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = OpenLogFile(file);
                var inheritedCarry = string.Empty;
                Utf8FileDecodeState? inheritedDecodeState = null;
                LogCheckpoint.FileState? inheritedFileState = null;
                long offset;
                if (source.FollowRotatedFiles
                    && !offsets.ContainsKey(file)
                    && TryGetRotatedSourcePath(file, out var rotatedSourcePath))
                {
                    offset = offsets.TryGetValue(rotatedSourcePath, out var sourceOffset) ? sourceOffset : 0;
                    if (carries.TryGetValue(rotatedSourcePath, out var sourceCarry))
                    {
                        inheritedCarry = sourceCarry;
                    }
                    if (decodeStates.TryGetValue(rotatedSourcePath, out var sourceDecodeState))
                    {
                        inheritedDecodeState = sourceDecodeState;
                    }
                    if (fileStates.TryGetValue(rotatedSourcePath, out var sourceFileState))
                    {
                        inheritedFileState = sourceFileState;
                    }

                    // The active path now points to a new file. Preserve its zero offset even if it
                    // appears after this scan, while the renamed file inherits the consumed prefix.
                    offsets[rotatedSourcePath] = 0;
                    carries.Remove(rotatedSourcePath);
                    decodeStates.Remove(rotatedSourcePath);
                    fileStates.Remove(rotatedSourcePath);
                }
                else
                {
                    offset = offsets.TryGetValue(file, out var knownOffset) ? knownOffset : 0;
                    if (carries.TryGetValue(file, out var carry))
                    {
                        inheritedCarry = carry;
                    }
                    if (fileStates.TryGetValue(file, out var fileState))
                    {
                        inheritedFileState = fileState;
                    }
                }

                if (stream.Length < offset || !IsContinuous(stream, offset, inheritedFileState))
                {
                    offset = 0;
                    inheritedCarry = string.Empty;
                    inheritedDecodeState = null;
                    carries.Remove(file);
                    decodeStates.Remove(file);
                    fileStates.Remove(file);
                }

                var decodeState = inheritedDecodeState
                    ?? (decodeStates.TryGetValue(file, out var knownDecodeState)
                        ? knownDecodeState
                        : new Utf8FileDecodeState(stripLeadingBom: offset == 0));
                decodeStates[file] = decodeState;
                stream.Seek(offset, SeekOrigin.Begin);
                var builder = new StringBuilder(inheritedCarry);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    var bytesConsumed = 0;
                    while (bytesConsumed < bytesRead)
                    {
                        decodeState.Decoder.Convert(
                            byteBuffer,
                            bytesConsumed,
                            bytesRead - bytesConsumed,
                            characterBuffer,
                            0,
                            characterBuffer.Length,
                            flush: false,
                            out var bytesUsed,
                            out var charactersUsed,
                            out _);
                        bytesConsumed += bytesUsed;
                        AppendDecodedCharacters(
                            characterBuffer,
                            charactersUsed,
                            decodeState,
                            builder,
                            processLine);
                    }
                }

                var newOffset = stream.Position;
                offsets[file] = newOffset;
                fileStates[file] = CaptureFileState(stream, newOffset);
                totalBytes += Math.Max(0, newOffset - offset);

                if (flushCarries)
                {
                    decodeState.Decoder.Convert(
                        Array.Empty<byte>(),
                        0,
                        0,
                        characterBuffer,
                        0,
                        characterBuffer.Length,
                        flush: true,
                        out _,
                        out var charactersUsed,
                        out _);
                    AppendDecodedCharacters(
                        characterBuffer,
                        charactersUsed,
                        decodeState,
                        builder,
                        processLine);
                    EmitLine(builder, processLine, retainOverlap: false);

                    carries[file] = string.Empty;
                }
                else
                {
                    carries[file] = builder.ToString();
                }
            }
            catch (IOException)
            {
                readFailed = true;
            }
            catch (UnauthorizedAccessException)
            {
                readFailed = true;
            }
        }

        if (!source.FollowRotatedFiles)
        {
            RemoveMissingFiles(offsets, activeFiles);
            RemoveMissingFiles(carries, activeFiles);
            RemoveMissingFiles(decodeStates, activeFiles);
            RemoveMissingFiles(fileStates, activeFiles);
        }

        return new LogReadResult(totalBytes, readFailed);
    }

    private static FileStream OpenLogFile(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool IsContinuous(
        FileStream stream,
        long offset,
        LogCheckpoint.FileState? previousState)
    {
        if (previousState is null)
        {
            return true;
        }

        var identity = GetFileIdentity(stream.SafeFileHandle);
        return previousState.Offset == offset
            && (previousState.Identity is null || identity is null || previousState.Identity == identity)
            && previousState.ContinuityWindow.AsSpan().SequenceEqual(ReadContinuityWindow(stream, offset));
    }

    private static LogCheckpoint.FileState CaptureFileState(FileStream stream, long offset) =>
        new(offset, GetFileIdentity(stream.SafeFileHandle), ReadContinuityWindow(stream, offset));

    private static byte[] ReadContinuityWindow(FileStream stream, long offset)
    {
        var count = (int)Math.Min(offset, ContinuityWindowBytes);
        var window = new byte[count];
        var bytesRead = 0;
        while (bytesRead < count)
        {
            var read = RandomAccess.Read(
                stream.SafeFileHandle,
                window.AsSpan(bytesRead),
                offset - count + bytesRead);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            bytesRead += read;
        }
        return window;
    }

    private static LogCheckpoint.FileIdentity? GetFileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        return new LogCheckpoint.FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void AppendDecodedCharacters(
        char[] characters,
        int count,
        Utf8FileDecodeState decodeState,
        StringBuilder builder,
        Action<string> processLine)
    {
        var start = 0;
        if (decodeState.StripLeadingBom && count > 0)
        {
            decodeState.StripLeadingBom = false;
            if (characters[0] == '\uFEFF')
            {
                start = 1;
            }
        }

        for (var index = start; index < count; index++)
        {
            var character = characters[index];
            if (character == '\n')
            {
                EmitLine(builder, processLine, retainOverlap: false);
            }
            else
            {
                builder.Append(character);
                if (builder.Length >= MaxBufferedLineCharacters)
                {
                    EmitLine(builder, processLine, retainOverlap: true);
                }
            }
        }
    }

    private static void EmitLine(StringBuilder builder, Action<string> processLine, bool retainOverlap)
    {
        var length = builder.Length;
        if (!retainOverlap && length > 0 && builder[length - 1] == '\r')
        {
            length--;
        }

        if (length > 0)
        {
            var line = builder.ToString(0, length);
            if (!string.IsNullOrWhiteSpace(line))
            {
                processLine(line);
            }
        }

        if (retainOverlap)
        {
            var overlapStart = Math.Max(0, builder.Length - LongLineOverlapCharacters);
            var overlap = builder.ToString(overlapStart, builder.Length - overlapStart);
            builder.Clear();
            builder.Append(overlap);
        }
        else
        {
            builder.Clear();
        }
    }

    private static void RemoveMissingFiles<T>(IDictionary<string, T> values, ISet<string> activeFiles)
    {
        foreach (var file in values.Keys.Where(file => !activeFiles.Contains(file)).ToArray())
        {
            values.Remove(file);
        }
    }

    private static string[] EnumerateLogFiles(LogSource source, out bool readFailed)
    {
        readFailed = false;
        try
        {
            return Directory.EnumerateFiles(
                    source.DirectoryPath,
                    source.SearchPattern,
                    source.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => source.FollowRotatedFiles && IsRotatedFile(path) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            readFailed = true;
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            readFailed = true;
            return Array.Empty<string>();
        }
    }

    private static bool IsRotatedFile(string path) =>
        Path.GetFileName(path).Contains(".bak.", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRotatedSourcePath(string path, out string sourcePath)
    {
        var fileName = Path.GetFileName(path);
        var backupMarkerIndex = fileName.IndexOf(".bak.", StringComparison.OrdinalIgnoreCase);
        if (backupMarkerIndex <= 0)
        {
            sourcePath = string.Empty;
            return false;
        }

        var extension = Path.GetExtension(fileName);
        var sourceFileName = fileName[..backupMarkerIndex] + extension;
        sourcePath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, sourceFileName);
        return true;
    }

    private static void AddWorkItem(ISet<string> workItems, string? workItemId)
    {
        if (!string.IsNullOrWhiteSpace(workItemId))
        {
            workItems.Add(workItemId);
        }
    }

    private static LogExcerptEntry CreateExcerptEntry(long sequence, string line)
    {
        var compact = line.Length <= 600 ? line : string.Concat(line.AsSpan(0, 597), "...");
        return new LogExcerptEntry(sequence, compact);
    }

    private static void AddExcerpt(Queue<LogExcerptEntry> excerpt, LogExcerptEntry entry)
    {
        excerpt.Enqueue(entry);
        while (excerpt.Count > MaxExcerptLines)
        {
            excerpt.Dequeue();
        }
    }

    private static IReadOnlyList<string> BuildExcerpt(
        Queue<LogExcerptEntry> recentExcerpt,
        Queue<LogExcerptEntry> evidenceExcerpt,
        Queue<LogExcerptEntry> criticalExcerpt)
    {
        var selected = new Dictionary<long, LogExcerptEntry>();
        AddNewest(criticalExcerpt);
        AddNewest(evidenceExcerpt);
        AddNewest(recentExcerpt);
        return selected.Values
            .OrderBy(entry => entry.Sequence)
            .Select(entry => entry.Text)
            .ToArray();

        void AddNewest(IEnumerable<LogExcerptEntry> source)
        {
            foreach (var entry in source.Reverse())
            {
                if (selected.Count >= MaxExcerptLines)
                {
                    return;
                }

                selected.TryAdd(entry.Sequence, entry);
            }
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed record LogExcerptEntry(long Sequence, string Text);

    private sealed record LogReadResult(long BytesRead, bool ReadFailed);

    private sealed class Utf8FileDecodeState(bool stripLeadingBom)
    {
        public Decoder Decoder { get; } = new UTF8Encoding(false, false).GetDecoder();

        public bool StripLeadingBom { get; set; } = stripLeadingBom;
    }
}

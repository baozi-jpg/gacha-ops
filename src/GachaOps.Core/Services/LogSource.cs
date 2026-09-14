namespace GachaOps.Core.Services;

public sealed record LogSource(
    string DirectoryPath,
    string SearchPattern,
    bool IncludeSubdirectories = false,
    bool FollowRotatedFiles = false);

public sealed record LogCheckpoint(IReadOnlyDictionary<string, long> Offsets)
{
    public static LogCheckpoint Empty { get; } = new(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

    internal IReadOnlyDictionary<string, FileState> FileStates { get; init; } =
        new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);

    internal sealed record FileState(long Offset, FileIdentity? Identity, byte[] ContinuityWindow);
}

namespace DriveLedger.Core;

public sealed record FileObservation(string FileId, IReadOnlyList<string> Paths, long? AllocatedBytes,
    long? LogicalBytes, bool IsDirectory = false, bool IsReparsePoint = false);
public sealed record JournalCheckpoint(string VolumeId, string JournalId, long NextUsn);
public sealed record DiskSample(DateTimeOffset At, string VolumeId, long TotalBytes, long FreeBytes);
public sealed record CoverageNote(string Code, string Message, DateTimeOffset Start, DateTimeOffset? End);
public sealed record CollectorStatus(string State = "未启动", bool BaselineReady = false,
    long FilesObserved = 0, long AccessFailures = 0, string? Message = null, DateTimeOffset? UpdatedAt = null);
public sealed record FolderChange(string Path, long AddedBytes, long ReleasedBytes, long MigratedInBytes, long MigratedOutBytes)
{
    public long NetBytes => AddedBytes - ReleasedBytes + MigratedInBytes - MigratedOutBytes;
}
public sealed record FileChange(string FileId, string? OldPath, string? Path, DateTimeOffset At,
    long AddedBytes, long ReleasedBytes, long MigratedBytes, long? AllocatedBytes, long? LogicalBytes, string Kind,
    IReadOnlyList<string>? Paths = null, DateTimeOffset? PreviousObservedAt = null);
public sealed record ReportSnapshot(DateTimeOffset From, DateTimeOffset To,
    DiskSample? FirstObservation, DiskSample? LastObservation, long? DiskDeltaBytes,
    long AddedBytes, long ReleasedBytes, IReadOnlyList<FolderChange> Folders,
    IReadOnlyList<FileChange> Files, IReadOnlyList<DiskSample> Samples, IReadOnlyList<CoverageNote> CoverageNotes,
    bool FileDetailsComplete, DateTimeOffset? FileDetailsAvailableFrom, CollectorStatus Status)
{
    public long NetBytes => AddedBytes - ReleasedBytes;
    public long? UnattributedBytes => DiskDeltaBytes - NetBytes;
}

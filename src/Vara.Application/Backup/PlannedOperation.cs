namespace Vara.Application.Backup;

public closed record PlannedOperation(string RelativePath);

/// <summary>An operation that copies file content from the source into the mirror.</summary>
public closed record TransferOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt) : PlannedOperation(RelativePath);

/// <summary>An operation that only updates the manifest and mirror metadata, never reading source content.</summary>
public closed record MetadataOperation(string RelativePath) : PlannedOperation(RelativePath);

public sealed record AddOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt) : TransferOperation(RelativePath, SourceAbsolutePath, Size, SourceModifiedAt);

public sealed record ChangeOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? PreviousContentHash) : TransferOperation(RelativePath, SourceAbsolutePath, Size, SourceModifiedAt);

public sealed record MoveOperation(
    string RelativePath,
    string PreviousRelativePath,
    string KnownContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null) : MetadataOperation(RelativePath);

public sealed record DeleteOperation(
    string RelativePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? KnownContentHash,
    string? QuickHash = null,
    int? QuickHashScheme = null) : MetadataOperation(RelativePath);

public sealed record LinkOperation(
    string RelativePath,
    string? PreviousContentHash,
    string? LinkTarget) : MetadataOperation(RelativePath);

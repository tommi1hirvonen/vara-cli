namespace Vara.Application.Backup;

public closed record PlannedOperation(string RelativePath);

public sealed record AddOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt) : PlannedOperation(RelativePath);

public sealed record ChangeOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? PreviousContentHash) : PlannedOperation(RelativePath);

public sealed record MoveOperation(
    string RelativePath,
    string PreviousRelativePath,
    string KnownContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null) : PlannedOperation(RelativePath);

public sealed record DeleteOperation(
    string RelativePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? KnownContentHash,
    string? QuickHash = null,
    int? QuickHashScheme = null) : PlannedOperation(RelativePath);

public sealed record LinkOperation(
    string RelativePath,
    string? PreviousContentHash,
    string? LinkTarget) : PlannedOperation(RelativePath);

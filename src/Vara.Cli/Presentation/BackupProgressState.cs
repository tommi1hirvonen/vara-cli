using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// A value-type display snapshot stashed in a task's <see cref="Spectre.Console.ProgressTask.State"/>
/// under <see cref="BackupProgressColumn.ProgressKey"/>. The optional calculated
/// snapshot keeps numeric display values stable during active-path-only refreshes.
/// <see cref="Spectre.Console.ProgressTaskState"/> only accepts <c>struct</c> values, so
/// this wraps <see cref="BackupProgress"/> (a reference-type record) in a storable value type.
/// </summary>
public readonly record struct BackupProgressState(
    long BytesTransferred,
    long TotalBytes,
    int FilesTransferred = 0,
    int TotalFiles = 0,
    IReadOnlyList<string>? ActivePaths = null,
    ProgressSnapshot? Snapshot = null)
{
    public BackupProgress ToProgress() => new(BytesTransferred, TotalBytes, FilesTransferred, TotalFiles, ActivePaths);
}

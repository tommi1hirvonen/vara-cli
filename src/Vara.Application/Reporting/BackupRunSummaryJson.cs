using Vara.Application.Backup;

namespace Vara.Application.Reporting;

/// <summary>
/// Machine-readable JSON shape shared by a dry run's planned outcome and a completed
/// (or gracefully cancelled) real run's outcome, discriminated by <see cref="Mode"/> -
/// see design.md's "One JSON schema shared by dry-run and executed outcomes" decision.
/// For <c>mode: "dry-run"</c>, <see cref="Added"/>/<see cref="Changed"/>/<see cref="Moved"/>/
/// <see cref="Deleted"/> are the *planned* counts, <see cref="BytesTransferred"/> is the
/// *planned* total bytes that would be transferred, <see cref="Failed"/>/
/// <see cref="FailedPaths"/> reflect scan failures only (nothing has executed yet to fail),
/// and <see cref="Cancelled"/> is always <see langword="false"/>. For <c>mode: "executed"</c>,
/// every field carries its existing real-run meaning, unchanged from today.
/// </summary>
public sealed record BackupRunSummaryJson(
    string Mode,
    string Profile,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Cancelled,
    int Added,
    int Changed,
    int Moved,
    int Deleted,
    int Failed,
    IReadOnlyList<string> FailedPaths,
    long BytesTransferred);

/// <summary>Maps a completed real run's result, or a dry run's plan summary, to the shared <see cref="BackupRunSummaryJson"/> shape.</summary>
public static class BackupRunSummaryJsonMapper
{
    public const string ExecutedMode = "executed";
    public const string DryRunMode = "dry-run";

    public static BackupRunSummaryJson ToJson(this BackupRunResult result, string profileName) => new(
        ExecutedMode,
        profileName,
        result.StartedAt,
        result.CompletedAt,
        result.Cancelled,
        result.Stats.FilesAdded,
        result.Stats.FilesChanged,
        result.Stats.FilesMoved,
        result.Stats.FilesDeleted,
        result.Stats.FilesFailed,
        result.FailedPaths,
        result.Stats.BytesTransferred);

    public static BackupRunSummaryJson ToJson(this BackupPlanSummary summary, string profileName, DateTimeOffset startedAt, DateTimeOffset completedAt) => new(
        DryRunMode,
        profileName,
        startedAt,
        completedAt,
        Cancelled: false,
        summary.FilesAdded,
        summary.FilesChanged,
        summary.FilesMoved,
        summary.FilesDeleted,
        summary.FailedPaths.Count,
        summary.FailedPaths,
        summary.TotalBytesToTransfer);
}

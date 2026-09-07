using System.Text;
using Vara.Application.Backup;

namespace Vara.Application.Reporting;

/// <summary>
/// Formats a dry run's planned outcome (<see cref="BackupPlanSummary"/>) for the CLI's
/// end-of-run summary, reusing <see cref="BackupRunSummaryFormatter"/>'s style
/// conventions - see backup-execution's "Dry-run mode reports planned changes without
/// executing them" requirement.
/// </summary>
public static class BackupPlanSummaryFormatter
{
    public static string Format(BackupPlanSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Dry run - no changes were made");
        builder.AppendLine($"  Added:       {summary.FilesAdded}");
        builder.AppendLine($"  Changed:     {summary.FilesChanged}");
        builder.AppendLine($"  Moved:       {summary.FilesMoved}");
        builder.AppendLine($"  Deleted:     {summary.FilesDeleted}");
        builder.AppendLine($"  To transfer: {BackupRunSummaryFormatter.FormatBytes(summary.TotalBytesToTransfer)}");

        if (summary.FailedPaths.Count > 0)
        {
            builder.AppendLine($"  Failed ({summary.FailedPaths.Count}):");
            foreach (var path in summary.FailedPaths)
            {
                builder.AppendLine($"    - {path}");
            }
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }
}

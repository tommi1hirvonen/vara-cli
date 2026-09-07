using System.Text;
using Vara.Application.Backup;

namespace Vara.Application.Reporting;

/// <summary>
/// Formats a completed backup run's outcome for the CLI's end-of-run summary, per the
/// progress-reporting spec's "Run summary statistics" requirement.
/// </summary>
public static class BackupRunSummaryFormatter
{
    public static string Format(BackupRunResult result)
    {
        var builder = new StringBuilder();
        var headlineVerb = result.Cancelled ? "cancelled after" : "completed in";
        builder.AppendLine($"Snapshot #{result.SnapshotId} {headlineVerb} {FormatDuration(result.Elapsed)}");
        builder.AppendLine($"  Added:       {result.Stats.FilesAdded}");
        builder.AppendLine($"  Changed:     {result.Stats.FilesChanged}");
        builder.AppendLine($"  Moved:       {result.Stats.FilesMoved}");
        builder.AppendLine($"  Deleted:     {result.Stats.FilesDeleted}");
        builder.AppendLine($"  Transferred: {FormatBytes(result.Stats.BytesTransferred)}");

        if (result.FailedPaths.Count > 0)
        {
            builder.AppendLine($"  Failed ({result.FailedPaths.Count}):");
            foreach (var path in result.FailedPaths)
            {
                builder.AppendLine($"    - {path}");
            }
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[0]}" : $"{value:0.##} {units[unitIndex]}";
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m {duration.Seconds}s"
                : $"{duration.TotalSeconds:0.#}s";
}

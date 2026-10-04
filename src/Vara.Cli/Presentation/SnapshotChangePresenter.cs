using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

public static class SnapshotChangePresenter
{
    public static void Render(IAnsiConsole console, SnapshotChangeReport report, IReadOnlySet<string> sourceRoots)
    {
        var snapshot = report.Snapshot;
        console.WriteLine($"Snapshot #{snapshot.Id} - {snapshot.StartedAt:yyyy-MM-dd HH:mm:ss zzz} - {snapshot.Status}");
        console.WriteLine(
            $"Changes: +{snapshot.Stats.FilesAdded} ~{snapshot.Stats.FilesChanged} ->{snapshot.Stats.FilesMoved} " +
            $"-{snapshot.Stats.FilesDeleted} | {BackupRunSummaryFormatter.FormatBytes(snapshot.Stats.BytesTransferred)} transferred");

        var scopePath = NormalizePath(report.DirectoryPath);
        var root = report.Directories.Single(d => string.Equals(d.RelativePath, scopePath, StringComparison.OrdinalIgnoreCase));
        var tree = new Tree(new Text(DirectoryLabel(root, scopePath.Length == 0 ? "." : scopePath, sourceRoots)));
        var nodes = new Dictionary<string, IHasTreeNodes>(StringComparer.OrdinalIgnoreCase)
        {
            [scopePath] = tree,
        };

        foreach (var directory in report.Directories
                     .Where(d => !string.Equals(d.RelativePath, scopePath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(d => GetDepth(d.RelativePath))
                     .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = GetParentPath(directory.RelativePath);
            if (!nodes.TryGetValue(parentPath, out var parent))
            {
                continue;
            }

            var node = parent.AddNode(new Text(DirectoryLabel(directory, GetName(directory.RelativePath), sourceRoots)));
            nodes[directory.RelativePath] = node;
        }

        foreach (var directory in report.Directories)
        {
            if (directory.Files.Count == 0 || !nodes.TryGetValue(directory.RelativePath, out var parent))
            {
                continue;
            }

            foreach (var file in directory.Files)
            {
                var name = GetName(file.RelativePath);
                var change = file.ChangeKind switch
                {
                    FileChangeKind.Moved when file.PreviousRelativePath is not null =>
                        $"Moved from {file.PreviousRelativePath}",
                    _ => file.ChangeKind.ToString(),
                };
                parent.AddNode(new Text($"{name} - {change}"));
            }
        }

        console.Write(tree);
    }

    private static string DirectoryLabel(SnapshotChangeDirectory directory, string name, IReadOnlySet<string> sourceRoots)
    {
        var sourceLabel = sourceRoots.Contains(NormalizePath(directory.RelativePath)) ? " [source]" : string.Empty;
        return $"{name}{sourceLabel} (+{directory.FilesAdded} ~{directory.FilesChanged} ->{directory.FilesMoved} -{directory.FilesDeleted} link {directory.FilesLinked})";
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string GetName(string path)
    {
        var normalized = path.Replace('/', '\\');
        var separator = normalized.LastIndexOf('\\');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static int GetDepth(string path) => path.Count(c => c == '\\');

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Trim('\\', '/').Replace('/', '\\');
        return normalized == "." ? string.Empty : normalized;
    }
}

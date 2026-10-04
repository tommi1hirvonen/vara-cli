using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

public static class SnapshotChangePresenter
{
    public static void Render(
        IAnsiConsole console,
        SnapshotChangeReport report,
        IReadOnlySet<string> sourceRoots,
        int? depth = null)
    {
        if (depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Tree depth must be non-negative.");
        }

        var snapshot = report.Snapshot;
        console.WriteLine($"Snapshot #{snapshot.Id} - {snapshot.StartedAt:yyyy-MM-dd HH:mm:ss zzz} - {snapshot.Status}");
        console.WriteLine(
            $"Changes: +{snapshot.Stats.FilesAdded} ~{snapshot.Stats.FilesChanged} ->{snapshot.Stats.FilesMoved} " +
            $"-{snapshot.Stats.FilesDeleted} | {BackupRunSummaryFormatter.FormatBytes(snapshot.Stats.BytesTransferred)} transferred");

        var scopePath = NormalizePath(report.DirectoryPath);
        var root = report.Directories.Single(d => string.Equals(d.RelativePath, scopePath, StringComparison.OrdinalIgnoreCase));
        var tree = new Tree(DirectoryLabel(root, scopePath.Length == 0 ? "." : scopePath, sourceRoots));
        var nodes = new Dictionary<string, IHasTreeNodes>(StringComparer.OrdinalIgnoreCase)
        {
            [scopePath] = tree,
        };

        foreach (var directory in report.Directories
                     .Where(d => !string.Equals(d.RelativePath, scopePath, StringComparison.OrdinalIgnoreCase))
                     .Where(d => depth is null || GetRelativeDepth(d.RelativePath, scopePath) <= depth)
                     .OrderBy(d => GetDepth(d.RelativePath))
                     .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = GetParentPath(directory.RelativePath);
            if (!nodes.TryGetValue(parentPath, out var parent))
            {
                continue;
            }

            var node = parent.AddNode(DirectoryLabel(directory, GetName(directory.RelativePath), sourceRoots));
            nodes[directory.RelativePath] = node;
        }

        if (depth is { } maxDepth)
        {
            var omittedCounts = report.Directories
                .Where(directory => GetRelativeDepth(directory.RelativePath, scopePath) > maxDepth)
                .GroupBy(directory => GetAncestorAtDepth(directory.RelativePath, scopePath, maxDepth), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var (directoryPath, omittedCount) in omittedCounts)
            {
                if (nodes.TryGetValue(directoryPath, out var node))
                {
                    var noun = omittedCount == 1 ? "directory" : "directories";
                    node.AddNode(new Text($"... {omittedCount} deeper {noun} not shown"));
                }
            }
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

    private static Markup DirectoryLabel(SnapshotChangeDirectory directory, string name, IReadOnlySet<string> sourceRoots)
    {
        var sourceLabel = sourceRoots.Contains(NormalizePath(directory.RelativePath)) ? " [source]" : string.Empty;
        return new Markup(
            $"{Markup.Escape($"{name}{sourceLabel}")} " +
            $"([{FileChangeKindStyle.For(FileChangeKind.Added).MarkupColor}]+{directory.FilesAdded}[/] " +
            $"[{FileChangeKindStyle.For(FileChangeKind.Changed).MarkupColor}]~{directory.FilesChanged}[/] " +
            $"[{FileChangeKindStyle.For(FileChangeKind.Moved).MarkupColor}]->{directory.FilesMoved}[/] " +
            $"[{FileChangeKindStyle.For(FileChangeKind.Deleted).MarkupColor}]-{directory.FilesDeleted}[/] " +
            $"link [{FileChangeKindStyle.For(FileChangeKind.Linked).MarkupColor}]{directory.FilesLinked}[/])");
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

    private static int GetRelativeDepth(string path, string scopePath)
    {
        if (string.Equals(path, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var relativePath = scopePath.Length == 0
            ? path
            : path[(scopePath.Length + 1)..];
        return GetDepth(relativePath) + 1;
    }

    private static string GetAncestorAtDepth(string path, string scopePath, int depth)
    {
        if (depth == 0)
        {
            return scopePath;
        }

        var relativePath = scopePath.Length == 0
            ? path
            : path[(scopePath.Length + 1)..];
        var separatorIndex = -1;
        for (var level = 0; level < depth; level++)
        {
            separatorIndex = relativePath.IndexOf('\\', separatorIndex + 1);
            if (separatorIndex < 0)
            {
                break;
            }
        }

        var boundaryPath = separatorIndex < 0 ? relativePath : relativePath[..separatorIndex];
        return scopePath.Length == 0 ? boundaryPath : $"{scopePath}\\{boundaryPath}";
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Trim('\\', '/').Replace('/', '\\');
        return normalized == "." ? string.Empty : normalized;
    }
}

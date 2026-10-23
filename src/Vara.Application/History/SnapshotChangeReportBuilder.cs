using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// Rolls a snapshot's recorded file changes up into a sparse per-directory report: every
/// directory from the scope down to a changed file's parent appears with its cumulative
/// change counts, so a presenter can render a tree without re-aggregating.
/// </summary>
internal static class SnapshotChangeReportBuilder
{
    /// <param name="prefix">The normalized scope prefix (see <see cref="HistoryPaths.NormalizeDirectoryPrefix"/>).</param>
    /// <param name="rows">The snapshot's change rows under <paramref name="prefix"/>; must be non-empty.</param>
    /// <param name="includeFiles">Whether each directory retains the changed file rows directly inside it.</param>
    public static IReadOnlyList<SnapshotChangeDirectory> Build(
        string prefix,
        IReadOnlyList<FileVersionRecord> rows,
        bool includeFiles)
    {
        var scopePath = prefix.TrimEnd('\\');
        var directories = new Dictionary<string, MutableDirectory>(StringComparer.OrdinalIgnoreCase)
        {
            [scopePath] = new MutableDirectory(scopePath),
        };

        foreach (var row in rows)
        {
            var path = row.RelativePath.Replace('/', '\\');
            var relativePath = prefix.Length == 0
                ? path
                : path[prefix.Length..];
            var fileSeparator = relativePath.LastIndexOf('\\');
            var parentRelativePath = fileSeparator < 0 ? string.Empty : relativePath[..fileSeparator];
            var directory = parentRelativePath.Length == 0
                ? scopePath
                : scopePath.Length == 0 ? parentRelativePath : $"{scopePath}\\{parentRelativePath}";
            if (includeFiles)
            {
                GetOrAddDirectory(directory).Files.Add(row);
            }

            while (true)
            {
                GetOrAddDirectory(directory).Count(row.ChangeKind);

                if (string.Equals(directory, scopePath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var separator = directory.LastIndexOf('\\');
                directory = separator < 0 ? scopePath : directory[..separator];
            }
        }

        return directories.Values
            .OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(d => new SnapshotChangeDirectory(
                d.RelativePath,
                d.FilesAdded,
                d.FilesChanged,
                d.FilesMoved,
                d.FilesDeleted,
                d.FilesLinked,
                d.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        MutableDirectory GetOrAddDirectory(string path)
        {
            if (!directories.TryGetValue(path, out var directory))
            {
                directory = new MutableDirectory(path);
                directories.Add(path, directory);
            }

            return directory;
        }
    }

    private sealed class MutableDirectory(string relativePath)
    {
        public string RelativePath { get; } = relativePath;
        public int FilesAdded { get; private set; }
        public int FilesChanged { get; private set; }
        public int FilesMoved { get; private set; }
        public int FilesDeleted { get; private set; }
        public int FilesLinked { get; private set; }
        public List<FileVersionRecord> Files { get; } = [];

        public void Count(FileChangeKind kind)
        {
            switch (kind)
            {
                case FileChangeKind.AddedChange:
                    FilesAdded++;
                    break;
                case FileChangeKind.ChangedChange:
                    FilesChanged++;
                    break;
                case FileChangeKind.MovedChange:
                    FilesMoved++;
                    break;
                case FileChangeKind.DeletedChange:
                    FilesDeleted++;
                    break;
                case FileChangeKind.LinkedChange:
                    FilesLinked++;
                    break;
            }
        }
    }
}

using System.Diagnostics;
using Vara.Core.Configuration;
using Vara.Infrastructure.FileSystem;
using Xunit;

namespace Vara.Infrastructure.Tests.FileSystem;

public class DirectoryFileSystemScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");
    private readonly string _outsideTarget = Path.Combine(Path.GetTempPath(), $"vara-test-outside-{Guid.NewGuid():N}");
    private readonly DirectoryFileSystemScanner _scanner = new();

    public DirectoryFileSystemScannerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // The junction point (if created) must be removed before recursively deleting
        // its parent, otherwise Directory.Delete can fail trying to descend through it.
        var junctionPath = Path_("linked-dir");
        if (Directory.Exists(junctionPath))
        {
            Directory.Delete(junctionPath, recursive: false);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_outsideTarget))
        {
            Directory.Delete(_outsideTarget, recursive: true);
        }
    }

    private string Path_(params string[] segments) => Path.Combine([_root, .. segments]);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Scans_all_files_recursively_by_default()
    {
        WriteFile(Path_("a.txt"), "a");
        WriteFile(Path_("sub", "b.txt"), "b");

        var entries = _scanner.Scan([new Source(_root)]).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.RelativePath == "a.txt");
        Assert.Contains(entries, e => e.RelativePath == Path.Combine("sub", "b.txt"));
    }

    [Fact]
    public void Non_recursive_source_only_scans_the_top_level()
    {
        WriteFile(Path_("a.txt"), "a");
        WriteFile(Path_("sub", "b.txt"), "b");

        var entries = _scanner.Scan([new Source(_root, recursive: false)]).ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("a.txt", entry.RelativePath);
    }

    [Fact]
    public void Excluded_subfolder_is_skipped()
    {
        WriteFile(Path_("keep.txt"), "keep");
        WriteFile(Path_("excluded", "skip.txt"), "skip");

        var entries = _scanner.Scan([new Source(_root, excludes: ["excluded"])]).ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("keep.txt", entry.RelativePath);
    }

    [Fact]
    public void A_source_pointing_directly_at_a_single_file_scans_just_that_file()
    {
        var filePath = Path_("standalone.db");
        WriteFile(filePath, "data");

        var entries = _scanner.Scan([new Source(filePath)]).ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("standalone.db", entry.RelativePath);
        Assert.False(entry.IsLink);
    }

    [Fact]
    public void Missing_source_path_yields_no_entries()
    {
        var entries = _scanner.Scan([new Source(Path_("does-not-exist"))]).ToList();

        Assert.Empty(entries);
    }

    [Fact]
    public void A_junction_is_reported_but_not_traversed()
    {
        Directory.CreateDirectory(_outsideTarget);
        WriteFile(Path.Combine(_outsideTarget, "hidden-behind-link.txt"), "should not be traversed");

        var junctionPath = Path_("linked-dir");
        CreateJunction(junctionPath, _outsideTarget);

        var entries = _scanner.Scan([new Source(_root)]).ToList();

        // The junction itself is reported as a link entry ...
        Assert.Contains(entries, e => e.RelativePath == "linked-dir" && e.IsLink);
        // ... but nothing behind it is traversed or reported.
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("hidden-behind-link.txt"));
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

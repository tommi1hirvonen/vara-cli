using System.Diagnostics;
using Vara.Core.Abstractions;
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

        var entries = _scanner.Scan([new Source(_root)]).Entries.ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.RelativePath == "a.txt");
        Assert.Contains(entries, e => e.RelativePath == Path.Combine("sub", "b.txt"));
    }

    [Fact]
    public void Non_recursive_source_only_scans_the_top_level()
    {
        WriteFile(Path_("a.txt"), "a");
        WriteFile(Path_("sub", "b.txt"), "b");

        var entries = _scanner.Scan([new Source(_root, recursive: false)]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("a.txt", entry.RelativePath);
    }

    [Fact]
    public void Excluded_subfolder_is_skipped()
    {
        WriteFile(Path_("keep.txt"), "keep");
        WriteFile(Path_("excluded", "skip.txt"), "skip");

        var entries = _scanner.Scan([new Source(_root, excludes: ["excluded"])]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("keep.txt", entry.RelativePath);
    }

    [Fact]
    public void A_source_pointing_directly_at_a_single_file_scans_just_that_file()
    {
        var filePath = Path_("standalone.db");
        WriteFile(filePath, "data");

        var entries = _scanner.Scan([new Source(filePath)]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("standalone.db", entry.RelativePath);
        Assert.False(entry.IsLink);
    }

    [Fact]
    public void Missing_source_path_yields_no_entries()
    {
        var entries = _scanner.Scan([new Source(Path_("does-not-exist"))]).Entries.ToList();

        Assert.Empty(entries);
    }

    [Fact]
    public void A_junction_is_reported_but_not_traversed()
    {
        Directory.CreateDirectory(_outsideTarget);
        WriteFile(Path.Combine(_outsideTarget, "hidden-behind-link.txt"), "should not be traversed");

        var junctionPath = Path_("linked-dir");
        CreateJunction(junctionPath, _outsideTarget);

        var entries = _scanner.Scan([new Source(_root)]).Entries.ToList();

        // The junction itself is reported as a link entry ...
        Assert.Contains(entries, e => e.RelativePath == "linked-dir" && e.IsLink);
        // ... but nothing behind it is traversed or reported.
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("hidden-behind-link.txt"));
    }

    // NOTE: There is deliberately no ACL-based "permission-denied file" test here.
    // Verified empirically (see design.md's "Verified limitation" note): Windows'
    // File.GetAttributes/FileInfo metadata queries do not enforce a per-file ACL deny
    // for the current user - only Directory.EnumerateFileSystemEntries (tested below)
    // and actually opening file content (already covered by BackupExecutorTests) do.
    // The per-entry catch widening in Walk/ToEntry/TryGetLinkTarget is kept as
    // defensive coding matching BackupExecutor's existing pattern, but isn't
    // reproducible - and therefore isn't asserted - via ACL manipulation in a test.

    [Fact]
    public void A_permission_denied_directory_is_reported_as_a_single_scan_failure_and_siblings_still_scan()
    {
        WriteFile(Path_("keep.txt"), "keep");
        var deniedDir = Path_("denied-dir");
        Directory.CreateDirectory(deniedDir);
        WriteFile(Path.Combine(deniedDir, "hidden.txt"), "hidden");

        DenyCurrentUser(deniedDir, "RD"); // RD = list directory - what enumerating its contents needs.
        try
        {
            var result = _scanner.Scan([new Source(_root)]);
            var entries = result.Entries.ToList();

            // Nothing from inside the denied subtree is returned ...
            Assert.DoesNotContain(entries, e => e.RelativePath.Contains("hidden.txt"));
            // ... but a sibling outside the subtree still scans normally.
            Assert.Contains(entries, e => e.RelativePath == "keep.txt");

            // Exactly one failure for the subtree root, not one per file that would
            // have been inside it.
            var failure = Assert.Single(result.Failures);
            Assert.Equal("denied-dir", failure.RelativePath);
            Assert.Equal(ScanFailureReason.UnreadableDirectory, failure.Reason);
        }
        finally
        {
            RemoveDeny(deniedDir);
        }
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

    /// <summary>Adds an explicit ACL deny entry for the current user on <paramref name="path"/>, denying <paramref name="rights"/> (an icacls simple/specific rights token, e.g. "RA" or "RD"). Explicit deny entries take precedence over any inherited allow, including group-based ones such as Administrators.</summary>
    private static void DenyCurrentUser(string path, string rights) => RunIcacls($"\"{path}\" /deny \"%USERNAME%:({rights})\"");

    /// <summary>Removes the deny entry added by <see cref="DenyCurrentUser"/>, restoring normal access.</summary>
    private static void RemoveDeny(string path) => RunIcacls($"\"{path}\" /remove:d \"%USERNAME%\"");

    private static void RunIcacls(string arguments)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c icacls {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"icacls {arguments} failed with exit code {process.ExitCode}: {error}");
    }
}

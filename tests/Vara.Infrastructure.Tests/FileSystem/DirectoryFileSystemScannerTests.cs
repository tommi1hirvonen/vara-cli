using System.Diagnostics;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.FileSystem;
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

    /// <summary>Expected <see cref="ScannedEntry.RelativePath"/> for an entry at <see cref="Path_"/>, per the mirror-absolute-source-paths change: derived from the entry's full absolute path with the drive letter's colon stripped.</summary>
    private string MirrorPath_(params string[] segments) => AbsolutePathMirrorMapper.ToMirrorPath(Path_(segments));

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
        Assert.Contains(entries, e => e.RelativePath == MirrorPath_("a.txt"));
        Assert.Contains(entries, e => e.RelativePath == MirrorPath_("sub", "b.txt"));
    }

    [Fact]
    public void Non_recursive_source_only_scans_the_top_level()
    {
        WriteFile(Path_("a.txt"), "a");
        WriteFile(Path_("sub", "b.txt"), "b");

        var entries = _scanner.Scan([new Source(_root, recursive: false)]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(MirrorPath_("a.txt"), entry.RelativePath);
    }

    [Fact]
    public void Excluded_subfolder_is_skipped()
    {
        WriteFile(Path_("keep.txt"), "keep");
        WriteFile(Path_("excluded", "skip.txt"), "skip");

        var entries = _scanner.Scan([new Source(_root, excludes: ["excluded"])]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(MirrorPath_("keep.txt"), entry.RelativePath);
    }

    [Fact]
    public void Nested_exclude_written_with_forward_slash_is_skipped()
    {
        WriteFile(Path_("keep.txt"), "keep");
        WriteFile(Path_("sub", "excluded", "skip.txt"), "skip");

        var entries = _scanner.Scan([new Source(_root, excludes: ["sub/excluded"])]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(MirrorPath_("keep.txt"), entry.RelativePath);
    }

    [Fact]
    public void Nested_exclude_written_with_backslash_is_skipped()
    {
        WriteFile(Path_("keep.txt"), "keep");
        WriteFile(Path_("sub", "excluded", "skip.txt"), "skip");

        var entries = _scanner.Scan([new Source(_root, excludes: ["sub\\excluded"])]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(MirrorPath_("keep.txt"), entry.RelativePath);
    }

    [Fact]
    public void A_source_pointing_directly_at_a_single_file_scans_just_that_file()
    {
        var filePath = Path_("standalone.db");
        WriteFile(filePath, "data");

        var entries = _scanner.Scan([new Source(filePath)]).Entries.ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(AbsolutePathMirrorMapper.ToMirrorPath(filePath), entry.RelativePath);
        Assert.False(entry.IsLink);
    }

    [Fact]
    public void A_drive_root_source_is_scanned_from_the_actual_root_not_the_current_directory()
    {
        // Reproduces the bug fixed by using Path.TrimEndingDirectorySeparator instead of a raw
        // TrimEnd('\\', '/'): `subst` maps a drive letter onto _root so the test can control a
        // drive's contents without touching a real system drive. Setting the process's current
        // directory to a decoy subfolder on that drive is exactly the situation Windows resolves
        // a bare "L:" (no trailing separator) against instead of the drive's actual root - which
        // is what a naive TrimEnd produces from a configured source of "L:\". This test fails if
        // that trailing-separator trim ever regresses to producing the bare drive-relative form.
        WriteFile(Path_("marker.txt"), "root");
        WriteFile(Path_("decoy", "decoyfile.txt"), "decoy");

        var driveLetter = FindUnusedDriveLetter();
        Subst(driveLetter, _root);
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Directory.SetCurrentDirectory($"{driveLetter}:\\decoy");

            var entries = _scanner.Scan([new Source($"{driveLetter}:\\")]).Entries.ToList();

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.RelativePath == AbsolutePathMirrorMapper.ToMirrorPath($"{driveLetter}:\\marker.txt"));
            Assert.Contains(entries, e => e.RelativePath == AbsolutePathMirrorMapper.ToMirrorPath($"{driveLetter}:\\decoy\\decoyfile.txt"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            Unsubst(driveLetter);
        }
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
        Assert.Contains(entries, e => e.RelativePath == MirrorPath_("linked-dir") && e.IsLink);
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
            Assert.Contains(entries, e => e.RelativePath == MirrorPath_("keep.txt"));

            // Exactly one failure for the subtree root, not one per file that would
            // have been inside it.
            var failure = Assert.Single(result.Failures);
            Assert.Equal("denied-dir", failure.RelativePath);
            Assert.Equal(ScanFailureReason.UnreadableDirectory, failure.Reason);
            Assert.Equal(MirrorPath_("denied-dir"), failure.MirrorPath);
        }
        finally
        {
            RemoveDeny(deniedDir);
        }
    }

    [Fact]
    public void A_source_path_that_does_not_exist_is_reported_as_a_single_scan_failure_with_no_entries()
    {
        var missingPath = Path_("does-not-exist");

        var result = _scanner.Scan([new Source(missingPath)]);
        var entries = result.Entries.ToList();

        Assert.Empty(entries);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(missingPath, failure.RelativePath);
        Assert.Equal(ScanFailureReason.SourceUnavailable, failure.Reason);
        Assert.Equal(AbsolutePathMirrorMapper.ToMirrorPath(missingPath), failure.MirrorPath);
    }

    [Fact]
    public void A_UNC_source_path_completes_scanning_and_is_reported_with_its_mirror_path_unchanged()
    {
        // AbsolutePathMirrorMapper.ToMirrorPath has no defined mapping for a UNC path yet (see
        // the enforce-mirror-path-containment change's proposal.md) - it passes it through
        // unchanged. This confirms scanning a UNC source completes without throwing or hanging
        // (an unreachable UNC host is treated the same as any other nonexistent source path),
        // and that the failure's recorded MirrorPath is the UNC path itself, unmapped - exactly
        // what would otherwise reach FileSystemContentStore's mirror-write containment guard if
        // this source had instead produced a scanned entry.
        const string uncSourcePath = @"\\vara-test-unreachable-host\share\file.txt";

        var result = _scanner.Scan([new Source(uncSourcePath)]);
        var entries = result.Entries.ToList();

        Assert.Empty(entries);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScanFailureReason.SourceUnavailable, failure.Reason);
        Assert.Equal(uncSourcePath, failure.MirrorPath);
    }

    /// <summary>Finds a drive letter with no filesystem currently mounted on it, for use with <see cref="Subst"/>.</summary>
    private static char FindUnusedDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        throw new InvalidOperationException("No unused drive letter available for the drive-root scan test.");
    }

    /// <summary>Maps <paramref name="driveLetter"/> to <paramref name="targetPath"/> as a virtual drive, via the `subst` command. Remove with <see cref="Unsubst"/>.</summary>
    private static void Subst(char driveLetter, string targetPath) => RunCmd($"subst {driveLetter}: \"{targetPath}\"");

    /// <summary>Removes a virtual drive mapping created by <see cref="Subst"/>.</summary>
    private static void Unsubst(char driveLetter) => RunCmd($"subst {driveLetter}: /d");

    private static void RunCmd(string arguments)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"cmd /c {arguments} failed with exit code {process.ExitCode}: {error}");
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

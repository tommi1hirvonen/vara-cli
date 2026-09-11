using System.Threading;
using Microsoft.Data.Sqlite;
using Spectre.Console.Testing;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class RestoreCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");
    private readonly string _outRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-out-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }

        if (Directory.Exists(_outRoot))
        {
            Directory.Delete(_outRoot, recursive: true);
        }
    }

    // Neither dependency is ever exercised by most tests below: --recursive/--version and
    // --at/--version mutual exclusion (like --out/--in-place's existing check) is validated
    // by RestoreCommand.Create's parsed-args handler before profile resolution or service
    // construction is ever reached, so a real profile/hasher is unnecessary for those.
    private sealed class UnusedProfileConfigLoader : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new NotSupportedException();
        public string DefaultConfigPath => throw new NotSupportedException();
    }

    private sealed class UnusedHasher : IHasher
    {
        public string ComputeHash(Stream content) => throw new NotSupportedException();
    }

    private sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
        public string DefaultConfigPath => "unused";
    }

    private static System.CommandLine.Command CreateCommand() =>
        RestoreCommand.Create(new ProfileResolver(new UnusedProfileConfigLoader()), new ProfileServiceFactory(new UnusedHasher()));

    private System.CommandLine.Command CreateRealCommand(CancellationToken cancellationToken = default, Spectre.Console.IAnsiConsole? console = null)
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        return RestoreCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(_hasher),
            cancellationToken,
            console);
    }

    /// <summary>Stores two files' content in the profile's real content store and records
    /// both as live in a single completed snapshot under "src\", mirroring the shape
    /// <c>PlanDirectoryRestore</c>/<c>ExecuteDirectoryRestore</c> expect.</summary>
    private void SeedTwoTrackedFiles()
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashB, sizeB) = contentStore.StoreFromStream(new MemoryStream("content b"u8.ToArray()));

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, @"src\a.txt", null, hashA, sizeA, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, @"src\b.txt", null, hashB, sizeB, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
    }

    /// <summary>Stores two files' content and records both as live under the mirror-relative
    /// path that <see cref="Vara.Core.FileSystem.AbsolutePathMirrorMapper"/> derives from the
    /// profile's actual <c>_sourceRoot</c> - so a cwd of <c>_sourceRoot</c> resolving `.` through
    /// the absolute-source-path mapping lands exactly on this tracked prefix, unlike
    /// <see cref="SeedTwoTrackedFiles"/>'s arbitrary "src\" prefix (which deliberately does not
    /// correspond to any real source root).</summary>
    private void SeedTwoTrackedFilesUnderSourceRoot()
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashB, sizeB) = contentStore.StoreFromStream(new MemoryStream("content b"u8.ToArray()));

        var mirrorPrefix = Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(_sourceRoot);

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, Path.Combine(mirrorPrefix, "a.txt"), null, hashA, sizeA, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, Path.Combine(mirrorPrefix, "b.txt"), null, hashB, sizeB, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
    }

    [Fact]
    public void Recursive_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--recursive", "--version", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Recursive_without_version_and_without_out_or_in_place_still_requires_a_destination()
    {
        var command = CreateCommand();

        // --recursive alone doesn't bypass the pre-existing --out/--in-place requirement.
        var exitCode = command.Parse(["src", "--recursive"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void At_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--at", "2025-01-15", "--version", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void A_gracefully_cancelled_recursive_restore_returns_exit_code_partial_failure_and_writes_nothing()
    {
        SeedTwoTrackedFiles();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var command = CreateRealCommand(cts.Token);

        // --force skips the confirmation prompt so the pre-cancelled token's effect - stopping
        // ExecuteDirectoryRestore before its first planned write/removal - is exercised
        // directly, mirroring PruneCommandTests'/CheckCommandTests' own pre-cancelled-token tests.
        var exitCode = command.Parse(["src", "--profile", "test-profile", "--recursive", "--out", _outRoot, "--force"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
        Assert.False(File.Exists(Path.Combine(_outRoot, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_outRoot, "b.txt")));
    }

    /// <summary>Covers the snapshot-history delta's "Restore without --recursive given a
    /// tracked directory path" scenario: "src" matches no tracked file (only "src\a.txt" and
    /// "src\b.txt" are tracked files), but is itself a tracked directory prefix, so restoring it
    /// without --recursive must fail with RestoreTargetIsDirectoryException's exit code rather
    /// than treating it as an ordinary unknown path. The exact message mapping is covered by
    /// ErrorReportingTests; this only exercises that RestoreCommand's single-file branch reaches
    /// that exception instead of NoHistoryForPathException or a crash.</summary>
    [Fact]
    public void Restoring_a_tracked_directory_path_without_recursive_fails_with_a_hard_error()
    {
        SeedTwoTrackedFiles();
        var command = CreateRealCommand();

        var exitCode = command.Parse(["src", "--profile", "test-profile", "--in-place", "--at", "2025-01-15"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
    }

    /// <summary>Regression guard for the proposal's Non-Goal: a path that matches neither a
    /// tracked file nor a tracked directory must still fail the same way it always has (still a
    /// hard error - untouched by the new directory-detection branch).</summary>
    [Fact]
    public void Restoring_a_path_with_no_history_at_all_still_fails_with_a_hard_error()
    {
        SeedTwoTrackedFiles();
        var command = CreateRealCommand();

        var exitCode = command.Parse(["never-tracked", "--profile", "test-profile", "--in-place", "--at", "2025-01-15"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
    }

    /// <summary>Covers the snapshot-history delta's "Current directory outside the mirror has
    /// no recorded history" scenario for `restore --recursive .`: run from a cwd inside the
    /// profile's source tree whose source-mapped location has nothing tracked under it (only
    /// the unrelated "src\" prefix is tracked), so the resolver falls back to the mirror root
    /// and the command prints the fallback message before restoring the mirror root's
    /// contents.</summary>
    [Fact]
    public void Recursive_restore_of_dot_from_a_source_directory_with_no_tracked_history_falls_back_to_mirror_root_with_a_message()
    {
        SeedTwoTrackedFiles();
        Directory.CreateDirectory(_sourceRoot);
        var testConsole = new TestConsole();
        var command = CreateRealCommand(console: testConsole);

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_sourceRoot);
            var exitCode = command.Parse([".", "--profile", "test-profile", "--recursive", "--out", _outRoot, "--force"]).Invoke();

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Contains("no recorded history", testConsole.Output);
            // Falling back to the mirror root restores everything tracked, preserving each
            // file's mirror-relative path - here "src\" (SeedTwoTrackedFiles' prefix) - rather
            // than flattening it into _outRoot, unlike restoring a specific subdirectory below.
            Assert.True(File.Exists(Path.Combine(_outRoot, "src", "a.txt")));
            Assert.True(File.Exists(Path.Combine(_outRoot, "src", "b.txt")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    /// <summary>Covers the snapshot-history delta's "Browsing or recursively restoring the
    /// current directory from within a source" scenario for `restore --recursive .`: run from a
    /// cwd inside the profile's source tree that *does* map to tracked history, and confirm the
    /// command restores that source-mapped directory - not the mirror root - with no fallback
    /// message.</summary>
    [Fact]
    public void Recursive_restore_of_dot_from_a_source_directory_with_tracked_history_restores_that_directory()
    {
        SeedTwoTrackedFilesUnderSourceRoot();
        Directory.CreateDirectory(_sourceRoot);
        var testConsole = new TestConsole();
        var command = CreateRealCommand(console: testConsole);

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_sourceRoot);
            var exitCode = command.Parse([".", "--profile", "test-profile", "--recursive", "--out", _outRoot, "--force"]).Invoke();

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.DoesNotContain("no recorded history", testConsole.Output);
            Assert.True(File.Exists(Path.Combine(_outRoot, "a.txt")));
            Assert.True(File.Exists(Path.Combine(_outRoot, "b.txt")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void BuildRestoreTaskLabel_leaves_a_short_path_unchanged()
    {
        var label = RestoreCommand.BuildRestoreTaskLabel(@"src\a.txt", terminalWidth: 120);

        Assert.Equal(@"Restoring 'src\a.txt'", label);
    }

    [Fact]
    public void BuildRestoreTaskLabel_bounds_a_long_path_to_the_computed_budget()
    {
        var longPath = string.Join('\\', Enumerable.Repeat("a-fairly-long-directory-name", 20)) + @"\file.txt";

        var label = RestoreCommand.BuildRestoreTaskLabel(longPath, terminalWidth: 200);

        Assert.True(label.Length <= RestoreProgressLabelBudget.Compute(200));
        Assert.EndsWith("file.txt'", label);
        Assert.Contains("...", label);
    }

    [Fact]
    public void BuildDirectoryRestoreTaskLabel_leaves_a_short_path_unchanged()
    {
        var label = RestoreCommand.BuildDirectoryRestoreTaskLabel(@"src\dir", terminalWidth: 120);

        Assert.Equal(@"Restoring directory 'src\dir'", label);
    }

    [Fact]
    public void BuildDirectoryRestoreTaskLabel_bounds_a_long_path_to_the_computed_budget()
    {
        var longPath = string.Join('\\', Enumerable.Repeat("a-fairly-long-directory-name", 20));

        var label = RestoreCommand.BuildDirectoryRestoreTaskLabel(longPath, terminalWidth: 200);

        Assert.True(label.Length <= RestoreProgressLabelBudget.Compute(200));
        Assert.Contains("...", label);
    }

    [Fact]
    public void SelectableVersionsForInteractivePicker_excludes_deleted_and_linked_versions()
    {
        var t0 = DateTimeOffset.UtcNow;
        var added = new FileVersionRecord(1, 1, "a.txt", null, "hash-1", 5, t0, FileChangeKind.Added, t0);
        var deleted = new FileVersionRecord(2, 2, "a.txt", null, "hash-1", 5, t0.AddMinutes(1), FileChangeKind.Deleted, t0.AddMinutes(1));
        var linked = new FileVersionRecord(3, 3, "a.txt", null, null, 0, t0.AddMinutes(2), FileChangeKind.Linked, t0.AddMinutes(2), LinkTarget: @"C:\target");
        var history = new List<FileVersionRecord> { linked, deleted, added };

        var selectable = RestoreCommand.SelectableVersionsForInteractivePicker(history);

        Assert.Single(selectable);
        Assert.Equal(added, selectable[0]);
    }

    [Fact]
    public void SelectableVersionsForInteractivePicker_is_empty_when_every_version_is_deleted_or_linked()
    {
        var t0 = DateTimeOffset.UtcNow;
        var deleted = new FileVersionRecord(1, 1, "link", null, "hash-1", 5, t0, FileChangeKind.Deleted, t0);
        var linked = new FileVersionRecord(2, 2, "link", null, null, 0, t0.AddMinutes(1), FileChangeKind.Linked, t0.AddMinutes(1), LinkTarget: @"C:\target");
        var history = new List<FileVersionRecord> { linked, deleted };

        var selectable = RestoreCommand.SelectableVersionsForInteractivePicker(history);

        Assert.Empty(selectable);
    }

    [Fact]
    public void Snapshot_combined_with_at_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--recursive", "--snapshot", "5", "--at", "2025-01-15", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Snapshot_without_recursive_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--snapshot", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Snapshot_with_a_valid_id_resolves_and_restores_without_prompting()
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashB, sizeB) = contentStore.StoreFromStream(new MemoryStream("content b"u8.ToArray()));

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        long firstSnapshotId;
        using (var repository = new SqliteSnapshotRepository(dbPath))
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            firstSnapshotId = repository.BeginSnapshot(t0);
            repository.RecordFileVersion(firstSnapshotId, @"src\a.txt", null, hashA, sizeA, t0, FileChangeKind.Added, t0);
            repository.CompleteSnapshot(firstSnapshotId, t0, SnapshotStats.Empty);

            var t1 = t0.AddDays(1);
            var secondSnapshotId = repository.BeginSnapshot(t1);
            repository.RecordFileVersion(secondSnapshotId, @"src\b.txt", null, hashB, sizeB, t1, FileChangeKind.Added, t1);
            repository.CompleteSnapshot(secondSnapshotId, t1, SnapshotStats.Empty);
        }

        var command = CreateRealCommand();

        // --force skips the (unrelated) write/removal confirmation; --snapshot's own
        // resolution never prompts regardless of --force, since it bypasses the picker
        // entirely (task 3.2).
        var exitCode = command.Parse(["src", "--profile", "test-profile", "--recursive", "--snapshot", firstSnapshotId.ToString(), "--out", _outRoot, "--force"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(_outRoot, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_outRoot, "b.txt")));
    }

    [Fact]
    public void Snapshot_naming_an_id_that_never_touched_the_directory_fails_with_a_hard_error()
    {
        SeedTwoTrackedFiles();
        var command = CreateRealCommand();

        var exitCode = command.Parse(["src", "--profile", "test-profile", "--recursive", "--snapshot", "999999", "--out", _outRoot, "--force"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
    }

    /// <summary>
    /// The test process's own console/input is never interactive (xunit redirects both), so
    /// <c>canPromptForVersion</c> is always <c>false</c> here - exercising exactly the branch
    /// the directory snapshot picker must NOT be shown for. If the picker were mistakenly
    /// shown regardless of interactivity, this would hang waiting for input (or throw, for a
    /// non-interactive <see cref="TestConsole"/>) instead of completing and restoring the
    /// directory's current tracked state, per the "Recursive restore without an explicit date"
    /// requirement's non-interactive fallback.
    /// </summary>
    [Fact]
    public void Recursive_restore_without_at_or_snapshot_in_a_non_interactive_session_restores_current_state_without_showing_the_picker()
    {
        SeedTwoTrackedFiles();
        var command = CreateRealCommand();

        var exitCode = command.Parse(["src", "--profile", "test-profile", "--recursive", "--out", _outRoot, "--force"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(_outRoot, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_outRoot, "b.txt")));
    }

    [Fact]
    public void FormatDirectorySnapshotChoice_colors_a_Complete_snapshot_with_the_success_color()
    {
        var snapshot = new Snapshot(7, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SnapshotStatus.Complete, SnapshotStats.Empty);
        var candidate = new DirectorySnapshotCandidate(snapshot, 1, 2, 0, 1, 500, 10, 2048, 1);

        var label = RestoreCommand.FormatDirectorySnapshotChoice(candidate);

        Assert.Contains("#7", label);
        Assert.Contains("[bold palegreen1]Complete[/]", label);
        Assert.Contains("+1/~2/\u21920/-1", label);
        Assert.Contains("+500 B", label);
        Assert.Contains("10 file(s)", label);
        Assert.Contains("1 link(s)", label);
    }

    [Fact]
    public void FormatDirectorySnapshotChoice_colors_a_Cancelled_snapshot_distinctly_from_Complete()
    {
        var snapshot = new Snapshot(8, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SnapshotStatus.Cancelled, SnapshotStats.Empty);
        var candidate = new DirectorySnapshotCandidate(snapshot, 0, 0, 0, 0, 0, 0, 0, 0);

        var label = RestoreCommand.FormatDirectorySnapshotChoice(candidate);

        Assert.Contains("[bold lightgoldenrod2]Cancelled[/]", label);
        Assert.DoesNotContain("palegreen1", label);
    }

    [Fact]
    public void FormatDirectorySnapshotChoice_formats_a_negative_net_bytes_delta_with_a_leading_minus()
    {
        var snapshot = new Snapshot(9, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SnapshotStatus.Complete, SnapshotStats.Empty);
        var candidate = new DirectorySnapshotCandidate(snapshot, 0, 0, 0, 1, -1024, 0, 0, 0);

        var label = RestoreCommand.FormatDirectorySnapshotChoice(candidate);

        Assert.Contains("-1 KB", label);
    }

    [Fact]
    public void FormatDirectorySnapshotChoice_omits_the_links_clause_when_there_are_no_links()
    {
        var snapshot = new Snapshot(10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SnapshotStatus.Complete, SnapshotStats.Empty);
        var candidate = new DirectorySnapshotCandidate(snapshot, 1, 0, 0, 0, 10, 1, 10, 0);

        var label = RestoreCommand.FormatDirectorySnapshotChoice(candidate);

        Assert.DoesNotContain("link(s)", label);
    }
}

using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Testing;
using Vara.Application.Backup;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class BackupCommandTests
{
    private static BackupRunResult MakeResult(IReadOnlyList<string> failedPaths, bool cancelled = false)
    {
        var startedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stats = SnapshotStats.Empty with { FilesFailed = failedPaths.Count };
        return new BackupRunResult(1, startedAt, startedAt.AddSeconds(1), stats, failedPaths, cancelled);
    }

    [Fact]
    public void ResolveOutcome_is_success_when_no_files_failed()
    {
        var result = MakeResult([]);

        Assert.Equal(BackupProgressOutcome.Success, BackupCommand.ResolveOutcome(result));
    }

    [Fact]
    public void ResolveOutcome_is_partial_failure_when_one_or_more_files_failed()
    {
        var result = MakeResult(["some/failed/path.txt"]);

        Assert.Equal(BackupProgressOutcome.PartialFailure, BackupCommand.ResolveOutcome(result));
    }

    [Fact]
    public void ResolveOutcome_is_partial_failure_when_cancelled_even_with_no_failed_paths()
    {
        var result = MakeResult([], cancelled: true);

        Assert.Equal(BackupProgressOutcome.PartialFailure, BackupCommand.ResolveOutcome(result));
    }
}

/// <summary>
/// Exercises <see cref="BackupCommand"/>'s wiring end-to-end (via <c>SetAction</c>), against a
/// real profile-scoped repository/content store (through the real
/// <see cref="ProfileServiceFactory"/>) but a fake <see cref="IFileSystemScanner"/>, whose
/// reported entries/failures are test-controlled - covers this change's "exit code reflects
/// FailedPaths" wiring (tasks.md 2.3/2.4) without needing to reproduce a real locked/denied
/// file on disk.
/// </summary>
public class BackupCommandExitCodeTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private sealed class SingleProfileConfigLoader(Vara.Core.Configuration.Profile profile) : IProfileConfigLoader
    {
        public IReadOnlyList<Vara.Core.Configuration.Profile> LoadProfiles(string configPath) => [profile];
        public string DefaultConfigPath => "unused";
    }

    /// <summary>Fake scanner returning a pre-set, test-controlled list of entries (and,
    /// optionally, scan failures) regardless of the sources argument - copied from the
    /// equivalent fake in <c>Vara.Application.Tests</c>/<c>Vara.IntegrationTests</c> per this
    /// project's convention of duplicating small, drift-risk-free test fakes rather than
    /// referencing another test project.</summary>
    private sealed class FakeFileSystemScanner(IReadOnlyList<ScanFailure>? failures = null, IReadOnlyList<ScannedEntry>? entries = null) : IFileSystemScanner
    {
        public ScanResult Scan(IReadOnlyList<Source> sources) => new(entries ?? [], failures ?? []);
    }

    private System.CommandLine.Command CreateCommand(IFileSystemScanner scanner, CancellationToken cancellationToken = default, TextWriter? jsonOutput = null, IAnsiConsole? console = null)
    {
        var profile = new Vara.Core.Configuration.Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        var hasher = new XxHash128Hasher();
        return BackupCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(hasher),
            scanner,
            hasher,
            cancellationToken,
            jsonOutput,
            console);
    }

    [Fact]
    public void A_run_with_no_failed_paths_returns_exit_code_success()
    {
        var command = CreateCommand(new FakeFileSystemScanner());

        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void A_run_with_one_or_more_failed_paths_returns_exit_code_partial_failure()
    {
        var failure = new ScanFailure(@"D:\missing-drive\app-data", ScanFailureReason.SourceUnavailable, "app-data");
        var command = CreateCommand(new FakeFileSystemScanner([failure]));

        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
    }

    [Fact]
    public void A_hard_error_before_the_run_completes_returns_exit_code_hard_error_not_partial_failure()
    {
        var command = CreateCommand(new FakeFileSystemScanner());

        // Requests a profile name the loader doesn't know about, so ProfileResolver.Resolve
        // throws UnknownProfileException before `result` is ever assigned - guards against the
        // partial-failure override in BackupCommand firing on an unassigned/default result.
        var exitCode = command.Parse(["--profile", "no-such-profile"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
    }

    [Fact]
    public void A_gracefully_cancelled_run_returns_exit_code_partial_failure_not_success()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var command = CreateCommand(new FakeFileSystemScanner(), cts.Token);

        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
    }

    [Fact]
    public void Dry_run_alone_prints_a_plain_text_planned_summary_and_performs_zero_writes()
    {
        Directory.CreateDirectory(_sourceRoot);
        var sourceFile = Path.Combine(_sourceRoot, "a.txt");
        File.WriteAllText(sourceFile, "hello");
        var entry = new ScannedEntry("a.txt", sourceFile, new FileInfo(sourceFile).Length, File.GetLastWriteTimeUtc(sourceFile), false, null);

        // Passed directly via BackupCommand.Create's optional console parameter,
        // rather than swapping the process-wide AnsiConsole.Console static - the
        // latter would race other tests' console output when the suite runs with its
        // default parallelization.
        var testConsole = new TestConsole();
        var command = CreateCommand(new FakeFileSystemScanner(entries: [entry]), console: testConsole);

        var exitCode = command.Parse(["--profile", "test-profile", "--dry-run"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        var output = testConsole.Output;
        Assert.Contains("Dry run", output);
        Assert.Contains("Added:       1", output);
        Assert.DoesNotContain("{", output);

        // Zero writes: no file placed in the mirror (target root), and no snapshot
        // recorded in the manifest.
        Assert.False(File.Exists(Path.Combine(_targetRoot, "a.txt")));
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath, createIfMissing: false);
        Assert.Empty(repository.ListSnapshots());
    }

    [Fact]
    public void Json_alone_prints_a_single_json_object_for_an_executed_run_with_no_progress_output()
    {
        // BackupCommand.Create's optional jsonOutput writer (the same testability seam
        // BackupPipeline's diagnostics writer uses) is passed directly here, rather than
        // swapping the process-wide Console.Out - the latter would race other tests'
        // console output when the suite runs with its default parallelization.
        var writer = new StringWriter();
        var command = CreateCommand(new FakeFileSystemScanner(), jsonOutput: writer);

        var exitCode = command.Parse(["--profile", "test-profile", "--json"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("executed", parsed.RootElement.GetProperty("mode").GetString());
        Assert.Equal("test-profile", parsed.RootElement.GetProperty("profile").GetString());
        Assert.False(parsed.RootElement.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public void Dry_run_and_json_together_prints_a_single_json_object_with_mode_dry_run_and_no_progress_output()
    {
        var writer = new StringWriter();
        var command = CreateCommand(new FakeFileSystemScanner(), jsonOutput: writer);

        var exitCode = command.Parse(["--profile", "test-profile", "--dry-run", "--json"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("dry-run", parsed.RootElement.GetProperty("mode").GetString());
        Assert.False(parsed.RootElement.GetProperty("cancelled").GetBoolean());
    }
}

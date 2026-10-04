using Microsoft.Data.Sqlite;
using System.Text.Json;
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
            foreach (var file in Directory.EnumerateFiles(_targetRoot, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

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

    private sealed class BlockingFileSystemScanner : IFileSystemScanner, IDisposable
    {
        public ManualResetEventSlim ScanStarted { get; } = new();
        public ManualResetEventSlim ContinueScan { get; } = new();

        public ScanResult Scan(IReadOnlyList<Source> sources)
        {
            ScanStarted.Set();
            ContinueScan.Wait();
            return new ScanResult([], []);
        }

        public void Dispose()
        {
            ScanStarted.Dispose();
            ContinueScan.Dispose();
        }
    }

    private System.CommandLine.Command CreateCommand(
        IFileSystemScanner scanner,
        CancellationToken cancellationToken = default,
        TextWriter? jsonOutput = null,
        IAnsiConsole? console = null,
        IBackupPowerRequestFactory? powerRequestFactory = null,
        IAnsiConsole? errorConsole = null)
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
            console,
            powerRequestFactory,
            errorConsole);
    }

    [Fact]
    public void A_run_with_no_failed_paths_returns_exit_code_success()
    {
        var command = CreateCommand(new FakeFileSystemScanner());

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void A_run_with_one_or_more_failed_paths_returns_exit_code_partial_failure()
    {
        var failure = new ScanFailure(@"D:\missing-drive\app-data", ScanFailureReason.SourceUnavailable, "app-data");
        var command = CreateCommand(new FakeFileSystemScanner([failure]));

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
    }

    [Fact]
    public void A_hard_error_before_the_run_completes_returns_exit_code_hard_error_not_partial_failure()
    {
        var command = CreateCommand(new FakeFileSystemScanner());

        // Requests a profile name the loader doesn't know about, so ProfileResolver.Resolve
        // throws UnknownProfileException before `result` is ever assigned - guards against the
        // partial-failure override in BackupCommand firing on an unassigned/default result.
        var exitCode = command.Parse(["no-such-profile"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
    }

    [Fact]
    public void A_gracefully_cancelled_run_returns_exit_code_partial_failure_not_success()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var powerRequest = new FakeBackupPowerRequestFactory();
        var command = CreateCommand(new FakeFileSystemScanner(), cts.Token, powerRequestFactory: powerRequest);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
        Assert.Equal(1, powerRequest.AcquireCount);
        Assert.Equal(1, powerRequest.DisposeCount);
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

        var exitCode = command.Parse(["test-profile", "--dry-run"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        var output = testConsole.Output;
        Assert.Contains("Dry run", output);
        Assert.Contains("Added:       1", output);
        Assert.DoesNotContain("{", output);
        Assert.DoesNotContain("Scanning files", output);

        // Zero writes: no file placed in the mirror (target root), and no snapshot
        // recorded in the manifest.
        Assert.False(File.Exists(Path.Combine(_targetRoot, "a.txt")));
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath, createIfMissing: false);
        Assert.Empty(repository.ListSnapshots());
    }

    [Fact]
    public async Task Dry_run_shows_indeterminate_progress_while_planning_in_a_live_terminal()
    {
        using var scanner = new BlockingFileSystemScanner();
        var console = new TestConsole();
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);
        var command = CreateCommand(scanner, console: console);
        var invocation = Task.Run(() => command.Parse(["test-profile", "--dry-run"]).Invoke());
        int exitCode;

        try
        {
            Assert.True(await Task.Run(() => scanner.ScanStarted.Wait(TimeSpan.FromSeconds(5))));
            await Task.Delay(250);
        }
        finally
        {
            scanner.ContinueScan.Set();
            exitCode = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(ExitCodes.Success, exitCode);
        var output = console.Output;
        Assert.True(output.IndexOf("Scanning files...", StringComparison.Ordinal) < output.IndexOf("Dry run", StringComparison.Ordinal));
        Assert.Contains("Added:", output);
    }

    [Fact]
    public void Json_alone_prints_a_single_json_object_for_an_executed_run_with_no_progress_output()
    {
        // BackupCommand.Create's optional jsonOutput writer (the same testability seam
        // BackupPipeline's diagnostics writer uses) is passed directly here, rather than
        // swapping the process-wide Console.Out - the latter would race other tests'
        // console output when the suite runs with its default parallelization.
        var writer = new StringWriter();
        var powerRequest = new FakeBackupPowerRequestFactory();
        var command = CreateCommand(new FakeFileSystemScanner(), jsonOutput: writer, powerRequestFactory: powerRequest);

        var exitCode = command.Parse(["test-profile", "--json"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("executed", parsed.RootElement.GetProperty("mode").GetString());
        Assert.Equal("test-profile", parsed.RootElement.GetProperty("profile").GetString());
        Assert.False(parsed.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Equal(1, powerRequest.AcquireCount);
        Assert.Equal(1, powerRequest.DisposeCount);
    }

    [Fact]
    public void Non_interactive_progress_includes_transferred_file_count_and_existing_statistics()
    {
        Directory.CreateDirectory(_sourceRoot);
        var sourceFile = Path.Combine(_sourceRoot, "a.txt");
        File.WriteAllText(sourceFile, "hello");
        var entry = new ScannedEntry("a.txt", sourceFile, new FileInfo(sourceFile).Length, File.GetLastWriteTimeUtc(sourceFile), false, null);
        var console = new TestConsole();
        var command = CreateCommand(new FakeFileSystemScanner(entries: [entry]), console: console);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Files 1 / 1", console.Output);
        Assert.Contains("%", console.Output);
        Assert.Contains("/s", console.Output);
        Assert.Contains("ETA", console.Output);
    }

    [Fact]
    public void Interactive_progress_acquires_and_releases_a_power_request()
    {
        var console = new TestConsole();
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);
        var powerRequest = new FakeBackupPowerRequestFactory();
        var command = CreateCommand(
            new FakeFileSystemScanner(),
            console: console,
            powerRequestFactory: powerRequest);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, powerRequest.AcquireCount);
        Assert.Equal(1, powerRequest.DisposeCount);
    }

    [Fact]
    public void Dry_run_does_not_acquire_or_release_a_power_request()
    {
        var powerRequest = new FakeBackupPowerRequestFactory();
        var command = CreateCommand(new FakeFileSystemScanner(), powerRequestFactory: powerRequest);

        var exitCode = command.Parse(["test-profile", "--dry-run"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, powerRequest.AcquireCount);
        Assert.Equal(0, powerRequest.DisposeCount);
    }

    [Fact]
    public void Power_request_activation_failure_warns_and_continues_with_json_only_on_stdout()
    {
        var writer = new StringWriter();
        var errorConsole = new TestConsole();
        var powerRequest = new FakeBackupPowerRequestFactory
        {
            AcquireFailure = new PowerRequestException("request unavailable"),
        };
        var command = CreateCommand(
            new FakeFileSystemScanner(),
            jsonOutput: writer,
            powerRequestFactory: powerRequest,
            errorConsole: errorConsole);

        var exitCode = command.Parse(["test-profile", "--json"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("executed", parsed.RootElement.GetProperty("mode").GetString());
        Assert.Contains("Warning", errorConsole.Output);
        Assert.Contains("request unavailable", errorConsole.Output);
        Assert.Equal(1, powerRequest.AcquireCount);
        Assert.Equal(0, powerRequest.DisposeCount);
    }

    [Fact]
    public void Power_request_release_failure_warns_without_changing_backup_outcome()
    {
        var errorConsole = new TestConsole();
        var powerRequest = new FakeBackupPowerRequestFactory
        {
            DisposeFailure = new PowerRequestException("clear request failed"),
        };
        var command = CreateCommand(
            new FakeFileSystemScanner(),
            powerRequestFactory: powerRequest,
            errorConsole: errorConsole);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("could not be released", errorConsole.Output);
        Assert.Contains("clear request failed", errorConsole.Output);
        Assert.Equal(1, powerRequest.DisposeCount);
    }

    [Fact]
    public void Power_request_is_released_when_the_backup_pipeline_throws()
    {
        var powerRequest = new FakeBackupPowerRequestFactory();
        var command = CreateCommand(new ThrowingFileSystemScanner(), powerRequestFactory: powerRequest);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
        Assert.Equal(1, powerRequest.AcquireCount);
        Assert.Equal(1, powerRequest.DisposeCount);
    }

    [Fact]
    public void Dry_run_and_json_together_prints_a_single_json_object_with_mode_dry_run_and_no_progress_output()
    {
        var writer = new StringWriter();
        var console = new TestConsole();
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);
        var command = CreateCommand(new FakeFileSystemScanner(), jsonOutput: writer, console: console);

        var exitCode = command.Parse(["test-profile", "--dry-run", "--json"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(console.Output);
        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("dry-run", parsed.RootElement.GetProperty("mode").GetString());
        Assert.False(parsed.RootElement.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public void Missing_profile_argument_fails()
    {
        var command = CreateCommand(new FakeFileSystemScanner());
        var parseResult = command.Parse([]);
        Assert.NotEmpty(parseResult.Errors);
        var exitCode = parseResult.Invoke();
        Assert.NotEqual(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void Passing_profile_option_fails_as_unrecognized_option()
    {
        var command = CreateCommand(new FakeFileSystemScanner());
        var parseResult = command.Parse(["--profile", "test-profile"]);
        Assert.NotEmpty(parseResult.Errors);
        var exitCode = parseResult.Invoke();
        Assert.NotEqual(ExitCodes.Success, exitCode);
    }

    private sealed class FakeBackupPowerRequestFactory : IBackupPowerRequestFactory
    {
        public int AcquireCount { get; private set; }
        public int DisposeCount { get; private set; }
        public PowerRequestException? AcquireFailure { get; init; }
        public PowerRequestException? DisposeFailure { get; init; }

        public IDisposable Acquire()
        {
            AcquireCount++;
            if (AcquireFailure is not null)
            {
                throw AcquireFailure;
            }

            return new FakePowerRequest(() =>
            {
                DisposeCount++;
                if (DisposeFailure is not null)
                {
                    throw DisposeFailure;
                }
            });
        }

        private sealed class FakePowerRequest(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class ThrowingFileSystemScanner : IFileSystemScanner
    {
        public ScanResult Scan(IReadOnlyList<Source> sources) => throw new InvalidOperationException("scan failed");
    }

    private sealed class FakeTerminalOutput(TextWriter writer) : IAnsiConsoleOutput
    {
        public TextWriter Writer { get; } = writer;
        public bool IsTerminal => true;
        public int Width => 120;
        public int Height => 30;

        public void SetEncoding(System.Text.Encoding encoding)
        {
        }
    }
}

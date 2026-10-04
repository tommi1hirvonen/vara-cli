using Microsoft.Data.Sqlite;
using Spectre.Console.Testing;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using IAnsiConsole = Spectre.Console.IAnsiConsole;
using IAnsiConsoleOutput = Spectre.Console.IAnsiConsoleOutput;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Exercises <see cref="CheckCommand"/>'s wiring against a real profile-scoped
/// repository and content store (via the real <see cref="ProfileServiceFactory"/>),
/// mirroring <c>PruneCommandTests</c>'s style.
/// </summary>
public class CheckCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
        public string DefaultConfigPath => "unused";
    }

    private System.CommandLine.Command CreateCommand(CancellationToken cancellationToken = default, IAnsiConsole? console = null)
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        return CheckCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(_hasher),
            _hasher,
            cancellationToken,
            console);
    }

    /// <summary>Stores <paramref name="content"/> in the profile's real content store
    /// and records it as referenced by <paramref name="relativePath"/> in a completed
    /// snapshot, returning the content's hash.</summary>
    private string SeedReferencedBlob(string relativePath, string content)
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hash, size) = contentStore.StoreFromStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, relativePath, null, hash, size, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        return hash;
    }

    /// <summary>References a hash in the manifest without ever storing its content -
    /// simulates a blob lost from the target.</summary>
    private void SeedMissingReference(string relativePath, string hash)
    {
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, relativePath, null, hash, 10, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
    }

    /// <summary>Overwrites an already-stored blob's on-disk bytes directly, without
    /// touching its filename (the hash) - simulates target-side bit rot/corruption.</summary>
    private void CorruptBlobOnDisk(string hash, string corruptedContent)
    {
        var blobPath = Path.Combine(_targetRoot, ".vara", "versions", hash[..2], hash);
        File.WriteAllText(blobPath, corruptedContent);
    }

    [Fact]
    public void No_problems_found_exits_successfully()
    {
        SeedReferencedBlob("a.txt", "hello world");
        var command = CreateCommand();

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Redirected_progress_includes_percentage_blob_count_and_eta()
    {
        SeedReferencedBlob("a.txt", "hello world");
        var console = new TestConsole();
        var command = CreateCommand(console: console);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains("100.0%", console.Output);
        Assert.Contains("1 of 1 blobs", console.Output);
        Assert.Contains("ETA 0s", console.Output);
    }

    [Fact]
    public void Live_progress_includes_percentage_blob_count_and_eta()
    {
        SeedReferencedBlob("a.txt", "hello world");
        var console = CreateLiveConsole();
        var command = CreateCommand(console: console);

        var exitCode = command.Parse(["test-profile", "--quick"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains("100", console.Output);
        Assert.Contains("Checking blobs 1 / 1 - ETA 0s", console.Output);
    }

    [Fact]
    public void Live_full_check_reports_weighted_progress()
    {
        SeedReferencedBlob("a.txt", "hello world");
        var console = CreateLiveConsole();
        var command = CreateCommand(console: console);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains("100", console.Output);
        Assert.Contains("Checking blobs 1 / 1 - ETA 0s", console.Output);
    }

    [Fact]
    public void Live_progress_completes_for_missing_manifest()
    {
        SeedMissingReference("missing.txt", "hash-missing");
        var missingConsole = CreateLiveConsole();
        var missingCommand = CreateCommand(console: missingConsole);

        var missingExitCode = missingCommand.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, missingExitCode);
        Assert.Contains("Checking blobs 1 / 1 - ETA 0s", missingConsole.Output);
    }

    [Fact]
    public void Live_progress_completes_for_empty_manifest()
    {
        var emptyConsole = CreateLiveConsole();
        var emptyCommand = CreateCommand(console: emptyConsole);

        var emptyExitCode = emptyCommand.Parse(["test-profile"]).Invoke();

        Assert.Equal(0, emptyExitCode);
        Assert.Contains("Checking blobs 0 / 0 - ETA 0s", emptyConsole.Output);
        Assert.Contains("100", emptyConsole.Output);
    }

    [Fact]
    public void A_missing_blob_causes_a_partial_failure_exit_code()
    {
        SeedMissingReference("a.txt", "hash-never-stored");
        var command = CreateCommand();

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Quick_option_is_passed_through_and_skips_detecting_a_corrupted_blob()
    {
        var hash = SeedReferencedBlob("a.txt", "original content");
        CorruptBlobOnDisk(hash, "corrupted content");
        var command = CreateCommand();

        // Quick mode never re-hashes, so the corrupted-but-present blob is not detected.
        var quickExitCode = command.Parse(["test-profile", "--quick"]).Invoke();
        Assert.Equal(0, quickExitCode);

        // The same corrupted blob, checked in full (default) mode, is detected and
        // fails the run - proving --quick genuinely changed the outcome above.
        var fullExitCode = command.Parse(["test-profile"]).Invoke();
        Assert.Equal(2, fullExitCode);
    }

    [Fact]
    public void A_gracefully_cancelled_run_returns_exit_code_partial_failure_even_with_no_problems_found()
    {
        SeedReferencedBlob("a.txt", "hello world");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var command = CreateCommand(cts.Token);

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
    }

    [Fact]
    public void Missing_profile_argument_fails()
    {
        var command = CreateCommand();
        var parseResult = command.Parse([]);
        Assert.NotEmpty(parseResult.Errors);
        var exitCode = parseResult.Invoke();
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Passing_profile_option_fails_as_unrecognized_option()
    {
        var command = CreateCommand();
        var parseResult = command.Parse(["--profile", "test-profile"]);
        Assert.NotEmpty(parseResult.Errors);
        var exitCode = parseResult.Invoke();
        Assert.NotEqual(0, exitCode);
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

    private static TestConsole CreateLiveConsole()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 120;
        return console;
    }
}

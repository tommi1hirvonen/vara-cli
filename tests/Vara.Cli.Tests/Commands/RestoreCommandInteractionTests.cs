using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Option validation messages and the interactive paths of <see cref="RestoreCommand"/> (version
/// picker, overwrite confirmation, directory-restore confirmation and snapshot picker), driven
/// through the command's injectable consoles and input-redirection flag.
/// </summary>
public class RestoreCommandInteractionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly ProfileFixture _fixture = new();
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), $"vara-clitest-out-{Guid.NewGuid():N}");
    private readonly TestConsole _prompts = new();

    public RestoreCommandInteractionTests()
    {
        _prompts.Profile.Capabilities.Interactive = true;
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_outDir))
        {
            Directory.Delete(_outDir, recursive: true);
        }
    }

    private int Run(TestConsole console, bool inputRedirected, params string[] args) =>
        RestoreCommand.Create(
                _fixture.Resolver,
                _fixture.Services,
                console: console,
                errorConsole: _prompts,
                isInputRedirected: () => inputRedirected)
            .Parse([.. args, "--profile", "test-profile"]).Invoke();

    private int RunValidationOnly(params string[] args) =>
        RestoreCommand.Create(
                new Vara.Application.Profiles.ProfileResolver(new UnusedProfileConfigLoader()),
                new Vara.Cli.Composition.ProfileServiceFactory(new UnusedHasher()),
                errorConsole: _prompts,
                isInputRedirected: () => true)
            .Parse(args).Invoke();

    private string OutFile(string name = "restored.txt") => Path.Combine(_outDir, name);

    // ---- option validation (rejected before any profile resolution) ----

    [Theory]
    [InlineData(new[] { "src", "--out", "x", "--in-place", "--at", "2025-01-01" }, "--out and --in-place are mutually exclusive.")]
    [InlineData(new[] { "src", "--at", "2025-01-01" }, "specify either --out <path> or --in-place.")]
    [InlineData(new[] { "src", "--out", "x", "--recursive", "--version", "5" }, "--recursive and --version are mutually exclusive.")]
    [InlineData(new[] { "src", "--out", "x", "--at", "2025-01-15", "--version", "5" }, "--at and --version are mutually exclusive.")]
    [InlineData(new[] { "src", "--out", "x", "--recursive", "--snapshot", "3", "--at", "2025-01-15" }, "--snapshot and --at are mutually exclusive.")]
    [InlineData(new[] { "src", "--out", "x", "--snapshot", "3" }, "--snapshot only applies to a recursive directory restore (--recursive).")]
    [InlineData(new[] { "src", "--out", "x" }, "specify either --at <date> or --version <id>.")]
    public void Invalid_option_combinations_are_rejected_with_a_specific_message(string[] args, string expectedMessage)
    {
        var exitCode = RunValidationOnly(args);

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedMessage, _prompts.Output);
    }

    // ---- single-file restore ----

    [Fact]
    public void The_interactive_picker_restores_the_version_chosen_by_the_user()
    {
        _fixture.Record("a.txt", "first", FileChangeKind.Added, T0);
        _fixture.Record("a.txt", "second", FileChangeKind.Changed, T0.AddDays(1));
        _prompts.Input.PushKey(ConsoleKey.DownArrow);
        _prompts.Input.PushKey(ConsoleKey.Enter);
        var console = TestConsoles.CreateLive();

        var exitCode = Run(console, inputRedirected: false, "a.txt", "--out", OutFile());

        Assert.Equal(0, exitCode);
        Assert.Contains("Select a version to restore", _prompts.Output);
        Assert.Equal("first", File.ReadAllText(OutFile()));
        Assert.Contains("Restored 'a.txt'", console.Output);
    }

    [Fact]
    public void An_existing_destination_is_overwritten_when_the_user_confirms()
    {
        var version = _fixture.Record("a.txt", "fresh", FileChangeKind.Added, T0);
        File.WriteAllText(OutFile(), "stale");
        _prompts.Input.PushTextWithEnter("y");

        var exitCode = Run(new TestConsole(), inputRedirected: false, "a.txt", "--out", OutFile(), "--version", version.ToString());

        Assert.Equal(0, exitCode);
        Assert.Contains("already exists. Overwrite?", _prompts.Output);
        Assert.Equal("fresh", File.ReadAllText(OutFile()));
    }

    [Fact]
    public void An_existing_destination_is_kept_when_the_user_declines()
    {
        var version = _fixture.Record("a.txt", "fresh", FileChangeKind.Added, T0);
        File.WriteAllText(OutFile(), "stale");
        _prompts.Input.PushTextWithEnter("n");
        var console = new TestConsole();

        var exitCode = Run(console, inputRedirected: false, "a.txt", "--out", OutFile(), "--version", version.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("stale", File.ReadAllText(OutFile()));
        Assert.Contains("Restore cancelled: destination not overwritten.", console.Output);
    }

    [Fact]
    public void An_existing_destination_without_force_fails_when_input_is_redirected()
    {
        var version = _fixture.Record("a.txt", "fresh", FileChangeKind.Added, T0);
        File.WriteAllText(OutFile(), "stale");

        var exitCode = Run(new TestConsole(), inputRedirected: true, "a.txt", "--out", OutFile(), "--version", version.ToString());

        Assert.Equal(1, exitCode);
        Assert.Equal("stale", File.ReadAllText(OutFile()));
        Assert.Contains("--force", _prompts.Output);
    }

    [Fact]
    public void Force_overwrites_an_existing_destination_without_prompting()
    {
        var version = _fixture.Record("a.txt", "fresh", FileChangeKind.Added, T0);
        File.WriteAllText(OutFile(), "stale");

        var exitCode = Run(
            new TestConsole(), inputRedirected: true, "a.txt", "--out", OutFile(), "--version", version.ToString(), "--force");

        Assert.Equal(0, exitCode);
        Assert.Equal("fresh", File.ReadAllText(OutFile()));
        Assert.Equal(string.Empty, _prompts.Output);
    }

    [Fact]
    public void A_tracked_directory_without_recursive_is_pointed_at_recursive()
    {
        _fixture.Record(@"src\a.txt", "x", FileChangeKind.Added, T0);

        var exitCode = Run(new TestConsole(), inputRedirected: true, "src", "--out", OutFile(), "--at", "2025-02-01");

        Assert.Equal(1, exitCode);
        Assert.Contains("--recursive", _prompts.Output);
    }

    // ---- recursive (directory) restore ----

    private void SeedDirectory()
    {
        _fixture.Record(@"src\a.txt", "content a", FileChangeKind.Added, T0);
        _fixture.Record(@"src\b.txt", "content b", FileChangeKind.Added, T0.AddDays(1));
    }

    private string[] RestoredFiles() =>
        Directory.GetFiles(_outDir, "*", SearchOption.AllDirectories);

    [Fact]
    public void Recursive_restore_writes_the_directory_after_the_user_confirms()
    {
        SeedDirectory();
        _prompts.Input.PushTextWithEnter("y");
        var console = new TestConsole();

        var exitCode = Run(
            console, inputRedirected: false, "src", "--recursive", "--out", _outDir, "--at", "2025-02-01");

        Assert.Equal(0, exitCode);
        Assert.Contains("This will write 2 file(s)", _prompts.Output);
        Assert.Equal(2, RestoredFiles().Length);
        Assert.Contains("Restored directory 'src': 2 file(s) written", console.Output);
    }

    [Fact]
    public void Recursive_restore_writes_nothing_when_the_user_declines()
    {
        SeedDirectory();
        _prompts.Input.PushTextWithEnter("n");
        var console = new TestConsole();

        var exitCode = Run(
            console, inputRedirected: false, "src", "--recursive", "--out", _outDir, "--at", "2025-02-01");

        Assert.Equal(0, exitCode);
        Assert.Empty(RestoredFiles());
        Assert.Contains("Restore cancelled", console.Output);
    }

    [Fact]
    public void Recursive_restore_without_force_fails_when_input_is_redirected()
    {
        SeedDirectory();

        var exitCode = Run(
            new TestConsole(), inputRedirected: true, "src", "--recursive", "--out", _outDir, "--at", "2025-02-01");

        Assert.Equal(1, exitCode);
        Assert.Empty(RestoredFiles());
        Assert.Contains("--force", _prompts.Output);
    }

    [Fact]
    public void Recursive_restore_with_force_runs_through_the_live_display()
    {
        SeedDirectory();

        var exitCode = Run(
            TestConsoles.CreateLive(), inputRedirected: true, "src", "--recursive", "--out", _outDir, "--force");

        Assert.Equal(0, exitCode);
        Assert.Equal(2, RestoredFiles().Length);
    }

    [Fact]
    public void Recursive_restore_snapshot_option_restores_that_snapshots_state()
    {
        SeedDirectory();
        var firstSnapshot = _fixture.SnapshotIds().Min();

        var exitCode = Run(
            new TestConsole(), inputRedirected: true,
            "src", "--recursive", "--out", _outDir, "--force", "--snapshot", firstSnapshot.ToString());

        Assert.Equal(0, exitCode);
        var restored = Assert.Single(RestoredFiles());
        Assert.Equal("content a", File.ReadAllText(restored));
    }

    [Fact]
    public void Recursive_restore_interactive_picker_defaults_to_the_current_tracked_state()
    {
        SeedDirectory();
        _prompts.Input.PushKey(ConsoleKey.Enter);
        _prompts.Input.PushTextWithEnter("y");

        var exitCode = Run(TestConsoles.CreateLive(), inputRedirected: false, "src", "--recursive", "--out", _outDir);

        Assert.Equal(0, exitCode);
        Assert.Contains("Select a point in time to restore", _prompts.Output);
        Assert.Equal(2, RestoredFiles().Length);
    }

    [Fact]
    public void A_pre_cancelled_recursive_restore_reports_partial_failure()
    {
        SeedDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var console = new TestConsole();

        var exitCode = RestoreCommand.Create(
                _fixture.Resolver,
                _fixture.Services,
                cts.Token,
                console,
                _prompts,
                () => true)
            .Parse(["src", "--recursive", "--out", _outDir, "--force", "--profile", "test-profile"]).Invoke();

        Assert.Equal(2, exitCode);
        Assert.Contains("cancelled: stopped early", console.Output);
    }
}

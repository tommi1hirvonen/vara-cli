using System.CommandLine;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Covers what can be exercised from an automated, non-interactive test host: the
/// `--help`/option wiring, and the "requires an interactive terminal" refusal (this test
/// host's standard input/output are always redirected, as in CI - the same reason
/// <c>ShowCommandTests</c> can't drive its own interactive-only branch). The rest of the
/// interactive menu/edit-screen/source-editor flow (tasks 4.3-7.2) is verified manually per
/// this change's tasks.md, since it is built entirely out of Spectre.Console prompts that
/// read raw terminal input a redirected test host cannot supply.
/// </summary>
public class ProfilesCommandTests
{
    private sealed class EmptyProfileConfigLoader : IProfileConfigLoader
    {
        public string DefaultConfigPath => @"C:\fake\.vara\profiles.yml";
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => [];
    }

    private sealed class RecordingProfileConfigWriter : IProfileConfigWriter
    {
        public bool WasCalled { get; private set; }

        public void WriteProfiles(IReadOnlyList<Profile> profiles, string configPath) => WasCalled = true;
    }

    private sealed class ThrowingProfileConfigWriter : IProfileConfigWriter
    {
        public void WriteProfiles(IReadOnlyList<Profile> profiles, string configPath) =>
            throw new ProfileConfigWriteFailedException(configPath, "disk is full", new IOException("disk is full"));
    }

    private static Command CreateCommand(out RecordingProfileConfigWriter writer)
    {
        writer = new RecordingProfileConfigWriter();
        return ProfilesCommand.Create(new EmptyProfileConfigLoader(), writer);
    }

    [Fact]
    public void Help_shows_the_config_option()
    {
        var command = CreateCommand(out _);

        var configOption = Assert.Single(command.Options, o => o.Name == "--config");

        Assert.Contains("profiles configuration file", configOption.Description);
    }

    [Fact]
    public void Running_with_redirected_input_and_output_reports_an_interactive_terminal_error_and_makes_no_change()
    {
        var command = CreateCommand(out var writer);
        var configPath = Path.Combine(Path.GetTempPath(), $"vara-profiles-cli-test-{Guid.NewGuid():N}", "profiles.yml");

        // This test host's standard input/output are redirected (as in CI), so
        // ProfilesCommand's non-interactive check always trips here, regardless of the
        // real terminal the developer running these tests happens to be using.
        var exitCode = command.Parse(["--config", configPath]).Invoke();

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(configPath));
        Assert.False(writer.WasCalled);
    }

    [Fact]
    public void LoadProfilesOrEmpty_treats_a_missing_configuration_file_as_zero_profiles()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"vara-profiles-cli-test-{Guid.NewGuid():N}", "profiles.yml");

        var profiles = ProfilesCommand.LoadProfilesOrEmpty(new EmptyProfileConfigLoader(), missingPath);

        Assert.Empty(profiles);
    }

    [Fact]
    public void LoadProfilesOrEmpty_loads_through_the_loader_when_the_file_exists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vara-profiles-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "profiles.yml");
        File.WriteAllText(configPath, "profiles: []");
        try
        {
            var loader = new StubLoaderWithOneProfile();

            var profiles = ProfilesCommand.LoadProfilesOrEmpty(loader, configPath);

            Assert.Single(profiles);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubLoaderWithOneProfile : IProfileConfigLoader
    {
        public string DefaultConfigPath => @"C:\fake\.vara\profiles.yml";

        public IReadOnlyList<Profile> LoadProfiles(string configPath) =>
            [new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null)];
    }

    [Fact]
    public void ApplySave_adds_a_new_profile_when_OriginalName_is_null()
    {
        var writer = new RecordingProfileConfigWriter();
        var profiles = new List<Profile>();
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";
        draft.Sources.Add(new SourceDraft { Path = @"C:\data" });
        Assert.True(draft.Revalidate(profiles));

        var saved = ProfilesCommand.ApplySave(draft, draft.CurrentValidProfile!, profiles, writer, "unused-path");

        Assert.Equal("files", saved.Name);
        Assert.Single(profiles);
        Assert.True(writer.WasCalled);
    }

    [Fact]
    public void ApplySave_replaces_the_entry_found_by_OriginalName_not_the_drafts_current_name()
    {
        var writer = new RecordingProfileConfigWriter();
        var original = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);
        var profiles = new List<Profile> { original };
        var draft = ProfileDraft.FromProfile(original);
        draft.Name = "documents"; // renamed
        Assert.True(draft.Revalidate(profiles));

        ProfilesCommand.ApplySave(draft, draft.CurrentValidProfile!, profiles, writer, "unused-path");

        var remaining = Assert.Single(profiles);
        Assert.Equal("documents", remaining.Name);
    }

    [Fact]
    public void ApplyDelete_removes_only_the_matching_profile_and_writes_the_remaining_list()
    {
        var writer = new RecordingProfileConfigWriter();

        // Use a unique, never-created temp path (rather than a fixed literal like
        // @"D:\backup") for the profile being deleted, so the "never touches disk" check
        // below can't coincidentally pass/fail based on what a real drive on the machine
        // running this test happens to contain.
        var toDeleteTarget = Path.Combine(Path.GetTempPath(), $"vara-profiles-cli-test-{Guid.NewGuid():N}");
        var toDelete = new Profile("files", toDeleteTarget, [new Source(@"C:\data")], retention: null);
        var toKeep = new Profile("photos", @"D:\photos", [new Source(@"C:\photos")], retention: null);
        var profiles = new List<Profile> { toDelete, toKeep };

        ProfilesCommand.ApplyDelete(profiles, toDelete, writer, "unused-path");

        var remaining = Assert.Single(profiles);
        Assert.Equal("photos", remaining.Name);
        Assert.True(writer.WasCalled);

        // Deleting only edits the in-memory list / configuration file - it never touches
        // anything under the deleted profile's target root on disk.
        Assert.False(Directory.Exists(toDelete.TargetRoot));
    }

    [Fact]
    public void TrySave_persists_the_profile_produced_by_its_own_revalidation_when_the_draft_is_valid()
    {
        var writer = new RecordingProfileConfigWriter();
        var profiles = new List<Profile>();
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";
        draft.Sources.Add(new SourceDraft { Path = @"C:\data" });

        var succeeded = ProfilesCommand.TrySave(draft, profiles, writer, "unused-path", out var saved);

        Assert.True(succeeded);
        Assert.NotNull(saved);
        Assert.Equal("files", saved!.Name);
        Assert.Single(profiles);
        Assert.True(writer.WasCalled);
    }

    [Fact]
    public void TrySave_refuses_and_writes_nothing_when_a_draft_that_was_previously_valid_has_since_been_mutated_into_an_invalid_state()
    {
        // Reproduces the reported bug: a valid draft that has already been revalidated
        // (so it has a stale CurrentValidProfile/CurrentError = null), then mutated by a
        // path that does not itself call Revalidate - such as "Add source" appending an
        // empty SourceDraft. Save must refuse based on the draft's *current* state, not
        // the stale cached snapshot from before the mutation.
        var writer = new RecordingProfileConfigWriter();
        var profiles = new List<Profile>();
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";
        draft.Sources.Add(new SourceDraft { Path = @"C:\data" });
        Assert.True(draft.Revalidate(profiles));

        // Simulates "Add source" appending an empty SourceDraft without revalidating.
        draft.Sources.Add(new SourceDraft());

        var succeeded = ProfilesCommand.TrySave(draft, profiles, writer, "unused-path", out var saved);

        Assert.False(succeeded);
        Assert.Null(saved);
        Assert.NotNull(draft.CurrentError);
        Assert.Empty(profiles);
        Assert.False(writer.WasCalled);
    }

    [Fact]
    public void ApplySave_leaves_the_in_memory_list_unchanged_when_the_writer_throws()
    {
        var writer = new ThrowingProfileConfigWriter();
        var existing = new Profile("photos", @"D:\photos", [new Source(@"C:\photos")], retention: null);
        var profiles = new List<Profile> { existing };
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";
        draft.Sources.Add(new SourceDraft { Path = @"C:\data" });
        Assert.True(draft.Revalidate(profiles));

        var thrown = Assert.Throws<ProfileConfigWriteFailedException>(
            () => ProfilesCommand.ApplySave(draft, draft.CurrentValidProfile!, profiles, writer, "unused-path"));

        Assert.Contains("unused-path", thrown.Message);
        var remaining = Assert.Single(profiles);
        Assert.Equal("photos", remaining.Name);
    }

    [Fact]
    public void ApplyDelete_leaves_the_in_memory_list_unchanged_when_the_writer_throws()
    {
        var writer = new ThrowingProfileConfigWriter();
        var toDelete = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);
        var profiles = new List<Profile> { toDelete };

        Assert.Throws<ProfileConfigWriteFailedException>(
            () => ProfilesCommand.ApplyDelete(profiles, toDelete, writer, "unused-path"));

        var remaining = Assert.Single(profiles);
        Assert.Equal("files", remaining.Name);
    }

    [Fact]
    public void TrySave_leaves_the_draft_intact_on_a_write_failure_and_a_retry_with_a_working_writer_then_succeeds()
    {
        var throwingWriter = new ThrowingProfileConfigWriter();
        var profiles = new List<Profile>();
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";
        draft.Sources.Add(new SourceDraft { Path = @"C:\data" });
        Assert.True(draft.Revalidate(profiles));

        Assert.Throws<ProfileConfigWriteFailedException>(
            () => ProfilesCommand.TrySave(draft, profiles, throwingWriter, "unused-path", out _));

        // The draft's own fields are untouched by the failed attempt - no re-entry needed.
        Assert.Equal("files", draft.Name);
        Assert.Equal(@"D:\backup", draft.Target);
        Assert.Empty(profiles);

        var workingWriter = new RecordingProfileConfigWriter();
        var succeeded = ProfilesCommand.TrySave(draft, profiles, workingWriter, "unused-path", out var saved);

        Assert.True(succeeded);
        Assert.Equal("files", saved!.Name);
        Assert.Single(profiles);
        Assert.True(workingWriter.WasCalled);
    }

    [Fact]
    public void RegisterCancellationExit_calls_the_exit_action_on_cancellation_without_touching_any_writer()
    {
        var writer = new RecordingProfileConfigWriter();
        var exitCalls = new List<int>();
        using var cts = new CancellationTokenSource();

        using (ProfilesCommand.RegisterCancellationExit(cts.Token, exitCalls.Add))
        {
            cts.Cancel();
        }

        Assert.Equal([Vara.Cli.Composition.ExitCodes.Success], exitCalls);
        Assert.False(writer.WasCalled);
    }

    [Fact]
    public void RegisterCancellationExit_does_not_fire_once_disposed()
    {
        var exitCalls = new List<int>();
        using var cts = new CancellationTokenSource();

        var registration = ProfilesCommand.RegisterCancellationExit(cts.Token, exitCalls.Add);
        registration.Dispose();
        cts.Cancel();

        Assert.Empty(exitCalls);
    }
}

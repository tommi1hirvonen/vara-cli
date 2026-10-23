using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Configuration;
using Xunit;
using Profile = Vara.Core.Configuration.Profile;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Drives the interactive `vara profiles` menu end to end by scripting the keystrokes into a
/// <see cref="TestConsole"/>, with the real YAML loader/writer against a temporary file.
/// </summary>
/// <remarks>
/// Prompt layouts the scripts rely on (selection index of each item):
/// main menu with no profiles [0 Add new profile, 1 Quit]; with one profile
/// [0 the profile, 1 Add new profile, 2 Delete a profile, 3 Quit];
/// edit screen [0 name, 1 target, 2 sources, 3 retention, 4 concurrency, 5 Save, 6 Discard];
/// sources screen [0..n-1 existing sources, n Add source, n+1 Back];
/// source editor [0 path, 1 recursive, 2 excludes, 3 include globs, 4 exclude globs, 5 remove, 6 Back];
/// list editor [0 Add entry, 1 Remove entry, 2 Done];
/// delete menu [0..n-1 profiles, n Cancel].
/// </remarks>
public class ProfilesCommandInteractionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vara-profiles-ui-{Guid.NewGuid():N}");
    private readonly string _configPath;
    private readonly string _sourcePath = Path.Combine(Path.GetTempPath(), "vara-ui-source");
    private readonly string _targetPath = Path.Combine(Path.GetTempPath(), "vara-ui-target");
    private readonly TestConsole _console = TestConsoles.CreateLive();

    public ProfilesCommandInteractionTests()
    {
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "profiles.yml");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Selects the item at <paramref name="index"/> of the prompt currently shown.</summary>
    private void Choose(int index)
    {
        for (var i = 0; i < index; i++)
        {
            _console.Input.PushKey(ConsoleKey.DownArrow);
        }

        _console.Input.PushKey(ConsoleKey.Enter);
    }

    private void Type(string text) => _console.Input.PushTextWithEnter(text);

    private void Answer(bool yes) => _console.Input.PushTextWithEnter(yes ? "y" : "n");

    private int Run(IProfileConfigWriter? writer = null, bool inputRedirected = false) =>
        ProfilesCommand.Create(
                new YamlProfileConfigLoader(),
                writer ?? new YamlProfileConfigWriter(),
                console: _console,
                errorConsole: _console,
                isInputRedirected: () => inputRedirected)
            .Parse(["--config", _configPath]).Invoke();

    private List<Profile> SavedProfiles() =>
        [.. new YamlProfileConfigLoader().LoadProfiles(_configPath)];

    private Profile SeedProfile(string name = "work")
    {
        var profile = new Profile(name, _targetPath, [new Source(_sourcePath)], retention: null);
        new YamlProfileConfigWriter().WriteProfiles([profile], _configPath);
        return profile;
    }

    /// <summary>Scripts: main menu -> Add new profile -> name + target + one source with the given path.</summary>
    private void ScriptNewProfileWithOneSource(string name)
    {
        Choose(0); // Add new profile
        Choose(0); // Edit name
        Type(name);
        Choose(1); // Edit target root
        Type(_targetPath);
        Choose(2); // Manage sources
        Choose(0); // Add source
        Choose(0); // Edit path
        Type(_sourcePath);
        Choose(6); // Back (source editor)
        Choose(2); // Back (sources screen: [source 1, Add source, Back])
    }

    [Fact]
    public void Refuses_to_run_when_input_is_redirected()
    {
        var exitCode = Run(inputRedirected: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires an interactive terminal", _console.Output);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Quitting_immediately_changes_nothing()
    {
        Choose(1); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Adding_a_profile_saves_it_to_the_configuration_file_and_reports_it()
    {
        ScriptNewProfileWithOneSource("work");
        Choose(5); // Save
        Choose(3); // Quit (menu now lists the new profile)

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        var saved = Assert.Single(SavedProfiles());
        Assert.Equal("work", saved.Name);
        Assert.Equal(_targetPath, saved.TargetRoot);
        Assert.Equal(_sourcePath, Assert.Single(saved.Sources).Path);
        Assert.Contains("Saved profile 'work'.", _console.Output);
    }

    [Fact]
    public void Discarding_an_edit_leaves_the_configuration_untouched()
    {
        ScriptNewProfileWithOneSource("work");
        Choose(6); // Discard
        Choose(1); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Saving_an_invalid_draft_is_refused_and_the_edit_screen_stays_open()
    {
        Choose(0); // Add new profile
        Choose(5); // Save with nothing entered
        Choose(6); // Discard
        Choose(1); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("Cannot save - validation error", _console.Output);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Editing_an_existing_profile_renames_it_in_place()
    {
        SeedProfile("work");
        Choose(0); // Edit "work"
        Choose(0); // Edit name
        Type("office");
        Choose(5); // Save
        Choose(3); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Equal("office", Assert.Single(SavedProfiles()).Name);
    }

    [Fact]
    public void Retention_and_concurrency_settings_are_saved_with_the_profile()
    {
        SeedProfile("work");
        Choose(0); // Edit "work"
        Choose(3); // Retention
        Answer(yes: true);
        Type("7");
        Type("4");
        Type("12");
        Type("2");
        Choose(4); // Concurrency
        Answer(yes: true);
        Type("3"); // scan_concurrency
        Type(string.Empty); // transfer_concurrency left unset
        Choose(5); // Save
        Choose(3); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        var saved = Assert.Single(SavedProfiles());
        Assert.Equal(new RetentionPolicy(7, 4, 12, 2), saved.Retention);
        Assert.Equal(3, saved.Concurrency?.ScanConcurrency);
        Assert.Null(saved.Concurrency?.TransferConcurrency);
    }

    [Fact]
    public void Deleting_a_profile_removes_it_after_confirmation()
    {
        SeedProfile("work");
        Choose(2); // Delete a profile
        Choose(0); // "work"
        Answer(yes: true);
        Choose(1); // Quit (menu now [Add new profile, Quit])

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Empty(SavedProfiles());
        Assert.Contains("Deleted profile 'work'.", _console.Output);
    }

    [Fact]
    public void Declining_the_delete_confirmation_keeps_the_profile()
    {
        SeedProfile("work");
        Choose(2); // Delete a profile
        Choose(0); // "work"
        Answer(yes: false);
        Choose(3); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Equal("work", Assert.Single(SavedProfiles()).Name);
    }

    [Fact]
    public void Cancelling_from_the_delete_menu_keeps_the_profile()
    {
        SeedProfile("work");
        Choose(2); // Delete a profile
        Choose(1); // Cancel
        Choose(3); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Single(SavedProfiles());
    }

    [Fact]
    public void A_failed_write_on_delete_is_shown_in_the_menu_and_keeps_the_profile_listed()
    {
        SeedProfile("work");
        Choose(2); // Delete a profile
        Choose(0); // "work"
        Answer(yes: true);
        Choose(3); // Quit - still [work, Add, Delete, Quit] because the delete failed

        var exitCode = Run(new FailingWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("disk is full", _console.Output);
        Assert.Single(SavedProfiles());
    }

    [Fact]
    public void Include_globs_containing_square_brackets_can_be_added_and_removed()
    {
        SeedProfile("work");
        Choose(0); // Edit "work"
        Choose(2); // Manage sources
        Choose(0); // the existing source
        Choose(3); // Edit include-glob list
        Choose(0); // Add entry
        Type("**/[ab]*.txt");
        Choose(1); // Remove entry
        Choose(0); // the only entry
        Choose(2); // Done
        Choose(6); // Back (source editor)
        Choose(2); // Back (sources screen)
        Choose(6); // Discard
        Choose(3); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("1. **/[ab]*.txt", _console.Output);
        Assert.DoesNotContain("[[ab]]", _console.Output);
        Assert.DoesNotContain("Unhandled exception", _console.Output);
    }

    [Fact]
    public void Removing_a_source_surfaces_the_no_sources_validation_error()
    {
        SeedProfile("work");
        Choose(0); // Edit "work"
        Choose(2); // Manage sources
        Choose(0); // the existing source
        Choose(5); // Remove this source
        Choose(1); // Back (sources screen: [Add source, Back])
        Choose(5); // Save -> refused, no sources
        Choose(6); // Discard
        Choose(3); // Quit

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("Cannot save - validation error", _console.Output);
        Assert.Equal(_sourcePath, Assert.Single(Assert.Single(SavedProfiles()).Sources).Path);
    }

    private sealed class FailingWriter : IProfileConfigWriter
    {
        public void WriteProfiles(IReadOnlyList<Profile> profiles, string configPath) =>
            throw new ProfileConfigWriteFailedException(configPath, "disk is full", new IOException("disk is full"));
    }
}

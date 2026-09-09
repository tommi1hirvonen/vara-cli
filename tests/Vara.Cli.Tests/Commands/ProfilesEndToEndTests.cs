using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Infrastructure.Configuration;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Exercises the end-to-end `vara profiles` persistence flow - add a profile with one
/// source through to a saved file, edit it (rename, add a second source, configure
/// retention), then delete it - confirming the final <c>profiles.yml</c> state after each
/// step via the real <see cref="YamlProfileConfigLoader"/>/<see cref="YamlProfileConfigWriter"/>
/// pair. Drives the same <see cref="ProfileDraft"/>/<see cref="ProfilesCommand.ApplySave"/>/
/// <see cref="ProfilesCommand.ApplyDelete"/> logic the interactive screens use, without going
/// through the Spectre.Console prompts themselves (see <c>ProfilesCommandTests</c> for why).
/// </summary>
public class ProfilesEndToEndTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vara-profiles-e2e-{Guid.NewGuid():N}");
    private readonly YamlProfileConfigWriter _writer = new();
    private readonly YamlProfileConfigLoader _loader = new();

    private string ConfigPath => Path.Combine(_tempDir, "profiles.yml");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Add_then_edit_then_delete_a_profile_produces_the_expected_file_state_at_each_step()
    {
        var profiles = new List<Vara.Core.Configuration.Profile>();

        // 1. Add a new profile with one source.
        var addDraft = ProfileDraft.ForNewProfile();
        addDraft.Name = "files";
        addDraft.Target = @"D:\backup";
        addDraft.Sources.Add(new SourceDraft { Path = @"C:\data" });
        Assert.True(addDraft.Revalidate(profiles));
        ProfilesCommand.ApplySave(addDraft, profiles, _writer, ConfigPath);

        var afterAdd = Assert.Single(_loader.LoadProfiles(ConfigPath));
        Assert.Equal("files", afterAdd.Name);
        Assert.Single(afterAdd.Sources);
        Assert.Null(afterAdd.Retention);

        // 2. Edit it: rename, add a second source, configure retention.
        var editDraft = ProfileDraft.FromProfile(afterAdd);
        editDraft.Name = "documents";
        editDraft.Sources.Add(new SourceDraft { Path = @"C:\other-data" });
        editDraft.HasRetention = true;
        editDraft.KeepDaily = 7;
        editDraft.KeepWeekly = 4;
        editDraft.KeepMonthly = 3;
        editDraft.KeepYearly = 1;
        Assert.True(editDraft.Revalidate(profiles));
        ProfilesCommand.ApplySave(editDraft, profiles, _writer, ConfigPath);

        var afterEdit = Assert.Single(_loader.LoadProfiles(ConfigPath));
        Assert.Equal("documents", afterEdit.Name);
        Assert.Equal(2, afterEdit.Sources.Count);
        Assert.NotNull(afterEdit.Retention);
        Assert.Equal(7, afterEdit.Retention!.KeepDaily);

        // 3. Delete it.
        ProfilesCommand.ApplyDelete(profiles, afterEdit, _writer, ConfigPath);

        Assert.Empty(_loader.LoadProfiles(ConfigPath));
    }
}

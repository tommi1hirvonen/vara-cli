using Vara.Application.History;
using Xunit;

namespace Vara.Application.Tests.History;

public class SnapshotPathResolverTests
{
    private const string MirrorRoot = @"D:\Backups\Profile1";

    [Fact]
    public void A_literal_mirror_relative_path_resolves_regardless_of_working_directory()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\file.txt" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"C\Users\john\file.txt",
            tracked.Contains,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\file.txt", resolvedPath);
    }

    [Fact]
    public void A_path_relative_to_a_working_directory_inside_the_mirror_resolves()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\file.txt" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            "file.txt",
            tracked.Contains,
            out var resolvedPath,
            currentDirectory: MirrorRoot + @"\C\Users\john");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\file.txt", resolvedPath);
    }

    [Fact]
    public void An_absolute_path_already_inside_the_mirror_resolves()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\file.txt" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            MirrorRoot + @"\C\Users\john\file.txt",
            tracked.Contains,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\file.txt", resolvedPath);
    }

    [Fact]
    public void A_path_relative_to_a_working_directory_inside_a_source_resolves_via_the_mirror_transform()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\Projects\src\main.py" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"src\main.py",
            tracked.Contains,
            out var resolvedPath,
            currentDirectory: @"C:\Users\john\Projects");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\Projects\src\main.py", resolvedPath);
    }

    [Fact]
    public void An_absolute_source_path_given_directly_resolves_via_the_mirror_transform()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\Projects\src\main.py" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"C:\Users\john\Projects\src\main.py",
            tracked.Contains,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\Projects\src\main.py", resolvedPath);
    }

    [Fact]
    public void No_interpretation_matching_recorded_history_fails_to_resolve()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\file.txt" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"C:\Users\john\nonexistent.txt",
            tracked.Contains,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.False(resolved);
        Assert.Equal(string.Empty, resolvedPath);
    }
}

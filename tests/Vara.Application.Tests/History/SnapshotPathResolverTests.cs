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
            IsWithinMirror,
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
            IsWithinMirror,
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
            IsWithinMirror,
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
            IsWithinMirror,
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
            IsWithinMirror,
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
            IsWithinMirror,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.False(resolved);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void When_isWithinMirror_reports_containment_but_the_literal_prefix_cannot_be_stripped_it_falls_through_to_the_source_path_transform()
    {
        // Simulates the reparse-point case: a fake isWithinMirror reports "yes, contained" for
        // an absolute path that does not share MirrorRoot's literal string prefix at all (as a
        // real caller's IContentStore.IsWithinMirror could, for a path reached only through a
        // symlink/junction whose real target lies inside the mirror). TryResolve must not
        // fabricate an incorrect mirror-relative path from a prefix strip that doesn't apply -
        // it should fall through to the absolute-source-path interpretation instead.
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\Projects\src\main.py" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"C:\Users\john\Projects\src\main.py",
            tracked.Contains,
            isWithinMirror: static _ => true,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\Projects\src\main.py", resolvedPath);
    }

    [Fact]
    public void When_the_working_directory_is_inside_the_mirror_the_cwd_relative_candidate_wins_over_a_coincidental_literal_match()
    {
        // Two distinct recorded paths: one matches the literal raw input exactly, the other
        // matches the cwd-relative interpretation. Standing inside the mirror, the cwd-relative
        // candidate must win, even though the literal input also happens to match elsewhere.
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"file.txt",
            @"C\Users\john\Projects\file.txt",
        };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            "file.txt",
            tracked.Contains,
            IsWithinMirror,
            out var resolvedPath,
            currentDirectory: MirrorRoot + @"\C\Users\john\Projects");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\Projects\file.txt", resolvedPath);
    }

    [Fact]
    public void When_the_working_directory_is_outside_the_mirror_the_literal_match_still_wins()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\file.txt" };

        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"C\Users\john\file.txt",
            tracked.Contains,
            IsWithinMirror,
            out var resolvedPath,
            currentDirectory: @"C:\Windows");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\file.txt", resolvedPath);
    }

    [Fact]
    public void When_the_working_directory_is_inside_the_mirror_and_the_cwd_relative_candidate_does_not_match_the_literal_input_is_still_used_as_a_fallback()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C\Users\john\file.txt" };

        // Standing inside "...\Projects", the cwd-relative candidate becomes
        // "C\Users\john\Projects\C\Users\john\file.txt", which is not recorded, so this must
        // fall back to the literal input.
        var resolved = SnapshotPathResolver.TryResolve(
            MirrorRoot,
            @"C\Users\john\file.txt",
            tracked.Contains,
            IsWithinMirror,
            out var resolvedPath,
            currentDirectory: MirrorRoot + @"\C\Users\john\Projects");

        Assert.True(resolved);
        Assert.Equal(@"C\Users\john\file.txt", resolvedPath);
    }

    /// <summary>Plain lexical containment check standing in for the reparse-aware
    /// <c>IContentStore.IsWithinMirror</c> a real caller passes - equivalent for these tests
    /// since none of them exercise a symlink/junction.</summary>
    private static bool IsWithinMirror(string absolutePath) =>
        absolutePath.Equals(MirrorRoot, StringComparison.OrdinalIgnoreCase)
        || absolutePath.StartsWith(MirrorRoot + @"\", StringComparison.OrdinalIgnoreCase);
}

using System.Diagnostics;
using Vara.Infrastructure.Interop;
using Xunit;

namespace Vara.Infrastructure.Tests.Interop;

public class Kernel32Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");
    private readonly string _outsideTarget = Path.Combine(Path.GetTempPath(), $"vara-test-outside-{Guid.NewGuid():N}");

    public Kernel32Tests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // The junction point (if created) must be removed before recursively deleting its
        // parent, otherwise Directory.Delete can fail trying to descend through it.
        var junctionPath = Path.Combine(_root, "linked-dir");
        if (Directory.Exists(junctionPath))
        {
            Directory.Delete(junctionPath, recursive: false);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_outsideTarget))
        {
            Directory.Delete(_outsideTarget, recursive: true);
        }
    }

    [Fact]
    public void A_plain_path_with_no_reparse_points_resolves_unchanged()
    {
        var filePath = Path.Combine(_root, "file.txt");
        File.WriteAllText(filePath, "content");

        var resolved = Kernel32.ResolveRealPath(filePath);

        Assert.Equal(Path.GetFullPath(filePath), resolved, ignoreCase: true);
    }

    [Fact]
    public void A_path_through_a_real_junction_resolves_to_the_junctions_target()
    {
        Directory.CreateDirectory(_outsideTarget);
        var targetFile = Path.Combine(_outsideTarget, "file.txt");
        File.WriteAllText(targetFile, "content");

        var junctionPath = Path.Combine(_root, "linked-dir");
        CreateJunction(junctionPath, _outsideTarget);

        var resolved = Kernel32.ResolveRealPath(Path.Combine(junctionPath, "file.txt"));

        Assert.Equal(Path.GetFullPath(targetFile), resolved, ignoreCase: true);
    }

    [Fact]
    public void A_not_yet_existing_destination_under_an_existing_real_directory_resolves_with_its_trailing_segment_preserved()
    {
        var notYetExisting = Path.Combine(_root, "new-file.txt");

        var resolved = Kernel32.ResolveRealPath(notYetExisting);

        Assert.Equal(Path.GetFullPath(notYetExisting), resolved, ignoreCase: true);
    }

    [Fact]
    public void A_not_yet_existing_destination_through_a_junction_resolves_through_the_junctions_target()
    {
        Directory.CreateDirectory(_outsideTarget);

        var junctionPath = Path.Combine(_root, "linked-dir");
        CreateJunction(junctionPath, _outsideTarget);

        var notYetExisting = Path.Combine(junctionPath, "sub", "new-file.txt");

        var resolved = Kernel32.ResolveRealPath(notYetExisting);

        Assert.Equal(Path.GetFullPath(Path.Combine(_outsideTarget, "sub", "new-file.txt")), resolved, ignoreCase: true);
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

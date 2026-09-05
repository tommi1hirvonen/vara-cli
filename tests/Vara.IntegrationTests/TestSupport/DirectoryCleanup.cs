namespace Vara.IntegrationTests.TestSupport;

/// <summary>
/// Test-only cleanup helper. Hardlinked mirror entries produced by the real
/// <c>FileSystemContentStore</c> are marked read-only (protect-hardlinked-mirror-files), and
/// <see cref="Directory.Delete(string, bool)"/> throws <see cref="UnauthorizedAccessException"/>
/// against a read-only file - clear the attribute on every file first so test teardown can
/// recursively delete a target root that contains real hardlinked mirror entries.
/// </summary>
internal static class DirectoryCleanup
{
    public static void ClearReadOnlyAndDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }
}

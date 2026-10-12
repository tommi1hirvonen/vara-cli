namespace Vara.Benchmarks;

internal static class BenchmarkFiles
{
    public static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vara-benchmarks", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static byte[] CreateDeterministicBytes(int size, int salt)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i + salt) % 251);
        }

        return bytes;
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}

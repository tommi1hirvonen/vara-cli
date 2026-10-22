namespace Vara.Application.History;

public closed record ShowRefusal(string Message);

public sealed record BinaryShowRefusal(string RelativePath)
    : ShowRefusal(
        $"Cannot show '{RelativePath}': content appears to be binary. " +
        "Pass --force-binary to show it anyway, or redirect output to a file.");

public sealed record LinkedEntryShowRefusal(string RelativePath)
    : ShowRefusal($"Cannot show '{RelativePath}': the resolved version is a symlink/junction with no stored content.");

public readonly union ShowResult(Stream, ShowRefusal);

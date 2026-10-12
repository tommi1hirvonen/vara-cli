namespace Vara.Application.History;

public sealed record DiffStreams(Stream Left, Stream Right) : IDisposable
{
    public void Dispose()
    {
        Left.Dispose();
        Right.Dispose();
    }
}

public closed record DiffRefusal(string Message);

public sealed record OversizedDiffRefusal(
    string RelativePath,
    long Limit,
    long? LeftSize,
    long? RightSize) : DiffRefusal(BuildMessage(RelativePath, Limit, LeftSize, RightSize))
{
    private static string BuildMessage(string relativePath, long limit, long? leftSize, long? rightSize)
    {
        var sides = new List<string>();
        if (leftSize is { } left)
        {
            sides.Add($"left side is {left:N0} bytes");
        }

        if (rightSize is { } right)
        {
            sides.Add($"right side is {right:N0} bytes");
        }

        return $"Cannot diff '{relativePath}': {string.Join(" and ", sides)}, exceeding the {limit:N0}-byte diff size limit.";
    }
}

public sealed record BinaryDiffRefusal(
    string RelativePath,
    bool LeftIsBinary,
    bool RightIsBinary) : DiffRefusal(BuildMessage(RelativePath, LeftIsBinary, RightIsBinary))
{
    private static string BuildMessage(string relativePath, bool leftIsBinary, bool rightIsBinary)
    {
        var sides = new List<string>();
        if (leftIsBinary)
        {
            sides.Add("left side");
        }

        if (rightIsBinary)
        {
            sides.Add("right side");
        }

        return $"Cannot diff '{relativePath}': {string.Join(" and ", sides)} appears to be binary content.";
    }
}

public sealed record LinkedEntryDiffRefusal(
    string RelativePath,
    bool LeftIsLinked,
    bool RightIsLinked) : DiffRefusal(BuildMessage(RelativePath, LeftIsLinked, RightIsLinked))
{
    private static string BuildMessage(string relativePath, bool leftIsLinked, bool rightIsLinked)
    {
        var sides = new List<string>();
        if (leftIsLinked)
        {
            sides.Add("left side");
        }

        if (rightIsLinked)
        {
            sides.Add("right side");
        }

        return $"Cannot diff '{relativePath}': {string.Join(" and ", sides)} is a symlink/junction with no stored content.";
    }
}

public readonly union DiffResult(DiffStreams, DiffRefusal);

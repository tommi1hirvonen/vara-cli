using System.Runtime.CompilerServices;

namespace Vara.Core.Abstractions;

public readonly record struct ScannedFile(
    string RelativePath,
    string AbsolutePath,
    long Size,
    DateTimeOffset ModifiedAt);

public readonly record struct ScannedLink(
    string RelativePath,
    string AbsolutePath,
    string? LinkTarget);

[Union]
public readonly struct ScanItem : IUnion
{
    private readonly ScannedFile _file;
    private readonly ScannedLink _link;
    private readonly byte _tag;

    public ScanItem(ScannedFile file)
    {
        _file = file;
        _link = default;
        _tag = 1;
    }

    public ScanItem(ScannedLink link)
    {
        _file = default;
        _link = link;
        _tag = 2;
    }

    public bool HasValue => _tag != 0;

    public bool TryGetValue(out ScannedFile file)
    {
        file = _file;
        return _tag == 1;
    }

    public bool TryGetValue(out ScannedLink link)
    {
        link = _link;
        return _tag == 2;
    }

    public object? Value => _tag switch
    {
        1 => _file,
        2 => _link,
        _ => null
    };

    public static ScanItem File(string relativePath, string absolutePath, long size, DateTimeOffset modifiedAt) =>
        new(new ScannedFile(relativePath, absolutePath, size, modifiedAt));

    public static ScanItem Link(string relativePath, string absolutePath, string? linkTarget) =>
        new(new ScannedLink(relativePath, absolutePath, linkTarget));
}

using Spectre.Console;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

internal readonly record struct FileChangeKindStyle(Style Style, string MarkupColor)
{
    public static FileChangeKindStyle For(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => new(new Style(Color.PaleGreen1), "PaleGreen1"),
        FileChangeKind.Changed => new(new Style(Color.LightGoldenrod2), "LightGoldenrod2"),
        FileChangeKind.Moved => new(new Style(Color.LightSkyBlue1), "LightSkyBlue1"),
        FileChangeKind.Deleted => new(new Style(Color.IndianRed), "IndianRed"),
        FileChangeKind.Linked => new(new Style(Color.Plum2), "Plum2"),
        _ => new(Style.Plain, "default"),
    };
}

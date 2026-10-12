using Spectre.Console;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

internal readonly record struct FileChangeKindStyle(Style Style, string MarkupColor)
{
    public static FileChangeKindStyle For(FileChangeKind kind) => kind switch
    {
        FileChangeKind.AddedChange => new(new Style(Color.PaleGreen1), "PaleGreen1"),
        FileChangeKind.ChangedChange => new(new Style(Color.LightGoldenrod2), "LightGoldenrod2"),
        FileChangeKind.MovedChange => new(new Style(Color.LightSkyBlue1), "LightSkyBlue1"),
        FileChangeKind.DeletedChange => new(new Style(Color.IndianRed), "IndianRed"),
        FileChangeKind.LinkedChange => new(new Style(Color.Plum2), "Plum2"),
    };
}

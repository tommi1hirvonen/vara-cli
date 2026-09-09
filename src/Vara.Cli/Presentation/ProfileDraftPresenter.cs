using Spectre.Console;
using Vara.Application.Profiles;

namespace Vara.Cli.Presentation;

/// <summary>
/// Pure rendering of a <see cref="ProfileDraft"/>'s current state for the `vara profiles`
/// edit screen - kept separate from <c>Vara.Cli.Commands.ProfilesCommand</c>'s prompt/loop
/// orchestration, following the existing commands-orchestrate / presentation-renders split.
/// Uses plain <c>WriteLine</c> (not <c>MarkupLine</c>) throughout, so arbitrary profile/path
/// content is never misinterpreted as markup syntax.
/// </summary>
public static class ProfileDraftPresenter
{
    public static void Render(IAnsiConsole console, ProfileDraft draft)
    {
        console.WriteLine();
        console.WriteLine($"Name: {FormatOptional(draft.Name)}");
        console.WriteLine($"Target root: {FormatOptional(draft.Target)}");
        console.WriteLine($"Sources: {draft.Sources.Count}");
        for (var i = 0; i < draft.Sources.Count; i++)
        {
            console.WriteLine($"  {i + 1}. {FormatSourceSummary(draft.Sources[i])}");
        }

        console.WriteLine(draft.HasRetention
            ? $"Retention: keep_daily={draft.KeepDaily}, keep_weekly={draft.KeepWeekly}, keep_monthly={draft.KeepMonthly}, keep_yearly={draft.KeepYearly}"
            : "Retention: not set");
        console.WriteLine(draft.HasConcurrency
            ? $"Concurrency: scan_concurrency={FormatOptional(draft.ScanConcurrency)}, transfer_concurrency={FormatOptional(draft.TransferConcurrency)}"
            : "Concurrency: not set");

        if (draft.CurrentError is not null)
        {
            OutcomeStyle.WriteLineError(console, $"Validation error: {draft.CurrentError}");
        }
        else
        {
            OutcomeStyle.WriteLineSuccess(console, "Draft is valid.");
        }

        console.WriteLine();
    }

    public static void RenderSource(IAnsiConsole console, SourceDraft source, string? currentError)
    {
        console.WriteLine();
        console.WriteLine($"Path: {FormatOptional(source.Path)}");
        console.WriteLine($"Recursive: {source.Recursive}");
        console.WriteLine($"Exclude: {FormatList(source.Excludes)}");
        console.WriteLine($"Include globs: {FormatList(source.IncludeGlobs)}");
        console.WriteLine($"Exclude globs: {FormatList(source.ExcludeGlobs)}");

        if (currentError is not null)
        {
            OutcomeStyle.WriteLineError(console, $"Validation error: {currentError}");
        }
    }

    public static string FormatSourceSummary(SourceDraft source) =>
        $"{FormatOptional(source.Path)} (recursive: {source.Recursive}, exclude: {source.Excludes.Count}, include_globs: {source.IncludeGlobs.Count}, exclude_globs: {source.ExcludeGlobs.Count})";

    public static string FormatList(IReadOnlyList<string> list) => list.Count == 0 ? "(none)" : string.Join(", ", list);

    private static string FormatOptional(string? value) => string.IsNullOrEmpty(value) ? "(not set)" : value;

    private static string FormatOptional(int? value) => value?.ToString() ?? "(not set)";
}

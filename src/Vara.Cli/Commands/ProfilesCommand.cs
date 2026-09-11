using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Profile = Vara.Core.Configuration.Profile;

namespace Vara.Cli.Commands;

/// <summary>
/// Implements the `vara profiles` interactive terminal UI for creating, editing, and
/// deleting backup profiles - see the `profile-management` capability's spec.md for the
/// full set of requirements this drives.
/// </summary>
public static class ProfilesCommand
{
    public static Command Create(IProfileConfigLoader configLoader, IProfileConfigWriter configWriter, CancellationToken cancellationToken = default)
    {
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("profiles", "Interactively create, edit, and delete backup profiles.") { configOption };

        command.SetAction(parseResult =>
        {
            var configPath = parseResult.GetValue(configOption) ?? configLoader.DefaultConfigPath;

            // Non-interactive detection needs both directions: OutputMode.IsLiveCapable
            // covers the output side (a live-redrawable terminal - what it was originally
            // built for, the backup progress bar), Console.IsInputRedirected covers the
            // input side (a real keyboard for the prompts this command is built entirely
            // out of) - see design.md's "Command/presentation shape" decision. Checked
            // before any prompt is shown, so a redirected session fails cleanly with no
            // half-rendered prompt and no file change.
            if (Console.IsInputRedirected || !OutputMode.IsLiveCapable(AnsiConsole.Console))
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: 'profiles' requires an interactive terminal.");
                return 1;
            }

            // A single Ctrl+C exits this command promptly instead of cooperating with the
            // shared token the way backup/restore/prune/check do: the editor never writes
            // outside an explicit Save/delete confirm, so an interrupt at any prompt is
            // simply a discard, with nothing to drain - see design.md's "Ctrl+C: cancel the
            // session, do not cooperate with it" decision. Disposed via `using` so the
            // registration cannot fire for a later command in the same process once this
            // one returns.
            using var cancellationRegistration = RegisterCancellationExit(cancellationToken, Environment.Exit);

            return ErrorReporting.Run(() => RunMenu(AnsiConsole.Console, configLoader, configWriter, configPath));
        });

        return command;
    }

    // Extracted so a test can invoke cancellation directly without a live terminal or a
    // real Environment.Exit call. The callback only ever calls <paramref name="exit"/> -
    // it has no access to any IProfileConfigWriter, so cancellation structurally cannot
    // trigger a configuration write.
    internal static IDisposable RegisterCancellationExit(CancellationToken cancellationToken, Action<int> exit) =>
        cancellationToken.Register(() => exit(ExitCodes.Success));

    // Internal (rather than private) so Vara.Cli.Tests can exercise the "missing file"
    // behavior directly - the rest of the interactive menu can't be driven from an
    // automated, non-interactive test host (see ProfilesCommandTests for why).
    internal static List<Profile> LoadProfilesOrEmpty(IProfileConfigLoader configLoader, string configPath) =>
        // A missing file is zero profiles for this command specifically (per the "Configuration
        // file does not yet exist" scenario), rather than the loader's own not-found error.
        File.Exists(configPath) ? [.. configLoader.LoadProfiles(configPath)] : [];

    private static void RunMenu(IAnsiConsole console, IProfileConfigLoader configLoader, IProfileConfigWriter configWriter, string configPath)
    {
        var profiles = LoadProfilesOrEmpty(configLoader, configPath);

        while (true)
        {
            var row = console.Prompt(BuildMainMenuPrompt(profiles));

            switch (row.Kind)
            {
                case MenuRowKind.Quit:
                    return;
                case MenuRowKind.AddNew:
                    RunEditScreen(console, ProfileDraft.ForNewProfile(), profiles, configWriter, configPath);
                    break;
                case MenuRowKind.DeletePrompt:
                    RunDeleteFlow(console, profiles, configWriter, configPath);
                    break;
                case MenuRowKind.Profile:
                    RunEditScreen(console, ProfileDraft.FromProfile(row.Profile!), profiles, configWriter, configPath);
                    break;
            }
        }
    }

    // ---- Main menu ----

    private enum MenuRowKind { Profile, AddNew, DeletePrompt, Quit, Cancel }

    private sealed class MenuRow
    {
        public required MenuRowKind Kind { get; init; }
        public Profile? Profile { get; init; }
        public required string Label { get; init; }
    }

    private static SelectionPrompt<MenuRow> BuildMainMenuPrompt(List<Profile> profiles)
    {
        var prompt = new SelectionPrompt<MenuRow>()
            .Title("Vara profiles - select a profile to edit, or an action:")
            .PageSize(15)
            // Every prompt over a non-primitive type must set an explicit Converter - see
            // design.md's "Command/presentation shape" decision: MenuRow has no
            // TypeConverter, so the default conversion path would throw
            // InvalidOperationException the first time this prompt is shown.
            .UseConverter(row => row.Label);

        foreach (var profile in profiles)
        {
            prompt.AddChoice(new MenuRow
            {
                Kind = MenuRowKind.Profile,
                Profile = profile,
                Label = Markup.Escape($"{profile.Name} - {profile.TargetRoot} ({profile.Sources.Count} source(s))"),
            });
        }

        prompt.AddChoice(new MenuRow { Kind = MenuRowKind.AddNew, Label = "Add new profile" });

        if (profiles.Count > 0)
        {
            prompt.AddChoice(new MenuRow { Kind = MenuRowKind.DeletePrompt, Label = "Delete a profile" });
        }

        prompt.AddChoice(new MenuRow { Kind = MenuRowKind.Quit, Label = "Quit" });

        return prompt;
    }

    private static void RunDeleteFlow(IAnsiConsole console, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath)
    {
        var prompt = new SelectionPrompt<MenuRow>()
            .Title("Select a profile to delete:")
            .PageSize(15)
            .UseConverter(row => row.Label);

        foreach (var profile in profiles)
        {
            prompt.AddChoice(new MenuRow
            {
                Kind = MenuRowKind.Profile,
                Profile = profile,
                Label = Markup.Escape($"{profile.Name} - {profile.TargetRoot}"),
            });
        }

        prompt.AddChoice(new MenuRow { Kind = MenuRowKind.Cancel, Label = "Cancel" });

        var selected = console.Prompt(prompt);
        if (selected.Kind != MenuRowKind.Profile)
        {
            return;
        }

        var target = selected.Profile!;
        var confirmed = console.Confirm(
            $"Delete profile '{Markup.Escape(target.Name)}' (target '{Markup.Escape(target.TargetRoot)}')? This will not touch any backup data already written under that target.",
            defaultValue: false);

        if (!confirmed)
        {
            return;
        }

        try
        {
            ApplyDelete(profiles, target, configWriter, configPath);
            OutcomeStyle.WriteLineSuccess(console, $"Deleted profile '{target.Name}'.");
        }
        catch (ProfileConfigWriteFailedException ex)
        {
            // Writing failed, so ApplyDelete left `profiles` untouched (write-then-commit
            // ordering - see design.md's "Order of operations on delete" decision): the
            // profile is still listed, and returning to the main menu here reflects that
            // accurately rather than showing it as deleted.
            OutcomeStyle.WriteLineError(console, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes <paramref name="target"/> from a candidate copy of <paramref name="profiles"/>
    /// and persists that candidate list, only replacing the contents of <paramref name="profiles"/>
    /// itself once the write has succeeded - so a failed write leaves the session's in-memory
    /// list exactly as it was (per the "no change" scenario) rather than out of sync with the
    /// configuration file. The part of the delete flow that doesn't depend on any prompt, so it
    /// can be exercised directly by an automated test. Only ever touches the configuration
    /// file; never the deleted profile's target root.
    /// </summary>
    internal static void ApplyDelete(List<Profile> profiles, Profile target, IProfileConfigWriter configWriter, string configPath)
    {
        var candidate = new List<Profile>(profiles);
        candidate.RemoveAll(p => string.Equals(p.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        configWriter.WriteProfiles(candidate, configPath);

        profiles.Clear();
        profiles.AddRange(candidate);
    }

    // ---- Profile edit screen ----

    private enum EditAction { Name, Target, Sources, Retention, Concurrency, Save, Discard }

    private sealed record EditActionRow(EditAction Action, string Label);

    private static void RunEditScreen(IAnsiConsole console, ProfileDraft draft, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath)
    {
        draft.Revalidate(profiles);

        // Captured once, when this screen is first entered: the terminal row immediately
        // below whatever is already on screen (which may be completely unrelated to vara -
        // earlier shell commands, output from before `profiles` was even run). Every
        // subsequent redraw repositions the cursor back to exactly this row and blanks
        // everything below it (see ClearOwnRegion), so this screen can never leave a stale
        // copy of itself behind - without ever touching anything above this row. A global
        // `IAnsiConsole.Clear()` was tried first and rejected: it clears the whole terminal
        // (and on some terminals, the scrollback), which wipes unrelated content that has
        // nothing to do with vara - see design.md's "console.Clear() placement in
        // RunEditScreen" decision for the full rationale.
        // + 1 => preserve the vara command
        var screenStartRow = System.Console.CursorTop + 1;

        while (true)
        {
            // Erased before every (re-)render - including on returning here after editing a
            // field or a sub-screen - so a previously rendered copy of the draft's summary is
            // never left on screen underneath the new one.
            ClearOwnRegion(console, screenStartRow);
            ProfileDraftPresenter.Render(console, draft);

            var rows = new List<EditActionRow>
            {
                new(EditAction.Name, $"Edit name (currently: {Markup.Escape(FormatOptional(draft.Name))})"),
                new(EditAction.Target, $"Edit target root (currently: {Markup.Escape(FormatOptional(draft.Target))})"),
                new(EditAction.Sources, $"Manage sources ({draft.Sources.Count} configured)"),
                new(EditAction.Retention, $"Retention ({(draft.HasRetention ? "configured" : "not set")})"),
                new(EditAction.Concurrency, $"Concurrency ({(draft.HasConcurrency ? "configured" : "not set")})"),
                new(EditAction.Save, "Save"),
                new(EditAction.Discard, "Discard"),
            };

            var action = console.Prompt(new SelectionPrompt<EditActionRow>()
                .Title("Choose a field to edit, or Save/Discard:")
                .PageSize(15)
                .UseConverter(r => r.Label)
                .AddChoices(rows)).Action;

            switch (action)
            {
                case EditAction.Name:
                    draft.Name = PromptOptionalText(console, "Name", draft.Name);
                    draft.Revalidate(profiles);
                    break;
                case EditAction.Target:
                    draft.Target = PromptOptionalText(console, "Target root", draft.Target);
                    draft.Revalidate(profiles);
                    break;
                case EditAction.Sources:
                    RunSourcesScreen(console, draft, profiles);
                    draft.Revalidate(profiles);
                    break;
                case EditAction.Retention:
                    EditRetention(console, draft);
                    draft.Revalidate(profiles);
                    break;
                case EditAction.Concurrency:
                    EditConcurrency(console, draft);
                    draft.Revalidate(profiles);
                    break;
                case EditAction.Save:
                    try
                    {
                        if (TrySave(draft, profiles, configWriter, configPath, out var saved))
                        {
                            // Erased before the confirmation, not after: this wipes the edit
                            // screen's last render so it can't linger underneath whatever the
                            // main menu displays next, while still leaving the confirmation
                            // itself visible on the now-clean screen.
                            ClearOwnRegion(console, screenStartRow);
                            OutcomeStyle.WriteLineSuccess(console, $"Saved profile '{saved!.Name}'.");
                            return;
                        }

                        OutcomeStyle.WriteLineError(console, $"Cannot save - validation error: {draft.CurrentError}");
                    }
                    catch (ProfileConfigWriteFailedException ex)
                    {
                        // Write-then-commit ordering (see ApplySave) means `profiles` and the
                        // draft are both untouched here, so staying on the edit screen lets
                        // the user retry Save once the cause is resolved without re-entering
                        // any field.
                        OutcomeStyle.WriteLineError(console, $"Error: {ex.Message}");
                    }

                    break;
                case EditAction.Discard:
                    // Erased before returning, so the edit screen's last render doesn't
                    // linger underneath the main menu's next prompt.
                    ClearOwnRegion(console, screenStartRow);
                    return;
            }
        }
    }

    /// <summary>
    /// Repositions the cursor to <paramref name="startRow"/> and blanks every row from there
    /// to the bottom of the visible window, then returns the cursor to
    /// <paramref name="startRow"/> - erasing only the region a caller has drawn since that row,
    /// never anything above it. Deliberately does not use <see cref="IAnsiConsole.Clear"/>
    /// (which clears the whole terminal, including content unrelated to vara - see
    /// design.md's "console.Clear() placement in RunEditScreen" decision) or
    /// <c>IAnsiConsole.WriteAnsi</c>'s ANSI erase-in-display sequence (which silently does
    /// nothing under Spectre's legacy, non-VT console backend). <see cref="IAnsiConsoleCursor.SetPosition"/>
    /// is an absolute-position move in both of Spectre's cursor backends (an ANSI CSI `H`
    /// sequence, or a direct <see cref="System.Console.CursorLeft"/>/<see
    /// cref="System.Console.CursorTop"/> set), so blanking line-by-line via plain text writes
    /// works identically regardless of which backend is active.
    /// </summary>
    private static void ClearOwnRegion(IAnsiConsole console, int startRow)
    {
        // One character short of the full width, not the full width itself - writing all
        // the way to the last column of a row risks some terminals eagerly wrapping/
        // scrolling once that column is filled, which would shift what "startRow" means for
        // every SetPosition call after that point.
        var blankLine = new string(' ', Math.Max(1, console.Profile.Width - 1));
        var height = Math.Max(startRow + 1, console.Profile.Height);

        for (var row = startRow; row < height; row++)
        {
            console.Cursor.SetPosition(0, row);
            console.Write(blankLine);
        }

        console.Cursor.SetPosition(0, startRow);
    }

    /// <summary>
    /// Re-validates <paramref name="draft"/> against <paramref name="profiles"/> at the
    /// moment Save is chosen, and, only when that revalidation succeeds, persists the
    /// profile it just produced via <see cref="ApplySave"/>. This is Save's single entry
    /// point precisely so that "is the draft valid" and "what gets written" are always
    /// answered by the same, current validation call - no code path may instead consult a
    /// stale <see cref="ProfileDraft.CurrentValidProfile"/> left over from an earlier edit.
    /// Returns <see langword="true"/> and sets <paramref name="saved"/> to the persisted
    /// profile on success; returns <see langword="false"/> (leaving <paramref name="saved"/>
    /// <see langword="null"/> and the configuration file untouched) when the draft is
    /// currently invalid, in which case <see cref="ProfileDraft.CurrentError"/> describes why.
    /// </summary>
    internal static bool TrySave(
        ProfileDraft draft,
        List<Profile> profiles,
        IProfileConfigWriter configWriter,
        string configPath,
        out Profile? saved)
    {
        if (!draft.Revalidate(profiles))
        {
            saved = null;
            return false;
        }

        saved = ApplySave(draft, draft.CurrentValidProfile!, profiles, configWriter, configPath);
        return true;
    }

    /// <summary>
    /// Persists <paramref name="profileToSave"/> - the profile a caller's own, current
    /// <see cref="ProfileDraft.Revalidate"/> call just produced, passed explicitly rather
    /// than read back from <see cref="ProfileDraft.CurrentValidProfile"/> - into a candidate
    /// copy of <paramref name="profiles"/>: added as a new entry when <see cref="ProfileDraft.OriginalName"/>
    /// is <see langword="null"/>, or replacing the matching entry (found by <c>OriginalName</c>,
    /// not the draft's current, possibly-renamed <see cref="ProfileDraft.Name"/>) otherwise -
    /// then writes the candidate list, and only replaces the contents of <paramref name="profiles"/>
    /// itself once that write has succeeded. This write-then-commit ordering means a failed
    /// write leaves both the session's in-memory list and the configuration file exactly as
    /// they were before Save was chosen - see design.md's "Order of operations on delete"
    /// decision (the same ordering applies here). Extracted from <see cref="TrySave"/> so the
    /// add-vs-replace-by-OriginalName logic can be exercised directly by an automated test,
    /// without needing to drive the surrounding prompts.
    /// </summary>
    internal static Profile ApplySave(ProfileDraft draft, Profile profileToSave, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath)
    {
        var saved = profileToSave;
        var candidate = new List<Profile>(profiles);
        if (draft.OriginalName is null)
        {
            candidate.Add(saved);
        }
        else
        {
            var index = candidate.FindIndex(p => string.Equals(p.Name, draft.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                candidate[index] = saved;
            }
            else
            {
                candidate.Add(saved);
            }
        }

        configWriter.WriteProfiles(candidate, configPath);

        profiles.Clear();
        profiles.AddRange(candidate);
        return saved;
    }

    private static void EditRetention(IAnsiConsole console, ProfileDraft draft)
    {
        draft.HasRetention = console.Confirm("Configure a retention policy for this profile?", draft.HasRetention);
        if (!draft.HasRetention)
        {
            return;
        }

        draft.KeepDaily = console.Prompt(new TextPrompt<int>("keep_daily:").DefaultValue(draft.KeepDaily));
        draft.KeepWeekly = console.Prompt(new TextPrompt<int>("keep_weekly:").DefaultValue(draft.KeepWeekly));
        draft.KeepMonthly = console.Prompt(new TextPrompt<int>("keep_monthly:").DefaultValue(draft.KeepMonthly));
        draft.KeepYearly = console.Prompt(new TextPrompt<int>("keep_yearly:").DefaultValue(draft.KeepYearly));
    }

    private static void EditConcurrency(IAnsiConsole console, ProfileDraft draft)
    {
        draft.HasConcurrency = console.Confirm("Configure concurrency settings for this profile?", draft.HasConcurrency);
        if (!draft.HasConcurrency)
        {
            return;
        }

        draft.ScanConcurrency = PromptOptionalInt(console, "scan_concurrency", draft.ScanConcurrency);
        draft.TransferConcurrency = PromptOptionalInt(console, "transfer_concurrency", draft.TransferConcurrency);
    }

    // ---- Source sub-editor ----

    private sealed record SourceRow(int Index, string Label);

    private static void RunSourcesScreen(IAnsiConsole console, ProfileDraft draft, List<Profile> profiles)
    {
        while (true)
        {
            var rows = new List<SourceRow>();
            for (var i = 0; i < draft.Sources.Count; i++)
            {
                rows.Add(new SourceRow(i, $"{i + 1}. {Markup.Escape(ProfileDraftPresenter.FormatSourceSummary(draft.Sources[i]))}"));
            }

            rows.Add(new SourceRow(-1, "Add source"));
            rows.Add(new SourceRow(-2, "Back"));

            var selected = console.Prompt(new SelectionPrompt<SourceRow>()
                .Title("Sources:")
                .PageSize(15)
                .UseConverter(r => r.Label)
                .AddChoices(rows));

            switch (selected.Index)
            {
                case -2:
                    return;
                case -1:
                    var newSource = new SourceDraft();
                    draft.Sources.Add(newSource);
                    draft.Revalidate(profiles);
                    EditSource(console, draft, newSource, profiles);
                    break;
                default:
                    EditSource(console, draft, draft.Sources[selected.Index], profiles);
                    break;
            }
        }
    }

    private enum SourceFieldAction { Path, Recursive, Excludes, IncludeGlobs, ExcludeGlobs, Remove, Back }

    private sealed record SourceFieldActionRow(SourceFieldAction Action, string Label);

    private static void EditSource(IAnsiConsole console, ProfileDraft draft, SourceDraft source, List<Profile> profiles)
    {
        while (true)
        {
            ProfileDraftPresenter.RenderSource(console, source, draft.CurrentError);

            var rows = new List<SourceFieldActionRow>
            {
                new(SourceFieldAction.Path, $"Edit path (currently: {Markup.Escape(FormatOptional(source.Path))})"),
                new(SourceFieldAction.Recursive, $"Toggle recursive (currently: {source.Recursive})"),
                new(SourceFieldAction.Excludes, $"Edit exclude list ({source.Excludes.Count} entries)"),
                new(SourceFieldAction.IncludeGlobs, $"Edit include-glob list ({source.IncludeGlobs.Count} entries)"),
                new(SourceFieldAction.ExcludeGlobs, $"Edit exclude-glob list ({source.ExcludeGlobs.Count} entries)"),
                new(SourceFieldAction.Remove, "Remove this source"),
                new(SourceFieldAction.Back, "Back"),
            };

            var action = console.Prompt(new SelectionPrompt<SourceFieldActionRow>()
                .Title("Edit source:")
                .PageSize(15)
                .UseConverter(r => r.Label)
                .AddChoices(rows)).Action;

            switch (action)
            {
                case SourceFieldAction.Path:
                    source.Path = PromptOptionalText(console, "Path", source.Path);
                    draft.Revalidate(profiles);
                    break;
                case SourceFieldAction.Recursive:
                    source.Recursive = console.Confirm("Recursive?", source.Recursive);
                    draft.Revalidate(profiles);
                    break;
                case SourceFieldAction.Excludes:
                    EditStringList(console, "exclude entry", source.Excludes);
                    draft.Revalidate(profiles);
                    break;
                case SourceFieldAction.IncludeGlobs:
                    EditStringList(console, "include-glob pattern", source.IncludeGlobs);
                    draft.Revalidate(profiles);
                    break;
                case SourceFieldAction.ExcludeGlobs:
                    EditStringList(console, "exclude-glob pattern", source.ExcludeGlobs);
                    draft.Revalidate(profiles);
                    break;
                case SourceFieldAction.Remove:
                    // Only permitted display-side - the containing ProfileDraft's own
                    // validation (via Profile's constructor) already rejects a save with
                    // zero sources, so removing the last one simply surfaces that error
                    // through the normal live-validation path rather than being blocked here.
                    draft.Sources.Remove(source);
                    draft.Revalidate(profiles);
                    return;
                case SourceFieldAction.Back:
                    return;
            }
        }
    }

    private static void EditStringList(IAnsiConsole console, string itemLabel, List<string> list)
    {
        while (true)
        {
            console.WriteLine($"Current {itemLabel} entries: {ProfileDraftPresenter.FormatList(list)}");

            // Plain strings are on Spectre's intrinsic primitive converter list, so no
            // explicit Converter is needed for this prompt.
            var action = console.Prompt(new SelectionPrompt<string>()
                .Title($"Manage {itemLabel} list:")
                .AddChoices("Add entry", "Remove entry", "Done"));

            switch (action)
            {
                case "Add entry":
                    var entry = console.Prompt(new TextPrompt<string>($"New {itemLabel}:"));
                    list.Add(entry);
                    break;
                case "Remove entry":
                    if (list.Count == 0)
                    {
                        OutcomeStyle.WriteLineError(console, "There are no entries to remove.");
                        break;
                    }

                    var indexedRows = list.Select((value, i) => $"{i + 1}. {value}").ToList();
                    var toRemove = console.Prompt(new SelectionPrompt<string>().Title("Remove which entry?").AddChoices(indexedRows));
                    list.RemoveAt(indexedRows.IndexOf(toRemove));
                    break;
                case "Done":
                    return;
            }
        }
    }

    // ---- Shared prompt helpers ----

    private static string? PromptOptionalText(IAnsiConsole console, string label, string? current)
    {
        var input = console.Prompt(new TextPrompt<string>(
                $"{label} (currently: {Markup.Escape(FormatOptional(current))}; leave blank to clear):")
            .AllowEmpty());
        return string.IsNullOrWhiteSpace(input) ? null : input;
    }

    private static int? PromptOptionalInt(IAnsiConsole console, string label, int? current)
    {
        var input = console.Prompt(new TextPrompt<string>(
                $"{label} (currently: {FormatOptional(current)}; leave blank for the pipeline's own default):")
            .AllowEmpty()
            .Validate(value => string.IsNullOrWhiteSpace(value) || int.TryParse(value, CultureInfo.InvariantCulture, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error("Enter a whole number, or leave blank.")));

        return string.IsNullOrWhiteSpace(input) ? null : int.Parse(input, CultureInfo.InvariantCulture);
    }

    internal static string FormatOptional(string? value) => string.IsNullOrEmpty(value) ? "(not set)" : value;

    internal static string FormatOptional(int? value) => value?.ToString() ?? "(not set)";
}

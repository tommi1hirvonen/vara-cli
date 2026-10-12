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

            var console = AnsiConsole.Console;

            // A single Ctrl+C exits this command promptly. The cleanup callback ensures
            // that the alternate screen buffer is exited and cursor restored before process
            // termination, so the user's terminal is never left in an inconsistent state.
            using var cancellationRegistration = RegisterCancellationExit(
                cancellationToken,
                Environment.Exit,
                () => RestorePrimaryScreenBuffer(console));

            string? lastSavedProfile = null;

            var exitCode = ErrorReporting.Run(() =>
            {
                console.AlternateScreen(() =>
                {
                    lastSavedProfile = RunMenu(console, configLoader, configWriter, configPath);
                });
            });

            if (exitCode == ExitCodes.Success && lastSavedProfile is not null)
            {
                OutcomeStyle.WriteLineSuccess(console, $"Saved profile '{lastSavedProfile}'.");
            }

            return exitCode;
        });

        return command;
    }

    /// <summary>
    /// Emits the terminal control sequences to exit the alternate screen buffer and make
    /// the cursor visible. Safe to call as a cleanup step on cancellation or abnormal exit.
    /// </summary>
    internal static void RestorePrimaryScreenBuffer(IAnsiConsole console)
    {
        try
        {
            console.Write(new ControlCode("\u001b[?1049l\u001b[?25h"));
        }
        catch
        {
            // Best-effort cleanup on cancellation or abnormal termination
        }
    }

    // Extracted so a test can invoke cancellation directly without a live terminal or a
    // real Environment.Exit call. The callback only ever calls <paramref name="exit"/>
    // after running any optional <paramref name="cleanup"/> action.
    internal static IDisposable RegisterCancellationExit(CancellationToken cancellationToken, Action<int> exit, Action? cleanup = null) =>
        cancellationToken.Register(() =>
        {
            cleanup?.Invoke();
            exit(ExitCodes.Success);
        });

    // Internal (rather than private) so Vara.Cli.Tests can exercise the "missing file"
    // behavior directly - the rest of the interactive menu can't be driven from an
    // automated, non-interactive test host (see ProfilesCommandTests for why).
    internal static List<Profile> LoadProfilesOrEmpty(IProfileConfigLoader configLoader, string configPath) =>
        // A missing file is zero profiles for this command specifically (per the "Configuration
        // file does not yet exist" scenario), rather than the loader's own not-found error.
        File.Exists(configPath) ? [.. configLoader.LoadProfiles(configPath)] : [];

    private static string? RunMenu(IAnsiConsole console, IProfileConfigLoader configLoader, IProfileConfigWriter configWriter, string configPath)
    {
        var profiles = LoadProfilesOrEmpty(configLoader, configPath);
        string? statusMessage = null;
        var isErrorStatus = false;
        string? lastSavedProfile = null;

        while (true)
        {
            console.Clear();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                if (isErrorStatus)
                {
                    OutcomeStyle.WriteLineError(console, statusMessage);
                }
                else
                {
                    OutcomeStyle.WriteLineSuccess(console, statusMessage);
                }

                console.WriteLine();
                statusMessage = null;
                isErrorStatus = false;
            }

            var action = console.Prompt(BuildMainMenuPrompt(profiles));

            switch (action)
            {
                case QuitAction:
                    return lastSavedProfile;
                case AddNewProfile:
                    var newSaved = RunEditScreen(console, ProfileDraft.ForNewProfile(), profiles, configWriter, configPath);
                    if (newSaved is not null)
                    {
                        statusMessage = $"Saved profile '{newSaved.Name}'.";
                        lastSavedProfile = newSaved.Name;
                    }
                    break;
                case StartDeleteProfile:
                    var deletedName = RunDeleteFlow(console, profiles, configWriter, configPath, out var deleteError);
                    if (deletedName is not null)
                    {
                        statusMessage = $"Deleted profile '{deletedName}'.";
                    }
                    else if (deleteError is not null)
                    {
                        statusMessage = $"Error: {deleteError}";
                        isErrorStatus = true;
                    }
                    break;
                case EditProfile editProfile:
                    var editSaved = RunEditScreen(console, ProfileDraft.FromProfile(editProfile.Profile), profiles, configWriter, configPath);
                    if (editSaved is not null)
                    {
                        statusMessage = $"Saved profile '{editSaved.Name}'.";
                        lastSavedProfile = editSaved.Name;
                    }
                    break;
                case DeleteProfile:
                case CancelAction:
                    throw new InvalidOperationException($"Unexpected menu action: {action.GetType().Name}.");
            }
        }
    }

    // ---- Main menu ----

    private closed record MenuAction(string Label);

    private sealed record EditProfile(Profile Profile, string Label) : MenuAction(Label);
    private sealed record AddNewProfile() : MenuAction("Add new profile");
    private sealed record StartDeleteProfile() : MenuAction("Delete a profile");
    private sealed record DeleteProfile(Profile Profile, string Label) : MenuAction(Label);
    private sealed record QuitAction() : MenuAction("Quit");
    private sealed record CancelAction() : MenuAction("Cancel");

    private static SelectionPrompt<MenuAction> BuildMainMenuPrompt(List<Profile> profiles)
    {
        var prompt = new SelectionPrompt<MenuAction>()
            .Title("Vara profiles - select a profile to edit, or an action:")
            .PageSize(15)
            // Every prompt over a non-primitive type must set an explicit Converter - see
            // design.md's "Command/presentation shape" decision: MenuAction has no
            // TypeConverter, so the default conversion path would throw
            // InvalidOperationException the first time this prompt is shown.
            .UseConverter(menuAction => menuAction.Label);

        foreach (var profile in profiles)
        {
            prompt.AddChoice(new EditProfile(
                profile,
                Markup.Escape($"{profile.Name} - {profile.TargetRoot} ({profile.Sources.Count} source(s))")));
        }

        prompt.AddChoice(new AddNewProfile());

        if (profiles.Count > 0)
        {
            prompt.AddChoice(new StartDeleteProfile());
        }

        prompt.AddChoice(new QuitAction());

        return prompt;
    }

    private static string? RunDeleteFlow(IAnsiConsole console, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath, out string? errorMessage)
    {
        errorMessage = null;
        console.Clear();

        var prompt = new SelectionPrompt<MenuAction>()
            .Title("Select a profile to delete:")
            .PageSize(15)
            .UseConverter(menuAction => menuAction.Label);

        foreach (var profile in profiles)
        {
            prompt.AddChoice(new DeleteProfile(profile, Markup.Escape($"{profile.Name} - {profile.TargetRoot}")));
        }

        prompt.AddChoice(new CancelAction());

        var selected = console.Prompt(prompt);
        var target = selected switch
        {
            DeleteProfile deleteProfile => deleteProfile.Profile,
            CancelAction => null,
            EditProfile or AddNewProfile or StartDeleteProfile or QuitAction =>
                throw new InvalidOperationException($"Unexpected delete-menu action: {selected.GetType().Name}."),
        };
        if (target is null)
        {
            return null;
        }

        var confirmed = console.Confirm(
            $"Delete profile '{Markup.Escape(target.Name)}' (target '{Markup.Escape(target.TargetRoot)}')? This will not touch any backup data already written under that target.",
            defaultValue: false);

        if (!confirmed)
        {
            return null;
        }

        try
        {
            ApplyDelete(profiles, target, configWriter, configPath);
            return target.Name;
        }
        catch (ProfileConfigWriteFailedException ex)
        {
            // Writing failed, so ApplyDelete left `profiles` untouched (write-then-commit
            // ordering - see design.md's "Order of operations on delete" decision): the
            // profile is still listed, and returning to the main menu here reflects that
            // accurately rather than showing it as deleted.
            errorMessage = ex.Message;
            return null;
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

    private closed record EditFieldAction(string Label);

    private sealed record EditNameAction(string Label) : EditFieldAction(Label);
    private sealed record EditTargetAction(string Label) : EditFieldAction(Label);
    private sealed record ManageSourcesAction(string Label) : EditFieldAction(Label);
    private sealed record EditRetentionAction(string Label) : EditFieldAction(Label);
    private sealed record EditConcurrencyAction(string Label) : EditFieldAction(Label);
    private sealed record SaveProfileAction(string Label) : EditFieldAction(Label);
    private sealed record DiscardProfileAction(string Label) : EditFieldAction(Label);

    private static Profile? RunEditScreen(IAnsiConsole console, ProfileDraft draft, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath)
    {
        draft.Revalidate(profiles);
        string? errorMessage = null;

        while (true)
        {
            console.Clear();

            if (!string.IsNullOrEmpty(errorMessage))
            {
                OutcomeStyle.WriteLineError(console, errorMessage);
                console.WriteLine();
                errorMessage = null;
            }

            ProfileDraftPresenter.Render(console, draft);

            var rows = new List<EditFieldAction>
            {
                new EditNameAction($"Edit name (currently: {Markup.Escape(FormatOptional(draft.Name))})"),
                new EditTargetAction($"Edit target root (currently: {Markup.Escape(FormatOptional(draft.Target))})"),
                new ManageSourcesAction($"Manage sources ({draft.Sources.Count} configured)"),
                new EditRetentionAction($"Retention ({(draft.HasRetention ? "configured" : "not set")})"),
                new EditConcurrencyAction($"Concurrency ({(draft.HasConcurrency ? "configured" : "not set")})"),
                new SaveProfileAction("Save"),
                new DiscardProfileAction("Discard"),
            };

            var action = console.Prompt(new SelectionPrompt<EditFieldAction>()
                .Title("Choose a field to edit, or Save/Discard:")
                .PageSize(15)
                .UseConverter(fieldAction => fieldAction.Label)
                .AddChoices(rows));

            switch (action)
            {
                case EditNameAction:
                    draft.Name = PromptOptionalText(console, "Name", draft.Name);
                    draft.Revalidate(profiles);
                    break;
                case EditTargetAction:
                    draft.Target = PromptOptionalText(console, "Target root", draft.Target);
                    draft.Revalidate(profiles);
                    break;
                case ManageSourcesAction:
                    RunSourcesScreen(console, draft, profiles);
                    draft.Revalidate(profiles);
                    break;
                case EditRetentionAction:
                    EditRetention(console, draft);
                    draft.Revalidate(profiles);
                    break;
                case EditConcurrencyAction:
                    EditConcurrency(console, draft);
                    draft.Revalidate(profiles);
                    break;
                case SaveProfileAction:
                    try
                    {
                        if (TrySave(draft, profiles, configWriter, configPath, out var saved))
                        {
                            return saved;
                        }

                        errorMessage = $"Cannot save - validation error: {draft.CurrentError}";
                    }
                    catch (ProfileConfigWriteFailedException ex)
                    {
                        // Write-then-commit ordering (see ApplySave) means `profiles` and the
                        // draft are both untouched here, so staying on the edit screen lets
                        // the user retry Save once the cause is resolved without re-entering
                        // any field.
                        errorMessage = $"Error: {ex.Message}";
                    }

                    break;
                case DiscardProfileAction:
                    return null;
            }
        }
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
        console.Clear();
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
        console.Clear();
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
            console.Clear();

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

    private closed record SourceFieldAction(string Label);

    private sealed record EditSourcePathAction(string Label) : SourceFieldAction(Label);
    private sealed record ToggleSourceRecursiveAction(string Label) : SourceFieldAction(Label);
    private sealed record EditSourceExcludesAction(string Label) : SourceFieldAction(Label);
    private sealed record EditSourceIncludeGlobsAction(string Label) : SourceFieldAction(Label);
    private sealed record EditSourceExcludeGlobsAction(string Label) : SourceFieldAction(Label);
    private sealed record RemoveSourceAction(string Label) : SourceFieldAction(Label);
    private sealed record BackFromSourceAction(string Label) : SourceFieldAction(Label);

    private static void EditSource(IAnsiConsole console, ProfileDraft draft, SourceDraft source, List<Profile> profiles)
    {
        while (true)
        {
            console.Clear();
            ProfileDraftPresenter.RenderSource(console, source, draft.CurrentError);

            var rows = new List<SourceFieldAction>
            {
                new EditSourcePathAction($"Edit path (currently: {Markup.Escape(FormatOptional(source.Path))})"),
                new ToggleSourceRecursiveAction($"Toggle recursive (currently: {source.Recursive})"),
                new EditSourceExcludesAction($"Edit exclude list ({source.Excludes.Count} entries)"),
                new EditSourceIncludeGlobsAction($"Edit include-glob list ({source.IncludeGlobs.Count} entries)"),
                new EditSourceExcludeGlobsAction($"Edit exclude-glob list ({source.ExcludeGlobs.Count} entries)"),
                new RemoveSourceAction("Remove this source"),
                new BackFromSourceAction("Back"),
            };

            var action = console.Prompt(new SelectionPrompt<SourceFieldAction>()
                .Title("Edit source:")
                .PageSize(15)
                .UseConverter(fieldAction => fieldAction.Label)
                .AddChoices(rows));

            switch (action)
            {
                case EditSourcePathAction:
                    source.Path = PromptOptionalText(console, "Path", source.Path);
                    draft.Revalidate(profiles);
                    break;
                case ToggleSourceRecursiveAction:
                    source.Recursive = console.Confirm("Recursive?", source.Recursive);
                    draft.Revalidate(profiles);
                    break;
                case EditSourceExcludesAction:
                    EditStringList(console, "exclude entry", source.Excludes);
                    draft.Revalidate(profiles);
                    break;
                case EditSourceIncludeGlobsAction:
                    EditStringList(console, "include-glob pattern", source.IncludeGlobs);
                    draft.Revalidate(profiles);
                    break;
                case EditSourceExcludeGlobsAction:
                    EditStringList(console, "exclude-glob pattern", source.ExcludeGlobs);
                    draft.Revalidate(profiles);
                    break;
                case RemoveSourceAction:
                    // Only permitted display-side - the containing ProfileDraft's own
                    // validation (via Profile's constructor) already rejects a save with
                    // zero sources, so removing the last one simply surfaces that error
                    // through the normal live-validation path rather than being blocked here.
                    draft.Sources.Remove(source);
                    draft.Revalidate(profiles);
                    return;
                case BackFromSourceAction:
                    return;
            }
        }
    }

    private static void EditStringList(IAnsiConsole console, string itemLabel, List<string> list)
    {
        string? errorMessage = null;

        while (true)
        {
            console.Clear();

            if (!string.IsNullOrEmpty(errorMessage))
            {
                OutcomeStyle.WriteLineError(console, errorMessage);
                console.WriteLine();
                errorMessage = null;
            }

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
                    if (!string.IsNullOrWhiteSpace(entry))
                    {
                        list.Add(entry.Trim());
                    }
                    break;
                case "Remove entry":
                    if (list.Count == 0)
                    {
                        errorMessage = "There are no entries to remove.";
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

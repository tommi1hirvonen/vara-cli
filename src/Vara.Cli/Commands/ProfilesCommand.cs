using System.CommandLine;
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
    public static Command Create(IProfileConfigLoader configLoader, IProfileConfigWriter configWriter)
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

            return ErrorReporting.Run(() => RunMenu(AnsiConsole.Console, configLoader, configWriter, configPath));
        });

        return command;
    }

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

        ApplyDelete(profiles, target, configWriter, configPath);
        OutcomeStyle.WriteLineSuccess(console, $"Deleted profile '{target.Name}'.");
    }

    /// <summary>
    /// Removes <paramref name="target"/> from <paramref name="profiles"/> and persists the
    /// remaining list - the part of the delete flow that doesn't depend on any prompt, so it
    /// can be exercised directly by an automated test. Only ever touches the configuration
    /// file; never the deleted profile's target root.
    /// </summary>
    internal static void ApplyDelete(List<Profile> profiles, Profile target, IProfileConfigWriter configWriter, string configPath)
    {
        profiles.RemoveAll(p => string.Equals(p.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        configWriter.WriteProfiles(profiles, configPath);
    }

    // ---- Profile edit screen ----

    private enum EditAction { Name, Target, Sources, Retention, Concurrency, Save, Discard }

    private sealed record EditActionRow(EditAction Action, string Label);

    private static void RunEditScreen(IAnsiConsole console, ProfileDraft draft, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath)
    {
        draft.Revalidate(profiles);

        while (true)
        {
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
                    if (draft.CurrentError is not null)
                    {
                        OutcomeStyle.WriteLineError(console, $"Cannot save - validation error: {draft.CurrentError}");
                        break;
                    }

                    var saved = ApplySave(draft, profiles, configWriter, configPath);
                    OutcomeStyle.WriteLineSuccess(console, $"Saved profile '{saved.Name}'.");
                    return;
                case EditAction.Discard:
                    return;
            }
        }
    }

    /// <summary>
    /// Persists <paramref name="draft"/>'s currently-valid profile (see
    /// <see cref="ProfileDraft.CurrentValidProfile"/>) into <paramref name="profiles"/> - added
    /// as a new entry when <see cref="ProfileDraft.OriginalName"/> is <see langword="null"/>,
    /// or replacing the matching entry (found by <c>OriginalName</c>, not the draft's current,
    /// possibly-renamed <see cref="ProfileDraft.Name"/>) otherwise - then writes the updated
    /// list. Callers must only invoke this once <see cref="ProfileDraft.CurrentError"/> is
    /// <see langword="null"/>. Extracted from the edit screen's Save action so the
    /// add-vs-replace-by-OriginalName logic can be exercised directly by an automated test,
    /// without needing to drive the surrounding prompts.
    /// </summary>
    internal static Profile ApplySave(ProfileDraft draft, List<Profile> profiles, IProfileConfigWriter configWriter, string configPath)
    {
        var saved = draft.CurrentValidProfile!;
        if (draft.OriginalName is null)
        {
            profiles.Add(saved);
        }
        else
        {
            var index = profiles.FindIndex(p => string.Equals(p.Name, draft.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                profiles[index] = saved;
            }
            else
            {
                profiles.Add(saved);
            }
        }

        configWriter.WriteProfiles(profiles, configPath);
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
            .Validate(value => string.IsNullOrWhiteSpace(value) || int.TryParse(value, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error("Enter a whole number, or leave blank.")));

        return string.IsNullOrWhiteSpace(input) ? null : int.Parse(input);
    }

    internal static string FormatOptional(string? value) => string.IsNullOrEmpty(value) ? "(not set)" : value;

    internal static string FormatOptional(int? value) => value?.ToString() ?? "(not set)";
}

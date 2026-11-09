/// The prompts a command asks for input with. The only module that calls Spectre.Console's prompts: a command is given
/// the `Prompts` record and never sees a console type, so a test passes functions that answer in place of a user.
namespace Vara.Cli

open Spectre.Console
open Vara.Domain

/// Two functions: choose one of several lines, and answer yes or no.
type Prompts =
    {
        /// `Select title choices defaultIndex` shows the choices, each a line of styled spans, with the default
        /// highlighted, and returns the index of the one the user picked.
        Select: string -> Span list list -> int -> int
        /// `Confirm question` asks a yes or no question whose default is no.
        Confirm: string -> bool
    }

module Prompts =
    /// A line of spans as markup: every span's text escaped, so that a path with brackets is shown as it is.
    let private markup (spans: Span list) : string =
        spans
        |> List.map (fun span ->
            let escaped = Markup.Escape span.Text
            let style = Terminal.styleOfTone span.Tone

            if style = Style.Plain then escaped else $"[{style.ToMarkup()}]{escaped}[/]")
        |> String.concat ""

    /// How many choices a selection shows at once; the C# build shows ten too.
    let private pageSize = 10

    /// The prompts on a console, which is standard error in the commands, so that they stay visible when standard
    /// output is redirected.
    let onConsole (console: IAnsiConsole) : Prompts =
        { Select =
            fun title choices defaultIndex ->
                let labels = choices |> List.map markup |> Array.ofList
                let prompt = SelectionPrompt<int>()
                prompt.Title <- Markup.Escape title
                prompt.PageSize <- pageSize
                prompt.UseConverter(fun index -> labels[index]) |> ignore
                prompt.AddChoices([| 0 .. labels.Length - 1 |]) |> ignore
                prompt.DefaultValue <- defaultIndex
                console.Prompt prompt
          Confirm = fun question -> console.Confirm(Markup.Escape question, false) }

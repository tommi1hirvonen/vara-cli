// Reading single key presses for a key-driven view. The only module that calls the console's key API: a view is given
// the `KeyInput` record and never sees a console type, so a test passes a script of key presses in place of a user.
namespace Vara.Cli

open System
open System.Threading
open Vara.Domain

/// What happened while a key-driven view waited: a key it understands (or `Key.Other`), Ctrl+C, or a resize.
[<RequireQualifiedAccess>]
type KeyEvent =
    | Pressed of Key
    | Interrupted
    | Resized

/// The calls of a key-driven view.
type KeyInput =
    {
        /// Starts a session in which Ctrl+C arrives as an event instead of ending the process; disposing it restores
        /// the console's own handling.
        Open: unit -> IDisposable
        /// Waits for the next event.
        Next: unit -> KeyEvent
        /// The height of the console window in lines.
        Height: unit -> int
    }

module KeyInput =
    /// How often a wait looks for a key, and so for a resize too.
    let private pollMilliseconds = 50

    let private keyOf (info: ConsoleKeyInfo) : KeyEvent =
        let control = info.Modifiers.HasFlag ConsoleModifiers.Control

        match info.Key with
        | ConsoleKey.C when control -> KeyEvent.Interrupted
        | ConsoleKey.UpArrow -> KeyEvent.Pressed Key.Up
        | ConsoleKey.DownArrow -> KeyEvent.Pressed Key.Down
        | ConsoleKey.LeftArrow -> KeyEvent.Pressed Key.Left
        | ConsoleKey.RightArrow -> KeyEvent.Pressed Key.Right
        | ConsoleKey.Home -> KeyEvent.Pressed Key.Home
        | ConsoleKey.End -> KeyEvent.Pressed Key.End
        | ConsoleKey.Escape -> KeyEvent.Pressed Key.Quit
        | _ ->
            match info.KeyChar with
            | 'q'
            | 'Q' -> KeyEvent.Pressed Key.Quit
            | '*' -> KeyEvent.Pressed Key.ExpandAll
            | _ -> KeyEvent.Pressed Key.Other

    /// The real console: keys are read without echo, and a changed window size ends the wait.
    let console: KeyInput =
        { Open =
            fun () ->
                let previous = Console.TreatControlCAsInput
                Console.TreatControlCAsInput <- true

                { new IDisposable with
                    member _.Dispose() = Console.TreatControlCAsInput <- previous }
          Next =
            fun () ->
                let size = struct (Console.WindowWidth, Console.WindowHeight)
                let mutable result = ValueNone

                while result.IsNone do
                    if Console.KeyAvailable then
                        result <- ValueSome(keyOf (Console.ReadKey true))
                    elif struct (Console.WindowWidth, Console.WindowHeight) <> size then
                        result <- ValueSome KeyEvent.Resized
                    else
                        Thread.Sleep pollMilliseconds

                result.Value
          Height = fun () -> Console.WindowHeight }

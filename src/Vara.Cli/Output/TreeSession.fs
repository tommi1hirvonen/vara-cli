/// A key-driven change tree on the alternate screen: draws a frame, waits for a key, and repeats until the user quits.
/// The terminal is restored however the session ends.
module Vara.Cli.TreeSession

open Vara.Domain

/// The sequence that hides the text cursor. `AlternateScreen.Leave` shows it again.
let private hideCursor: string = "\u001b[?25l"

/// Runs the browser from the state until the user quits (success) or presses Ctrl+C (the cancellation outcome).
let run (env: CliEnv) (initial: TreeState) : Outcome =
    use _keys = env.Keys.Open()
    env.Alternate.Enter()

    try
        LiveRenderer.writeSequence env.Console hideCursor

        let rec loop (state: TreeState) : Outcome =
            // One line fewer than the window, so the last line's line break does not scroll the header away.
            let fitted, display = TreeBrowser.frame (env.Keys.Height() - 1) state
            env.Alternate.Clear()
            LiveRenderer.print env.Console display

            match env.Keys.Next() with
            | KeyEvent.Interrupted -> Outcome.PartialFailure
            | KeyEvent.Resized -> loop fitted
            | KeyEvent.Pressed key ->
                let next = TreeBrowser.step key fitted
                if next.Finished then Outcome.Success else loop next

        loop initial
    finally
        env.Alternate.Leave()

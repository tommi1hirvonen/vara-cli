namespace Vara.Domain

open System

/// Shortens a path to a fixed character budget for a progress label (progress-reporting: "Active file transfers
/// listed in progress display", "Restore task labels are bounded to a fixed width").
module PathLabel =
    let private ellipsis = "..."
    let private separators = [| '\\'; '/' |]

    /// The last `length` characters of the name, behind an ellipsis. A budget no longer than the ellipsis keeps the
    /// name's first characters instead.
    let private truncateName (name: string) (budget: int) : string =
        if name.Length <= budget then name
        elif budget <= ellipsis.Length then name.Substring(0, budget)
        else ellipsis + name.Substring(name.Length - (budget - ellipsis.Length))

    /// The path unchanged when it fits. Otherwise leading parent directories are dropped behind a `...\` prefix,
    /// keeping as many trailing segments as fit. When even the filename does not fit beside the prefix, the filename
    /// alone is truncated, with no prefix.
    let truncate (budget: int) (path: string) : string =
        if budget <= 0 then ""
        elif path.Length <= budget then path
        else
            let segments = path.Split(separators, StringSplitOptions.RemoveEmptyEntries)

            if segments.Length = 0 then
                truncateName path budget
            else
                let fileName = segments[segments.Length - 1]
                let prefixLength = ellipsis.Length + 1

                if fileName.Length + prefixLength > budget then
                    truncateName fileName budget
                else
                    // Walk the parents from the nearest, keeping each while the whole label still fits.
                    let rec keep (index: int) (length: int) : int =
                        if index < 0 then 0
                        else
                            let candidate = length + 1 + segments[index].Length

                            if prefixLength + candidate > budget then index + 1 else keep (index - 1) candidate

                    let first = keep (segments.Length - 2) fileName.Length
                    ellipsis + "\\" + String.Join("\\", segments, first, segments.Length - first)

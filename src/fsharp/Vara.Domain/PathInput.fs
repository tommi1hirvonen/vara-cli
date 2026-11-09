namespace Vara.Domain

open System
open System.IO

/// What path resolution needs to know about the machine: where the mirror is, where the command runs, and whether
/// a path lies in the mirror once reparse points are followed (the shell answers that; it touches the disk).
type PathContext =
    { MirrorRoot: string
      WorkingDirectory: string
      IsWithinMirror: string -> bool }

/// The directory a command works on, and whether it is the mirror root shown because the working directory has no
/// history of its own.
type DirectoryResolution =
    { Directory: string
      FellBackToMirrorRoot: bool }

/// The flexible path forms that `history`, `restore`, `show`, `diff`, `browse` and `snapshot` accept: relative to
/// the working directory inside the mirror, exactly as recorded, or an absolute source path.
module PathInput =
    let private fullPath (workingDirectory: string) (raw: string) : string voption =
        try
            ValueSome(Path.GetFullPath(raw, workingDirectory))
        with :? ArgumentException ->
            // Not a well-formed path (invalid characters): only the literal reading can still match.
            ValueNone

    /// The part of the absolute path after the mirror root; nothing when it is not lexically below it.
    let private stripMirrorRoot (mirrorRoot: string) (absolute: string) : string voption =
        let root = Path.GetFullPath mirrorRoot

        let rootWithSeparator =
            if root.EndsWith Path.DirectorySeparatorChar then root else root + string Path.DirectorySeparatorChar

        if String.Equals(absolute, root, StringComparison.OrdinalIgnoreCase) then
            ValueSome ""
        elif absolute.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) then
            ValueSome(absolute.Substring rootWithSeparator.Length)
        else
            ValueNone

    /// The first reading of the input that `hasHistory` accepts: the working-directory reading when it lies in the
    /// mirror, then the text exactly as given, then an absolute source path converted to its mirror form.
    let resolve (context: PathContext) (hasHistory: string -> bool) (raw: string) : string voption =
        let absolute = fullPath context.WorkingDirectory raw

        // Reparse-aware containment says "inside" for a path reached through a link into the mirror, which has no
        // literal prefix to strip: then there is no working-directory reading, and the others are tried.
        let inMirror =
            match absolute with
            | ValueSome path when context.IsWithinMirror path -> stripMirrorRoot context.MirrorRoot path
            | ValueSome _
            | ValueNone -> ValueNone

        let candidates =
            [ match inMirror with
              | ValueSome relative -> relative
              | ValueNone -> ()
              raw
              match absolute with
              | ValueSome path -> SourceLocation.toMirror path
              | ValueNone -> () ]

        candidates |> List.tryFind hasHistory |> ValueOption.ofOption

    /// The directory a command works on. The mirror root is always a valid directory: `.` or blank names it. From
    /// outside the mirror, `.` or blank names the working directory mapped into the mirror when that has history,
    /// and otherwise falls back to the root, which the caller reports.
    let resolveDirectory (context: PathContext) (isDirectoryTracked: string -> bool) (raw: string) : DirectoryResolution =
        let absolute = fullPath context.WorkingDirectory raw
        let insideMirror = absolute |> ValueOption.exists context.IsWithinMirror

        if Prefix.isRoot raw && not insideMirror then
            let mapped =
                absolute
                |> ValueOption.map SourceLocation.toMirror
                |> ValueOption.filter isDirectoryTracked

            match mapped with
            | ValueSome directory ->
                { Directory = directory
                  FellBackToMirrorRoot = false }
            | ValueNone ->
                { Directory = raw
                  FellBackToMirrorRoot = true }
        else
            let hasHistoryOrIsRoot (candidate: string) : bool = Prefix.isRoot candidate || isDirectoryTracked candidate

            { Directory = resolve context hasHistoryOrIsRoot raw |> ValueOption.defaultValue raw
              FellBackToMirrorRoot = false }

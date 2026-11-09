namespace Vara.Domain

open System

/// The pure rules that decide which paths a scan reports and where they land in the mirror.
module ScanRules =
    let private forward (path: string) : string = path.Replace('\\', '/')

    /// Prepares a source's literal exclude entries once per source: trimmed, without trailing separators,
    /// with `/` as the separator. An entry written with either separator then matches the same paths.
    let normalizeExcludes (excludes: string list) : string list =
        excludes |> List.map (fun exclude -> (exclude.Trim().TrimEnd('\\', '/')) |> forward)

    /// True when the source-relative path is an excluded entry or lies beneath one.
    /// `normalizedExcludes` comes from `normalizeExcludes`. The path may use either separator.
    let isExcluded (normalizedExcludes: string list) (relativePath: string) : bool =
        // A recursive walk rather than List.exists, so no closure is allocated per scanned entry.
        let rec any (path: string) (excludes: string list) =
            match excludes with
            | [] -> false
            | exclude :: rest ->
                String.Equals(path, exclude, StringComparison.OrdinalIgnoreCase)
                || (path.Length > exclude.Length
                    && path[exclude.Length] = '/'
                    && path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
                || any path rest

        match normalizedExcludes with
        | [] -> false
        | _ -> any (forward relativePath) normalizedExcludes

    /// Applies a glob matcher supplied by the shell to a source-relative path written with either separator.
    /// The matcher itself (include and exclude patterns) is built from the source in `Vara.Engine`, which owns the
    /// globbing package; the rule that decides which form of the path it sees lives here.
    let matchesGlobs (matcher: string -> bool) (relativePath: string) : bool = matcher (forward relativePath)

    let private isDriveLetter (c: char) : bool = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')

    let private isSeparator (c: char) : bool = c = '\\' || c = '/'

    /// The path an absolute source path occupies under the target root: the drive letter's colon is dropped
    /// (`C:\Users\john\file.txt` becomes `C\Users\john\file.txt`, `D:\` becomes `D\`).
    /// None for a UNC path or a device path, which has no mirror location.
    let toMirrorPath (absolutePath: string) : MirrorPath voption =
        if absolutePath.Length >= 2 && isSeparator absolutePath[0] && isSeparator absolutePath[1] then
            ValueNone
        elif absolutePath.Length >= 2 && isDriveLetter absolutePath[0] && absolutePath[1] = ':' then
            ValueSome(MirrorPath.ofString (String.Concat(absolutePath.AsSpan(0, 1), absolutePath.AsSpan 2)))
        else
            ValueSome(MirrorPath.ofString absolutePath)

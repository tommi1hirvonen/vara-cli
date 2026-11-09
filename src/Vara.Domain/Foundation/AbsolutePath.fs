namespace Vara.Domain

open System
open System.IO

/// A fully-qualified path in canonical form: separators normalized, `.` and `..` resolved, a single trailing separator kept.
type AbsolutePath = private AbsolutePath of string

module AbsolutePath =
    /// None when the text is not fully qualified. Only manipulates the string; no file is touched.
    let tryCreate (text: string) : AbsolutePath option =
        if Path.IsPathFullyQualified text then
            Some(AbsolutePath(Path.GetFullPath text))
        else
            None

    let value (AbsolutePath text) : string = text

    let private comparable (AbsolutePath text) : string =
        text.Trim().TrimEnd('\\', '/').Replace('/', Path.DirectorySeparatorChar)

    /// True when the paths are the same location or one contains the other.
    /// Ignores letter case and a trailing separator.
    let overlaps (left: AbsolutePath) (right: AbsolutePath) : bool =
        let left, right = comparable left, comparable right
        let separator = string Path.DirectorySeparatorChar

        String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + separator, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + separator, StringComparison.OrdinalIgnoreCase)

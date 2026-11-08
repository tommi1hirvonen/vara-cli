namespace Vara.Domain

/// A path inside the target's mirror, relative to the target root (`C\Users\john\file.txt`).
/// Compared case-insensitively by the code that keys on it; the value keeps its original casing.
[<Struct>]
type MirrorPath = private MirrorPath of string

module MirrorPath =
    let ofString (text: string) : MirrorPath = MirrorPath text
    let value (MirrorPath text) : string = text

/// The full path of an entry in a configured source, as the file system knows it.
[<Struct>]
type SourcePath = private SourcePath of string

module SourcePath =
    let ofString (text: string) : SourcePath = SourcePath text
    let value (SourcePath text) : string = text

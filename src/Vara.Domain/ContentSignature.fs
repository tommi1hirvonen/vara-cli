namespace Vara.Domain

open System

/// The full-content hash of a stored file, as the content store names its blob. Never empty.
[<Struct>]
type ContentHash = private ContentHash of string

module ContentHash =
    /// None for a null or empty string, which the manifest uses for "no content" (a link or an unknown hash).
    let tryCreate (text: string) : ContentHash voption =
        if String.IsNullOrEmpty text then ValueNone else ValueSome(ContentHash text)

    let value (ContentHash text) : string = text

/// A hash of a bounded prefix of a file, with the scheme that produced it. A signature of another scheme
/// cannot rule a move candidate out, so it counts as unavailable.
[<Struct>]
type QuickSignature = { QuickHash: string; QuickScheme: int }

module QuickSignature =
    /// The scheme of signatures this build records: a 64 KiB prefix window.
    let currentScheme: int = 1

    let windowSizeBytes: int = 64 * 1024

    let isUsable (signature: QuickSignature) : bool = signature.QuickScheme = currentScheme

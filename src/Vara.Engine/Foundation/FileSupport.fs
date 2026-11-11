namespace Vara.Engine

open System
open System.IO

/// Matches the errors that end the one file or directory being worked on and not the run: the path could not be read
/// or written. Gives back the exception, so a handler can word it.
[<AutoOpen>]
module IoFailurePattern =
    [<return: Struct>]
    let (|IoFailure|_|) (error: exn) : exn voption =
        match error with
        | :? IOException
        | :? UnauthorizedAccessException -> ValueSome error
        | _ -> ValueNone

/// File-system steps that the content store and the restore share.
module FileSupport =
    /// Clears the read-only attribute so an overwrite or delete does not throw. The attribute is shared by every
    /// name of a hard-linked file, so this affects them all.
    let clearReadOnly (path: string) : unit =
        if File.Exists path then
            let attributes = File.GetAttributes path

            if attributes.HasFlag FileAttributes.ReadOnly then
                File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly)

    let ensureReadOnly (path: string) : unit =
        if File.Exists path then
            let attributes = File.GetAttributes path

            if not (attributes.HasFlag FileAttributes.ReadOnly) then
                File.SetAttributes(path, attributes ||| FileAttributes.ReadOnly)

    /// Best-effort removal for cleanup; a file that is locked stays.
    let tryDelete (path: string) : unit =
        try
            clearReadOnly path
            File.Delete path
        with IoFailure _ ->
            ()

namespace Vara.Engine

#nowarn "9" // native pointers are the point of this file

open System
open System.Buffers
open System.IO
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop
open Microsoft.Win32.SafeHandles

/// Win32 calls the BCL does not cover: hard-link creation, and real-path resolution through any reparse point
/// along a path's ancestry.
module Kernel32 =
    [<DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool private CreateHardLinkW(string newLink, string existing, nativeint securityAttributes)

    [<DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern SafeFileHandle private CreateFileW(
        string fileName,
        uint32 desiredAccess,
        uint32 shareMode,
        nativeint securityAttributes,
        uint32 creationDisposition,
        uint32 flagsAndAttributes,
        nativeint templateFile)

    [<DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern uint32 private GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint32 bufferLength, uint32 flags)

    let private genericRead = 0x80000000u
    let private shareReadWriteDelete = 0x7u
    let private openExisting = 3u
    let private backupSemantics = 0x02000000u // required to open a directory handle
    let private nameNormalized = 0u
    let private stackBufferSize = 512

    /// Creates `newLink` as another name for the file at `existing`. Raises an `IOException` carrying the Win32
    /// error when the volume has no hard links, the paths are on different volumes, or the file's link limit is reached.
    let createHardLink (newLink: string) (existing: string) : unit =
        if not (CreateHardLinkW(newLink, existing, 0n)) then
            let error = Marshal.GetLastWin32Error()
            raise (IOException($"Failed to create hard link '{newLink}' -> '{existing}' (Win32 error {error}).", error))

    let private readFinalPath (handle: SafeFileHandle) (path: string) (buffer: nativeptr<char>) (length: uint32) : string =
        let written = GetFinalPathNameByHandleW(handle, buffer, length, nameNormalized)

        if written = 0u || written >= length then
            let error = Marshal.GetLastWin32Error()
            raise (IOException($"Failed to resolve the real path of '{path}' (Win32 error {error}).", error))

        String(buffer, 0, int written)

    /// The final location of an existing file or directory, following reparse points, in `\\?\` form.
    let getFinalPathName (path: string) : string =
        use handle = CreateFileW(path, genericRead, shareReadWriteDelete, 0n, openExisting, backupSemantics, 0n)

        if handle.IsInvalid then
            let error = Marshal.GetLastWin32Error()
            raise (IOException($"Failed to open '{path}' to resolve its real path (Win32 error {error}).", error))

        // A zero-length buffer makes the call report the size it needs, terminator included.
        let required = GetFinalPathNameByHandleW(handle, NativePtr.nullPtr, 0u, nameNormalized)

        if required = 0u then
            let error = Marshal.GetLastWin32Error()
            raise (IOException($"Failed to resolve the real path of '{path}' (Win32 error {error}).", error))

        if required > uint32 Int32.MaxValue then
            raise (IOException $"The resolved path of '{path}' is too long to represent.")

        if int required <= stackBufferSize then
            let buffer = NativePtr.stackalloc<char> stackBufferSize
            readFinalPath handle path buffer required
        else
            let rented = ArrayPool<char>.Shared.Rent(int required)

            try
                use pinned = fixed rented
                readFinalPath handle path pinned required
            finally
                ArrayPool<char>.Shared.Return(rented, false)

    let private stripExtendedPrefix (path: string) : string =
        if path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) then @"\\" + path.Substring 8
        elif path.StartsWith(@"\\?\", StringComparison.Ordinal) then path.Substring 4
        else path

    /// The real, final location of a path, following reparse points anywhere in its ancestry. Finds the deepest
    /// ancestor that exists, resolves it, and appends the segments that do not exist yet unchanged, because a
    /// destination usually does not exist before it is written. Falls back to the plain full path when no ancestor
    /// can be opened, so a containment check keeps at least its lexical protection instead of throwing.
    let resolveRealPath (path: string) : string =
        let absolute = Path.GetFullPath path

        let rec deepestExisting (current: string) (trailing: string list) : (string * string list) voption =
            if Directory.Exists current || File.Exists current then
                ValueSome(current, trailing)
            else
                match Path.GetDirectoryName current with
                | null -> ValueNone
                | parent when parent.Length = 0 || parent = current -> ValueNone
                | parent -> deepestExisting parent (Path.GetFileName current :: trailing)

        match deepestExisting absolute [] with
        | ValueNone -> absolute
        | ValueSome(ancestor, trailing) ->
            let resolved =
                try
                    ValueSome(stripExtendedPrefix (getFinalPathName ancestor))
                with :? IOException ->
                    ValueNone

            match resolved with
            | ValueNone -> absolute
            | ValueSome real -> Path.Combine(Array.ofList (real :: trailing))

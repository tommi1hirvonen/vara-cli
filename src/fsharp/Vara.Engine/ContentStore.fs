namespace Vara.Engine

open System
open System.IO
open Vara.Domain

/// A mirror path resolved outside the target root. An `IOException`, so the per-operation failure handling that
/// catches an unreadable or unwritable path catches this too, instead of the run aborting.
type MirrorPathEscapesTargetRootException(mirrorPath: string, resolved: string, targetRoot: string) =
    inherit IOException($"Mirror path '{mirrorPath}' resolves to '{resolved}', which is outside the target root '{targetRoot}'.")

/// The effect the content store needs that tests vary: making a hard link (`newLink existing`).
/// A test passes a function that raises, in place of the C# test hooks.
type Links = { Create: string -> string -> unit }

/// Where a target's files live: the mirror at the root, blobs and staging under `.vara`.
type StoreLayout =
    { MirrorRoot: string
      VersionsRoot: string
      TempRoot: string }

/// The content-addressed store for one target: a live mirror, deduplicated blobs under `.vara/versions/<hash[0..2]>`,
/// and staging under `.vara/tmp` for atomic writes. Every function takes the layout and the effects it uses.
module ContentStore =
    // Under 85,000 bytes, so buffers stay off the large object heap.
    let private bufferSize = 81_920

    let layout (targetRoot: string) : StoreLayout =
        { MirrorRoot = targetRoot
          VersionsRoot = Path.Combine(targetRoot, ".vara", "versions")
          TempRoot = Path.Combine(targetRoot, ".vara", "tmp") }

    let realLinks: Links = { Create = Kernel32.createHardLink }

    /// Creates the mirror, versions and temp directories. A read-only command does not call this.
    let ensureDirectories (store: StoreLayout) : unit =
        Directory.CreateDirectory store.MirrorRoot |> ignore
        Directory.CreateDirectory store.VersionsRoot |> ignore
        Directory.CreateDirectory store.TempRoot |> ignore

    let private stagingPath (store: StoreLayout) : string =
        Path.Combine(store.TempRoot, Guid.NewGuid().ToString "N")

    let blobPath (store: StoreLayout) (hash: ContentHash) : string =
        let text = ContentHash.value hash
        Path.Combine(store.VersionsRoot, text.Substring(0, 2), text)

    /// Clears the read-only attribute so an overwrite or delete does not throw. The attribute is shared by every
    /// name of a hard-linked file, so this affects them all.
    let private clearReadOnly (path: string) : unit =
        if File.Exists path then
            let attributes = File.GetAttributes path

            if attributes.HasFlag FileAttributes.ReadOnly then
                File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly)

    let private ensureReadOnly (path: string) : unit =
        if File.Exists path then
            let attributes = File.GetAttributes path

            if not (attributes.HasFlag FileAttributes.ReadOnly) then
                File.SetAttributes(path, attributes ||| FileAttributes.ReadOnly)

    /// Best-effort removal for cleanup; a file that is locked stays.
    let private tryDelete (path: string) : unit =
        try
            clearReadOnly path
            File.Delete path
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    /// Tries a throwaway hard link in the temp directory. False when the volume or the process cannot make one.
    let probeHardlinkSupport (links: Links) (store: StoreLayout) : bool =
        let source = Path.Combine(store.TempRoot, $"probe-src-{Guid.NewGuid():N}.tmp")
        let link = Path.Combine(store.TempRoot, $"probe-link-{Guid.NewGuid():N}.tmp")

        try
            try
                File.WriteAllBytes(source, [| 0uy |])
                links.Create link source
                true
            with
            | :? IOException
            | :? UnauthorizedAccessException -> false
        finally
            tryDelete source
            tryDelete link

    /// Reads the stream once, writing it to a temp file while hashing it, then makes the temp file the blob for
    /// that hash, or discards it when the blob already exists. The source stream is not disposed.
    let storeFromStream
        (store: StoreLayout)
        (content: Stream)
        (onBytesWritten: (int64 -> unit) voption)
        : struct (ContentHash * int64) =
        let temp = stagingPath store
        let source = new BufferedStream(content, bufferSize) // not disposed: that would dispose the caller's stream

        let struct (hash, size) =
            use tempFile = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)
            use sink = new BufferedStream(tempFile, bufferSize)
            use tee = new TeeStream(source, sink, onBytesWritten)
            let hash = Hashing.hashStream tee
            struct (hash, tee.TotalBytesCopied)

        let blob = blobPath store hash

        if File.Exists blob then
            File.Delete temp
        else
            Directory.CreateDirectory(Path.GetDirectoryName blob |> nonNull) |> ignore

            try
                File.Move(temp, blob)
            with :? IOException when File.Exists blob ->
                // A concurrent store of the same content won the race; this copy is redundant.
                File.Delete temp

        struct (hash, size)

    let hasContent (store: StoreLayout) (hash: ContentHash) : bool = File.Exists(blobPath store hash)

    let private copyWithProgress (source: string) (destination: string) (onBytesCopied: (int64 -> unit) voption) : unit =
        use input = new BufferedStream(File.OpenRead source, bufferSize)
        use output = new BufferedStream(new FileStream(destination, FileMode.CreateNew, FileAccess.Write), bufferSize)
        use tee = new TeeStream(input, output, onBytesCopied)
        tee.CopyTo Stream.Null

    /// Whether `candidate` is the root or inside it, after resolving reparse points on both. Case-insensitive,
    /// like the file systems the tool targets, and safe against a trailing separator.
    let private isWithinRoot (root: string) (candidate: string) : bool =
        let resolvedRoot = Kernel32.resolveRealPath root

        let rootWithSeparator =
            if resolvedRoot.EndsWith Path.DirectorySeparatorChar then
                resolvedRoot
            else
                resolvedRoot + string Path.DirectorySeparatorChar

        let resolved = Kernel32.resolveRealPath candidate

        resolved.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase)
        || resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)

    /// The one place every mirror write resolves its path: raises when the path would leave the target root, for
    /// example a UNC source that has no mirror location or a path with `..` segments.
    let private resolveMirrorPath (store: StoreLayout) (mirrorPath: MirrorPath) : string =
        let relative = MirrorPath.value mirrorPath
        let combined = Path.Combine(store.MirrorRoot, relative)

        if isWithinRoot store.MirrorRoot combined then
            combined
        else
            raise (
                MirrorPathEscapesTargetRootException(
                    relative,
                    Path.GetFullPath combined,
                    Path.GetFullPath store.MirrorRoot
                )
            )

    /// Whether an absolute path lies inside the mirror, for restore destinations.
    let isWithinMirror (store: StoreLayout) (absolutePath: string) : bool = isWithinRoot store.MirrorRoot absolutePath

    /// Places a stored blob at a mirror path: a hard link (marked read-only, since an in-place edit would rewrite the
    /// blob) or, when hard links are unsupported or this blob has hit its link limit, an independent writable copy.
    /// The staged file replaces any existing entry in one rename. `previous` is the content the path held, whose
    /// blob is re-protected after the read-only attribute was cleared for the overwrite.
    let placeAtMirrorPath
        (links: Links)
        (store: StoreLayout)
        (supportsHardlinks: bool)
        (hash: ContentHash)
        (mirrorPath: MirrorPath)
        (onBytesCopied: (int64 -> unit) voption)
        (previous: ContentHash voption)
        : unit =
        let blob = blobPath store hash

        if not (File.Exists blob) then
            raise (FileNotFoundException($"No stored content for hash '{ContentHash.value hash}'.", blob))

        let destination = resolveMirrorPath store mirrorPath
        Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull) |> ignore
        let staging = stagingPath store

        // Tried afresh for every placement rather than remembered per blob: a blob's link count changes as other
        // paths are moved or removed, so a later attempt can succeed again.
        let placedViaHardlink =
            if supportsHardlinks then
                try
                    links.Create staging blob
                    true
                with :? IOException ->
                    // Most often the 1024-link cap of NTFS: a real copy for this placement keeps the run going.
                    copyWithProgress blob staging onBytesCopied
                    false
            else
                copyWithProgress blob staging onBytesCopied
                false

        let attributes = File.GetAttributes staging

        File.SetAttributes(
            staging,
            if placedViaHardlink then
                attributes ||| FileAttributes.ReadOnly
            else
                attributes &&& ~~~FileAttributes.ReadOnly
        )

        // Move(overwrite) throws on a read-only destination, so it is cleared first.
        clearReadOnly destination
        File.Move(staging, destination, true)

        // When the destination already was a link to this very blob, clearing it cleared the staged file and the
        // blob too (they are one file), so protection is asserted again after the move.
        if placedViaHardlink then
            ensureReadOnly destination

        // The clear above also cleared every other name of the previous content, including its blob.
        match previous with
        | ValueSome content -> ensureReadOnly (blobPath store content)
        | ValueNone -> ()

    /// Relocates a mirror entry. When the source is gone but the destination exists, an earlier interrupted run
    /// already did this move, so it counts as done. The entry keeps the read-only attribute it had.
    let moveMirrorEntry (store: StoreLayout) (fromPath: MirrorPath) (toPath: MirrorPath) : unit =
        let source = resolveMirrorPath store fromPath
        let destination = resolveMirrorPath store toPath

        if not (File.Exists source) && File.Exists destination then
            ()
        else
            Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull) |> ignore
            // Read before clearing the destination: when both are links to one blob, clearing one clears both.
            let sourceWasReadOnly =
                File.Exists source && (File.GetAttributes source).HasFlag FileAttributes.ReadOnly

            clearReadOnly destination
            File.Move(source, destination, true)

            if sourceWasReadOnly then
                ensureReadOnly destination

    /// Removes a mirror entry, then re-protects the blob that its read-only clearing exposed.
    let removeFromMirror (store: StoreLayout) (mirrorPath: MirrorPath) (hash: ContentHash) : unit =
        let path = resolveMirrorPath store mirrorPath

        if File.Exists path then
            clearReadOnly path
            File.Delete path
            ensureReadOnly (blobPath store hash)

    /// Deletes staging files that a crashed run left behind.
    let cleanupOrphanedTemp (store: StoreLayout) : unit =
        if Directory.Exists store.TempRoot then
            for file in Directory.EnumerateFiles store.TempRoot |> Seq.toArray do
                tryDelete file

    /// Opens a stored blob for reading. Used by the parity checks and by later changes (restore).
    let openRead (store: StoreLayout) (hash: ContentHash) : Stream =
        let blob = blobPath store hash

        if not (File.Exists blob) then
            raise (FileNotFoundException($"No stored content for hash '{ContentHash.value hash}'.", blob))

        new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read)

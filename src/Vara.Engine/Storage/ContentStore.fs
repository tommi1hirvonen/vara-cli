namespace Vara.Engine

open System
open System.IO
open Vara.Domain
open System.Threading

/// A mirror path resolved outside the target root. An `IOException`, so the per-operation failure handling that
/// catches an unreadable or unwritable path catches this too, instead of the run aborting.
type MirrorPathEscapesTargetRootException(mirrorPath: string, resolved: string, targetRoot: string) =
    inherit IOException $"Mirror path '{mirrorPath}' resolves to '{resolved}', which is outside the target root '{targetRoot}'."

/// The effect the content store needs that tests vary: making a hard link (`newLink existing`).
/// A test passes a function that raises, in place of the C# test hooks.
type Links = { Create: string -> string -> unit }

/// Where a target's files live: the mirror at the root, blobs and staging under `.vara`.
type StoreLayout =
    { MirrorRoot: string
      VersionsRoot: string
      TempRoot: string
      /// The mirror root with every reparse point followed, ending in a directory separator, so that a containment
      /// test is a prefix test. Resolved on first use and kept: the root does not move during a run, and resolving it
      /// opens a handle, which every mirror write would otherwise pay for again.
      MirrorRootPrefix: Lazy<string>
      /// Forces a written staging file's data to persistent storage (`FlushFileBuffers`). A test replaces it to
      /// observe that it runs before the blob has its content name.
      Flush: FileStream -> unit
      /// One lock per two-character blob directory name, serializing the check and repair of a present blob.
      HashLocks: Lock array }

/// The content-addressed store for one target: a live mirror, deduplicated blobs under `.vara/versions/<hash[0..2]>`,
/// and staging under `.vara/tmp` for atomic writes. Every function takes the layout and the effects it uses.
module ContentStore =
    open FileSupport
    
    // Under 85,000 bytes, so buffers stay off the large object heap.
    let private bufferSize = 81_920

    let layout (targetRoot: string) : StoreLayout =
        { MirrorRoot = targetRoot
          VersionsRoot = Path.Combine(targetRoot, ".vara", "versions")
          TempRoot = Path.Combine(targetRoot, ".vara", "tmp")
          MirrorRootPrefix =
            lazy
                (let resolved = Kernel32.resolveRealPath targetRoot

                 if resolved.EndsWith Path.DirectorySeparatorChar then
                     resolved
                 else
                     resolved + string Path.DirectorySeparatorChar)
          Flush = fun stream -> stream.Flush true
          HashLocks = Array.init 256 (fun _ -> Lock()) }

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

    /// Tries a throwaway hard link in the temp directory. False when the volume or the process cannot make one.
    let probeHardlinkSupport (links: Links) (store: StoreLayout) : bool =
        let source = Path.Combine(store.TempRoot, $"probe-src-{Guid.NewGuid():N}.tmp")
        let link = Path.Combine(store.TempRoot, $"probe-link-{Guid.NewGuid():N}.tmp")

        try
            try
                File.WriteAllBytes(source, [| 0uy |])
                links.Create link source
                true
            with IoFailure _ ->
                false
        finally
            tryDelete source
            tryDelete link

    /// The lock for a content name: its two-character directory name, the store's own fan-out, folded into the table.
    let private lockIndex (hash: ContentHash) : int =
        let text = ContentHash.value hash
        (int text[0] * 31 + int text[1]) % 256

    /// With the blob present: discards the staging file when the blob has the size of the content just read, and
    /// otherwise overwrites the blob in place from the staging file. In place keeps every hard link to the blob seeing
    /// the repaired content. The read-only attribute is cleared for the overwrite and always restored. On a failure
    /// the staging file is deleted and the exception propagates; the blob stays damaged and the next store repairs it.
    let private repairIfDamaged (store: StoreLayout) (temp: string) (blob: string) (size: int64) : unit =
        try
            if FileInfo(blob).Length <> size then
                let attributes = File.GetAttributes blob
                clearReadOnly blob

                try
                    use staged = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read)
                    use target = new FileStream(blob, FileMode.Create, FileAccess.Write, FileShare.None)
                    staged.CopyTo(target, bufferSize)
                    target.Flush()
                    store.Flush target
                finally
                    if attributes.HasFlag FileAttributes.ReadOnly then
                        ensureReadOnly blob
        finally
            tryDelete temp

    /// Reads the stream once, writing it to a temp file while hashing it. The temp file is flushed to persistent
    /// storage before it takes its content name, so a power loss cannot leave a truncated blob under a valid name.
    /// It then becomes the blob for that hash, or is discarded when a blob of the same size already exists. A present
    /// blob of a different size is damaged and is overwritten in place, so every hard link to it sees the complete
    /// content; the check and the repair run under a lock per content name, so parallel stores of the same content
    /// never rewrite the blob at the same time and none reads it while it is rewritten.
    /// The source stream is not disposed.
    let storeFromStream
        (store: StoreLayout)
        (content: Stream)
        (onBytesWritten: (int64 -> unit) voption)
        : struct (ContentHash * int64) =
        let temp = stagingPath store

        let struct (hash, size) =
            // No buffering of its own: the hash reads, and so the tee writes, in large chunks, which a buffer would only copy.
            use tempFile = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 0)
            use tee = new TeeStream(content, tempFile, onBytesWritten)
            let hash = Hashing.hashStream tee
            store.Flush tempFile
            struct (hash, tee.TotalBytesCopied)

        let blob = blobPath store hash

        if File.Exists blob then
            lock store.HashLocks[lockIndex hash] (fun () -> repairIfDamaged store temp blob size)
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
        use input = File.OpenRead source
        use output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 0)
        use tee = new TeeStream(input, output, onBytesCopied)
        tee.CopyTo(Stream.Null, bufferSize)

    /// Whether `candidate` is the mirror root or inside it, after resolving reparse points on both. Case-insensitive,
    /// like the file systems the tool targets, and safe against a trailing separator. The root itself has no trailing
    /// separator in `candidate`, so it is the one case that matches the prefix one character short.
    let private isWithinMirrorRoot (store: StoreLayout) (candidate: string) : bool =
        let prefix = store.MirrorRootPrefix.Value
        let resolved = Kernel32.resolveRealPath candidate

        resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        || resolved.Length = prefix.Length - 1 && prefix.StartsWith(resolved, StringComparison.OrdinalIgnoreCase)

    /// The one place every mirror write resolves its path: raises when the path would leave the target root, for
    /// example a UNC source that has no mirror location or a path with `..` segments.
    let private resolveMirrorPath (store: StoreLayout) (mirrorPath: MirrorPath) : string =
        let relative = MirrorPath.value mirrorPath
        let combined = Path.Combine(store.MirrorRoot, relative)

        if isWithinMirrorRoot store combined then
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
    let isWithinMirror (store: StoreLayout) (absolutePath: string) : bool = isWithinMirrorRoot store absolutePath

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

    /// The name of every blob in the store. A name that cannot be a hash (empty) is not one.
    let storedHashes (store: StoreLayout) : Set<ContentHash> =
        if Directory.Exists store.VersionsRoot then
            Directory.EnumerateFiles(store.VersionsRoot, "*", SearchOption.AllDirectories)
            |> Seq.choose (fun path -> Path.GetFileName path |> ContentHash.tryCreate |> ValueOption.toOption)
            |> Set.ofSeq
        else
            Set.empty

    /// Deletes a blob. The blob can be read-only (it is while a mirror entry links to it), so the attribute is cleared
    /// first; nothing is restored afterwards, because a prune deletes only blobs that nothing references. A blob that
    /// is already gone is not an error.
    let deleteBlob (store: StoreLayout) (hash: ContentHash) : unit =
        let blob = blobPath store hash

        if File.Exists blob then
            clearReadOnly blob
            File.Delete blob

    /// Opens a stored blob for reading. Used by the parity checks and by later changes (restore).
    let openRead (store: StoreLayout) (hash: ContentHash) : Stream =
        let blob = blobPath store hash

        if not (File.Exists blob) then
            raise (FileNotFoundException($"No stored content for hash '{ContentHash.value hash}'.", blob))

        new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read)

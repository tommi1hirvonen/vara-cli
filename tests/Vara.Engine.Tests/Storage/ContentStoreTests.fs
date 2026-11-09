/// Scenario tests for the content store, named after the scenarios of openspec/specs/backup-execution/spec.md.
module Vara.Engine.Tests.ContentStoreTests

open System
open System.IO
open System.Text
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests

let private mirror (path: string) : MirrorPath = MirrorPath.ofString path

let private newStore (root: string) : StoreLayout =
    let store = ContentStore.layout (Path.Combine(root, "target"))
    ContentStore.ensureDirectories store
    store

/// Stores the text as a blob and returns its hash.
let private store (layout: StoreLayout) (text: string) : ContentHash =
    use stream = new MemoryStream(Encoding.UTF8.GetBytes text)
    let struct (hash, _) = ContentStore.storeFromStream layout stream ValueNone
    hash

let private place (layout: StoreLayout) (links: Links) (supportsHardlinks: bool) (hash: ContentHash) (path: string) (previous: ContentHash voption) =
    ContentStore.placeAtMirrorPath links layout supportsHardlinks hash (mirror path) ValueNone previous

let private mirrorFile (layout: StoreLayout) (path: string) : string = Path.Combine(layout.MirrorRoot, path)

let private isReadOnly (path: string) : bool = (File.GetAttributes path).HasFlag FileAttributes.ReadOnly

let private failingLinks: Links =
    { Create = fun _ _ -> raise (IOException "Simulated hard-link limit.") }

let private realLinks = ContentStore.realLinks

module ``Content deduplication`` =
    [<Fact>]
    let ``Identical content in two files`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let first = store layout (String('x', 5000))
            let second = store layout (String('x', 5000))
            Assert.Equal(first, second)
            let blobs = Directory.EnumerateFiles(layout.VersionsRoot, "*", SearchOption.AllDirectories) |> Seq.length
            Assert.Equal(1, blobs)
            // The staging directory holds nothing once both are stored.
            Assert.Empty(Directory.EnumerateFiles layout.TempRoot))

    [<Fact>]
    let ``Storing reports the size and each chunk`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let data = Encoding.UTF8.GetBytes(String('y', 200_000))
            use stream = new MemoryStream(data)
            let reported = ref 0L
            let struct (hash, size) = ContentStore.storeFromStream layout stream (ValueSome(fun n -> reported.Value <- reported.Value + n))
            Assert.Equal(200_000L, size)
            Assert.Equal(200_000L, reported.Value)
            Assert.True(ContentStore.hasContent layout hash)
            use blob = ContentStore.openRead layout hash
            use copy = new MemoryStream()
            blob.CopyTo copy
            Assert.Equal<byte[]>(data, copy.ToArray()))

module ``Graceful degradation on filesystems without hardlink support`` =
    [<Fact>]
    let ``exFAT target`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks false hash "C\\a.txt" ValueNone
            place layout realLinks false hash "C\\b.txt" ValueNone

            // A full one-to-one copy of the mirror, one stored blob, and no read-only protection needed.
            Assert.Equal("content", File.ReadAllText(mirrorFile layout "C\\a.txt"))
            Assert.Equal("content", File.ReadAllText(mirrorFile layout "C\\b.txt"))
            Assert.False(isReadOnly (mirrorFile layout "C\\a.txt"))
            Assert.Equal(1, Directory.EnumerateFiles(layout.VersionsRoot, "*", SearchOption.AllDirectories) |> Seq.length))

    [<Fact>]
    let ``The probe reports hard-link support from the link effect`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            Assert.True(ContentStore.probeHardlinkSupport realLinks layout)
            Assert.False(ContentStore.probeHardlinkSupport failingLinks layout)
            // The probe removes its throwaway files either way.
            Assert.Empty(Directory.EnumerateFiles layout.TempRoot))

module ``Graceful degradation when a blob's hard-link limit is reached`` =
    [<Fact>]
    let ``Blob already referenced at the hard-link limit`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            // The link effect fails as NTFS does at its per-file cap; placement still succeeds with a copy.
            place layout failingLinks true hash "C\\a.txt" ValueNone
            Assert.Equal("content", File.ReadAllText(mirrorFile layout "C\\a.txt"))
            Assert.False(isReadOnly (mirrorFile layout "C\\a.txt")))

    [<Fact>]
    let ``Failure recovers on a later run without permanent degradation`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout failingLinks true hash "C\\a.txt" ValueNone
            // A later placement attempts the hard link again instead of assuming a copy is needed.
            place layout realLinks true hash "C\\b.txt" ValueNone
            Assert.True(isReadOnly (mirrorFile layout "C\\b.txt")))

module ``Hardlinked mirror entries are read-only`` =
    [<Fact>]
    let ``File placed via hardlink is read-only`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks true hash "C\\a.txt" ValueNone
            let path = mirrorFile layout "C\\a.txt"
            Assert.True(isReadOnly path)
            Assert.Throws<UnauthorizedAccessException>(fun () -> File.WriteAllText(path, "edited")) |> ignore
            Assert.Equal("content", File.ReadAllText path))

    [<Fact>]
    let ``File placed via copy fallback remains writable`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks false hash "C\\a.txt" ValueNone
            let path = mirrorFile layout "C\\a.txt"
            File.WriteAllText(path, "edited")
            Assert.Equal("edited", File.ReadAllText path)
            // Editing the copy cannot reach the blob.
            use blob = ContentStore.openRead layout hash
            use reader = new StreamReader(blob)
            Assert.Equal("content", reader.ReadToEnd()))

    [<Fact>]
    let ``Placement kind changes from hardlink to copy fallback between runs`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let first = store layout "first"
            let second = store layout "second"
            place layout realLinks true first "C\\a.txt" ValueNone
            place layout failingLinks true second "C\\a.txt" (ValueSome first)
            let path = mirrorFile layout "C\\a.txt"
            Assert.False(isReadOnly path)
            Assert.Equal("second", File.ReadAllText path)
            // The superseded blob keeps its protection.
            Assert.True(isReadOnly (ContentStore.blobPath layout first)))

    [<Fact>]
    let ``Placement kind changes from copy fallback to hardlink between runs`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let first = store layout "first"
            let second = store layout "second"
            place layout realLinks false first "C\\a.txt" ValueNone
            place layout realLinks true second "C\\a.txt" (ValueSome first)
            Assert.True(isReadOnly (mirrorFile layout "C\\a.txt")))

    [<Fact>]
    let ``Moving or removing a read-only hardlinked mirror entry still succeeds`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks true hash "C\\a.txt" ValueNone
            ContentStore.moveMirrorEntry layout (mirror "C\\a.txt") (mirror "C\\sub\\b.txt")
            Assert.False(File.Exists(mirrorFile layout "C\\a.txt"))
            Assert.True(isReadOnly (mirrorFile layout "C\\sub\\b.txt"))
            ContentStore.removeFromMirror layout (mirror "C\\sub\\b.txt") hash
            Assert.False(File.Exists(mirrorFile layout "C\\sub\\b.txt")))

    [<Fact>]
    let ``Deleting or changing one hardlinked mirror path does not weaken protection on a sibling sharing the same content`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let shared = store layout "shared"
            let other = store layout "other"
            place layout realLinks true shared "C\\a.txt" ValueNone
            place layout realLinks true shared "C\\b.txt" ValueNone
            ContentStore.removeFromMirror layout (mirror "C\\a.txt") shared
            Assert.True(isReadOnly (mirrorFile layout "C\\b.txt"))
            place layout realLinks true shared "C\\c.txt" ValueNone
            place layout realLinks true other "C\\b.txt" (ValueSome shared)
            Assert.True(isReadOnly (mirrorFile layout "C\\c.txt"))
            Assert.True(isReadOnly (ContentStore.blobPath layout shared)))

    [<Fact>]
    let ``Re-placing a mirror path with the content it already holds keeps it protected`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks true hash "C\\a.txt" ValueNone
            place layout realLinks true hash "C\\a.txt" ValueNone
            let path = mirrorFile layout "C\\a.txt"
            Assert.True(isReadOnly path)
            Assert.Throws<UnauthorizedAccessException>(fun () -> File.WriteAllText(path, "edited")) |> ignore
            Assert.True(isReadOnly (ContentStore.blobPath layout hash)))

    [<Fact>]
    let ``Relocating a read-only mirror entry onto an already-occupied mirror path succeeds`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let moving = store layout "moving"
            let leftover = store layout "leftover"
            place layout realLinks true moving "C\\old.txt" ValueNone
            place layout realLinks true leftover "C\\new.txt" ValueNone
            ContentStore.moveMirrorEntry layout (mirror "C\\old.txt") (mirror "C\\new.txt")
            Assert.Equal("moving", File.ReadAllText(mirrorFile layout "C\\new.txt"))
            Assert.True(isReadOnly (mirrorFile layout "C\\new.txt")))

    [<Fact>]
    let ``A move already completed by an interrupted run counts as done`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks true hash "C\\new.txt" ValueNone
            ContentStore.moveMirrorEntry layout (mirror "C\\old.txt") (mirror "C\\new.txt")
            Assert.Equal("content", File.ReadAllText(mirrorFile layout "C\\new.txt")))

module ``Mirror writes are confined to the target root`` =
    let private escapes (action: unit -> unit) =
        Assert.Throws<MirrorPathEscapesTargetRootException> action |> ignore

    [<Fact>]
    let ``Placing a file whose mirror path would escape the target root`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            escapes (fun () -> place layout realLinks true hash "..\\outside.txt" ValueNone)
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")))
            // Escaping is an IOException, so per-operation failure handling treats it like any unwritable path.
            Assert.IsAssignableFrom<IOException>(MirrorPathEscapesTargetRootException("a", "b", "c")) |> ignore)

    [<Fact>]
    let ``Moving a file whose resolved mirror path would escape the target root`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks true hash "C\\a.txt" ValueNone
            escapes (fun () -> ContentStore.moveMirrorEntry layout (mirror "C\\a.txt") (mirror "..\\moved.txt"))
            escapes (fun () -> ContentStore.moveMirrorEntry layout (mirror "..\\gone.txt") (mirror "C\\b.txt"))
            Assert.True(File.Exists(mirrorFile layout "C\\a.txt"))
            Assert.False(File.Exists(Path.Combine(root, "moved.txt"))))

    [<Fact>]
    let ``Removing a file whose resolved mirror path would escape the target root`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            let outside = Path.Combine(root, "outside.txt")
            File.WriteAllText(outside, "keep")
            escapes (fun () -> ContentStore.removeFromMirror layout (mirror "..\\outside.txt") hash)
            Assert.Equal("keep", File.ReadAllText outside))

    [<Fact>]
    let ``Source's original file is never touched by a refused mirror write`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            // A rooted mirror path replaces the root when combined: it resolves to the source file itself.
            let source = Path.Combine(root, "source.txt")
            File.WriteAllText(source, "original")
            let before = File.GetAttributes source
            escapes (fun () -> place layout realLinks true hash source ValueNone)
            escapes (fun () -> ContentStore.removeFromMirror layout (mirror source) hash)
            Assert.Equal("original", File.ReadAllText source)
            Assert.Equal(before, File.GetAttributes source))

    [<Fact>]
    let ``A path that stays inside the root is accepted whichever separator it uses`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            place layout realLinks true hash "C/sub/a.txt" ValueNone
            Assert.True(File.Exists(mirrorFile layout "C\\sub\\a.txt")))

    [<Fact>]
    let ``A path through a junction out of the root is refused`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let hash = store layout "content"
            let outside = Path.Combine(root, "outside")
            Directory.CreateDirectory outside |> ignore
            InteropTests.createJunction (Path.Combine(layout.MirrorRoot, "link")) outside
            escapes (fun () -> place layout realLinks true hash "link\\a.txt" ValueNone)
            Assert.Empty(Directory.EnumerateFileSystemEntries outside))

module ``Crash and interruption safety`` =
    [<Fact>]
    let ``Orphaned staging files are cleaned up`` () =
        TempDir.using (fun root ->
            let layout = newStore root
            let orphan = Path.Combine(layout.TempRoot, "orphan")
            File.WriteAllText(orphan, "x")
            File.SetAttributes(orphan, FileAttributes.ReadOnly)
            ContentStore.cleanupOrphanedTemp layout
            Assert.Empty(Directory.EnumerateFiles layout.TempRoot))

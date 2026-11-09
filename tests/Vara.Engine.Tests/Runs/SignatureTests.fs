module Vara.Engine.Tests.SignatureTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests

/// Counts the bytes read through it, per file, so a test can see how much of each file the engine read.
type private CountingStream(inner: Stream, onRead: int -> unit) =
    inherit Stream()
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = inner.Length

    override _.Position
        with get () = inner.Position
        and set value = inner.Position <- value

    override _.Read(buffer: Span<byte>) : int =
        let read = inner.Read buffer
        onRead read
        read

    override this.Read(buffer: byte[], offset: int, count: int) : int =
        this.Read(Span<byte>(buffer, offset, count))

    override _.Flush() = ()
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())

    override _.Dispose(disposing: bool) =
        if disposing then
            inner.Dispose()

        base.Dispose disposing

let private size = 1_000_000

/// Content of `size` bytes whose first `window` bytes are `lead` and whose tail is `tail`.
let private content (lead: char) (tail: char) : byte[] =
    let bytes = Array.create size (byte tail)
    Array.fill bytes 0 QuickSignature.windowSizeBytes (byte lead)
    bytes

let private hashOf (bytes: byte[]) : ContentHash =
    use stream = new MemoryStream(bytes)
    Hashing.hashStream stream

let private quickOf (bytes: byte[]) : QuickSignature =
    Signatures.quickOf (Array.sub bytes 0 QuickSignature.windowSizeBytes)

let private candidate (path: string) (bytes: byte[]) (quick: QuickSignature voption) : MirrorPath * FileContent =
    MirrorPath.ofString path,
    { Hash = hashOf bytes
      Size = int64 bytes.Length
      SourceModifiedAt = DateTimeOffset.UnixEpoch
      Quick = quick }

let private add (path: string) (length: int64) : ScannedFile =
    { FilePath = MirrorPath.ofString ("C\\" + Path.GetFileName path)
      FileSource = SourcePath.ofString path
      Size = length
      ModifiedAt = DateTimeOffset.UnixEpoch }

let private candidates (entries: (MirrorPath * FileContent) list) : MoveCandidates =
    let map = MoveCandidates()

    for _, content as entry in entries do
        map[content.Size] <- (match map.TryGetValue content.Size with
                              | true, existing -> existing @ [ entry ]
                              | false, _ -> [ entry ])

    map

/// Runs the read, returning the hashes and how many bytes were read from each file name.
let private run (adds: ScannedFile list) (entries: (MirrorPath * FileContent) list) =
    let reads = Dictionary<string, int>()
    let sync = Lock()

    let openRead (path: string) : Stream =
        new CountingStream(
            File.OpenRead path,
            fun count ->
                lock sync (fun () ->
                    let name = Path.GetFileName path
                    reads[name] <- (match reads.TryGetValue name with | true, n -> n + count | false, _ -> count))
        )

    let hashes = Signatures.readFullHashes openRead 4 (candidates entries) adds
    hashes, reads

module ``Move detection avoids unbounded reads for non-matching candidates`` =
    [<Fact>]
    let ``Same-size, differing-content candidate ruled out cheaply`` () =
        TempDir.using (fun root ->
            let recorded = content 'a' 'x'
            let added = content 'b' 'x' // same size, different first window
            let path = Path.Combine(root, "added.bin")
            File.WriteAllBytes(path, added)

            let hashes, reads =
                run [ add path (int64 size) ] [ candidate "C\\old.bin" recorded (ValueSome(quickOf recorded)) ]

            Assert.Empty hashes
            // Only the bounded prefix was read, never the whole megabyte.
            let bytesRead = reads["added.bin"]
            Assert.True(bytesRead <= QuickSignature.windowSizeBytes, $"read {bytesRead} bytes"))

    [<Fact>]
    let ``Full content match still required to confirm a move`` () =
        TempDir.using (fun root ->
            let recorded = content 'a' 'x'
            // Same first window, different tail: the prefix cannot rule the candidate out, the full hash does.
            let added = content 'a' 'y'
            let path = Path.Combine(root, "added.bin")
            File.WriteAllBytes(path, added)

            let hashes, reads =
                run [ add path (int64 size) ] [ candidate "C\\old.bin" recorded (ValueSome(quickOf recorded)) ]

            Assert.Equal(size, reads["added.bin"])
            Assert.Equal(hashOf added, hashes["C\\added.bin"])
            Assert.NotEqual(hashOf recorded, hashes["C\\added.bin"])

            // An identical file produces the hash the candidate recorded, so the planner can declare a move.
            let identical = Path.Combine(root, "same.bin")
            File.WriteAllBytes(identical, recorded)

            let hashes, _ =
                run [ add identical (int64 size) ] [ candidate "C\\old.bin" recorded (ValueSome(quickOf recorded)) ]

            Assert.Equal(hashOf recorded, hashes["C\\same.bin"]))

    [<Fact>]
    let ``Recorded signature unavailable for a candidate`` () =
        TempDir.using (fun root ->
            let recorded = content 'a' 'x'
            let added = content 'b' 'x'
            let path = Path.Combine(root, "added.bin")
            File.WriteAllBytes(path, added)

            // No usable signature: the candidate cannot be ruled out, so the file is read in full and hashed.
            let hashes, reads =
                run [ add path (int64 size) ] [ candidate "C\\old.bin" recorded ValueNone ]

            Assert.Equal(size, reads["added.bin"])
            Assert.Equal(hashOf added, hashes["C\\added.bin"])

            let otherScheme = ValueSome { QuickHash = "whatever"; QuickScheme = QuickSignature.currentScheme + 1 }
            let hashes, _ = run [ add path (int64 size) ] [ candidate "C\\old.bin" recorded otherScheme ]
            Assert.True(hashes.ContainsKey "C\\added.bin"))

module ``Signature reading`` =
    [<Fact>]
    let ``Many files are read in parallel and each is bounded`` () =
        TempDir.using (fun root ->
            let recorded = content 'a' 'x'
            let adds =
                [ for i in 1..20 ->
                    let path = Path.Combine(root, $"f{i}.bin")
                    File.WriteAllBytes(path, content 'b' (char (int 'a' + i % 20)))
                    add path (int64 size) ]

            let hashes, reads = run adds [ candidate "C\\old.bin" recorded (ValueSome(quickOf recorded)) ]
            Assert.Empty hashes
            Assert.Equal(20, reads.Count)
            Assert.All(reads.Values, fun bytes -> Assert.True(bytes <= QuickSignature.windowSizeBytes)))

    [<Fact>]
    let ``An unreadable file is left out so it is planned as a plain add`` () =
        TempDir.using (fun root ->
            let recorded = content 'a' 'x'
            let hashes, _ =
                run [ add (Path.Combine(root, "missing.bin")) (int64 size) ] [ candidate "C\\old.bin" recorded ValueNone ]

            Assert.Empty hashes)

    [<Fact>]
    let ``A file shorter than the window is read once`` () =
        TempDir.using (fun root ->
            let bytes = Encoding.UTF8.GetBytes "short"
            let path = Path.Combine(root, "short.txt")
            File.WriteAllBytes(path, bytes)

            let hashes, _ =
                run [ add path (int64 bytes.Length) ] [ candidate "C\\old.txt" bytes (ValueSome(Signatures.quickOf bytes)) ]

            Assert.Equal(hashOf bytes, hashes["C\\short.txt"]))

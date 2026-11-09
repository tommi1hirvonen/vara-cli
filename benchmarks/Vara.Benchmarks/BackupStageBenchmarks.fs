namespace Vara.Benchmarks

open System
open System.IO
open BenchmarkDotNet.Attributes
open Vara.Domain
open Vara.Engine

type DiffScenario =
    | Unchanged = 0
    | Added = 1
    | Changed = 2
    | Deleted = 3
    | Mixed = 4

/// Comparing a scan with the manifest's state, over prepared inputs: unchanged, added, changed, deleted and mixed
/// file-state distributions.
[<MemoryDiagnoser>]
type BackupDifferBenchmarks() =
    let mutable scanned: ScanEntry[] = [||]
    let mutable tracked = TrackedPaths StringComparer.OrdinalIgnoreCase

    [<Params(256, 4096)>]
    member val EntryCount = 0 with get, set

    [<Params(DiffScenario.Unchanged, DiffScenario.Added, DiffScenario.Changed, DiffScenario.Deleted, DiffScenario.Mixed)>]
    member val Scenario = DiffScenario.Unchanged with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        let entries = ResizeArray<ScanEntry> this.EntryCount
        let state = TrackedPaths StringComparer.OrdinalIgnoreCase
        let modifiedAt = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        let hash = (ContentHash.tryCreate "benchmark-hash").Value

        for i in 0 .. this.EntryCount - 1 do
            let relativePath = $"source\\folder-{i % 32:D2}\\file-{i:D6}.bin"
            let absolutePath = Path.Combine(Path.GetTempPath(), "vara-benchmark-source", $"file-{i:D6}.bin")

            // 0 unchanged, 1 added, 2 changed, 3 deleted.
            let kind =
                match this.Scenario with
                | DiffScenario.Mixed -> i % 4
                | DiffScenario.Unchanged -> 0
                | DiffScenario.Added -> 1
                | DiffScenario.Changed -> 2
                | _ -> 3

            if kind <= 2 then
                entries.Add(
                    ScanEntry.File
                        { FilePath = MirrorPath.ofString relativePath
                          FileSource = SourcePath.ofString absolutePath
                          Size = if kind = 2 then 4097L else 4096L
                          ModifiedAt = modifiedAt }
                )

            if kind <> 1 then
                state.Add(
                    relativePath,
                    TrackedState.Regular
                        { Hash = hash
                          Size = 4096L
                          SourceModifiedAt = modifiedAt
                          Quick = ValueNone }
                )

        scanned <- entries.ToArray()
        tracked <- state

    [<Benchmark>]
    member _.Diff() : DiffResult = Diff.run tracked scanned

/// Move detection for a same-size candidate: the real file reads and hashing are part of the measured operation, both
/// when the contents match and when the quick hash rules out a match.
[<MemoryDiagnoser>]
type BackupPlannerBenchmarks() =
    let mutable scratchDirectory = ""
    let mutable diff: DiffResult = { Pending = []; Deleted = []; Failures = [] }
    let mutable tracked = TrackedPaths StringComparer.OrdinalIgnoreCase

    [<Params(262144, 4194304)>]
    member val ContentSize = 0 with get, set

    [<Params(true, false)>]
    member val IsMoveMatch = false with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        scratchDirectory <- BenchmarkFiles.createScratchDirectory ()
        let priorContent = BenchmarkFiles.createDeterministicBytes this.ContentSize 1

        let addedContent =
            if this.IsMoveMatch then priorContent else BenchmarkFiles.createDeterministicBytes this.ContentSize 2

        let addedPath = Path.Combine(scratchDirectory, "new-location.bin")
        File.WriteAllBytes(addedPath, addedContent)

        let fullHash =
            use stream = new MemoryStream(priorContent, false)
            Hashing.hashStream stream

        let quick =
            use stream = new MemoryStream(priorContent, false)
            Signatures.quickOf (Signatures.readPrefix stream)

        let oldPath = MirrorPath.ofString "source\\old-location.bin"
        let info = FileInfo addedPath
        let modifiedAt = DateTimeOffset info.LastWriteTimeUtc

        let scannedFile: ScannedFile =
            { FilePath = MirrorPath.ofString "source\\new-location.bin"
              FileSource = SourcePath.ofString addedPath
              Size = info.Length
              ModifiedAt = modifiedAt }

        let state =
            TrackedState.Regular
                { Hash = fullHash
                  Size = int64 this.ContentSize
                  SourceModifiedAt = modifiedAt
                  Quick = ValueSome quick }

        let paths = TrackedPaths StringComparer.OrdinalIgnoreCase
        paths[MirrorPath.value oldPath] <- state
        tracked <- paths

        diff <-
            { Pending = [ PendingChange.Add scannedFile ]
              Deleted = [ struct (oldPath, state) ]
              Failures = [] }

    [<GlobalCleanup>]
    member _.Cleanup() : unit = BenchmarkFiles.deleteDirectory scratchDirectory

    [<Benchmark>]
    member _.PlanMoveDetection() : BackupPlan =
        let candidates = Planning.moveCandidates diff.Deleted
        let adds = Planning.sizeMatchedAdds candidates diff.Pending
        let hashes = Signatures.readFullHashes Signatures.openFile 1 candidates adds
        Planning.plan tracked diff candidates hashes

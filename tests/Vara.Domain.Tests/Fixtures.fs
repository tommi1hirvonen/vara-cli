/// Small builders for the domain values the engine tests need.
module Vara.Domain.Tests.Fixtures

open System
open Vara.Domain

let at (minutes: int) : DateTimeOffset =
    DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(float minutes)

let hash (text: string) : ContentHash = (ContentHash.tryCreate text).Value

let content (hashText: string) (size: int64) (minutes: int) : FileContent =
    { Hash = hash hashText
      Size = size
      SourceModifiedAt = at minutes
      Quick = ValueNone }

let mirror (path: string) : MirrorPath = MirrorPath.ofString path

/// A scanned regular file whose source path is the mirror path under `S:\`.
let file (path: string) (size: int64) (minutes: int) : ScannedFile =
    { FilePath = mirror path
      FileSource = SourcePath.ofString ("S:\\" + path)
      Size = size
      ModifiedAt = at minutes }

let link (path: string) (target: string voption) : ScannedLink =
    { LinkPath = mirror path
      LinkSource = SourcePath.ofString ("S:\\" + path)
      LinkTarget = target }

let failure (covers: string) (reason: ScanFailureReason) : ScanFailure =
    { Reported = covers
      Covers = mirror covers
      Reason = reason }

let tracked (entries: (string * TrackedState) list) : TrackedPaths =
    let paths = TrackedPaths StringComparer.OrdinalIgnoreCase

    for path, state in entries do
        paths[path] <- state

    paths

let regular (hashText: string) (size: int64) (minutes: int) : TrackedState =
    TrackedState.Regular(content hashText size minutes)

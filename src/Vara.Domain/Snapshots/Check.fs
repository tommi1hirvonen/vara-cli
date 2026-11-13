namespace Vara.Domain

/// What a check saw of one referenced blob. Quick mode never reads a blob, so it has nothing to recompute.
[<RequireQualifiedAccess>]
type Observation =
    | Absent
    | Present of recomputed: ContentHash voption

[<RequireQualifiedAccess>]
type Verdict =
    | Intact
    | Missing
    | Corrupt

/// A missing or corrupt blob, with some of the paths that reference it so that the finding can be acted on.
type Finding =
    { Hash: ContentHash
      /// At most `Check.maxPaths` of them.
      Paths: string list
      /// How many paths reference the blob, including those not listed.
      PathCount: int }

/// What a check found. While it is folded, the findings are newest first; `Check.finish` puts them in order.
type CheckResult =
    { Checked: int
      Missing: Finding list
      Corrupt: Finding list
      /// Blobs in the store that no snapshot references. Informational: a check never deletes them.
      Orphaned: ContentHash list
      /// A stop was requested before every referenced blob was verified.
      Stopped: bool }

/// Verification of stored content against the manifest (backup-integrity). The verdict and the fold decide; the shell
/// reads the blobs and hashes them.
module Check =
    /// The most paths listed for a finding, so that a blob shared by many files does not flood the report.
    let maxPaths = 5

    /// The verdict on one blob. A blob that is not in the store is missing whatever the mode; in quick mode a present
    /// blob is intact without being read; otherwise its recomputed hash must equal the recorded one.
    let verdict (quick: bool) (expected: ContentHash) (observed: Observation) : Verdict =
        match observed with
        | Observation.Absent -> Verdict.Missing
        | Observation.Present recomputed ->
            match quick, recomputed with
            | true, _ -> Verdict.Intact
            | false, ValueSome actual -> if actual = expected then Verdict.Intact else Verdict.Corrupt
            | false, ValueNone -> Verdict.Corrupt

    let empty: CheckResult =
        { Checked = 0
          Missing = []
          Corrupt = []
          Orphaned = []
          Stopped = false }

    /// Counts one verified blob. A problem becomes a finding, whose paths are looked up only then.
    let add (pathsOf: ContentHash -> string list) (hash: ContentHash) (verdict: Verdict) (result: CheckResult) : CheckResult =
        let finding () =
            let paths = pathsOf hash

            { Hash = hash
              Paths = List.truncate maxPaths paths
              PathCount = List.length paths }

        let counted = { result with Checked = result.Checked + 1 }

        match verdict with
        | Verdict.Intact -> counted
        | Verdict.Missing -> { counted with Missing = finding () :: counted.Missing }
        | Verdict.Corrupt -> { counted with Corrupt = finding () :: counted.Corrupt }

    /// Closes the fold: the findings in the order they were found, the orphans, and whether it was stopped.
    let finish (orphaned: ContentHash list) (stopped: bool) (result: CheckResult) : CheckResult =
        { result with
            Missing = List.rev result.Missing
            Corrupt = List.rev result.Corrupt
            Orphaned = orphaned
            Stopped = stopped }

    /// Whether the check found a blob that is missing or corrupt. Orphans are not problems.
    let hasProblems (result: CheckResult) : bool =
        not (List.isEmpty result.Missing && List.isEmpty result.Corrupt)

    /// A problem makes the run a partial failure, with the code that is distinct from a hard error. A stopped run is a
    /// partial failure too, so that a script cannot mistake an incomplete check for a clean one. The spec says a
    /// cancelled check exits successfully; this is a known gap, and this is the only place to change if it is resolved
    /// the other way.
    let outcome (result: CheckResult) : Outcome =
        if hasProblems result || result.Stopped then Outcome.PartialFailure else Outcome.Success

namespace Vara.Domain

open System
open System.Collections.Generic

/// What comparing a scan with the manifest found.
type DiffResult =
    { /// Entries that differ from the manifest, in scan order.
      Pending: PendingChange list
      /// Tracked paths the scan did not see and no failure hides, in the manifest's order. This order is the
      /// tie-break for move resolution.
      Deleted: struct (MirrorPath * TrackedState) list
      /// Every failure the scan reported, in the order it reported them.
      Failures: ScanFailure list }

/// Compares a scan with the manifest's current state: a size and modification time comparison only, never content.
module Diff =
    let private forward (path: string) : string = path.Replace('\\', '/')

    /// True when `path` equals one of the covered paths or lies beneath one, compared at segment boundaries,
    /// case-insensitively and with either separator: `C\Users\john` hides `C\Users\john\a.txt`, not `C\Users\john2\a.txt`.
    [<TailCall>]
    let rec private isCovered (path: string) (covered: string list) : bool =
        match covered with
        | [] -> false
        | cover :: rest ->
            String.Equals(path, cover, StringComparison.OrdinalIgnoreCase)
            || (path.Length > cover.Length
                && path[cover.Length] = '/'
                && path.StartsWith(cover, StringComparison.OrdinalIgnoreCase))
            || isCovered path rest

    /// The change an entry represents against the manifest, or ValueNone when it is unchanged.
    /// A link already tracked as a link is unchanged whatever its target; a regular file with the same size and
    /// modification time is unchanged; a file over a tracked link is a change that supersedes nothing.
    let classify (tracked: TrackedPaths) (entry: ScanEntry) : PendingChange voption =
        match entry with
        | ScanEntry.Failure _ -> ValueNone
        | ScanEntry.Link link ->
            match tracked.TryGetValue(MirrorPath.value link.LinkPath) with
            | true, TrackedState.Linked _ -> ValueNone
            | _ -> ValueSome(PendingChange.Link link)
        | ScanEntry.File file ->
            match tracked.TryGetValue(MirrorPath.value file.FilePath) with
            | false, _ -> ValueSome(PendingChange.Add file)
            | true, TrackedState.Linked _ -> ValueSome(PendingChange.Change file)
            | true, TrackedState.Regular content ->
                if content.Size = file.Size && content.SourceModifiedAt.Equals file.ModifiedAt then
                    ValueNone
                else
                    ValueSome(PendingChange.Change file)

    /// Walks the scan once: classifies each entry and notes which tracked paths were seen. A tracked path that was
    /// not seen is deleted unless a failure covers it, because then nobody knows whether it still exists.
    /// `tracked` must compare keys case-insensitively. Only the set of seen paths is mutated, and it is local.
    let run (tracked: TrackedPaths) (scanned: ScanEntry seq) : DiffResult =
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let step (struct (pending, failures)) (entry: ScanEntry) =
            let failures =
                match entry with
                | ScanEntry.File file ->
                    seen.Add(MirrorPath.value file.FilePath) |> ignore
                    failures
                | ScanEntry.Link link ->
                    seen.Add(MirrorPath.value link.LinkPath) |> ignore
                    failures
                | ScanEntry.Failure failure -> failure :: failures

            match classify tracked entry with
            | ValueSome change -> struct (change :: pending, failures)
            | ValueNone -> struct (pending, failures)

        let struct (pending, failures) = Seq.fold step (struct ([], [])) scanned
        let failures = List.rev failures
        let covered = failures |> List.map (fun failure -> forward (MirrorPath.value failure.Covers))

        let deleted =
            tracked
            |> Seq.filter (fun (KeyValue(path, _)) ->
                not (seen.Contains path || not covered.IsEmpty && isCovered (forward path) covered))
            |> Seq.map (fun (KeyValue(path, state)) -> struct (MirrorPath.ofString path, state))
            |> Seq.toList

        { Pending = List.rev pending
          Deleted = deleted
          Failures = failures }

namespace Vara.Domain

/// What `vara prune` says, as plain data. The message texts are the C# build's; the spec fixes what is reported and
/// not the words.
module PruneView =
    /// The question asked before snapshots are removed. The answer defaults to no.
    let confirmation (snapshots: int) : string =
        $"This will permanently remove {snapshots} snapshot(s) and any content no longer referenced. Continue?"

    /// The refusal of a session that cannot be asked, when snapshots are eligible and no override was given.
    let confirmationRequired (profile: string) (snapshots: int) : string =
        $"Profile '{profile}' has {snapshots} snapshot(s) eligible for removal. Pass --yes (or -y) to confirm, or re-run interactively to be prompted."

    /// The refusal of a profile that has no retention policy: nothing is deleted.
    let noPolicy (profile: string) : string = $"Profile '{profile}' has no retention policy configured."

    /// What a declined prompt reports: nothing changed, and that is not an error.
    let declined: Display = [ Line.Text(Tone.Neutral, "Prune cancelled: no snapshots removed.") ]

    /// A partial failure when a blob could not be deleted or the run was stopped; otherwise a success.
    let outcome (failed: BlobFailure list) (stopped: bool) : Outcome =
        if List.isEmpty failed && not stopped then Outcome.Success else Outcome.PartialFailure

    /// The summary of a prune that ran: the headline in the outcome's tone, the blobs that could not be deleted below.
    let summary (snapshotsRemoved: int) (blobsRemoved: int) (failed: BlobFailure list) (stopped: bool) : Display =
        let headline =
            $"Removed {snapshotsRemoved} snapshot(s) and {blobsRemoved} unreferenced content blob(s)."

        let headline = if stopped then headline + " Prune cancelled: stopped early." else headline

        let failures =
            match failed with
            | [] -> []
            | _ ->
                Line.Text(Tone.Plain, $"  Failed ({List.length failed}):")
                :: (failed
                    |> List.map (fun failure -> Line.Text(Tone.Plain, $"    - {ContentHash.value failure.Blob}: {failure.Reason}")))

        Line.Text(Display.toneOf (outcome failed stopped), headline) :: failures

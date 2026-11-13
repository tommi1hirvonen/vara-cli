namespace Vara.Domain

/// What `vara check` reports, as plain data (backup-integrity).
module CheckView =
    let private findingLine (label: string) (finding: Finding) : Line =
        let paths = finding.Paths |> String.concat ", "
        let hidden = finding.PathCount - List.length finding.Paths
        let more = if hidden > 0 then $" (+{hidden} more)" else ""
        Line.Text(Tone.Plain, $"  {label}: {ContentHash.value finding.Hash} - {paths}{more}")

    /// The report: the counts in the outcome's tone, then each missing and each corrupt blob with the paths that
    /// reference it. Problems found before a stop are reported exactly as they are for a completed run. An orphan is
    /// counted and never listed, as it is only informational.
    let report (result: CheckResult) : Display =
        let cancelled = if result.Stopped then " Check cancelled: stopped early." else ""

        let headline =
            $"Checked {result.Checked} blob(s): {List.length result.Missing} missing, {List.length result.Corrupt} corrupt, {List.length result.Orphaned} orphaned.{cancelled}"

        Line.Text(Display.toneOf (Check.outcome result), headline)
        :: ((result.Missing |> List.map (findingLine "Missing"))
            @ (result.Corrupt |> List.map (findingLine "Corrupt")))

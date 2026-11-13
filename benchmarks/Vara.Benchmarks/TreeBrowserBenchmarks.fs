namespace Vara.Benchmarks

open BenchmarkDotNet.Attributes
open Vara.Domain

/// The interactive change tree over a synthetic snapshot of many files in about 2,000 directories: opening it, expanding
/// everything, and a run of cursor steps each followed by a frame. The key presses and frames are the browser's own work;
/// nothing is drawn.
[<MemoryDiagnoser>]
type TreeBrowserBenchmarks() =
    let header: Display = [ Line.Text(Tone.Plain, "Snapshot #1") ]
    let mutable rollup: RollupDirectory list = []
    let mutable entries: ChangeEntry[] = [||]
    let mutable expanded: TreeState option = None

    [<Params(20000, 200000)>]
    member val EntryCount = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        entries <-
            Array.init this.EntryCount (fun i ->
                // About 2,000 directories, three levels below the scope root, with the files spread evenly over them.
                let path = $"C\d{i % 20}\e{(i / 20) % 25}\f{(i / 500) % 4}\file{i}.txt"

                let kind =
                    match i % 10 with
                    | 0 -> ChangeKind.Deleted
                    | 1 -> ChangeKind.Changed
                    | _ -> ChangeKind.Added

                { ChangedPath = path
                  ChangedKind = kind
                  MovedFrom = ValueNone })

        rollup <- History.rollupEntries "C" false entries
        expanded <- Some(TreeBrowser.step Key.ExpandAll (TreeBrowser.create header "C" Set.empty (ValueSome 1) rollup entries))

    [<Benchmark>]
    member _.Create() : TreeState = TreeBrowser.create header "C" Set.empty (ValueSome 1) rollup entries

    [<Benchmark>]
    member _.ExpandAll() : Display =
        let state = TreeBrowser.step Key.ExpandAll (TreeBrowser.create header "C" Set.empty (ValueSome 1) rollup entries)
        snd (TreeBrowser.frame 1000 40 state)

    [<Benchmark>]
    member _.TwentyCursorSteps() : int =
        let mutable state = expanded.Value
        let mutable lines = 0

        for _ in 1..20 do
            state <- TreeBrowser.step Key.Down state
            let fitted, display = TreeBrowser.frame 1000 40 state
            state <- fitted
            lines <- lines + display.Length

        lines

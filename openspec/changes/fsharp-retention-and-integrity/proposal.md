## Why

Retention pruning keeps the target's history bounded, and the integrity check verifies that stored content still matches the manifest. Both are maintenance commands over the same manifest and content store.
- **Prune** is driven by a tiered retention policy: daily, weekly, monthly and yearly buckets. That is a rule-heavy, naturally pure computation, and it follows from the profile model built in `fsharp-profile-config`.
- **Check** is a bounded-parallel read workload, with count-based progress.

Design and tasks for this change are written by `fsharp-snapshot-history`'s final task, using the findings of the changes before it.

## What Changes

- Add `vara prune`:
  - a pure retention evaluation that decides which snapshots to keep and which to prune;
  - pruning of manifest rows that never removes a path's current state;
  - garbage collection of content that is no longer referenced;
  - the run lock, progress, and graceful cancellation.
- Add `vara check`: full and quick verification of stored content against the manifest, with a scriptable exit status, progress with an ETA, and graceful cancellation.
- Add the count-based progress display used by both commands.
- As its last task, update the following changes with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

## Functional steering

This change is idiomatic F#, not a translation of the C# code. C# type and method names are not a guide: design from the spec. Each change in the series is applied in a separate session, so every proposal carries these rules.

**Series-wide rules**
- **Modules and data, not classes.** Behavior is functions in modules; data is records and unions. No interfaces, inheritance, service classes or dependency injection. A class is allowed only where .NET requires one: handle wrappers, `IDisposable` resources, and types that a host library demands.
- **Dependencies are parameters.** A function takes the functions it needs, not an object that holds them. Effects that travel together may be grouped as a record of functions, only when three or more are used together. Everything is assembled once, by partial application in the composition root (`Vara.Cli`).
- **Compose, don't sequence.** Build behavior from small functions joined with `|>`, `Result`, `Option` and `Seq` combinators, and a computation expression where a flow reads in order. Put the data argument last so that partial application works.
- **Types carry the rules.** Unions for alternatives, `[<RequireQualifiedAccess>]` on them, records with named fields, single-case unions or units of measure for values that could be confused. Matches are exhaustive with no wildcard on the code's own unions; no `null`; invalid states are unrepresentable.
- **Expected outcomes are values.** Use `Result` or `option` for them. Exceptions are for the unexpected, and are handled at the host boundary.
- **Pure core, effect shell.** Decisions never touch the file system, the clock, randomness or the console. The shell passes in values and performs the effects.
- **Mutation stays inside one function.** It is allowed in a hot loop, a pooled buffer or interop, when a benchmark or the API requires it. It never lives in a field and is never shared between functions. Allocation rules (struct unions, `voption`) change a value's representation, not the architecture.
- **No catch-all modules.** No `Helper`, `Manager`, `Service`, `Util` or `I`-prefixed names. One module per concept, with files in dependency order.
- **Review.** The change's tasks include a convention review, and its last task keeps this section in the proposals that follow, extended with what the change learned.

**Specific to this change**
- Retention evaluation is a pure function from the snapshot list, the policy and the current time to a keep set and a prune set. Each tier (daily, weekly, monthly, yearly) is a small bucketing function, and the policy is their composition. The result does not depend on input order.
- Pruning is a plan and then an execution: a pure plan of rows to remove and content to release, computed as set differences, then performed by the shell under the run lock. The same plan serves any preview.
- Check is a stream of entries mapped through a pure verdict function (`entry -> observed content -> Verdict`), folded into a summary and an `Outcome`. The shell reads the content in parallel; the verdict never reads.
- Count-based progress reuses the `Display` view from `fsharp-backup-presentation`, with no separate progress classes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`retention-pruning`:** every requirement.
- **`backup-integrity`:** every requirement.
- **`progress-reporting`:** "Count-based progress indication for non-byte-transfer operations".
- **`profile-config`:** "Profile without a retention policy".

## Impact

- **Code:** all three F# source projects grow. No changes to the C# projects.
- **Tests:** scenario tests, FsCheck properties for retention evaluation (for example, a snapshot kept by any tier is never pruned, and the pruned set does not depend on input order), and parity tests that run the C# and F# commands against the same targets and compare the remaining manifest and blobs.
- **Dependencies:** requires `fsharp-snapshot-history`, for the manifest queries and presentation it adds.

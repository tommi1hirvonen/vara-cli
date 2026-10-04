## Context

See proposal.md for the motivation. The snapshot report currently builds a sparse Spectre.Console tree from the affected-directory rollups. The `--files` option adds file leaves, and the directory labels currently format all five event counts as unstyled text. `HistoryTablePresenter` already defines the application's pastel change-kind palette.

## Goals / Non-Goals

**Goals:**
- Reuse the established change-kind palette for each rollup token without changing the palette's meaning.
- Bound only the rendered tree, retaining the complete report data for rollups and omitted-directory counts.
- Keep the existing unlimited tree and `--files` behavior when no depth is supplied.

**Non-Goals:**
- Limiting the snapshot query or changing directory aggregation.
- Introducing a default depth, pagination, interactive expansion, or a new output format.
- Styling the snapshot header statistics or changing file detail semantics beyond visibility under a depth limit.

## Decisions

- Represent each directory label as text segments so the five change-kind tokens can be styled independently while directory names and source labels remain literal, unstyled text. Reuse one shared change-kind style mapping with snapshot history rather than introducing a second palette. This preserves the existing mapping: added green, changed amber, moved blue, deleted red, and linked plum.
- Interpret the selected scope root as depth zero and each child directory as one deeper level. Keep the option optional; an absent value retains current unlimited rendering. Reject values that are not non-negative integers through command-line parsing.
- Build or filter the displayed tree based on the depth while retaining the full directory rollups. For every displayed directory at the requested boundary, count affected descendant directory records excluded beneath it and add an omitted-directory marker only when that count is nonzero. This makes truncation explicit without implying that visible rollup totals are partial.
- Keep file leaves controlled independently by `--files`; only add leaves for directories that are within the displayed depth. The depth count applies to directory levels, not file leaves.

Alternatives considered:
- A global depth default would make large outputs more manageable, but would change established command output and could hide information unexpectedly; use an opt-in limit instead.
- Silently truncating the tree is simpler, but gives no indication that more affected directories exist; use a counted marker at each boundary branch.
- Styling entire directory labels would reduce the visual separation between paths and counts; style only the per-kind indicators and numbers.

## Risks / Trade-offs

- [Counting omitted directory nodes could become inconsistent if based on the rendered tree rather than the full report] -> Compute omitted counts from the full affected-directory set, and test nested and sibling branches.
- [Tree labels may parse user-controlled path text as markup if assembled as markup strings] -> Use literal text segments and apply styles to only the controlled change-kind tokens.
- [Terminal color may be disabled or unsupported] -> Preserve all symbols, labels, and numbers in plain text, consistent with existing CLI color fallback behavior.

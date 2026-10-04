## 1. Change-kind styling

- [ ] 1.1 Share the established file-history change-kind palette with snapshot directory rollups and verify presenter tests assert every kind's styled indicator and count.
- [ ] 1.2 Verify snapshot rollup text remains complete and readable when ANSI color is disabled or unavailable.

## 2. Depth-limited tree output

- [ ] 2.1 Add optional non-negative integer `--depth` parsing to `vara snapshot` and verify command tests accept valid values and reject negative or non-integer values.
- [ ] 2.2 Limit displayed directory levels relative to the selected scope root while preserving unlimited output by default, and verify depth-zero, depth-one, default, and scoped command cases.
- [ ] 2.3 Add per-boundary omitted-directory counts based on the full affected-directory set and verify nested and sibling branches, unchanged ancestor rollups, and no marker when nothing is omitted.
- [ ] 2.4 Keep `--files` independent from depth and verify file leaves appear only within visible directories when both options are used.

## 3. Documentation and verification

- [ ] 3.1 Update snapshot command documentation with `--depth` semantics, its unlimited default, and the omitted-directory indicator; verify the documented examples match CLI help.
- [ ] 3.2 Run focused snapshot presenter and command tests and verify CLI help exposes the new option.

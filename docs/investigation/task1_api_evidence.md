# Task 1 — API / code evidence (agent)

Generated with investigation implementation. Operator UI results still PENDING where noted.

## Verified facts (user)

- Manual CU + `SP_M.CU_HARDWARE_PROPS_11X17` → no magenta `?`
- Our automation → magenta `?` in UI; PDF text has no `?`; API `PART_POS` real
- Therefore not a “run numbering” problem. Do not hide/clear marks.

## 1a — `--skip-mark-repair` default = skip

| Item | Evidence |
|------|----------|
| Default | `EnableMarkRepair = false`; Fit logs `mark-repair SKIPPED (default)` |
| Opt-in | `--enable-mark-repair` |
| Skip wins | If both `--enable-mark-repair` and `--skip-mark-repair`, repair stays off |
| Wired | `Program.RunCivilDrawings`, `RunCorrect`, `CivilDrawingBatchExtractor` → `PrecastDimensionPostProcessor` |
| Tag | `pre-task1-mark-investigation` |

**Build:** `dotnet build -c Release -p:PlatformTarget=x64` succeeded → `bin\Release\net48\TeklaExtractor.exe`

**Operator:** confirm console line on extract-only run. Live Tekla run = PENDING_OPERATOR.

## 1b — Staged Fit

| Stage | Code path |
|-------|-----------|
| 1 | `FitStagedInvestigation`: open/update/axis/scale only |
| 2 | + `PurgeTemporaryTags` |
| 3 | + `PlaceTiers` (includes `PurgeOldDimensions`) |
| 4 | + `ArrangeAllSheet1Views` + `ApplyCats` + seal |
| 5 | + `RepairBrokenPartMarks` only if `--enable-mark-repair` |

CLI: `--fit-stage 1..5`  
Report: `fit_stage_report_<piece>.txt`  
**First stage with `?`:** PENDING_OPERATOR (see `task1_operator_checklist.md`)

## 1c — Mark attribute dump

CLI: `--dump-mark-attrs --mark W10-175`  
Writes `mark_attrs_<mark>_latest.txt` + timestamped copy.

From `Tekla.Structures.Drawing.xml` (installed with build output):

| Name | Status |
|------|--------|
| `MarkBase.InsertionPoint`, `Placing`, `Attributes` | Public |
| `MarkBaseAttributes.Frame`, `ArrowHead`, `PreferredPlacing`, `TextAlignment`, … | Public |
| `Mark.MarkAttributes.Content` | Public |
| `ChangeSymbol` on public Mark | **Not public** — only `DrawingInternal.dotGrMarkBase_t.ChangeSymbol` (excluded) → dump reports unsure if missing |
| Font on MarkBaseAttributes | **Not listed** in XML → dump tries reflection → unsure if absent |

**Manual vs automated diff:** PENDING_OPERATOR (two dumps)

## 1d — Reopen `showDrawing:true`

CLI: `--reopen-drawing --mark W10-175`  
Uses `DrawingHandler.SetActiveDrawing(drawing, showDrawing: true)` (same overload already used with `false` elsewhere).  
Report: `reopen_test_<piece>.txt`  
**Glyphs changed?** PENDING_OPERATOR

## 1e — Remove change clouds

| Search | Result |
|--------|--------|
| Public Drawing API for remove mark change symbols | **Not found** in Drawing.xml member list |
| UI command | Operator: Drawing tab → Remove → mark/change symbols |
| Code call | **Not implemented** (per plan — wait for verified API) |

**UI clears magenta?** PENDING_OPERATOR

## Constraint check

- Numbering marks not hidden/deleted by new flags
- No `InsertTradeMarks` re-enabled
- No global `XS_*` changes
- Mass TextElement rewrite no longer default

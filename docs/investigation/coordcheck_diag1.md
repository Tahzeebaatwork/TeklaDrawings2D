# diag1: coordinate and unit check

Hypothesis under test: dimension points or offsets are placed in the wrong coordinate space or unit, or the code rescales the drawing to fit, so dimensions land in the wrong place or show wrong values. The diagnostic is log-only (`--coord-check`, `--coord-probe`). Answers marked *to confirm* come from reading the code; the run fills in the rest.

## Part 1: where things happen (file:line)

| # | What | Where |
|---|------|-------|
| 1 | Scale chosen to fit the sheet: first of 1:75, 1:60, 1:50, 1:100 whose wall width fits the band (max 140 mm) and whose wall height plus 70 + 50 mm row stacks fits the sheet | `PrecastDimensionPostProcessor.cs` 1026, 2236, 2346 |
| 1 | Chosen scale assigned to the front view | 1476, 1886, 1970, 5710 |
| 1 | View frame refit (frame only, not the objects) | `RefitFrontFrame` 1952, `SealView` 2050, `FitViewFrame` 2067 |
| 1 | Fixed scales on other views | A-A 1:20 `DimensionRouter.cs` 206; placing/sections 1:75 / 1:60, 6313-6428 |
| 1 | View rescale to 1:30 / 1:40 "if view exceeds viewport" | `src/LocalMacros/AutoCompressAndEdit11x17.cs` 87-89: copied into the model's macro folder, **not run by the pipeline** |
| 2 | Dimension points: global solid points → `ViewCoordinateSystem` (view-local, model mm) | `ToView` 1006, `BindView` 741, `PaperPoint` / `PaperBottomPoint` 937 / 951 (named "paper", return view-local) |
| 2 | Offset: computed in view units (paper mm × scale), divided by scale for `CreateDimensionSet`, then `set.Distance = distance` (view units) + `Modify` | rows 4045 → 4058 → 4082; vertical 4376 → 4387 → 4401 |
| 2 | Section dims: `paper` and a fixed 2.0 passed straight to `CreateDimensionSet`, no `Distance` reset | 7337, 7735 |
| 3 | Horizontal vs vertical: the panel is horizontal when \|dx\| ≥ \|dy\| in view-local; mirror/rotation only from the view coordinate system, no explicit flip | 751 |
| 4 | Offsets and steps, all paper mm (× scale where used): first row 6, row step 10, floor 7, vertical first 6, text 2.5, budgets L/R 28, top 70, bottom 50 | 1030, 1032, 1046-1066, 1153, 3745; row offset accumulates at 3703 / 3710 |
| 4 | Station merge 5 mm and edge touch 10 mm are model mm | 3716, `PrecastElevationRows.cs` 28 |
| 5 | Height columns: `RowHeights` gives interior Z, column = 0, interior…, H; emitted as `DimensionTypes.Absolute` | `PrecastHeightColumns.cs` 59, 235-237 |

**Side finding.** `StraightDimensionSet` and `StraightDimension` do not implement `IAxisAlignedBoundingBox` in this API, so the `is IAxisAlignedBoundingBox` checks over dimension sets (3172, 4960) never match. The "dimension set outside border" assertion has never checked anything. diag1 reads each child dimension's `Distance` and printed text (`Value`) instead.

## Part 3: answers

**a) Does code rescale or resize to fit?**
- Yes, but only through real Tekla view scales (`SelectScale`) and frame refits. Nothing scales objects.
- One risk to confirm: the `[CoordCheck] view` line flags `SCALE-MISMATCH` if the view's scale differs from the scale the offsets were computed with. `AutoCompressAndEdit11x17` would cause exactly that if someone ran it manually.

**b) One coordinate system for points?**
- Yes: view-local everywhere on page 1 (*to confirm*). The `dim` lines log every set's points as passed and converted to global.
- The `bbox` line checks view-local against model extents (expect 1.0) and the `toView` the height columns use (expect 1.0).

**c) Offset unit?** Page 1 passes paper mm to `CreateDimensionSet` and then overwrites `Distance` with view units.
- Whether both are right is what the probe decides: `probe RULE` per scale, type and mode.
- The section dims (7337, 7735) skip the overwrite, so if the two calls use different units, they will sit at a different offset than page 1.

**d) Rows where the printed value ≠ the geometric distance.** *From the run:* `RATIO!=1` rows in `coordcheck_diag1.csv`.

**e) REVEAL and OPENING.** *From the run:* the `[CoordCheck] row` lines list, per row, every model object (id, name, family and its source, along range and Z range).

**f) Is a squish ratio needed?** *From the run:* the `bbox` ratio, `paper/model` against `1/scale`, and the probe rule. If all are 1.0 or 1/scale, no.

Height columns showing 3475 eight times: the `hcol` lines separate the two causes. `type=Absolute` prints cumulative values ending in H. `HCOL-ONLY-2PTS` means the column only got 0 and H.

## Decision rule
1. A ratio is not 1.0, or `SCALE-MISMATCH`, or the probe shows a unit mismatch: that is the root cause; fix it first.
2. Ratios are 1.0, but the probe shows the `/ scale` + `Distance` pair is wrong: fix only the offset unit, at 4045/4082 and 4376/4401.
3. Ratios are 1.0 and the offset unit is right: coordinates are not the cause. Fix the row sources (REVEAL, OPENING, HOLE) from the `row` lines, and the column type or points from the `hcol` lines.

`n@s=total` (pages 2/3) is Phase D, not part of diag1.

## Run
```powershell
.\bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings --mark W10-78 --clean-mark --run-tag diag1 --coord-check --coord-probe
.\bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings --mark W10-175 --clean-mark --run-tag diag1 --coord-check
python Scripts\summarize_coordcheck.py --run-tag diag1
```
Run one exe at a time. Each run rewrites only its own piece's CSV rows, so the order of pieces doesn't matter. A non-probe rerun keeps the piece's earlier probe rows, and its header says `probe=kept-from-earlier-run`.

`--coord-probe-keep` (optional, last, own tag) keeps the probe sets plus red reference lines at "100 view units" and "100 paper mm" in the drawing. The exported PDF then shows directly which unit the dimension line follows:
```powershell
.\bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings --mark W10-78 --clean-mark --run-tag diag1-keep --coord-check --coord-probe-keep
```
A later normal `--clean-mark` run removes the kept probe views.

`ABS-READS-RELATIVE` on a set means its type is Absolute but its children read back segment values (sum = |A-B|). Whether the PDF prints cumulative or segment values is then answered by `probe TYPES` and the PDF.

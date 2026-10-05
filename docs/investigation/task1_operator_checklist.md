# Task 1 — Operator checklist (find magenta `?` source)

**Do not hide/clear numbering marks.** Model already has `PART_POS`. Magenta `?` is an automation/UI glyph issue.

Prerequisite: Tekla 2026 open + model loaded. Fresh **manual** Cast Unit drawing for W10-175 using only `SP_M.CU_HARDWARE_PROPS_11X17` (no macros / no prior Fit). Confirm UI has **no** magenta `?` before starting.

Exe (from repo root):

```bat
cd /d c:\Users\ASUS\Desktop\2d_tekla
bin\Release\net48\TeklaExtractor.exe
```

(or `bin\x64\Release\net48\TeklaExtractor.exe` if present)

---

## 1a — Default skip mark repair (code done)

```bat
TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175
```

Console must show: `mark-repair=SKIP (default)`.

Opt-in only:

```bat
TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175 --enable-mark-repair
```

---

## 1b — Staged Fit on **manual** `[W10-175 - 1]`

After each stage: open the drawing in Tekla UI, look at elevation for magenta `?`, record Yes/No.

| Stage | Command | `?` in UI? (Y/N) |
|-------|---------|------------------|
| 1 shell | `TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175 --fit-stage 1` | |
| 2 +purge tags | `... --fit-stage 2` | |
| 3 +tiers | `... --fit-stage 3` | |
| 4 +formwork/park | `... --fit-stage 4` | |
| 5 +mark repair | `... --fit-stage 5 --enable-mark-repair` | |

**First stage where `?` appears:** ________

Report also written: `fit_stage_report_W10-175.txt`

---

## 1c — Mark attribute dump (manual vs automated)

On **manual** drawing (no Fit / after stage that still has no `?`):

```bat
TeklaExtractor.exe --dump-mark-attrs --mark W10-175 --sample-count 2
```

Copy `mark_attrs_W10-175_latest.txt` → `mark_attrs_W10-175_MANUAL.txt`

On **automated** drawing (known `?` swarm):

```bat
TeklaExtractor.exe --dump-mark-attrs --mark W10-175 --sample-count 2
```

Copy latest → `mark_attrs_W10-175_AUTOMATED.txt`

Diff the two files (content element types, font/frame/arrow, ChangeSymbol, related PART_POS, InsertionPoint).

API note (from `Tekla.Structures.Drawing.xml`):

- Public: `MarkBase.Attributes` → `PreferredPlacing`, `Frame`, `ArrowHead`, `TextAlignment`, …
- `ChangeSymbol` is **DrawingInternal only** (excluded) — dump may show `unsure (no property)`
- No public “Remove change clouds” API found in Drawing.xml — UI-only for Task 1e

---

## 1d — Reopen with `showDrawing:true`

With drawing that shows `?` (no other Fit):

```bat
TeklaExtractor.exe --reopen-drawing --mark W10-175
```

Did magenta glyphs change? Y/N: ________  
File: `reopen_test_W10-175.txt` (edit `result=` line)

---

## 1e — UI “Remove change clouds”

In open drawing: Drawing tab → Remove → mark / change symbols (exact label as installed).

Did magenta clear? Y/N: ________  

**Do not** call from code until a verified public API exists (none found in Drawing.xml as of this investigation).

---

## Evidence summary (fill after runs)

| Item | Result |
|------|--------|
| 1a default skip logs | |
| 1b first stage with `?` | |
| 1c key attribute diffs | |
| 1d reopen changes glyphs | |
| 1e UI remove clears magenta | |

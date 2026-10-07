# Step 0 — Tekla live UI vs engineer page 1 (W10-175)

**Source:** Your Tekla Drawing Editor screenshot (post–S1–S5 run) + engineer GT `P22-132.W10-175 - 1..pdf` (17×11, 1:75).  
**Not used:** Export PDF, hardcoded PDF numbers.

| Area | Engineer page 1 (KEEP) | Live Tekla UI (observed) | Action |
|------|------------------------|---------------------------|--------|
| View count / scale | 1 front elevation, **1:75** (view + label agree) | 1 front; label **1:50** (stale); view scale likely mixed | Force scale **75** when fits; **sync/replace** TOP IN FORM text |
| Native vs Fit tiers | ~9 top + ~6 bottom category strings, ~4–6 mm tier gap | **10–12 top + 6–8 bottom**; gap ~**11 mm**; stacks **cross left border ~350 px** | **Disable preserve-native default**; rebuild tiers; **dynamic step** from height |
| Vertical chains | **≤1 short chain per side**, small offset | **5–6 left + multiple right**, offsets to ~170 mm | **Max 1 DimSet/side**; first offset **6 mm** paper; clamp in border |
| Tier order (top) | OVERALL→LEDGE→OPENING/REVEAL→P-602→P-300→SP30→SP15→SP25→P-205 | Order roughly OK but **HOLE PROFILE**, extra **HARDWARE** tiers | Drop **HOLE** tier; engineer labels only |
| Tier order (bot) | GT75-1219→GT100→GT75-686→CNDT/EB-S-PL→OPENING→OVERALL | Similar but crowded | Same order; fewer duplicates |
| Opening chain | **One merged string** (edge\|open\|pier\|open\|edge); sum = overall | Many opening edges; conservation not guaranteed | **1D span merge** + conservation log |
| Labels | ~**16** right of tiers, left of BOM | ~**30** stacked red labels | Idempotent tag replace; **no per-tier spam** |
| TOP IN FORM | **Once** under wall | Present; scale line wrong | **Replace** single block each Fit |
| SIDE/END | END 1–5, SIDE A–D once | END/SIDE present (verify dupes on 2nd Fit) | Keep idempotent insert |
| Paint | **None** | Possible paint text/hatch from layout | **Purge** PAINT / NO PAINT / colour strings |
| Rebar / BBS / sections | Pages 2–4 only | Should be parked/hidden | **HideSheet1Rebar** + park A-A/3D |
| Phantom marks | BOM-only marks | **BCMBD**, stray marks possible | **Suppress** marks not matching BOM/hardware set |
| BOM / notes / title | Right column; no dim overlap | Dims **crowd** notes; **blue square** in notes table | Bbox assert; **purge stray filled graphics** in notes band |
| Sheet frame | **One** 17:11 border | **Two nested blue frames** | **RemoveInnerSheetBorders** (keep outer Layout frame) |
| General notes values | 28 DAY, STRIPPING, etc. from **model UDAs** | **0** for 28 DAY / STRIPPING; **MPa** vs engineer **PSI** | **Log [NotesUDA]** warnings; do not hardcode |
| GT76-254 | Callout on sheet | Not verified in screenshot | Insert **once** from model trade hit |

## Root cause (code)

`PreserveNativeDimensions=true` + ≥8 native `StraightDimensionSet`s → Fit **skipped** `PurgeOldDimensions` / `PlaceTiers`, leaving layout-property mesh (many verticals, wrong scale label).

## Plan (S1–S5 refresh)

1. **S1:** Prefer scale **75**; real BOM scan; tier/vert budgets; layout log.  
2. **S2:** Engineer tier order; dynamic step; opening merge; no HOLE tier.  
3. **S3:** One vertical chain per side; 6 mm offset; clamp.  
4. **S4:** TOP IN FORM replace (scale sync); paint purge; SIDE/END once.  
5. **S5:** Bbox + paint + scale-label asserts; fit_report counts.

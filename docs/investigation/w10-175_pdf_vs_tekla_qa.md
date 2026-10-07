# W10-175 PDF vs Tekla QA matrix

**GT:** `P22-132.W10-175.Rev 2.pdf` (4 sheets, 17×11, elev 1:75). Rev 1 not in repo.  
**Per-page:** `Desktop\dd\P22-132.W10-175 - 1..pdf` … `- 4..pdf`  
**Audits:** `Final Check/Precast Drawing Comparison Report - W10-175p.pdf`, `Final Check/w10-175 master tab.pdf`  
**Tekla extract:** `Export/CivilDrawings/new with macros/SHOP/1_W10-175_SHOP.json`, `REBAR_BBS/1_W10-175_REBAR_BBS.json`  
**Fit:** `bin/.../fit_report_W10-175.txt` (2026-10-06) scale 1:75, bboxFailures=1  

Rule: PDF wins on disagreement. No invented values.

## 1. Envelope / notes

| Item | PDF (Rev 2) | Tekla value | Status |
|------|-------------|-------------|--------|
| Length mm | 9366 | 9366.054 | OK (±3) |
| Height × thick | 2975 × 254 | 2975\*254 | OK |
| Qty | 1 | 1 | OK |
| Weight lb | 24822 | 24299.2 (11021.94 kg) | **MISMATCH −523** |
| Volume yd³ | 6.05 (4.63 m³) | 4.7949 m³ ≈ 6.27 yd³ | **MISMATCH** |
| 28-day | 6000 PSI | 41.4 MPa (~6005 PSI) | **MISMATCH label/unit** |
| Stripping | 3500 PSI | (empty) | **MISMATCH** |
| Class | F2 | "1" | **MISMATCH** |
| Air | 4–7% | (empty) | **MISMATCH** |
| Min cover mm | 25 | 38.1 | **MISMATCH** |
| Rebar grade | 400W | present on bars | OK |
| Paint colours | 4 Gemite / BM colours + NO PAINT zones | layout-dependent; Fit previously purged paint text | **WIP — keep PDF paint on sheet 1** |

## 2. Hardware BOM qty

| Mark | PDF qty | Tekla SHOP qty | Status |
|------|--------:|---------------:|--------|
| P-205 | 4 | 4 | OK |
| GT75-686 | 2 | 2 | OK |
| GT75-1219 | 8 | 8 | OK |
| GT76-254 | 6 | 6 | OK |
| GT100-1219 | 1 | 3 | **MISMATCH +2** |
| P-300 | 5 | 5 | OK |
| P-602 | 1 | 1 | OK |
| SP15-457 | 2 | 6 | **MISMATCH +4** |
| SP25-1067 | 8 | 8 | OK |
| SP30-1219 | 1 | 1 | OK |
| CNDT | 1 | 1 | OK |
| EB-S-PL | 1 | 1 | OK |
| MPX11 (V-reveal) | not in PDF BOM | 1 | **MISMATCH — suppress on sheet 1 marks** |

Drawing-table BOM on Rev 2 / macro PDF: master tab says Macro Qty = Rev 2 Qty for all 12 rows (SP15=2, GT100=1). Sheet BOM display target = PDF.

## 3. Dimension chain descriptions (PDF GT vs Tekla UI)

### 3a. Screenshot delta

| Shot | Role | Dim layout | Pre-fix | After code (UI confirm pending) |
|------|------|------------|----------|----------------------------------|
| Engineer PAGE 3 (white) | Rebar TARGET | Tiered H/V around elev + plan + A-A + VIEW B | **TARGET** | — |
| Tekla UI placing TARGET | Rebar | Screenshot-2 layout (elev + END 2 dense native + detail) **plus** TOP IN FORM dims | **TARGET** | Rebuild elev only (`PlaceTiers`/`PlacePlacingElevDims`); **preserve END 2** native; keep SP25 detail |
| Tekla UI placing FAIL | Rebar | Elev no dims OR wiped END 2 into thin/overall-only | **FAIL** | Do not purge END 2 / detail dims |
| Tekla UI sheet 1 | Hardware | OPENING fracture; verts; ??? | **FAIL dims** | Fit opening/Side A/B/notch |

### 3c-extra. Placing sheet must-have dims (from engineer PAGE 3)

| View | Required dims | Tekla pre-fix | Code path |
|------|---------------|----------------|-----------|
| TOP IN FORM | Overall + opening H tiers; Side A/B verts; notch | Only overall + 2975 | `PlacePlacingElevDims` |
| VIEW END 2 | Plan/end H overall + opening; thickness if thin | None | `PlacePlacingEndViewDims` |
| VIEW A / A-A | Vertical height chain + thickness | None; label missing | `PlacePlacingSectionDims` + caption `A-A`/`VIEW A` |
| VIEW B (if present) | Same family as A-A | often absent | otherViews + `VIEW B` caption |

### 3b. Page 1 — hardware elevation (purpose of each chain)

| Tier | Purpose | PDF chain (mm) | Σ | Live Tekla (shot 3) | Status |
|------|---------|----------------|--:|---------------------|--------|
| OVERALL PROFILE | Full panel length | 9366 | 9366 | 9366 | OK |
| OPENING PROFILE | edge\|win\|**pier**\|win\|edge | **373\|975\|4928\|975\|2115** | 9366 | 373\|975\|**1336\|1830\|1762**\|975\|2115 | **MISMATCH fracture** |
| REVEAL (B) | Reveal paint/reveal edges | 372\|975\|497\|3548\|883\|975\|931\|943\|241 | 9365 | 860\|2739\|3165\|2603 | **MISMATCH** |
| NO PAINT | Paint boundary | 1857\|229\|3065\|229\|3745\|241 | 9366 | (often missing) | **WIP** |
| LEDGE | Top ledge steps | (model) | — | present as tier | compare |
| P-602 HARDWARE | Plate stations | (model) | — | 74\|1897\|3294\|4100 | compare |
| P-300 BRACING | Bracing inserts | 2360\|5106\|1900 | 9366 | 102\|2259\|5106\|1854\|44 | **near / check ends** |
| SP30 + GT100 | Coupler+tube group | 190\|… | — | 190\|9176 | OK-ish |
| SP15 + GT75-686 | Coupler+tube | @5399,8314 | — | 5399\|2915\|1052 | OK-ish |
| SP25 + GT75-1219 | Coupler+tube | 1580\|288×…\|220 | — | extra **28\|28** | **dedupe** |
| P-205 LIFTERS | Lifter spacing | 977\|2114\|2433\|2152\|1690 | 9366 | same | OK |
| GT* bottom | Mirror of SP groups | (paired) | — | present | OK |
| EB-S-PL + CNDT | Conduit/elec | 2381\|6985 | 9366 | 2381\|6985 | OK |
| Side A vertical | Base step + body | **375+2600** | 2975 | 1488\|257\|341\|638 | **MISMATCH** |
| Side B vertical | Notch + splits | **275+1850+850** | 2975 | 2975 only | **MISMATCH** |
| Notch / recess | Right notch 241×275; left base 375 H | PDF callouts | — | dashed only | **MISSING dims** |

### 3c. Page 3 — rebar elevation families (engineer)

| Family | Purpose | Engineer examples | Tekla UI (shot 2) |
|--------|---------|-------------------|-------------------|
| Elev H — outer | Overall / major segments | 419, 1432, 98, 1879… | Missing; left dump instead |
| Elev H — spacing | Rebar groups `N@S=L` | 4@203=812, 5@300=1500 | Not as tiers around wall |
| Elev V — Side A/B | Height + sill/notch | 2222, 334, 419; 5@300=1500 | Collapsed into left stack |
| Plan H | Thickness / notch plan | 5152\|400\|2322; detail 77\|1127… | Not arranged under elev |
| Section V | A-A / View B bar heights | multi-tier beside section | Partial detail view only |

### 3d. Fit / scale

| Item | PDF | Tekla | Status |
|------|-----|-------|--------|
| Fit scale | 1:75 | 1:75 | OK |
| Fit bbox | inside border | was Text outside (view-CS) / sheet3 left dump | **fix in code** |
| Magenta ??? | none | many on sheet 1 | **numbering / purge unresolved** |

## 4. Rebar (pages 3–4) — REBAR_BBS vs PDF

All PDF page-4 bent + straight targets match extract (**0 mismatches**):

Bent: RB10M 0040(10), 0046(19), 0049(12), 0050(12), 0051(8), 0052(13), 0125(4), 0135(22), RB15M 0068(2), RB25M 0139(1) — OK.  
Straight 10M/15M length×qty rows from brief — all OK.

Layout/spacing/legibility on sheet 3 still needs Tekla UI verify (Phase 3).

## 5. Sheet / view inventory

| Sheet | PDF Rev 2 | Our Export / Tekla | Status |
|-------|-----------|--------------------|--------|
| 1 Hardware elev | TOP IN FORM 1:75 | W10-175_-_1; Fit 1:75; bbox fail | **WIP** |
| 2 Sections/3D | A–D, View B, sill, drip, 3D ~1:50 | _-2 + _-2_Sections_3D sidecar | **WIP** |
| 3 Rebar layout | marks + spacings | W10-175_-_3 | UI verify |
| 4 Rebar BBS | schedule table | W10-175_-_4; data OK | UI verify |

## Implementation status (code — operator UI confirm pending)

| Phase | Status | Notes |
|-------|--------|-------|
| 0 QA matrix + dim descriptions | **Done** | §3a–3d; PDF chains only |
| 1 Sheet-1 Fit dims | **Coded** | OPENING two-window merge → log vs `373\|975\|4928\|975\|2115`; Side A/B from NotchHeights; notch/recess H; SP25 30 mm dedupe; mark-repair off + purge unresolved `?`; TOP IN FORM 1:75 |
| 2 Sections | **Coded** | `CleanSections` 2×N grid, 3D/details scales, front parked |
| 3 Sheet-3 CleanPlacing | **Coded** | Always purge left dump + rebuild TOP IN FORM / END 2 / A-A; park SP25 detail junk; no preserve-dump; `SyncViewScaleLabel` |
| 4 Peers | **Same path** | Fit/CleanPlacing shared for W10-78 / W10-48; UI OK/MISMATCH after screenshots |

## Peers + dim sign-off matrix (fill after CMD screenshots)

| Dim check | W10-175 | W10-78 | W10-48 |
|-----------|---------|--------|--------|
| Sheet1 OPENING edge\|win\|pier\|win\|edge (Σ≈overall) | | | |
| Sheet1 Side A/B verts readable | | | |
| Sheet1 notch/recess callout | | | |
| Sheet1 no magenta `?` | | | |
| Sheet3 no left dim dump; tiers around elev | | | |
| Sheet3 plan under elev; section/View B right | | | |

Final OK/MISMATCH for dim rows in §3b after operator screenshots. Model weight/volume/cover stay documented MISMATCH only.

## Sign-off notes

- Model physics (weight/volume/cover) stay documented MISMATCH — do not invent geometry.
- BOM table display qty target = PDF (SP15=2, GT100=1) even when SHOP instance count differs.
- Operator refresh (CMD, not Cursor):  
  `.\TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175`
- Peers: `--mark W10-78` then `--mark W10-48`; fill [`step4_ui_verify.md`](step4_ui_verify.md) + table above.
- Console expects: `[Fit] OPENING chain=…`, `[Fit] SideA verts`, `[Fit] notch/recess`, `[Placing] Sheet-3 engineer layout … orphansPurged=`.

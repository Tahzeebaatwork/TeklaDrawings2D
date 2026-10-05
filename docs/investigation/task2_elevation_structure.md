# Task 2 — Elevation structure investigation (W10-175 - 1 vs engineer page 1)

**Scope:** Investigate only. No elevation rebuild in this task. Geometry/stations must come from the **3D model**, not copied PDF numbers.

**Code refs:** [`Services/PrecastDimensionPostProcessor.cs`](../../Services/PrecastDimensionPostProcessor.cs)

---

## Table: engineer element vs code

| Item | Present / partial / missing | Cause | File:line (approx) |
|------|----------------------------|-------|---------------------|
| Horizontal tiers TOP — Lifters | Partial | `PlaceString` when `buckets.Lifters` non-empty; label hardcoded `P-205 LIFTERS` | PlaceTiers ~2820–2824 |
| Horizontal TOP — Splicers by type | Partial | Per `Splicers` dict keys; labels hardcode SP25/SP15/SP30 strings | ~2825–2855 |
| Horizontal TOP — Bracing | Partial | `buckets.Bracing`; label `P-300 BRACING` | ~2856–2860 |
| Horizontal TOP — Top embeds | Partial | `TopEmbeds`; label `P-602 HARDWARE` | ~2861–2865 |
| Horizontal TOP — Reveals | Partial | Placed if count>0; **shares one vertical step** with holes/openings | ~2866–2885 |
| Horizontal TOP — Holes | Partial | Same shared step as reveals/openings | ~2872–2875 |
| Horizontal TOP — Openings | Partial | Not always one consolidated chain; W10-175 **hardcoded stations** override | PlaceTiers ~2877–2880; override ~3330–3340 |
| Horizontal TOP — Ledge | Partial | If `Ledge` non-empty | ~2886–2890 |
| Horizontal TOP — Overall 9366 | Present (when tape OK) | Outer `tape` min/max = axis span | ~2891 |
| Horizontal TOP — NO PAINT | Missing | `StationBuckets.NoPaint` exists but **never filled or placed** | StationBuckets ~80; no PlaceTiers use |
| Horizontal BOTTOM — Grout tubes | Partial | By `BottomGroutTubes` key | ~2893–2910 |
| Horizontal BOTTOM — Secondary (CNDT/EB) | Partial | `BottomSecondary` | ~2911–2915 |
| Horizontal BOTTOM — Base openings | Partial | `BaseOpenings` | ~2916–2920 |
| Horizontal BOTTOM — Overall | Present | Same tape | ~2921 |
| Vertical LEFT multi-column | Partial / wrong content | Overall height + **fraction heuristics** (0.9/0.8×H), not opening/embed stations | PlaceVerticalDimensions ~3063–3116 |
| Vertical RIGHT multi-column | Partial / wrong content | Overall + lifter/splicer **fractions** (0.95/0.5×H) | ~3117–3140 |
| Row order (innermost→outer) | Partial | Code order ≈ Lifters→Splicers→Bracing→Embeds→Reveal/Hole/Opening→Ledge→Overall; engineer 7-tier may differ on paint/opening consolidation | PlaceTiers ~2810–2922 |
| Labels right of tiers | Partial | `PlaceTagBox` fixed X = right end + 2×scale; **no collision clamp** | PlaceTagBox ~3040+ |
| Opening X / hatch / section A–D | Missing from Fit | Not created in Fit; props/view representation; section **views** parked | ArrangeAllSheet1Views park; no SectionMark code |
| END 1–5 / SIDE A–D | Partial | `AddFormworkEdgeTags` text; can **duplicate** SIDE C/D END 3–5 | ~3460+ |
| TOP IN FORM | Missing | `AddTopInFormLabel` **dead** (no call); purge deletes `IN FORM` / `TOP IN FORM` text | AddTopInFormLabel; CollectSpam |
| Rebar on elevation | Hidden (OK vs engineer) | `HideSheet1Rebar` | ~1849+ |
| Native auto dims before Fit | Removed | `PurgeOldDimensions` deletes all front `StraightDimensionSet` before recreate | PlaceTiers first line; PurgeOldDimensions |

---

## Horizontal: what code computes vs skips

| Category | Computed? | Skipped / why |
|----------|-----------|----------------|
| Overall | Yes | Only overall if `TAPE_UNVERIFIED` (`overallOnly`) |
| Opening | Yes (edges) | Consolidation of two equal windows into one chain not done; W10-175 override hardcodes stations |
| Hole | Yes (heuristic) | May merge step with openings/reveals |
| Ledge | Yes | Depends on classification |
| Reveal | Yes | Shared step with openings |
| Hardware / lifters / splicers / bracing / GT | Yes | Via `CollectStations` / trade buckets |
| NO PAINT | **No** | Bucket unused |
| Per-mark rows matching every BOM line | Partial | Grouped by category/key, not every distinct PART_POS row |

---

## Vertical: today vs engineer columns

**Today:** left/right chains use wall height (`axis` paper Y span or fallback 2975) and **fractional** midpoints when bucket **counts** > 0 — not actual feature Z/Y stations from openings, embeds, lifters, reveals.

**Model features that should drive engineer-style columns (design proposal):**

- Opening sill/head projected to view Y
- Reveal / drip / ledge elevations
- Lifter / embed insertion heights on the wall face
- Overall 0 and panel height

---

## Purge / hide vs engineer-visible content

| Action | Removes / hides | Engineer impact |
|--------|-----------------|-----------------|
| `HideSheet1Rebar` | Reinforcement in all sheet views | Usually desired (engineer page 1 hides rebar) |
| `PurgeTemporaryTags` | Text matching RECESS, IN FORM, TOP IN FORM, ???, SECTION CLUSTER, …; WeldMark | Can delete TOP IN FORM / recess notes engineer shows |
| `PurgeOldDimensions` | All front straight dims | Wipes richer native chains before heuristic recreate |
| `ParkViewOffSheet` | 3D / section / bottom / others at (2500,2500) | Section views off sheet-1 (OK if marks remain); section **marks** not managed |
| Mark repair (now opt-in) | Recreate Mark + TextElement | Was rewriting all marks; default **skipped** after Task 1a |

---

## Proposed model-driven design (not implemented)

1. **Stations from geometry only**  
   Project part solids / boolean openings / report points onto `PanelAxis` (along) and view-up (height). No piece-specific constants in the general path.

2. **Group by category**  
   Lifters | Splicers(by family) | Bracing | Top embeds | Reveals | Holes | Openings | Ledge | NoPaint (if UDA/paint boundary exists) | Overall — same buckets, but place **each non-empty category on its own tier step** (stop sharing one step for reveal+hole+opening).

3. **Top / bottom split rule**  
   - Top face / upper half embeds → top ladder  
   - Base openings / grout tubes / lower secondary → bottom ladder  
   - Overall on both outermost  

4. **Opening consolidation**  
   When N equal-width window stations appear as edge pairs, merge to continuous chain `margin | width | pier | … | margin` summing to `axis.Span` (algorithmic, not W10-175 literals).

5. **Vertical column assignment**  
   - Left: opening/reveal height chains + overall  
   - Right: embed/lifter height chains + overall  
   Skip unverified → log `[TAPE_UNVERIFIED]`; never invent PDF numbers.

6. **Labels**  
   Tag text from live `PART_POS` / trade name; clamp Y against neighbours and BOM left (`ScanBomLeft` must be capped to sheet width — known bug, fix later).

7. **Hardcode removal**  
   Delete W10-175 opening override (~373,1348,…); replace with consolidated station builder.

---

## Wait

No elevation code changes in this task beyond Task 1 investigation flags. Next structural work needs Task 1a–e operator evidence + approval.

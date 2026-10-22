# W10-78 PDF vs Tekla QA matrix

**GT:** `Export/CivilDrawings/new with macros/PDF/P22-132.W10-78.Rev 2.pdf` (4 pages, 17×11).  
**Elevation:** TOP IN FORM 1:75. **VIEW A:** 1:50. **END 2:** 1:75.  
Rule: PDF wins on disagreement. No invented geometry. Every running chain is one tape of **5120 mm** (±1).

## 1. Envelope / notes

| Item | PDF (Rev 2) | Notes |
|------|-------------|--------|
| Length mm | 5120 | Overall, top and bottom |
| Height mm | 3476 left (`398\|3078`); 2476 right (`398\|1341\|737`) | Right edge is 1000 mm short of the left. Do not pad it to 3476 |
| Placing height | 3475 | 1 mm off the hardware left edge |
| Qty | 1 | |
| Weight lb | 18317 | Model value may differ. Document only |
| Volume yd³ | 4.53 | Model value may differ. Document only |
| 28-day / stripping | 0 MPa printed | Leave empty. Do not invent PSI |
| Class | F2 | |
| Air | 4% to 7% | |
| Min cover mm | 25 | Section / note, not an elevation chain |

## 2. Hardware BOM qty

| Mark | PDF qty |
|------|--------:|
| P-205 | 2 |
| GT76-254 | 1 |
| GT100-686 | 1 |
| GT100-1219 | 6 |
| P-300 | 4 |
| P-616 | 2 |
| SP25-1067 | 5 |
| SP30-1219 | 1 |

P-602 is a dimension tier and is not a BOM row. The SP25 tier tag prints `SP25-1067 + GT75-1219` even though GT75-1219 is not a BOM line. Keep that tag text.

## 3. Page 1 running chains (mm)

Above the wall, closest to the concrete first:

| Tier | Chain | Σ |
|------|--------|--:|
| P-205 LIFTERS | 630\|3672\|818 | 5120 |
| SP25-1067 + GT75-1219 | 1580\|288\|288\|288\|2290\|386 | 5120 |
| SP30-1219 + GT100-1219 | 190\|4930 | 5120 |
| P-300 BRACING | 102\|1098\|2720\|1200 | 5120 |
| P-602 HARDWARE | 1972\|2996\|51\|51\|51 | 5121 |
| REVEAL (B) | 3160\|1959 | 5119 |
| Untagged (no label in the text layer) | 838\|2043\|635\|1604 | 5120 |
| OVERALL PROFILE | 5120 | 5120 |

Below the wall, closest first:

| Tier | Chain | Σ |
|------|--------|--:|
| GT100-1219 HARDWARE | 220\|1080\|1196\|1405\|333\|333\|333\|220 | 5120 |
| OPENING PROFILE | 2617\|1087\|1416 | 5120 |
| OVERALL PROFILE | 5120 | 5120 |

One window. The opening chain is three segments. A second window must not be invented.

Verticals:

| Side | Chain | Σ |
|------|--------|--:|
| Left | 398\|3078 | 3476 |
| Right | 398\|1341\|737 | 2476 |

Form faces on the elevation: END 1–5, SIDE A–D. Face callout `GT76-254` only. No NO PAINT, LEDGE, SP15, or EB-S-PL tier.

The 51 mm ends in `51\|5018\|51` (page 3 END 2) and in the P-602 chain stay on the running tape. Dropping them leaves a sum that is not 5120.

## 4. Page 2

No dimension text in the Rev 2 extract. Sections / 3D only. Drip, chamfer, and the 25 mm cover belong here, not on the elevation.

## 5. Page 3 placing

Elevation: overall 5120 and height 3475, plus bar marks. Not a copy of the hardware tier stack.

END 2 native chains (each sums to 5120), including:

- `51\|5018\|51`
- `86\|240\|2020\|240\|1257\|240\|712\|240\|85`
- `1397\|1433\|2290`
- `713\|794\|400\|300\|2266\|647`
- `2220\|1987\|913`

Keep this stack when the view already has dense native dimensions. Do not rebuild it down to overall + opening.

VIEW A keeps spacing text such as `3@390=1170` and the length / size / qty marks.

## 6. Page 4 BBS

Bent marks include RB10M 0062, 0066 (T1), 0072 (shape 14), 0073 (TS67_2), 0074 (shape 14), 0148 (shape 17), RB25M 0138 (shape 17), plus straight 10M and 15M rows. Grade 400W. These are the engineered bars. A generated uniform mesh is not this schedule.

## 7. Sign-off

| Check | Expect |
|-------|--------|
| Opening chain | 3 segments, Σ = 5120 |
| Right vertical | Σ ≈ 2476, not padded to 3476 |
| END 3–5 and SIDE C/D | Present from outline corners when the solid has those faces |
| END 2 | Native dim count stays high |
| bbox | 0 failures |

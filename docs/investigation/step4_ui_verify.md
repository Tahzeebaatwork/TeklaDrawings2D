# Step 4 / fabrication — Tekla UI verify (operator)

**Pass/fail = Document Manager sheets, not Export/PDF cosmetics.**  
**GT:** `P22-132.W10-175.Rev 2.pdf` + `Desktop\dd\P22-132.W10-175 - N..pdf`  
**QA matrix:** [`w10-175_pdf_vs_tekla_qa.md`](w10-175_pdf_vs_tekla_qa.md)

After build (`bin\x64\Release\net48\TeklaExtractor.exe`), in **normal CMD** (not Cursor):

```powershell
cd c:\Users\ASUS\Desktop\2d_tekla\bin\x64\Release\net48
.\TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175
```

## Console checks

| Log | Expect |
|-----|--------|
| `[Fit] scale=1:75` / `TOP IN FORM synced 1:75` | yes |
| `[Fit] OPENING chain=` vs PDF `373\|975\|4928\|975\|2115` (W10-175) | OK or logged MISMATCH from model |
| `[Fit] SideA verts` / SideB / notch/recess | present |
| `[Fit] bboxAssert failures=0` | yes (view-CS false positives fixed) |
| `[BomQty] SP15-457` / `GT100-1219` | may MISMATCH model vs PDF — PDF wins for table |
| `[NotesUDA] WARNING` | empty stripping / PSI vs MPa |
| `[Sections] Sheet-2 arranged` | on sections role sheets |
| `[Placing] preserve-native` KEEP or REBUILD | per view |
| `[Placing] SyncViewScaleLabel 'A-A'` / `'END 2'` | `1:75` (not `1:??`) |
| `[Placing] Sheet-3 … preserved=` / `rebuilt=` | no `VIEW VIEW`; no left dump |
| `[BBS] Sheet-4` | on rebar BBS sheets |

## Sheet checklist

| Check | W10-175 | W10-78 | W10-48 |
|-------|---------|--------|--------|
| Sheet 1 inside border, no BOM overlap | | | |
| Scale 1:75 + TOP IN FORM once | | | |
| OPENING chain not fractured (pier merged) | | | |
| Side A/B verticals + notch/recess dims | | | |
| Tier merges SP+GT / EB+CNDT labels | | | |
| Paint / NO PAINT kept (Rev 2) | | | |
| Sheet 2 sections/3D legible grid | | | |
| Sheet 3 / placing: TOP IN FORM has overall+opening+verts (not only 9366) | | | |
| VIEW END 2 has H dims | | | |
| VIEW A / A-A has V dims + A-A label | | | |
| No left-border dim dump | | | |
| Sheet 4 BBS table clean (views parked) | | | |
| No magenta `?` (mark repair off + purge unresolved) | | | |

## Reports

- `fit_report_W10-175.txt` — bboxFailures, tiers, labels  
- `final_report.md`  
- `docs/investigation/w10-175_pdf_vs_tekla_qa.md` — PDF vs Tekla OK/MISMATCH  

## Peers

```powershell
.\TeklaExtractor.exe --civil-drawings --extract-only --mark W10-78
.\TeklaExtractor.exe --civil-drawings --extract-only --mark W10-48
```

Screenshot each Document Manager sheet `- 1` (and W10-175 sheets 2–4).

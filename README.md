# Tekla Structures 2026 — Civil drawing extractors

Live Open API extraction of five 2D drawing types used by civil engineers. Output is JSON (nested by piece) plus flat CSV (one row per BOM/rebar line, `PieceMark` as join key).

Background reading: [docs/CivilDrawings-PDF-Extraction.md](docs/CivilDrawings-PDF-Extraction.md) explains how the PDFs in `Export\CivilDrawings\PDF` were produced and how a drawing ends up inside the sheet boundary.

## Build (x64 only)

Tekla Structures **2026 only loads x64** Open API extensions. **x86 / AnyCPU will not load.**

This install’s Open API is **.NET Framework 4.8** (`Net48Runtime\Tekla.Structures.dll`), not .NET 8. The project is locked to that:

| Setting | Value |
|---|---|
| `TargetFramework` | `net48` |
| `PlatformTarget` | `x64` |
| `Platforms` | `x64` |
| Tekla DLLs | `C:\Program Files\Tekla Structures\2026.0\bin\` (2026 only — do not mix older versions) |

```powershell
cd C:\Users\ASUS\Desktop\2d_tekla
dotnet build -p:PlatformTarget=x64
```

Tekla Structures must be open with a model, with a cast unit selected:

```powershell
dotnet run -- --civil-drawings
```

Outputs go to `Export\CivilDrawings\new with macros\`. See the section below for every run mode.

## Terminal run commands

All commands are run from the project root with Tekla Structures open on the target model.

```powershell
cd C:\Users\ASUS\Desktop\2d_tekla
dotnet build -p:PlatformTarget=x64
dotnet run --no-build -- <flags>
```

If the build fails with a file-lock error, a previous run is still alive. Stop it first:

```powershell
Get-Process TeklaExtractor -ErrorAction SilentlyContinue | Stop-Process -Force
```

### Civil drawing runs

- `dotnet run -- --civil-drawings`  
  Default and safest mode. Runs the local macros (PG1 / PG2 / PG3 for walls and columns, BM_SET for beams) on the **current Tekla selection only**, then extracts. Select a cast unit in the model first. Output: `Export\CivilDrawings\new with macros\`.

- `dotnet run -- --civil-drawings --mark W10-67`  
  Single-piece run for one piece mark. Ignores the Tekla selection, so nothing has to be picked in the model.

- `dotnet run -- --civil-drawings --skip-macros --mark W10-67`  
  No `RunMacro` at all. Creates sheets through the Open API (`CastUnitDrawing.Insert`) and extracts only the sheets created by that run. Output: `Export\CivilDrawings\new without macros\`.

- `dotnet run -- --civil-drawings --extract-only`  
  Extracts existing sheets without creating anything. Add `--all` to resume across the whole model.

- `dotnet run -- --civil-drawings --extract-only --all --to-root`  
  Same as the **2 Sep** root extract: prints existing CU sheets into `Export\CivilDrawings\PDF` (plus SHOP/ERECTION/CONNECTION/REBAR_BBS at the CivilDrawings root). No macros, no leftover creates.

- `dotnet run -- --civil-drawings --mark W10-67 --clean-mark`  
  Deletes that piece's existing sheets in Tekla before creating new ones. Destructive, and requires `--mark`.

- `dotnet run -- --civil-drawings --all`  
  Whole WALL / COLUMN / BEAM model. Long-running and prone to crashing Tekla, which is why it must be requested explicitly.

### Flag reference

| Flag | Effect |
|---|---|
| `--civil-drawings` (`--civil`, `--civil-extract`) | Entry point for the five civil extractors |
| `--skip-macros` | Open API sheet creation instead of macros; writes to `new without macros` |
| `--extract-only` (`--resume`) | Skip creation, extract what already exists |
| `--mark <piece>` | Limit the run to one piece mark |
| `--all` (`--full`) | Whole model instead of the Tekla selection |
| `--to-root` (`--legacy-root`) | Write under `Export/CivilDrawings` (PDF at root) like the 2 Sep run |
| `--clean-mark` (`--delete-existing`) | Delete that mark's sheets before creating; needs `--mark` |

Without `--all` and without `--mark`, the run is restricted to the Tekla selection. This is deliberate: earlier full-model runs crashed Tekla.

### Other useful runs

```powershell
dotnet run -- --extract                  # ground-truth model dump
dotnet run -- --inventory                # every drawing → drawings_inventory.json + .csv
dotnet run -- --shop-drawings            # up-to-date cast unit shop drawings with contours
dotnet run -- --drawings --kind cast     # create drawings by kind, then export PDFs
dotnet run -- --gad                      # GADrawing JSON + PDF, floor-wise
dotnet run -- --floor-wise               # Export/<floor>/*.pdf + floor_wise.json
dotnet run -- --correct                  # fix existing drawings (CatA-CatG)
dotnet run -- --html-only                # rebuild index.html + manifest.json only
dotnet run -- --coverage-only            # rebuild coverage_report.txt only
```

Run `dotnet run` with no flags for the interactive menu, which accepts `civil`, `civil skip-macros`, `civil all`, `deep`, `inventory` and similar commands.

Before `--drawings`, run Numbering for modified objects in Tekla, otherwise sheets cannot be set active and PDF export fails.

### What a run prints

Progress lines are prefixed by stage: `[Civil]` for the batch extractor, `[Macros]` for macro or Open API creation, `[DrawingGenerator]` for PDF and DWG output. Useful markers:

- `drawings <before> → <after> (delta=N)` — how many sheets the run created
- `matched=N` — sheets that passed the filter and were extracted
- `blockedMacroSheets=N` — sheets deliberately excluded from an Open API run
- `PDFs=N` / `DWGs=N` — files written

PDF export uses `DrawingHandler.PrintDrawing` with the `TS PDF Writer` instance. DWG has no output type in Tekla 2026, so it is attempted through a catalog printer named `DWG`, `DWG/DXF` or `DXF`; if none exists, the DWG step is skipped and only PDFs are written.

### Output folders

| Path | Produced by |
|---|---|
| `Export\CivilDrawings\new with macros\` | macro runs (default, `--mark`, `--all`) |
| `Export\CivilDrawings\new without macros\` | `--skip-macros` Open API runs |
| `Export\CivilDrawings\PDF\`, `SHOP\`, `ERECTION\`, ... | older runs made before the folder split |

Each output folder holds `PDF\`, `DWG\`, the five JSON type folders, the combined CSVs, `macros.log`, `extract_errors.log`, `coverage_report.txt` and `index.html`.

## Modules

Folders below are relative to the run's output folder (`new with macros` or `new without macros`).

| Drawing type | Class | Folder |
|---|---|---|
| GA (General Arrangement) | `GaCivilExtractor` | `GA/` |
| Erection | `ErectionCivilExtractor` | `ERECTION/` |
| Shop / production piece | `ShopProductionExtractor` | `SHOP/` |
| Connection detail | `ConnectionDetailExtractor` | `CONNECTION/` |
| Rebar / BBS | `RebarBbsExtractor` | `REBAR_BBS/` |

Batch entry: `CivilDrawingBatchExtractor` (`DrawingHandler.GetDrawings()`). Failed pieces are logged to `extract_errors.log` in the output folder and the run continues.

File names: `{ProjectCode}_{PieceMark}_{DrawingType}.json`  
PDF names come from the Tekla drawing mark, so `[W10-67 - 1]` becomes `PDF\W10-67_-_1.pdf`.  
Combined CSVs: `GA.csv`, `ERECTION.csv`, `SHOP.csv`, `CONNECTION.csv`, `REBAR_BBS.csv` (deduplicated after every run)  
One sample JSON per type is copied to `samples/`.

Units are **raw millimetres** (and mm³ / kg). Grade strings (`400W`, `C40`, `40 MPa`) are not converted.

## Field → Tekla Open API (TS2026)

### Shared

| Field | API |
|---|---|
| Live connection | `Tekla.Structures.Model.Model.GetConnectionStatus()` |
| Drawings | `DrawingHandler.GetDrawings()` → `DrawingEnumerator` |
| Link sheet → 3D | `SinglePartDrawing.PartIdentifier`, `AssemblyDrawing.AssemblyIdentifier`, `CastUnitDrawing.CastUnitIdentifier`, `DrawingHandler.GetModelObjectIdentifiers(Drawing)` |
| Resolve object | `Model.SelectModelObject(Identifier)`, `Assembly.GetMainPart()`, `GetSecondaries()` |
| Piece mark | `GetReportProperty("CAST_UNIT_POS" / "ASSEMBLY_POS" / "PART_POS")` |
| Project code | `Model.GetProjectInfo().ProjectNumber` |
| Report / UDA | `ModelObject.GetReportProperty`, `GetUserProperty` |
| Drawing plugin input | `Tekla.Structures.Drawing.Plugin.GetPluginInput()` (**new in TS2026**) |
| Shared models | `ModelSharingHandler` exists on this SDK; extractors do **not** call it (live local model only) |

### 1. GA

| Field | API |
|---|---|
| Grid lines / labels | `Grid.CoordinateX/Y`, `Grid.LabelX/Y` |
| Piece XYZ | `Beam.StartPoint` / `EndPoint`; `Part.GetCoordinateSystem().Origin` |
| Floor / level | UDA `FLOOR_LEVEL`, `FLOOR`, `STOREY`, … |
| Overall building size | min/max of piece start/end |
| Piece list per grid/level | grouped by floor UDA + inferred grid from XY |
| Plan views | `Drawing.GetSheet().GetViews()` when a `GADrawing` exists |

If the model has no `GADrawing` sheets, a model-level GA is written from grids + assembly main parts.

### 2. Erection

| Field | API |
|---|---|
| Piece mark / position | `CAST_UNIT_POS` / `ASSEMBLY_POS`, coordinate-system origin |
| Orientation | `GetCoordinateSystem().AxisX/Y` |
| Anchors / embeds | secondary parts + `Part.GetBolts()` (`BoltGroup.BoltPositions`, `BoltStandard`, `BoltSize`) |
| Plan / elevation refs | drawing `View.ViewType` (`TopView` / `FrontView`) |
| Sequence notes | `Drawing.Title1/2/3`, `Drawing.Name` |

### 3. Shop / production (piece ticket)

Aligned to civil piece drawings (e.g. P22-132 / W8-22):

| Field | API |
|---|---|
| 28-day strength | UDA `STRENGTH_28DAY` / `FCK` / `FC_PRIME`, else parsed from `MATERIAL` / `CONCRETE_GRADE` |
| Stripping strength | UDA `STRIPPING_STRENGTH` |
| Weight (kg, lbs derived) | `GetReportProperty("WEIGHT")` — kg is raw; lbs is display-only (`× 2.2046226218`) |
| Air entrainment | UDA `AIR_ENTRAINMENT` |
| Min cover | `COVER` / `COVER_THICKNESS` |
| Class | `Part.Class` |
| Concrete volume | `VOLUME` (mm³ raw; m³ derived `/ 1e9`) |
| Hardware BOM | secondary parts (anchors, inserts, sleeves, grout tubes, conduit, plates) |
| Views End1 / End2 / Side A / B | `View.Name` / `View.ViewType` mapped by `MapShopViewName` |
| Identification | `Drawing.Name`, `Mark`, `CreationDate`, project info |

### 4. Connection detail

| Field | API |
|---|---|
| Joint type | heuristic from hardware + names (`piece-to-piece` vs `piece-to-foundation`) |
| Hardware list | same embed/bolt pass as shop |
| Detail dims / callouts | `StraightDimension` / `StraightDimensionSet` on sheet views |

### 5. Rebar / BBS

TS2026 unified **rebar sets** and groups. Extractor reads whichever is present:

| Source | API |
|---|---|
| Group | `RebarGroup` (`Size`, `Grade`, `Spacings`, hooks, `GetRebarGeometries(true)`) |
| Single | `SingleRebar` |
| Set | `RebarSet` + `RebarSet.RebarProperties` + `RebarSet.GetReinforcements()` (child `SingleRebar`) |
| Frozen (post-fab) | `RebarSet.FrozenState` = `NOT_FROZEN` / `PARTIALLY_FROZEN` / `FULLY_FROZEN` |
| Parenting | `RebarSet.FatherPart`, `RebarSet.GetAssembly()` |
| Bend A–H, H2, J, K, K2, O | `GetReportProperty("DIM_A"…"DIM_O")`, else legs from `RebarGeometry.Shape.Points` |
| Qty / length / area | `GetNumberOfRebars()`, `LENGTH`, `AREA`, `WEIGHT` |
| Shape | `SHAPE` / `SHAPE_CODE`; classified straight / bent / stirrup |

`Part.GetReinforcements()` is scanned first; leftover `RebarSet` objects are picked up via `GetAllObjectsWithType(typeof(RebarSet))`.

## JSON shape (shop example)

```json
{
  "Header": { "DrawingType": "SHOP", "ProjectCode": "...", "PieceMark": "W8-22", "Units": "mm" },
  "GeneralNotes": { "ConcreteGrade": "40 MPa", "WeightKg": 1234.5, "MinCoverMm": 40, "Class": "1" },
  "Bom": [ { "Mark": "...", "Description": "...", "Quantity": 4 } ],
  "Hardware": [ { "Kind": "GROUT_TUBE", "Position": { "X": 0, "Y": 0, "Z": 0 } } ],
  "Rebar": [ { "Mark": "R1", "Size": "15M", "Grade": "400W", "A": 1200, "B": 200 } ],
  "Dimensions": [],
  "Views": [ { "Name": "End1" }, { "Name": "Side A" } ]
}
```

Missing optional fields are `null` in JSON and blank in CSV.

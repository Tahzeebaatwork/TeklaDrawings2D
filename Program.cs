// Program.cs — 3D Tekla → Ground Truth DB → 2D drawings / PDFs
// ─────────────────────────────────────────────────────────────────
// Flowchart:
//   3D Tekla Model
//     → ModelReader.cs (Connect + Retry)
//     → Extract Data (Mark, Type, Profile, Material, Floor, Dimensions, Weight, GUID)
//         → Ground Truth DB (JSON + CSV)
//     → DrawingGenerator.cs (DrawingHandler API)
//         → Generated 2D Drawings (Single-Part / Assembly / Cast Unit)
//         → FloorWiseDrawingExtractor (FLOOR/Z groups → PDF + 2D JSON)
//         → Exported 2D PDFs
//
// CLI:
//   dotnet run
//   dotnet run -- --extract
//   dotnet run -- --drawings
//   dotnet run -- --drawings --kind single
//   dotnet run -- --drawings --kind assembly
//   dotnet run -- --drawings --kind cast
//   dotnet run -- --inventory
//   dotnet run -- --civil-drawings              (Tekla selection only)
//   dotnet run -- --civil-drawings --all        (full model; crash risk)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tekla.Structures.Model;
using TeklaExtractor.Services;

namespace TeklaExtractor
{
    /// <summary>
    /// Collects live Tekla Part objects for DrawingGenerator.
    /// Ground-truth JSON/CSV is written by GroundTruthExtractor.
    /// </summary>
    internal static class TeklaModelExtractor
    {
        public static List<Part> ExtractAll(Model model)
        {
            return GroundTruthExtractor.ExtractParts(model);
        }
    }

    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            PrintBanner();

            string baseDir = ResolveBaseDir(args);
            Console.WriteLine($"  Base dir   : {baseDir}\n");

            if (HasFlag(args, "--coverage-only") || HasFlag(args, "--coverage"))
            {
                CivilCoverageReporter.Write(Path.Combine(baseDir, "Export", "CivilDrawings"));
                return 0;
            }

            if (HasFlag(args, "--html-only") || HasFlag(args, "--html"))
            {
                string civilRoot = Path.Combine(baseDir, "Export", "CivilDrawings");
                CivilHtmlReview.WriteFolderIndex(Path.Combine(civilRoot, CivilDrawingTypes.MacrosFolder), CivilDrawingTypes.MacrosFolder);
                CivilHtmlReview.WriteFolderIndex(Path.Combine(civilRoot, "WithMacros"), "WithMacros");
                CivilHtmlReview.WriteFolderIndex(Path.Combine(civilRoot, CivilDrawingTypes.OpenApiFolder), CivilDrawingTypes.OpenApiFolder + " (Open API)");
                CivilHtmlReview.WriteCompare(civilRoot);
                Console.WriteLine("[HTML] viewer → " + Path.Combine(civilRoot, "index.html"));
                return 0;
            }

            bool extractOnly = HasFlag(args, "--extract");
            bool wantDeep = HasFlag(args, "--deep");
            bool selectedOnly = HasFlag(args, "--selected");
            bool wantDrawingJson = HasFlag(args, "--drawing-json") || HasFlag(args, "--dump-drawings") || HasFlag(args, "--pdf-json");
            bool pdfOnly = HasFlag(args, "--pdf-only") || HasFlag(args, "--pdf-json");
            bool wantDrawings = HasFlag(args, "--drawings") || args.Any(a => a.StartsWith("--drawings=", StringComparison.OrdinalIgnoreCase));
            bool wantCorrect = HasFlag(args, "--correct") || HasFlag(args, "--fix");
            bool wantFloorWise = HasFlag(args, "--floor-wise") || HasFlag(args, "--floorwise") || HasFlag(args, "--export-floor");
            bool wantGad = HasFlag(args, "--gad") || HasFlag(args, "--ga") || HasFlag(args, "--ga-json");
            bool wantCastUnits = HasFlag(args, "--cast-units") || HasFlag(args, "--cu-json")
                || HasFlag(args, "--piece-tickets") || HasFlag(args, "--floor-cast");
            bool wantDrawingQa = HasFlag(args, "--drawing-qa") || HasFlag(args, "--active-drawing")
                || HasFlag(args, "--qa-json");
            bool wantShop = HasFlag(args, "--shop-drawings") || HasFlag(args, "--cu-shop")
                || HasFlag(args, "--cast-unit-shop") || HasFlag(args, "--piece-ticket-drawings")
                || HasFlag(args, "--include-part-contours");
            bool wantInventory = HasFlag(args, "--inventory") || HasFlag(args, "--drawings-inventory");
            bool wantCivil = HasFlag(args, "--civil-drawings") || HasFlag(args, "--civil") || HasFlag(args, "--civil-extract");
            bool wantDelete = HasFlag(args, "--delete") || HasFlag(args, "--delete-drawing") || HasFlag(args, "--clean");
            bool wantOpen = HasFlag(args, "--open") || HasFlag(args, "--open-drawing");
            bool interactive = !extractOnly && !wantDrawings && !wantDeep && !wantDrawingJson
                && !pdfOnly && !wantCorrect && !wantFloorWise && !wantGad && !wantCastUnits
                && !wantDrawingQa && !wantShop && !wantInventory && !wantCivil && !wantDelete && !wantOpen;

            if (pdfOnly && !wantDrawings && !extractOnly && !wantDeep && !wantCorrect && !wantFloorWise && !wantGad && !wantCastUnits && !wantDrawingQa && !wantShop && !wantInventory && !wantCivil)
            {
                RunDrawingJson(baseDir, pdfOnly: true);
                return 0;
            }

            Model tekla;
            try
            {
                var reader = new ModelReader(maxRetries: 4, retryDelayMs: 2000);
                tekla = reader.Connect();
            }
            catch (ModelReaderException ex)
            {
                Console.WriteLine("  " + ex.Message);
                Console.WriteLine("  Open Tekla Structures with a model, then run again.");
                return 1;
            }

            var info = tekla.GetInfo();
            Console.WriteLine($"  Connected  : {info.ModelName}");
            Console.WriteLine($"  Model dir  : {info.ModelPath}");

            if (wantDelete)
            {
                string mark = FlagValue(args, "--mark");
                if (string.IsNullOrWhiteSpace(mark))
                {
                    Console.WriteLine("[Delete] Error: --mark <piece> is required (e.g. dotnet run -- --delete --mark P6-1).");
                    return 1;
                }
                DrawingSheetCleaner.DeleteByMark(mark);
                return 0;
            }

            if (wantOpen)
            {
                string mark = FlagValue(args, "--mark");
                OpenDrawingInUi(tekla, mark);
                return 0;
            }

            // Ground truth: interactive boot, or --extract.
            if (interactive || extractOnly)
                GroundTruthExtractor.Run(tekla, baseDir);

            // Deep local-CS JSON: --deep and also --extract (full dump).
            if (wantDeep || extractOnly)
                RunDeep(tekla, baseDir, selectedOnly);

            if (wantDrawings)
            {
                RunDrawings(tekla, baseDir, ParseKind(args), correct: true);
                if (!wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return 0;
            }

            if (wantCivil)
            {
                int civilCode = RunCivilDrawings(tekla, baseDir, args);
                if (!wantInventory && !wantShop && !wantDrawingQa && !wantCastUnits && !wantGad && !wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return civilCode;
            }

            if (wantInventory)
            {
                RunInventory(tekla, baseDir);
                if (!wantShop && !wantDrawingQa && !wantCastUnits && !wantGad && !wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return 0;
            }

            if (wantShop)
            {
                RunShopDrawings(tekla, baseDir, args);
                if (!wantDrawingQa && !wantCastUnits && !wantGad && !wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return 0;
            }

            if (wantDrawingQa)
            {
                RunDrawingQa(tekla, baseDir);
                if (!wantCastUnits && !wantGad && !wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return 0;
            }

            if (wantCastUnits)
            {
                RunCastUnits(tekla, baseDir);
                if (!wantGad && !wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return 0;
            }

            if (wantGad)
            {
                RunGad(tekla, baseDir);
                if (!wantDrawingJson && !wantCorrect && !wantFloorWise)
                    return 0;
            }

            if (wantFloorWise)
            {
                RunFloorWise(tekla, baseDir);
                if (!wantDrawingJson && !wantCorrect)
                    return 0;
            }

            if (wantCorrect)
            {
                RunCorrect(tekla, baseDir, args);
                if (!wantDrawingJson)
                    return 0;
            }

            if (wantDrawingJson)
            {
                RunDrawingJson(baseDir, pdfOnly);
                return 0;
            }

            if (extractOnly || wantDeep || wantInventory || wantCivil)
                return 0;

            PrintHelp();
            RunMenu(tekla, baseDir);
            return 0;
        }

        static int RunCivilDrawings(Model tekla, string baseDir, string[] args = null)
        {
            try
            {
                bool skipMacros = args != null && HasFlag(args, "--skip-macros");
                bool extractOnly = args != null && (HasFlag(args, "--extract-only") || HasFlag(args, "--resume"));
                bool wantAll = args != null && (HasFlag(args, "--all") || HasFlag(args, "--full"));
                bool toRoot = args != null && (HasFlag(args, "--to-root") || HasFlag(args, "--legacy-root"));
                bool cleanFirst = args != null && (HasFlag(args, "--clean-mark") || HasFlag(args, "--delete-existing"));
                string mark = FlagValue(args, "--mark");
                // Safe default: Tekla selection only. Full model requires explicit --all.
                // --mark already limits to one piece, so it does not use the selection.
                bool selectedOnly = !wantAll && string.IsNullOrWhiteSpace(mark);
                Console.WriteLine("[Civil] TS2026 Open API → GA / Erection / Shop / Connection / Rebar-BBS JSON+CSV");
                if (wantAll)
                    Console.WriteLine("[Civil] --all: full WALL/COLUMN/BEAM model (same crash risk as before). Prefer --civil-drawings with a selection.");
                if (toRoot)
                    Console.WriteLine("[Civil] --to-root → Export/CivilDrawings (PDF + JSON/CSV at root, Sep 2 layout)");
                if (skipMacros)
                    Console.WriteLine("[Civil] " + CivilDrawingTypes.OpenApiFolder + " folder (Open API create + extract, no RunMacro)");
                else if (extractOnly && toRoot)
                    Console.WriteLine("[Civil] extract-only → Export/CivilDrawings/PDF (existing sheets only)");
                else if (extractOnly)
                    Console.WriteLine("[Civil] extract-only → Export/CivilDrawings/" + CivilDrawingTypes.MacrosFolder);
                else if (selectedOnly)
                    Console.WriteLine("[Civil] selected CU: PG1/PG2/PG3 or BM_SET → Export/CivilDrawings/" + CivilDrawingTypes.MacrosFolder);
                else if (!string.IsNullOrWhiteSpace(mark))
                    Console.WriteLine("[Civil] mark " + mark + " → Export/CivilDrawings/" + CivilDrawingTypes.MacrosFolder);
                else
                    Console.WriteLine("[Civil] " + CivilDrawingTypes.MacrosFolder + " folder (local PG1/PG2/PG3/BM_SET then extract)");

                if (!extractOnly && PrecastDimensionPostProcessor.AbortIfNumberingStale("Civil"))
                    return 1;

                if (cleanFirst)
                {
                    if (string.IsNullOrWhiteSpace(mark))
                        Console.WriteLine("[Civil] --clean-mark ignored: needs --mark <piece>");
                    else
                    {
                        Console.WriteLine("[Civil] deleting existing sheets for " + mark + " before macros");
                        DrawingSheetCleaner.DeleteByMark(mark);
                    }
                }

                bool preflightQa = args != null && HasFlag(args, "--preflight-qa");
                if (preflightQa)
                    Console.WriteLine("[Civil] --preflight-qa → margin, BOM gap, tape delta");
                return new CivilDrawingBatchExtractor(tekla, baseDir, skipMacros, extractOnly, mark, selectedOnly, toRoot, preflightQa).Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Civil] {ex.Message}");
                return 1;
            }
        }

        static void RunInventory(Model tekla, string baseDir)
        {
            try
            {
                Console.WriteLine("[Inventory] DrawingHandler → drawings_inventory.json + .csv");
                new DrawingInventoryExtractor(tekla).Run(baseDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Inventory] {ex.Message}");
            }
        }

        static void RunDeep(Model tekla, string baseDir, bool selectedOnly)
        {
            try
            {
                var deep = new DeepDataExtractor(tekla);
                deep.Run(baseDir, selectedOnly);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Deep] {ex.Message}");
            }
        }

        static void RunDrawingJson(string baseDir, bool pdfOnly = false)
        {
            try
            {
                var dump = new DrawingJsonExtractor(baseDir);
                dump.Run(pdfOnly);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DrawingJSON] {ex.Message}");
            }
        }

        static void RunShopDrawings(Model tekla, string baseDir, string[] args)
        {
            try
            {
                string output = FlagValue(args, "--output")
                    ?? Path.Combine(baseDir, "Export", "CastUnitDrawings");
                bool upToDate = !HasFlag(args, "--all-status");
                bool failEmpty = !HasFlag(args, "--allow-empty-geometry");
                Console.WriteLine("[Shop] Precast piece tickets (Cast Unit drawings) → JSON/SVG/DXF/PDF");
                Console.WriteLine("[Shop] output " + output);
                new CastUnitShopDrawingExtractor(tekla, output, upToDate, failEmpty).Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Shop] {ex.Message}");
            }
        }

        static string FlagValue(string[] args, string flag)
        {
            if (args == null || string.IsNullOrWhiteSpace(flag)) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == null) continue;
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
                if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(flag.Length + 1);
            }
            return null;
        }

        static void RunDrawingQa(Model tekla, string baseDir)
        {
            try
            {
                new ActiveDrawingQaExtractor(tekla).Run(baseDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DrawingQA] {ex.Message}");
            }
        }

        static void RunCastUnits(Model tekla, string baseDir)
        {
            try
            {
                new FloorWiseCastUnitExtractor(tekla).Run(baseDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CastUnits] {ex.Message}");
            }
        }

        static void RunGad(Model tekla, string baseDir)
        {
            try
            {
                Console.WriteLine("[GAD] Tekla Open API → floor-wise GA JSON (members/grids/plan) + PDF...");
                if (!HasExistingGaDrawings())
                {
                    var parts = TeklaModelExtractor.ExtractAll(tekla);
                    new GaDrawingBuilder(tekla).CreateMissingFloorPlans(parts);
                }
                else
                {
                    Console.WriteLine("[GAD] GA sheets already exist — skipping recreate, extracting geometry.");
                }
                var ex = new FloorWiseDrawingExtractor(tekla, baseDir, Path.Combine("Export", "GAD"));
                ex.Run("GA");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GAD] {ex.Message}");
            }
        }

        static bool HasExistingGaDrawings()
        {
            try
            {
                var handler = new Tekla.Structures.Drawing.DrawingHandler();
                if (handler == null || !handler.GetConnectionStatus()) return false;
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    if (en.Current is Tekla.Structures.Drawing.GADrawing)
                        return true;
                }
            }
            catch { /* optional */ }
            return false;
        }

        static void RunFloorWise(Model tekla, string baseDir)
        {
            try
            {
                Console.WriteLine("[FloorWise] Creating missing Assembly + Cast Unit drawings via DrawingHandler...");
                var parts = TeklaModelExtractor.ExtractAll(tekla);
                var gen = new DrawingGenerator();
                if (gen.IsConnected)
                {
                    gen.EnsureCreated(parts, DrawingKind.Assembly);
                    gen.EnsureCreated(parts, DrawingKind.CastUnit);
                }
                else
                {
                    Console.WriteLine("[FloorWise] DrawingHandler not connected — extracting whatever sheets already exist.");
                }

                var ex = new FloorWiseDrawingExtractor(tekla, baseDir);
                ex.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FloorWise] {ex.Message}");
            }
        }

        static void RunCorrect(Model tekla, string baseDir, string[] args = null)
        {
            try
            {
                string mark = FlagValue(args, "--mark");
                var handler = new Tekla.Structures.Drawing.DrawingHandler();
                if (handler.GetConnectionStatus())
                {
                    string pdfDir = Path.Combine(baseDir, "Export", "CivilDrawings", "new with macros", "PDF");
                    var precastPost = new PrecastDimensionPostProcessor(tekla, handler, pdfDir);

                    var active = handler.GetActiveDrawing();
                    if (active != null && (string.IsNullOrWhiteSpace(mark) || (active.Mark ?? "").IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        Console.WriteLine($"[Correct] Post-processing active UI drawing '{active.Mark}' ({active.Name})...");
                        precastPost.Fit(active);
                    }
                    else
                    {
                        var en = handler.GetDrawings();
                        var matched = new List<Tekla.Structures.Drawing.Drawing>();
                        while (en != null && en.MoveNext())
                        {
                            var d = en.Current;
                            if (d != null)
                            {
                                if (string.IsNullOrWhiteSpace(mark) || (d.Mark ?? "").IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0)
                                    matched.Add(d);
                            }
                        }
                        Console.WriteLine($"[Correct] Found {matched.Count} drawing(s) for precast post-processing.");
                        foreach (var d in matched)
                        {
                            if (PrecastDimensionPostProcessor.IsHardwareShopSheet(d))
                            {
                                Console.WriteLine($"[Correct] Applying 7-tier precision dimensions to '{d.Mark}' ({d.Name})...");
                                precastPost.Fit(d);
                            }
                        }
                    }
                }
                var post = new DrawingPostProcessor(tekla);
                post.Run(baseDir, mark);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Correct] {ex.Message}");
            }
        }

        static void OpenDrawingInUi(Model tekla, string mark)
        {
            if (string.IsNullOrWhiteSpace(mark))
            {
                Console.WriteLine("[Open] Error: please specify --mark <piece> (e.g. dotnet run -- --open --mark P6-1)");
                return;
            }
            var handler = new Tekla.Structures.Drawing.DrawingHandler();
            if (!handler.GetConnectionStatus())
            {
                Console.WriteLine("[Open] DrawingHandler not connected. Open Tekla Structures with a model.");
                return;
            }
            var en = handler.GetDrawings();
            Tekla.Structures.Drawing.Drawing target = null;
            while (en != null && en.MoveNext())
            {
                var d = en.Current;
                if (d != null && (d.Mark ?? "").IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    target = d;
                    break;
                }
            }
            if (target != null)
            {
                try { handler.UpdateDrawing(target); } catch { }
                bool opened = handler.SetActiveDrawing(target, false);
                if (!opened) opened = handler.SetActiveDrawing(target, false, true);
                if (opened)
                    Console.WriteLine($"[Open] Success: Drawing '{target.Mark}' ({target.Name}) is now OPEN in Tekla Drawing Editor UI!");
                else
                    Console.WriteLine($"[Open] Failed to set drawing '{target.Mark}' active in Tekla UI.");
            }
            else
            {
                Console.WriteLine($"[Open] Drawing matching mark '{mark}' not found in Document Manager (Ctrl+L).");
            }
        }

        static void RunDrawings(Model tekla, string baseDir, DrawingKind kind, bool correct = true)
        {
            Console.WriteLine($"\n[Drawings] Generating {kind} drawings + PDF export...");

            var drawGen = new DrawingGenerator();
            if (!drawGen.IsConnected)
            {
                Console.WriteLine("[Drawings] DrawingHandler is not connected. Open the model in Tekla and retry.");
                return;
            }

            var parts = TeklaModelExtractor.ExtractAll(tekla);
            Console.WriteLine($"[Drawings] {parts.Count} parts in model.");

            DrawingPostProcessor post = null;
            if (correct)
            {
                post = new DrawingPostProcessor(tekla);
                Console.WriteLine("[Drawings] CatA–CatG post-processor enabled (correct before PDF).");
            }

            string outDir = Path.Combine(baseDir, "drawings");
            var results = drawGen.GenerateAndExport(parts, kind, outDir, post);

            int created = results.Count(r => r.Created);
            int exported = results.Count(r => r.Exported);
            int failed = results.Count(r => !string.IsNullOrEmpty(r.Error) || (r.Created && !r.Exported));

            Console.WriteLine($"[Drawings] created={created}  pdf={exported}  failed={failed}  → {outDir}");
        }

        static void RunMenu(Model tekla, string baseDir)
        {
            while (true)
            {
                Console.Write("\n  Command: ");
                string input = (Console.ReadLine() ?? "").Trim();
                if (string.IsNullOrEmpty(input)) continue;

                string lower = input.ToLowerInvariant();
                if (lower == "exit" || lower == "quit")
                {
                    Console.WriteLine("  Bye!");
                    break;
                }

                if (lower == "help") { PrintHelp(); continue; }

                if (lower == "extract" || lower == "gt" || lower == "groundtruth")
                {
                    GroundTruthExtractor.Run(tekla, baseDir);
                    continue;
                }

                if (lower == "deep" || lower == "deep all")
                {
                    RunDeep(tekla, baseDir, selectedOnly: false);
                    continue;
                }

                if (lower == "deep selected")
                {
                    RunDeep(tekla, baseDir, selectedOnly: true);
                    continue;
                }

                if (lower == "civil skip-macros" || lower == "civil-drawings skip-macros")
                {
                    RunCivilDrawings(tekla, baseDir, new[] { "--skip-macros" });
                    continue;
                }

                if (lower == "civil all" || lower == "civil-drawings all" || lower == "civil-drawings --all")
                {
                    RunCivilDrawings(tekla, baseDir, new[] { "--all" });
                    continue;
                }

                if (lower == "civil selected" || lower == "civil-drawings selected"
                    || lower == "civil" || lower == "civil-drawings" || lower == "civil-extract")
                {
                    RunCivilDrawings(tekla, baseDir);
                    continue;
                }

                if (lower == "inventory" || lower == "drawings-inventory")
                {
                    RunInventory(tekla, baseDir);
                    continue;
                }

                if (lower == "shop-drawings" || lower == "cu-shop" || lower == "piece-ticket-drawings")
                {
                    RunShopDrawings(tekla, baseDir, new string[0]);
                    continue;
                }

                if (lower == "drawing-qa" || lower == "active-drawing" || lower == "qa-json" || lower == "qa")
                {
                    RunDrawingQa(tekla, baseDir);
                    continue;
                }

                if (lower == "cast-units" || lower == "cu-json" || lower == "piece-tickets" || lower == "floor-cast")
                {
                    RunCastUnits(tekla, baseDir);
                    continue;
                }

                if (lower == "gad" || lower == "ga" || lower == "ga-json")
                {
                    RunGad(tekla, baseDir);
                    continue;
                }

                if (lower == "floor" || lower == "floor-wise" || lower == "floorwise" || lower == "export-floor")
                {
                    RunFloorWise(tekla, baseDir);
                    continue;
                }

                if (lower == "correct" || lower == "fix" || lower == "post")
                {
                    RunCorrect(tekla, baseDir);
                    continue;
                }

                if (lower.StartsWith("correct ") || lower.StartsWith("fix "))
                {
                    string m = input.Substring(input.IndexOf(' ') + 1).Trim();
                    RunCorrect(tekla, baseDir, new[] { "--mark", m });
                    continue;
                }

                if (lower.StartsWith("delete ") || lower.StartsWith("clean "))
                {
                    string m = input.Substring(input.IndexOf(' ') + 1).Trim();
                    DrawingSheetCleaner.DeleteByMark(m);
                    continue;
                }

                if (lower.StartsWith("open "))
                {
                    string m = input.Substring(input.IndexOf(' ') + 1).Trim();
                    OpenDrawingInUi(tekla, m);
                    continue;
                }

                if (lower == "drawing-json" || lower == "dump" || lower == "json")
                {
                    RunDrawingJson(baseDir);
                    continue;
                }

                if (lower == "drawings" || lower == "drawings assembly" || lower == "pdf")
                {
                    RunDrawings(tekla, baseDir, DrawingKind.Assembly);
                    continue;
                }

                if (lower == "drawings single" || lower == "drawings part")
                {
                    RunDrawings(tekla, baseDir, DrawingKind.SinglePart);
                    continue;
                }

                if (lower == "drawings cast" || lower == "drawings cu")
                {
                    RunDrawings(tekla, baseDir, DrawingKind.CastUnit);
                    continue;
                }

                Console.WriteLine("  Unknown command. Type help.");
            }
        }

        static DrawingKind ParseKind(string[] args)
        {
            string kind = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--kind", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    kind = args[i + 1];
                else if (args[i].StartsWith("--kind=", StringComparison.OrdinalIgnoreCase))
                    kind = args[i].Substring("--kind=".Length);
                else if (args[i].StartsWith("--drawings=", StringComparison.OrdinalIgnoreCase))
                    kind = args[i].Substring("--drawings=".Length);
            }

            switch ((kind ?? "assembly").Trim().ToLowerInvariant())
            {
                case "single":
                case "part":
                case "singlepart":
                case "w":
                    return DrawingKind.SinglePart;
                case "cast":
                case "cu":
                case "castunit":
                    return DrawingKind.CastUnit;
                default:
                    return DrawingKind.Assembly;
            }
        }

        static bool HasFlag(string[] args, string flag)
        {
            return args != null && args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        }

        static string ResolveBaseDir(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("--")) continue;
                if (Directory.Exists(arg))
                    return Path.GetFullPath(arg);
            }

            return Directory.GetCurrentDirectory();
        }

        static void PrintBanner()
        {
            Console.WriteLine("  ╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  Tekla 2D Pipeline  —  Ground Truth + Drawing Export    ║");
            Console.WriteLine("  ║  ModelReader → Extract → Drawings → PDF                 ║");
            Console.WriteLine("  ╚══════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }

        static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("  Commands:");
            Console.WriteLine("    extract                 write ground_truth.json + .csv");
            Console.WriteLine("    deep                    steel / precast / RCC JSON (local CS)");
            Console.WriteLine("    deep selected           same, but only objects selected in Tekla");
            Console.WriteLine("    civil-drawings          macros on Tekla selection → Export/CivilDrawings/new with macros");
            Console.WriteLine("    civil-drawings all      FULL model macros (crash risk) → Export/CivilDrawings/new with macros");
            Console.WriteLine("    civil-drawings skip-macros   Open API create+extract (selection) → Export/CivilDrawings/WithoutMacros");
            Console.WriteLine("    inventory               all drawings → drawings_inventory.json + .csv");
            Console.WriteLine("    shop-drawings           all Cast Unit piece tickets → JSON/SVG/DXF/PDF");
            Console.WriteLine("    drawing-qa              active CU/Assembly drawing → title/BOM/dims/marks JSON");
            Console.WriteLine("    cast-units              Model API floor-wise CU/Assembly JSON (piece tickets)");
            Console.WriteLine("    gad                     GA drawings only → Export/GAD (floor-wise JSON+PDF)");
            Console.WriteLine("    floor-wise              group by FLOOR/Z, PDF + deep 2D JSON");
            Console.WriteLine("    correct                 CatA–CatG DrawingHandler post-processor");
            Console.WriteLine("    drawing-json            dump all drawing types → JSON + readable TXT");
            Console.WriteLine("    drawings                Assembly drawings + PDFs");
            Console.WriteLine("    drawings single         Single-Part drawings + PDFs");
            Console.WriteLine("    drawings cast           Cast Unit drawings + PDFs");
            Console.WriteLine("    help | exit");
            Console.WriteLine();
            Console.WriteLine("  CLI flags:");
            Console.WriteLine("    --extract");
            Console.WriteLine("    --deep [--selected]");
            Console.WriteLine("    --drawing-json          Tekla sheets + PDF text/coords");
            Console.WriteLine("    --pdf-only              only parse drawings\\*.pdf");
            Console.WriteLine("    --civil-drawings        PG1/PG2/BM_SET on the Tekla selection → Export/CivilDrawings/new with macros");
            Console.WriteLine("    --civil-drawings --all  FULL WALL/COLUMN/BEAM model (crash risk); must be explicit");
            Console.WriteLine("    --civil-drawings --mark W10-50   one piece (smoke) then extract");
            Console.WriteLine("    --civil-drawings --mark W10-50 --clean-mark   delete that piece's old sheets first");
            Console.WriteLine("    --civil-drawings --extract-only  extract selected existing CU sheets (add --all to resume whole model)");
            Console.WriteLine("    --civil-drawings --extract-only --all --to-root   Sep-2 layout → Export/CivilDrawings/PDF");
            Console.WriteLine("    --preflight-qa          margin ≥ 15 mm, BOM gap ≥ 10 mm, tape delta ≤ 0.1 mm");
            Console.WriteLine("    --html-only             rebuild Export/CivilDrawings/index.html + manifest.json");
            Console.WriteLine("    --inventory             all drawings → drawings_inventory.json + .csv");
            Console.WriteLine("    --shop-drawings         all up-to-date Cast Unit shop drawings (contours)");
            Console.WriteLine("    --output <dir>          shop-drawing export folder");
            Console.WriteLine("    --drawing-qa            active CU/Assembly drawing QA JSON → model folder");
            Console.WriteLine("    --cast-units            Model API floor-wise CU/Assembly JSON → model folder");
            Console.WriteLine("    --gad                   Tekla Open API GADrawing JSON + PDF, floor-wise");
            Console.WriteLine("    --floor-wise            Export/<floor>/*.pdf + floor_wise.json");
            Console.WriteLine("    --correct               fix existing drawings (CatA–CatG)");
            Console.WriteLine("    --drawings [--kind assembly|single|cast]  (auto-corrects before PDF)");
            Console.WriteLine();
            Console.WriteLine("  Before --drawings: run Numbering (modified objects) in Tekla.");
        }
    }
}

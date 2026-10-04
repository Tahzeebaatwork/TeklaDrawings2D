using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Batch: DrawingHandler.GetDrawings() → five civil extractors →
    /// {ProjectCode}_{PieceMark}_{DrawingType}.json/csv plus combined CSVs.
    /// </summary>
    public class CivilDrawingBatchExtractor
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;
        private readonly string _outputRoot;
        private readonly string _civilRoot;
        private readonly bool _skipMacros;
        private readonly bool _extractOnly;
        private readonly bool _selectedOnly;
        private readonly bool _toRoot;
        private readonly string _markFilter;
        private HashSet<string> _selectedMarks;
        /// <summary>Sheets created by this Open API run; when set, nothing else is extracted.</summary>
        private List<Drawing> _onlyDrawings;
        private DateTime _runStart = DateTime.Now;
        private readonly bool _preflightQa;
        private bool _qaFailed;

        public CivilDrawingBatchExtractor(TSModel.Model model, string baseDir, bool skipMacros = false,
            bool extractOnly = false, string markFilter = null, bool selectedOnly = false, bool toRoot = false,
            bool preflightQa = false)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
            _skipMacros = skipMacros;
            _extractOnly = extractOnly;
            _selectedOnly = selectedOnly;
            _toRoot = toRoot;
            _preflightQa = preflightQa;
            _markFilter = string.IsNullOrWhiteSpace(markFilter) ? null : markFilter.Trim();
            _civilRoot = Path.Combine(baseDir ?? ".", "Export", "CivilDrawings");
            // Sep 2 style: write PDF/JSON/CSV under Export/CivilDrawings itself (not a subfolder).
            _outputRoot = toRoot
                ? _civilRoot
                : Path.Combine(_civilRoot, skipMacros ? CivilDrawingTypes.OpenApiFolder : CivilDrawingTypes.MacrosFolder);
        }

        public int Run()
        {
            _runStart = DateTime.Now;
            EnsureOutputFolders();

            string log = CivilDrawingSupport.LogPath(_outputRoot);
            if (!_extractOnly && _markFilter == null && !_selectedOnly && File.Exists(log))
                File.Delete(log);

            if (_handler == null || !_handler.GetConnectionStatus())
            {
                Console.WriteLine("[Civil] DrawingHandler not connected.");
            }

            if (!CivilDrawingSupport.Preflight(_model, _handler))
            {
                Console.WriteLine("[Civil] Extraction aborted — pre-flight failed.");
                return 0;
            }

            // Do not recreate PG sheets while model numbering is stale (magenta '?' marks).
            if (!_extractOnly && PrecastDimensionPostProcessor.AbortIfNumberingStale("Civil"))
                return 1;

            var runner = new LocalDrawingMacroRunner(_model, _outputRoot);
            int drawingsBefore = runner.CountDrawings();
            Console.WriteLine("[Civil] drawings before macros=" + drawingsBefore);

            if (_selectedOnly)
            {
                var probe = runner.CollectMembers(_markFilter, selectedOnly: true);
                if (probe.Marks.Count == 0)
                {
                    Console.WriteLine("[Civil] no precast WALL/COLUMN/BEAM in the Tekla selection.");
                    Console.WriteLine("[Civil] Select a Cast Unit / assembly in the model, then retry.");
                    Console.WriteLine("[Civil] Full-model macros require --all (crash risk).");
                    return 0;
                }
                _selectedMarks = probe.Marks;
                Console.WriteLine("[Civil] selected marks: " + string.Join(", ", _selectedMarks.OrderBy(m => m)));
            }

            try
            {
                if (_extractOnly)
                {
                    Console.WriteLine("[Civil] extract-only: skip drawing create");
                }
                else if (_skipMacros)
                {
                    Console.WriteLine("[Civil] " + CivilDrawingTypes.OpenApiFolder + ": Open API CastUnitDrawing.Insert + GA (no RunMacro)");
                    var created = runner.RunOpenApi(_markFilter, _selectedOnly);
                    if (_markFilter != null || _selectedOnly)
                    {
                        // Macro sheets share the piece mark, so the mark filter alone cannot keep
                        // them out of this folder. Pin the extract to the sheets we just inserted.
                        _onlyDrawings = created ?? new List<Drawing>();
                        Console.WriteLine("[Civil] extract restricted to " + _onlyDrawings.Count +
                            " Open API sheets (macro sheets excluded)");
                    }
                }
                else
                {
                    bool smoke = _markFilter != null && !_selectedOnly;
                    if (_selectedOnly)
                        Console.WriteLine("[Civil] " + CivilDrawingTypes.MacrosFolder + ": PG1/PG2/PG3 or BM_SET on Tekla selection");
                    else if (smoke)
                        Console.WriteLine("[Civil] " + CivilDrawingTypes.MacrosFolder + " smoke: PG1/PG2/PG3 for " + _markFilter + " (no beams, no floor GA)");
                    else
                        Console.WriteLine("[Civil] " + CivilDrawingTypes.MacrosFolder + ": local WALL/COLUMN/BEAM macros then extract");
                    runner.RunMacros(_markFilter, smoke, _selectedOnly);
                }
            }
            catch (Exception ex)
            {
                CivilDrawingSupport.LogError(_outputRoot, _skipMacros ? "open-api create" : "local macros", ex);
            }

            int drawingsAfter = runner.CountDrawings();
            Console.WriteLine("[Civil] drawings " + drawingsBefore + " → " + drawingsAfter +
                " (delta=" + (drawingsAfter - drawingsBefore) + ")");

            if (!_extractOnly && _markFilter == null && !_selectedOnly && _skipMacros)
                ClearPreviousOutputs();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExtractAllDrawings(seen);

            // Pure Sep-2-style root extract never creates sheets. Leftover macros only for
            // resume into the macros subfolder (not --to-root).
            if (_extractOnly && _markFilter == null && !_skipMacros && !_selectedOnly && !_toRoot)
            {
                var all = runner.CollectMembers();
                var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in all.Marks)
                {
                    if (!SeenContains(seen, m))
                        missing.Add(m);
                }
                if (missing.Count > 0)
                {
                    Console.WriteLine("[Civil] leftover marks with no CU sheet: " + missing.Count);
                    try { runner.RunMacrosForMissing(missing); }
                    catch (Exception ex) { CivilDrawingSupport.LogError(_outputRoot, "leftover macros", ex); }
                    ExtractAllDrawings(seen);
                }
                else
                    Console.WriteLine("[Civil] every W/C/B mark already had a sheet");
            }
            else if (_extractOnly && _toRoot)
            {
                Console.WriteLine("[Civil] --to-root extract-only: no leftover macros (print existing sheets only)");
            }

            bool skipGaDump = _extractOnly || _markFilter != null || _selectedOnly;
            if (!skipGaDump)
            {
                try
                {
                    Console.WriteLine("[Civil] writing WALL/COLUMN/BEAM GA dump (not grids-only, not every embed).");
                    var ga = new GaCivilExtractor(_model, _handler);
                    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    WriteGa(ga.ExtractWallColumnBeams(_outputRoot), counts);
                }
                catch (Exception ex) { CivilDrawingSupport.LogError(_outputRoot, "W/C/B GA", ex); }
            }
            else
                Console.WriteLine("[Civil] skipped WALL/COLUMN/BEAM GA dump");

            DedupeCombinedCsvs();
            WriteSamples(new Dictionary<string, int>());
            CopyBomCsv();
            CivilCoverageReporter.Write(_outputRoot);
            try { CivilHtmlReview.WriteFolderIndex(_outputRoot, _skipMacros ? CivilDrawingTypes.OpenApiFolder + " (Open API)" : CivilDrawingTypes.MacrosFolder); }
            catch (Exception ex) { Console.WriteLine("[Civil] index.html: " + ex.Message); }
            try { CivilHtmlReview.WriteCompare(_civilRoot); }
            catch (Exception ex) { Console.WriteLine("[Civil] compare.html: " + ex.Message); }
            Console.WriteLine("[Civil] output → " + _outputRoot);
            Console.WriteLine("[Civil] coverage → " + Path.Combine(_outputRoot, "coverage_report.txt"));
            Console.WriteLine("[Civil] review → " + Path.Combine(_outputRoot, "index.html"));
            Console.WriteLine("[Civil] compare → " + Path.Combine(_civilRoot, "compare.html"));
            Console.WriteLine("[Civil] errors → " + log);
            return _qaFailed ? 2 : 0;
        }

        private void EnsureOutputFolders()
        {
            if (!Directory.Exists(_outputRoot))
            {
                Directory.CreateDirectory(_outputRoot);
                Console.WriteLine("[Civil] created output folder → " + _outputRoot);
            }
            else
                Console.WriteLine("[Civil] using existing output folder → " + _outputRoot);

            foreach (var sub in new[] { CivilDrawingTypes.GA, CivilDrawingTypes.Erection, CivilDrawingTypes.Shop,
                         CivilDrawingTypes.Connection, CivilDrawingTypes.RebarBbs, "samples", "PDF", "DWG" })
                Directory.CreateDirectory(Path.Combine(_outputRoot, sub));
        }

        private void ExtractAllDrawings(HashSet<string> seen)
        {
            var ga = new GaCivilExtractor(_model, _handler);
            var erection = new ErectionCivilExtractor(_model, _handler);
            var shop = new ShopProductionExtractor(_model, _handler);
            var conn = new ConnectionDetailExtractor(_model, _handler);
            var bbs = new RebarBbsExtractor(_model, _handler);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var printer = new DrawingGenerator();
            string pdfDir = Path.Combine(_outputRoot, "PDF");
            string dwgDir = Path.Combine(_outputRoot, "DWG");
            int pdfOk = 0;
            int dwgOk = 0;
            int drawings = 0;
            int skipped = 0;
            int matched = 0;
            int blockedMacroSheets = 0;

            // A mark-filtered run still walks every sheet in the model, so report
            // progress more often; otherwise the scan looks hung.
            bool filtered = _markFilter != null || (_selectedMarks != null && _selectedMarks.Count > 0);
            int progressEvery = filtered ? 10 : 25;
            string filterLabel = _markFilter != null
                ? " mark=" + _markFilter
                : filtered ? " selected=" + _selectedMarks.Count : "";

            try
            {
                var en = _handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var drawing = en.Current as Drawing;
                    if (drawing == null) continue;
                    drawings++;
                    if (drawings % progressEvery == 0)
                        Console.WriteLine("[Civil] progress sheets=" + drawings + filterLabel +
                            " matched=" + matched + " pdf=" + pdfOk + " dwg=" + dwgOk + " skip=" + skipped);

                    if (_onlyDrawings != null && !IsFromThisOpenApiRun(drawing))
                    {
                        // Count only sheets that share this run's mark/selection — those are the
                        // macro (or older) sheets we intentionally kept out of the Open API folder.
                        string blockedMark = CivilDrawingSupport.StripMark(SafeMark(drawing));
                        if (MatchesExtractFilter(blockedMark, blockedMark))
                            blockedMacroSheets++;
                        skipped++;
                        continue;
                    }

                    bool isGaSheet = false;
                    try { isGaSheet = drawing is GADrawing; } catch { }
                    if (isGaSheet)
                    {
                        if (_extractOnly || _markFilter != null || _selectedOnly)
                        {
                            skipped++;
                            continue;
                        }
                        try
                        {
                            Console.WriteLine("[Civil] GA sheet '" + SafeMark(drawing) + "' (grids only; W/C/B dump written separately)");
                            WriteGa(ga.Extract(drawing, _outputRoot), counts);
                        }
                        catch (Exception ex)
                        {
                            CivilDrawingSupport.LogError(_outputRoot, SafeMark(drawing) + " GA", ex);
                        }
                        continue;
                    }

                    // Early-skip only when the drawing mark clearly looks like another piece
                    // (contains a digit). Generic names like "Shop Drawing" must resolve
                    // CAST_UNIT_POS via PartsFromDrawing before filtering.
                    string drawingMark = CivilDrawingSupport.StripMark(SafeMark(drawing));
                    if (filtered && LooksLikePieceMark(drawingMark) &&
                        !MatchesExtractFilter(drawingMark, drawingMark))
                    {
                        skipped++;
                        continue;
                    }

                    List<string> types;
                    try { types = CivilDrawingSupport.Classify(drawing); }
                    catch (Exception ex)
                    {
                        CivilDrawingSupport.LogError(_outputRoot, "classify", ex);
                        continue;
                    }

                    List<TSModel.Part> parts;
                    try { parts = CivilDrawingSupport.PartsFromDrawing(_model, _handler, drawing); }
                    catch (Exception ex)
                    {
                        CivilDrawingSupport.LogError(_outputRoot, SafeMark(drawing), ex);
                        parts = new List<TSModel.Part>();
                    }

                    string pieceGuess = CivilDrawingSupport.PieceMark(CivilDrawingSupport.MainPart(parts), drawing);
                    if (seen != null)
                    {
                        if (!string.IsNullOrWhiteSpace(pieceGuess) && pieceGuess != "UNKNOWN")
                            seen.Add(pieceGuess);
                        if (!string.IsNullOrWhiteSpace(drawingMark))
                            seen.Add(drawingMark);
                    }

                    if (!MatchesExtractFilter(drawingMark, pieceGuess))
                    {
                        skipped++;
                        continue;
                    }

                    if (_extractOnly && _markFilter == null && AlreadyExtracted(pieceGuess, drawingMark))
                    {
                        skipped++;
                        continue;
                    }

                    matched++;
                    CivilSheetCapture capture = null;
                    try
                    {
                        Console.WriteLine("[Civil] opening sheet '" + SafeMark(drawing) + "'");
                        capture = CivilDrawingSupport.CaptureActiveSheet(_handler, drawing, pieceGuess, _outputRoot);
                        CivilDrawingSupport.MergeSheetParts(_model, capture, parts);
                    }
                    catch (Exception ex)
                    {
                        CivilDrawingSupport.LogError(_outputRoot, pieceGuess + " open sheet", ex);
                        capture = new CivilSheetCapture { Error = ex.Message };
                    }

                    Console.WriteLine("[Civil] " + string.Join("+", types) + " '" + SafeMark(drawing) +
                        "' piece=" + pieceGuess +
                        " parts=" + parts.Count + " views=" + (capture != null ? capture.Views.Count : 0) +
                        " dims=" + (capture != null ? capture.Dimensions.Count : 0) +
                        " opened=" + (capture != null && capture.Opened));
                    // #region agent log
                    AgentLogSheetIdentity(drawing, pieceGuess, capture);
                    // #endregion

                    foreach (var type in types)
                    {
                        try
                        {
                            if (type == CivilDrawingTypes.GA)
                            {
                                WriteGa(ga.Extract(drawing, _outputRoot), counts);
                            }
                            else if (type == CivilDrawingTypes.Erection)
                                WriteErection(erection.Extract(drawing, parts, _outputRoot, capture), counts);
                            else if (type == CivilDrawingTypes.Shop)
                                WriteShop(shop.Extract(drawing, parts, _outputRoot, capture), counts);
                            else if (type == CivilDrawingTypes.Connection)
                                WriteConnection(conn.Extract(drawing, parts, _outputRoot, capture), counts);
                            else if (type == CivilDrawingTypes.RebarBbs)
                                WriteBbs(bbs.Extract(drawing, parts, _outputRoot, capture), counts);
                        }
                        catch (Exception ex)
                        {
                            CivilDrawingSupport.LogError(_outputRoot, SafeMark(drawing) + " " + type, ex);
                        }
                    }

                    try
                    {
                        string stem = CivilDrawingSupport.Sanitize(
                            CivilDrawingSupport.FirstNonEmpty(SafeMark(drawing), pieceGuess, "sheet"));
                        string pdfPath = Path.Combine(pdfDir, stem + ".pdf");
                        string dwgPath = Path.Combine(dwgDir, stem + ".dwg");
                        ShopFitResult fit = null;
                        if (PrecastDimensionPostProcessor.IsHardwareShopSheet(drawing))
                            fit = new PrecastDimensionPostProcessor(_model, _handler, pdfDir).Fit(drawing);
                        else if (PrecastDimensionPostProcessor.IsPlacingSheet(drawing))
                            new PrecastDimensionPostProcessor(_model, _handler, pdfDir).CleanPlacing(drawing);
                        else if (PrecastDimensionPostProcessor.IsBbsSheet(drawing))
                            new PrecastDimensionPostProcessor(_model, _handler, pdfDir).CleanBbs(drawing);
                        else if (PrecastDimensionPostProcessor.IsSectionsSheet(drawing))
                            new PrecastDimensionPostProcessor(_model, _handler, pdfDir).CleanSections(drawing);
                        if (fit != null && fit.Sheet2Updated)
                            ReprintSibling(printer, pdfDir, fit.SiblingMark);
                        if (_preflightQa && PrecastDimensionPostProcessor.IsHardwareShopSheet(drawing))
                        {
                            if (!PrecastQaValidator.Evaluate(fit))
                                _qaFailed = true;
                        }
                        if (printer.ExportToPdf(drawing, pdfPath))
                        {
                            pdfOk++;
                            Console.WriteLine("[Civil] PDF → " + pdfPath);
                        }
                        if (printer.ExportToDwg(drawing, dwgPath))
                        {
                            dwgOk++;
                            Console.WriteLine("[Civil] DWG → " + dwgPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        CivilDrawingSupport.LogError(_outputRoot, SafeMark(drawing) + " pdf/dwg", ex);
                    }

                    try { _handler.CloseActiveDrawing(false); } catch { }
                }
            }
            catch (Exception ex)
            {
                CivilDrawingSupport.LogError(_outputRoot, "GetDrawings", ex);
            }

            Console.WriteLine("[Civil] drawings scanned=" + drawings);
            Console.WriteLine("[Civil] matched=" + matched + filterLabel);
            Console.WriteLine("[Civil] skipped=" + skipped);
            if (_onlyDrawings != null)
                Console.WriteLine("[Civil] blockedMacroSheets=" + blockedMacroSheets);
            Console.WriteLine("[Civil] PDFs=" + pdfOk + " → " + pdfDir);
            Console.WriteLine("[Civil] DWGs=" + dwgOk + " → " + dwgDir);
            if (matched == 0 && filtered)
            {
                Console.WriteLine("[Civil] no sheet matched" + filterLabel +
                    " — Document Manager has no CU sheet for this mark/selection.");
                Console.WriteLine("[Civil] Create first: --civil-drawings --mark <piece> [--clean-mark] --to-root");
                Console.WriteLine("[Civil] Or Open API: --civil-drawings --skip-macros --mark <piece> --to-root");
            }
            foreach (var kv in counts.OrderBy(k => k.Key))
                Console.WriteLine("[Civil]   " + kv.Key + " = " + kv.Value);
        }

        /// <summary>
        /// True only for a sheet this run inserted: exact database identity plus a
        /// creation-stamp guard, so an older macro sheet with the same mark can never pass.
        /// </summary>
        private bool IsFromThisOpenApiRun(Drawing drawing)
        {
            bool sameObject = false;
            foreach (var created in _onlyDrawings)
            {
                if (created == null) continue;
                try
                {
                    if (created.IsSameDatabaseObject(drawing)) { sameObject = true; break; }
                }
                catch { }
            }
            if (!sameObject) return false;

            try
            {
                DateTime made = drawing.CreationDate;
                // CreationDate is stored at second resolution, so allow a minute of slack.
                if (made != default(DateTime) && made < _runStart.AddMinutes(-1)) return false;
            }
            catch { }
            return true;
        }

        private bool MatchesExtractFilter(string drawingMark, string pieceGuess)
        {
            if (_markFilter == null && (_selectedMarks == null || _selectedMarks.Count == 0))
                return true;
            if (_markFilter != null)
            {
                return CivilDrawingSupport.MarksMatch(pieceGuess, _markFilter)
                    || CivilDrawingSupport.MarksMatch(drawingMark, _markFilter);
            }
            foreach (var m in _selectedMarks)
            {
                if (CivilDrawingSupport.MarksMatch(pieceGuess, m) || CivilDrawingSupport.MarksMatch(drawingMark, m))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the text looks like a piece/drawing mark (e.g. W10-175 - 1),
        /// not a generic drawing name like "Shop Drawing".
        /// </summary>
        private static bool LooksLikePieceMark(string mark)
        {
            if (string.IsNullOrWhiteSpace(mark) || mark == "?") return false;
            foreach (char c in mark)
            {
                if (char.IsDigit(c)) return true;
            }
            return false;
        }

        private bool AlreadyExtracted(string pieceGuess, string drawingMark)
        {
            string piece = CivilDrawingSupport.StripMark(
                CivilDrawingSupport.FirstNonEmpty(pieceGuess, drawingMark));
            if (string.IsNullOrWhiteSpace(piece) || piece == "UNKNOWN") return false;
            string shopDir = Path.Combine(_outputRoot, CivilDrawingTypes.Shop);
            bool shop = Directory.Exists(shopDir) &&
                Directory.GetFiles(shopDir, "*" + piece + "*SHOP.json").Length > 0;
            string pdfDir = Path.Combine(_outputRoot, "PDF");
            bool pdf = Directory.Exists(pdfDir) &&
                Directory.GetFiles(pdfDir, piece + "*.pdf").Length > 0;
            return shop && pdf;
        }

        private static bool SeenContains(HashSet<string> seen, string mark)
        {
            if (seen == null || string.IsNullOrWhiteSpace(mark)) return false;
            if (seen.Contains(mark)) return true;
            foreach (var s in seen)
            {
                if (CivilDrawingSupport.MarksMatch(s, mark) || CivilDrawingSupport.MarksMatch(mark, s))
                    return true;
            }
            return false;
        }

        private void ClearPreviousOutputs()
        {
            try
            {
                foreach (var csv in Directory.GetFiles(_outputRoot, "*.csv"))
                    File.Delete(csv);
                foreach (var type in new[] { CivilDrawingTypes.GA, CivilDrawingTypes.Erection, CivilDrawingTypes.Shop,
                             CivilDrawingTypes.Connection, CivilDrawingTypes.RebarBbs })
                {
                    var dir = Path.Combine(_outputRoot, type);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                        File.Delete(f);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Civil] could not clear previous outputs: " + ex.Message);
            }
        }

        private void WriteGa(GaCivilDocument doc, Dictionary<string, int> counts)
        {
            string stem = CivilDrawingSupport.FileStem(doc.Header.ProjectCode, doc.Header.PieceMark, CivilDrawingTypes.GA);
            string json = Path.Combine(_outputRoot, CivilDrawingTypes.GA, stem + ".json");
            CivilDrawingSupport.WriteJson(doc, json);
            string csv = Path.Combine(_outputRoot, CivilDrawingTypes.GA + ".csv");
            CivilDrawingSupport.AppendCsv(csv,
                "PieceMark,Name,Profile,Material,Floor,Grid,PosX,PosY,PosZ,StartX,StartY,StartZ,EndX,EndY,EndZ,LengthMm,Guid",
                doc.Pieces.Select(p => CivilDrawingSupport.Csv(
                    p.PieceMark, p.Name, p.Profile, p.Material, p.Floor, p.Grid,
                    N(p.Position.X), N(p.Position.Y), N(p.Position.Z),
                    N(p.StartPoint.X), N(p.StartPoint.Y), N(p.StartPoint.Z),
                    N(p.EndPoint.X), N(p.EndPoint.Y), N(p.EndPoint.Z),
                    N(p.LengthMm), p.Guid)));
            Bump(counts, CivilDrawingTypes.GA);
        }

        private void WriteErection(ErectionCivilDocument doc, Dictionary<string, int> counts)
        {
            string stem = CivilDrawingSupport.FileStem(doc.Header.ProjectCode, doc.Header.PieceMark, CivilDrawingTypes.Erection);
            CivilDrawingSupport.WriteJson(doc, Path.Combine(_outputRoot, CivilDrawingTypes.Erection, stem + ".json"));
            CivilDrawingSupport.AppendCsv(Path.Combine(_outputRoot, CivilDrawingTypes.Erection + ".csv"),
                "PieceMark,Kind,Mark,Description,PosX,PosY,PosZ,Quantity,Guid",
                doc.AnchorsAndEmbeds.Select(h => CivilDrawingSupport.Csv(
                    h.PieceMark, h.Kind, h.Mark, h.Description,
                    h.Position != null ? N(h.Position.X) : "", h.Position != null ? N(h.Position.Y) : "",
                    h.Position != null ? N(h.Position.Z) : "", h.Quantity, h.Guid)));
            Bump(counts, CivilDrawingTypes.Erection);
        }

        private void WriteShop(ShopProductionDocument doc, Dictionary<string, int> counts)
        {
            string stem = CivilDrawingSupport.FileStem(doc.Header.ProjectCode, doc.Header.PieceMark, CivilDrawingTypes.Shop);
            CivilDrawingSupport.WriteJson(doc, Path.Combine(_outputRoot, CivilDrawingTypes.Shop, stem + ".json"));
            CivilDrawingSupport.AppendCsv(Path.Combine(_outputRoot, CivilDrawingTypes.Shop + ".csv"),
                "PieceMark,Mark,Kind,Description,Qty,Profile,Material,LengthMm,WeightKg,Guid",
                doc.Bom.Select(b => CivilDrawingSupport.Csv(
                    b.PieceMark, b.Mark, b.Kind, b.Description, b.Quantity, b.Profile, b.Material,
                    b.LengthMm, b.WeightKg, b.Guid)));
            Bump(counts, CivilDrawingTypes.Shop);
        }

        private void WriteConnection(ConnectionDetailDocument doc, Dictionary<string, int> counts)
        {
            string stem = CivilDrawingSupport.FileStem(doc.Header.ProjectCode, doc.Header.PieceMark, CivilDrawingTypes.Connection);
            CivilDrawingSupport.WriteJson(doc, Path.Combine(_outputRoot, CivilDrawingTypes.Connection, stem + ".json"));
            CivilDrawingSupport.AppendCsv(Path.Combine(_outputRoot, CivilDrawingTypes.Connection + ".csv"),
                "PieceMark,JointType,Kind,Mark,Description,Qty,Profile,Material,Guid",
                doc.Hardware.Select(h => CivilDrawingSupport.Csv(
                    h.PieceMark, doc.JointType, h.Kind, h.Mark, h.Description, h.Quantity, h.Profile, h.Material, h.Guid)));
            Bump(counts, CivilDrawingTypes.Connection);
        }

        private void WriteBbs(RebarBbsDocument doc, Dictionary<string, int> counts)
        {
            string stem = CivilDrawingSupport.FileStem(doc.Header.ProjectCode, doc.Header.PieceMark, CivilDrawingTypes.RebarBbs);
            CivilDrawingSupport.WriteJson(doc, Path.Combine(_outputRoot, CivilDrawingTypes.RebarBbs, stem + ".json"));
            CivilDrawingSupport.AppendCsv(Path.Combine(_outputRoot, CivilDrawingTypes.RebarBbs + ".csv"),
                "PieceMark,Mark,Size,Grade,ShapeType,ShapeCode,SourceType,FrozenState,Qty,DiameterMm,A,B,C,D,E,F,G,H,H2,J,K,K2,O,TotalLengthMm,TotalAreaMm2,WeightKg,Guid",
                doc.Rebar.Select(b => CivilDrawingSupport.Csv(
                    b.PieceMark, b.Mark, b.Size, b.Grade, b.ShapeType, b.ShapeCode, b.SourceType, b.FrozenState,
                    b.Quantity, b.DiameterMm, b.A, b.B, b.C, b.D, b.E, b.F, b.G, b.H, b.H2, b.J, b.K, b.K2, b.O,
                    b.TotalLengthMm, b.TotalAreaMm2, b.WeightKg, b.Guid)));
            Bump(counts, CivilDrawingTypes.RebarBbs);
        }

        private void WriteSamples(Dictionary<string, int> counts)
        {
            string dir = Path.Combine(_outputRoot, "samples");
            CopyFirst(CivilDrawingTypes.GA, dir);
            CopyFirst(CivilDrawingTypes.Erection, dir);
            CopyFirst(CivilDrawingTypes.Shop, dir);
            CopyFirst(CivilDrawingTypes.Connection, dir);
            CopyFirst(CivilDrawingTypes.RebarBbs, dir);
        }

        private void CopyFirst(string type, string sampleDir)
        {
            try
            {
                var dir = Path.Combine(_outputRoot, type);
                var file = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*.json").OrderBy(f => f).FirstOrDefault()
                    : null;
                if (file == null) return;
                File.Copy(file, Path.Combine(sampleDir, Path.GetFileName(file)), true);
                string csv = Path.Combine(_outputRoot, type + ".csv");
                if (File.Exists(csv))
                    File.Copy(csv, Path.Combine(sampleDir, type + "_combined.csv"), true);
            }
            catch { }
        }

        /// <summary>
        /// The combined CSVs are appended per sheet, so a piece that appears on more
        /// than one sheet — or a re-run into the same folder — repeats identical rows.
        /// Keep the header and the first occurrence of each row.
        /// </summary>
        private void DedupeCombinedCsvs()
        {
            foreach (var type in new[] { CivilDrawingTypes.GA, CivilDrawingTypes.Erection, CivilDrawingTypes.Shop,
                         CivilDrawingTypes.Connection, CivilDrawingTypes.RebarBbs })
            {
                string path = Path.Combine(_outputRoot, type + ".csv");
                try
                {
                    if (!File.Exists(path)) continue;
                    var lines = File.ReadAllLines(path);
                    if (lines.Length < 3) continue;

                    var kept = new List<string> { lines[0] };
                    var seenRows = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        if (!seenRows.Add(lines[i])) continue;
                        kept.Add(lines[i]);
                    }

                    int removed = lines.Length - kept.Count;
                    if (removed <= 0) continue;
                    File.WriteAllLines(path, kept);
                    Console.WriteLine("[Civil] " + type + ".csv deduped: removed " + removed +
                        " duplicate rows, kept " + (kept.Count - 1));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Civil] " + type + ".csv dedupe: " + ex.Message);
                }
            }
        }

        private void CopyBomCsv()
        {
            try
            {
                string shop = Path.Combine(_outputRoot, CivilDrawingTypes.Shop + ".csv");
                if (File.Exists(shop))
                    File.Copy(shop, Path.Combine(_outputRoot, "BOM.csv"), true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Civil] BOM.csv: " + ex.Message);
            }
        }

        private static void Bump(Dictionary<string, int> counts, string key)
        {
            if (!counts.ContainsKey(key)) counts[key] = 0;
            counts[key]++;
        }

        private void ReprintSibling(DrawingGenerator printer, string pdfDir, string siblingMark)
        {
            if (printer == null || string.IsNullOrWhiteSpace(siblingMark)) return;
            Drawing sibling = null;
            try
            {
                var en = _handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var d = en.Current;
                    string mark = CivilDrawingSupport.StripMark(SafeMark(d));
                    if (string.Equals(mark, siblingMark, StringComparison.OrdinalIgnoreCase))
                    {
                        sibling = d;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Civil] sheet 2 reprint lookup: " + ex.Message);
                return;
            }
            if (sibling == null)
            {
                Console.WriteLine("[Civil] sheet 2 reprint skipped: '" + siblingMark + "' not in Document Manager");
                return;
            }
            string stem = CivilDrawingSupport.Sanitize(siblingMark);
            string path = Path.Combine(pdfDir, stem + ".pdf");
            if (printer.ExportToPdf(sibling, path))
                Console.WriteLine("[Civil] PDF reprint → " + path);
        }

        private static string SafeMark(Drawing d)
        {
            try { return d.Mark ?? d.Name ?? "?"; } catch { return "?"; }
        }

        private static string N(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // #region agent log
        private static void AgentLogSheetIdentity(Drawing drawing, string piece, CivilSheetCapture capture)
        {
            string mark = "";
            try { mark = drawing.Mark ?? ""; } catch { }
            string blob = (mark + " " + (piece ?? "")).ToUpperInvariant();
            if (blob.IndexOf("W10-67", StringComparison.Ordinal) < 0 &&
                blob.IndexOf("W10-175", StringComparison.Ordinal) < 0)
                return;

            string name = "", t1 = "", t2 = "", t3 = "", created = "";
            try { name = drawing.Name ?? ""; } catch { }
            try { t1 = drawing.Title1 ?? ""; } catch { }
            try { t2 = drawing.Title2 ?? ""; } catch { }
            try { t3 = drawing.Title3 ?? ""; } catch { }
            try { created = drawing.CreationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); } catch { }

            double sw = 0, sh = 0, lw = 0, lh = 0;
            string layoutBits = "";
            try
            {
                var sheet = drawing.GetSheet();
                if (sheet != null) { sw = sheet.Width; sh = sheet.Height; }
            }
            catch { }
            try
            {
                var layout = drawing.Layout;
                if (layout != null && layout.SheetSize != null)
                {
                    lw = layout.SheetSize.Width;
                    lh = layout.SheetSize.Height;
                }
                if (layout != null)
                {
                    foreach (var p in layout.GetType().GetProperties())
                    {
                        if (p.PropertyType != typeof(string)) continue;
                        string v = "";
                        try { v = p.GetValue(layout, null) as string ?? ""; } catch { continue; }
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        layoutBits += p.Name + "=" + v + ";";
                    }
                }
            }
            catch { }

            var views = new System.Text.StringBuilder();
            if (capture != null && capture.Views != null)
            {
                foreach (var v in capture.Views)
                    views.Append(v.ViewType).Append(":").Append(v.Name).Append("@").Append(v.Scale.ToString("0.##", CultureInfo.InvariantCulture)).Append("|");
            }
            string notesHit = "";
            if (capture != null && capture.Texts != null)
            {
                foreach (var t in capture.Texts)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    string u = t.ToUpperInvariant();
                    if (u.Contains("GENERAL NOTES") || u.Contains("TOP IN FORM") || u.Contains("BILL OF MATERIALS"))
                        notesHit += t.Trim() + ";";
                }
            }

            string data = "{\"mark\":\"" + Esc(mark) + "\",\"piece\":\"" + Esc(piece) +
                "\",\"name\":\"" + Esc(name) + "\",\"title1\":\"" + Esc(t1) +
                "\",\"title2\":\"" + Esc(t2) + "\",\"title3\":\"" + Esc(t3) +
                "\",\"created\":\"" + Esc(created) +
                "\",\"sheetMm\":[" + sw.ToString("0.#", CultureInfo.InvariantCulture) + "," + sh.ToString("0.#", CultureInfo.InvariantCulture) +
                "],\"layoutSheetMm\":[" + lw.ToString("0.#", CultureInfo.InvariantCulture) + "," + lh.ToString("0.#", CultureInfo.InvariantCulture) +
                "],\"layoutStrings\":\"" + Esc(layoutBits) +
                "\",\"views\":\"" + Esc(views.ToString()) +
                "\",\"noteHits\":\"" + Esc(notesHit) + "\"}";
            AgentWrite("CivilDrawingBatchExtractor.cs", "live sheet identity", data, "A,C,D,E");
        }

        private static void AgentWrite(string location, string message, string dataJson, string hypothesisId)
        {
            try
            {
                string line = "{\"sessionId\":\"d1fc54\",\"runId\":\"pre-fix\",\"hypothesisId\":\"" + hypothesisId +
                    "\",\"location\":\"" + location + "\",\"message\":\"" + Esc(message) +
                    "\",\"data\":" + dataJson + ",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
                File.AppendAllText(@"c:\Users\ASUS\Desktop\2d_tekla\debug-d1fc54.log", line + Environment.NewLine);
            }
            catch { }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
        // #endregion
    }
}

// DrawingGenerator.cs
// ─────────────────────────────────────────────────────────────────
// Flowchart step: "DrawingGenerator.cs — DrawingHandler API"
//   → "Generated 2D Drawings (Single-Part / Assembly / Cast Unit)"
//   → DrawingPostProcessor (CatA–CatG corrections via DrawingHandler)
//   → "Exported 2D PDFs"
//
// Tekla Structures 2026 Open API notes (verified against
// Tekla.Structures.Drawing.dll in this install):
//   • SinglePartDrawing / AssemblyDrawing / CastUnitDrawing take an
//     Identifier, not a Part/Assembly object.
//   • There is no PrintOrExportToFile. PDF export is:
//       DrawingHandler.PrintDrawing(drawing, DPMPrinterAttributes, outputFile)
//     with DPMPrinterAttributes.OutputType = DotPrintOutputType.PDF.

using System;
using System.Collections.Generic;
using System.IO;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model.Operations;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public enum DrawingKind { SinglePart, Assembly, CastUnit }

    public class DrawingResult
    {
        public DrawingKind Kind { get; set; }
        public string Name { get; set; } = "";
        public bool Created { get; set; }
        public bool Exported { get; set; }
        public string PdfPath { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Wraps Tekla.Structures.Drawing.DrawingHandler to generate
    /// Single-Part, Assembly and Cast Unit drawings for a set of model
    /// parts, then export each one to PDF.
    /// </summary>
    public class DrawingGenerator
    {
        private readonly DrawingHandler _drawingHandler;

        public DrawingGenerator()
        {
            _drawingHandler = new DrawingHandler();
        }

        public bool IsConnected => _drawingHandler != null && _drawingHandler.GetConnectionStatus();

        /// <summary>
        /// Generates a drawing of the given kind for every part, then
        /// exports each one to PDF in outputDir.
        ///
        /// Every part is still visited (feature preserved). For Assembly /
        /// Cast Unit sheets we resolve Part → parent Assembly and pass
        /// assembly.Identifier — never the part id. HashSet only stops
        /// inserting the same Tekla drawing sheet twice.
        /// </summary>
        public List<DrawingResult> GenerateAndExport(
            IEnumerable<TSModel.Part> parts,
            DrawingKind kind,
            string outputDir,
            DrawingPostProcessor postProcessor = null)
        {
            var results = new List<DrawingResult>();

            if (!IsConnected)
            {
                Console.WriteLine("[DrawingGenerator] DrawingHandler is not connected to Tekla.");
                return results;
            }

            WarnIfNumberingStale();
            Directory.CreateDirectory(outputDir);

            var seen = new HashSet<int>();
            int skippedWrongType = 0;

            foreach (var part in parts)
            {
                if (part == null) continue;

                Identifier targetId = TargetIdentifier(part, kind);
                if (targetId == null || !targetId.IsValid() || targetId.ID == 0)
                {
                    skippedWrongType++;
                    continue;
                }

                if (!seen.Add(targetId.ID))
                    continue;

                var result = new DrawingResult { Kind = kind };
                try
                {
                    Drawing drawing = CreateOrGetDrawing(part, kind, targetId);
                    if (drawing == null)
                    {
                        result.Error = "Could not create/find a drawing for this part+kind combination.";
                        results.Add(result);
                        continue;
                    }

                    result.Created = true;
                    result.Name = DrawingName(drawing);

                    if (postProcessor != null)
                    {
                        try { postProcessor.CorrectDrawing(drawing); }
                        catch (Exception ex) { Console.WriteLine($"[Correct] {ex.Message}"); }
                    }

                    string pdfPath = UniquePdfPath(outputDir, result.Name);
                    result.Exported = ExportToPdf(drawing, pdfPath);
                    result.PdfPath = result.Exported ? pdfPath : "";

                    Console.WriteLine(result.Exported
                        ? $"[DrawingGenerator] {kind} '{result.Name}' -> {pdfPath}"
                        : $"[DrawingGenerator] {kind} '{result.Name}' created but PDF export failed.");
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    Console.WriteLine($"[DrawingGenerator] {kind} failed: {ex.Message}");
                }
                results.Add(result);
            }

            try { _drawingHandler.GetActiveDrawing()?.CommitChanges(); } catch { /* best effort */ }

            if (skippedWrongType > 0)
                Console.WriteLine($"[DrawingGenerator] skipped {skippedWrongType} parts whose parent is not a {kind} target.");

            return results;
        }

        /// <summary>
        /// Inserts missing Assembly / Cast Unit / Single-Part drawings via
        /// DrawingHandler. Does not print PDF — FloorWiseDrawingExtractor
        /// writes floor-organised PDFs afterwards.
        /// </summary>
        public int EnsureCreated(IEnumerable<TSModel.Part> parts, DrawingKind kind)
        {
            int created = 0;
            if (!IsConnected) return 0;
            WarnIfNumberingStale();

            var seen = new HashSet<int>();
            foreach (var part in parts)
            {
                if (part == null) continue;
                Identifier targetId = TargetIdentifier(part, kind);
                if (targetId == null || !targetId.IsValid() || targetId.ID == 0)
                    continue;
                if (!seen.Add(targetId.ID))
                    continue;
                try
                {
                    var drawing = CreateOrGetDrawing(part, kind, targetId);
                    if (drawing != null) created++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DrawingGenerator] Ensure {kind}: {ex.Message}");
                }
            }
            Console.WriteLine($"[DrawingGenerator] {kind} sheets ready: {created} unique.");
            return created;
        }

        /// <summary>
        /// Part → parent Assembly → assembly.Identifier.
        /// Never pass a Part identifier into AssemblyDrawing / CastUnitDrawing.
        /// </summary>
        private static Identifier TargetIdentifier(TSModel.Part part, DrawingKind kind)
        {
            switch (kind)
            {
                case DrawingKind.SinglePart:
                    return part.Identifier;

                case DrawingKind.Assembly:
                {
                    var assembly = DeepDataExtractor.ResolveParentAssembly(part);
                    if (assembly == null) return null;
                    var type = assembly.GetAssemblyType();
                    if (type != TSModel.Assembly.AssemblyTypeEnum.STEEL_ASSEMBLY &&
                        type != TSModel.Assembly.AssemblyTypeEnum.TIMBER_ASSEMBLY)
                        return null;
                    return assembly.Identifier;
                }

                case DrawingKind.CastUnit:
                {
                    var assembly = DeepDataExtractor.ResolveParentAssembly(part);
                    if (assembly == null) return null;
                    if (assembly.GetAssemblyType() != TSModel.Assembly.AssemblyTypeEnum.PRECAST_ASSEMBLY)
                        return null;
                    return assembly.Identifier;
                }

                default:
                    return null;
            }
        }

        private Drawing CreateOrGetDrawing(TSModel.Part part, DrawingKind kind, Identifier targetId)
        {
            Drawing candidate;
            try
            {
                switch (kind)
                {
                    case DrawingKind.SinglePart:
                        candidate = new SinglePartDrawing(targetId);
                        break;
                    case DrawingKind.Assembly:
                        candidate = new AssemblyDrawing(targetId);
                        break;
                    case DrawingKind.CastUnit:
                        candidate = new CastUnitDrawing(targetId);
                        break;
                    default:
                        return null;
                }

                if (candidate.Insert())
                    return candidate;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DrawingGenerator] Insert {kind} id={targetId.ID}: {ex.Message}");
                return FindExisting(kind, targetId);
            }

            return FindExisting(kind, targetId);
        }

        /// <summary>
        /// Insert() returns false when a drawing already exists for that
        /// object. Match the existing sheet by Identifier so re-export
        /// after a model change still hits the right drawing.
        /// </summary>
        private Drawing FindExisting(DrawingKind kind, Identifier targetId)
        {
            try
            {
                var all = _drawingHandler.GetDrawings();
                while (all.MoveNext())
                {
                    var current = all.Current;
                    if (current == null) continue;

                    if (kind == DrawingKind.SinglePart && current is SinglePartDrawing spd
                        && SameId(spd.PartIdentifier, targetId))
                        return spd;

                    if (kind == DrawingKind.Assembly && current is AssemblyDrawing ad
                        && SameId(ad.AssemblyIdentifier, targetId))
                        return ad;

                    if (kind == DrawingKind.CastUnit && current is CastUnitDrawing cud
                        && SameId(cud.CastUnitIdentifier, targetId))
                        return cud;
                }
            }
            catch { /* fall through */ }
            return null;
        }

        private static bool SameId(Identifier a, Identifier b)
        {
            if (a == null || b == null) return false;
            if (a.ID != 0 && b.ID != 0) return a.ID == b.ID;
            return a.GUID != Guid.Empty && a.GUID == b.GUID;
        }

        private static string DrawingName(Drawing drawing)
        {
            if (!string.IsNullOrWhiteSpace(drawing.Mark))
                return drawing.Mark;
            if (!string.IsNullOrWhiteSpace(drawing.Name))
                return drawing.Name;
            if (!string.IsNullOrWhiteSpace(drawing.Title1))
                return drawing.Title1;
            return drawing.GetType().Name + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string SafeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "drawing" : s;
        }

        private static string UniquePdfPath(string outputDir, string name)
        {
            string stem = SafeFileName(name);
            string path = Path.Combine(outputDir, stem + ".pdf");
            int n = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(outputDir, stem + "_" + n + ".pdf");
                n++;
            }
            return path;
        }

        /// <summary>
        /// Exports a single drawing to PDF via DrawingHandler.PrintDrawing
        /// and DPMPrinterAttributes (Tekla 2026).
        /// Drawing must be updated before SetActiveDrawing, otherwise Tekla
        /// throws: "In order to set the drawing as active, it must be first updated".
        /// </summary>
        public bool ExportToPdf(Drawing drawing, string pdfPath)
        {
            return PrintToFile(drawing, pdfPath, DotPrintOutputType.PDF, "TS PDF Writer");
        }

        /// <summary>
        /// Tekla 2026 DrawingHandler has no DWG OutputType (only Printer/PDF/Plot/Image).
        /// DWG is printed through a catalog printer instance named DWG / DWG/DXF / DXF.
        /// </summary>
        public bool ExportToDwg(Drawing drawing, string dwgPath)
        {
            if (drawing == null || string.IsNullOrWhiteSpace(dwgPath)) return false;
            if (_dwgPrinterMissing) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dwgPath) ?? ".");
                try { _drawingHandler.UpdateDrawing(drawing); } catch { }

                foreach (var printer in _dwgPrinter != null ? new[] { _dwgPrinter } : DwgPrinterNames)
                {
                    var attrs = new PrintAttributes
                    {
                        PrinterInstance = printer,
                        ScalingType = DotPrintScalingType.Auto,
                        Orientation = DotPrintOrientationType.Landscape,
                        NumberOfCopies = 1,
                        PrintToMultipleSheet = false,
                    };
                    if (!TryPrint(drawing, attrs, dwgPath)) continue;
                    if (!File.Exists(dwgPath) || new FileInfo(dwgPath).Length == 0) continue;

                    // A catalog printer named "DWG" can still emit PDF bytes, which
                    // would leave a mislabelled .dwg on disk. Only keep real CAD data.
                    if (!IsCadFile(dwgPath))
                    {
                        Console.WriteLine("[DrawingGenerator] printer '" + printer + "' produced non-CAD data (" +
                            Signature(dwgPath) + "); discarding " + Path.GetFileName(dwgPath));
                        try { File.Delete(dwgPath); } catch { }
                        continue;
                    }

                    _dwgPrinter = printer;
                    Console.WriteLine("[DrawingGenerator] DWG via printer '" + printer + "' → " + dwgPath);
                    return true;
                }

                // No catalog printer produced a file. Retrying six names on every
                // sheet only slows the scan, so stop asking for this session.
                _dwgPrinterMissing = true;
                Console.WriteLine("[DrawingGenerator] DWG skipped for the rest of this run (no DWG/DXF printer instance). Tried: " +
                    string.Join(", ", DwgPrinterNames));
                return false;
            }
            catch (Exception ex)
            {
                _dwgPrinterMissing = true;
                Console.WriteLine("[DrawingGenerator] DWG export failed: " + ex.Message);
                return false;
            }
        }

        private static readonly string[] DwgPrinterNames =
        {
            "DWG", "DWG/DXF", "DWG/DXF printer", "DXF", "Plot to DWG", "DWG printer"
        };

        private bool _dwgPrinterMissing;
        private string _dwgPrinter;

        /// <summary>True for binary DWG ("AC10.."/"AC-10") or ASCII DXF ("0\nSECTION").</summary>
        private static bool IsCadFile(string path)
        {
            string sig = Signature(path);
            if (sig.StartsWith("AC10", StringComparison.OrdinalIgnoreCase)) return true;
            if (sig.StartsWith("AC1", StringComparison.OrdinalIgnoreCase)) return true;
            if (sig.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (sig.IndexOf("AutoCAD", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string Signature(string path)
        {
            try
            {
                var buffer = new byte[16];
                int read;
                using (var fs = File.OpenRead(path))
                    read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0) return "";
                var sb = new System.Text.StringBuilder(read);
                for (int i = 0; i < read; i++)
                {
                    char c = (char)buffer[i];
                    sb.Append(c >= ' ' && c <= '~' ? c : '.');
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private bool PrintToFile(Drawing drawing, string path, DotPrintOutputType outputType, string printerName)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

                if (outputType == DotPrintOutputType.PDF)
                {
                    var dpm = new DPMPrinterAttributes
                    {
                        OutputType = DotPrintOutputType.PDF,
                        PaperSize = DotPrintPaperSize.Auto,
                        ScalingMethod = DotPrintScalingType.Auto,
                        Orientation = DotPrintOrientationType.Landscape,
                        ColorMode = DotPrintColor.Color,
                        OutputFileName = path,
                        OpenFileWhenFinished = false,
                        NumberOfCopies = 1,
                        PrintToMultipleSheet = DotPrintToMultipleSheet.Off,
                        PrinterName = string.IsNullOrWhiteSpace(printerName) ? "TS PDF Writer" : printerName,
                    };
                    try { _drawingHandler.UpdateDrawing(drawing); } catch { }
                    bool openedPdf = false;
                    try { openedPdf = _drawingHandler.SetActiveDrawing(drawing, false); } catch { }
                    if (!openedPdf)
                    {
                        try { openedPdf = _drawingHandler.SetActiveDrawing(drawing, false, true); } catch { }
                    }
                    AgentLogPrintIdentity(drawing, path);
                    bool okPdf = TryPrint(drawing, dpm, path);
                    bool pdfOk = okPdf && File.Exists(path);
                    if (pdfOk)
                        TryStripInnerPdfBorder(path);
                    return pdfOk;
                }

                var attrs = new PrintAttributes
                {
                    ScalingType = DotPrintScalingType.Auto,
                    Orientation = DotPrintOrientationType.Landscape,
                    NumberOfCopies = 1,
                    PrintToMultipleSheet = false,
                    PrinterInstance = printerName,
                };

                try { _drawingHandler.UpdateDrawing(drawing); }
                catch { /* numbering-stale drawings may still print after update */ }

                bool opened = false;
                try { opened = _drawingHandler.SetActiveDrawing(drawing, false); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DrawingGenerator] SetActiveDrawing: {ex.Message}");
                }

                if (!opened)
                {
                    try { opened = _drawingHandler.SetActiveDrawing(drawing, false, true); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DrawingGenerator] SetActiveDrawing(force): {ex.Message}");
                    }
                }

                // Do not resize Layout.SheetSize before print. Expanding 11x17 then
                // Auto-scaling shrinks views and makes the title block dominate the PDF.
                // Native sheet size is what produced W10-67-style tickets.
                // #region agent log
                AgentLogPrintIdentity(drawing, path);
                // #endregion

                // Fresh Open API inserts can print as title block only unless the sheet is
                // activated first. Always print from an active drawing when possible.
                bool ok = TryPrint(drawing, attrs, path);
                if ((!ok || !File.Exists(path)) && !opened)
                    ok = TryPrint(drawing, attrs, path);

                if (opened)
                {
                    try { _drawingHandler.CloseActiveDrawing(false); }
                    catch { /* best effort */ }
                }

                bool exists = ok && File.Exists(path);
                if (exists)
                    TryStripInnerPdfBorder(path);
                return exists;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DrawingGenerator] {outputType} export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Layout templates bake nested sheet frames that are not Drawing.Line objects.
        /// Cover the inner full-sheet rectangle in the PDF (keep outer border + BOM divider).
        /// </summary>
        private static void TryStripInnerPdfBorder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string name = Path.GetFileName(path) ?? "";
            // Sheet-1 fabrication elevation only (…_-_1.pdf).
            if (name.IndexOf("_-_1.pdf", StringComparison.OrdinalIgnoreCase) < 0) return;
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
                string script = Path.Combine(root, "Scripts", "strip_inner_sheet_border.py");
                if (!File.Exists(script))
                    script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "strip_inner_sheet_border.py");
                if (!File.Exists(script))
                {
                    Console.WriteLine("[border] strip script missing; skip " + name);
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "\"" + script + "\" \"" + path + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return;
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    if (!string.IsNullOrWhiteSpace(stdout))
                        Console.Write(stdout.TrimEnd() + Environment.NewLine);
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Console.Write(stderr.TrimEnd() + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[border] " + ex.Message);
            }
        }

        private bool TryPrint(Drawing drawing, DPMPrinterAttributes attrs, string path)
        {
            try { return _drawingHandler.PrintDrawing(drawing, attrs, path); }
            catch (Exception ex)
            {
                Console.WriteLine($"[DrawingGenerator] PrintDrawing: {ex.Message}");
                return false;
            }
        }

        private bool TryPrint(Drawing drawing, PrintAttributes attrs, string path)
        {
            try { return _drawingHandler.PrintDrawing(drawing, attrs, path); }
            catch
            {
                return false;
            }
        }

        // #region agent log
        private static void AgentLogPrintIdentity(Drawing drawing, string path)
        {
            string mark = "";
            try { mark = (drawing.Mark ?? "") + " " + (drawing.Name ?? ""); } catch { }
            string u = mark.ToUpperInvariant();
            if (u.IndexOf("W10-67", StringComparison.Ordinal) < 0 &&
                u.IndexOf("W10-175", StringComparison.Ordinal) < 0)
                return;
            double lw = 0, lh = 0;
            try
            {
                if (drawing.Layout != null && drawing.Layout.SheetSize != null)
                {
                    lw = drawing.Layout.SheetSize.Width;
                    lh = drawing.Layout.SheetSize.Height;
                }
            }
            catch { }
            Console.WriteLine("[DrawingGenerator] print mark=" + mark.Trim() +
                " sheet=" + lw.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                "x" + lh.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                " -> " + path);
        }
        // #endregion

        private static void WarnIfNumberingStale()
        {
            try
            {
                if (!Operation.IsNumberingUpToDateAll())
                {
                    Console.WriteLine("[DrawingGenerator] Numbering is not up to date.");
                    Console.WriteLine("  In Tekla: Drawings & reports → Numbering → Number modified objects,");
                    Console.WriteLine("  then re-run --drawings. Unnumbered sheets cannot be set active / printed.");
                }
            }
            catch { /* numbering query optional */ }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model.Operations;
using TSModel = Tekla.Structures.Model;
using TSUI = Tekla.Structures.Model.UI;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Runs local Paisley macros for WALL / COLUMN / BEAM only.
    /// Walls and columns: PG1 (hardware), PG2 (rebar placing), PG3 (rebar table).
    /// Beams: BM_SET (PG1–3 + fabricator). Never hollowcore / insulation / formliner.
    /// Single-part / assembly / GA sheets use local attribute files via the Drawing API.
    /// </summary>
    public class LocalDrawingMacroRunner
    {
        public const string Pg1Hardware = "SP_M.CU_PG1_11X17.cs";
        public const string Pg2Placing = "SP_M.CU_PG2_11X17.cs";
        public const string Pg3Table = "SP_M.CU_PG3_11X17.cs";
        public const string BeamSet = "SP_M.BM_SET_11x17.cs";
        /// <summary>Open API attribute used for Document Manager sheet -2 (sections/3D).</summary>
        public const string SectionsProps = "SP_M.CU_HARDWARE_PROPS_11X17";
        public const string SectionsDrawingName = "CU SECTIONS 3D";

        private static readonly string[] ForbiddenMacros =
        {
            "SP_M.HC Piece Ticket.cs",
            "SP_M.CU_INS_11x17.cs",
            "SP_M.CU_FORMLINER_11x17 v1.cs",
            "SP_M.CU_SET_11x17.cs",
            "tah.cs",
            "te.cs",
        };

        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;
        private readonly string _outputRoot;
        private readonly string _logPath;
        private bool _oneAtATime;

        public LocalDrawingMacroRunner(TSModel.Model model, string outputRoot = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
            _outputRoot = outputRoot;
            _logPath = string.IsNullOrWhiteSpace(outputRoot)
                ? null
                : Path.Combine(outputRoot, "macros.log");
        }

        public const int MacroBatchSize = 20;

        public void Run()
        {
            RunMacros();
        }

        /// <summary>Local Paisley macros (PG1–3 / BM_SET) then floor GA. No 40-piece cap.</summary>
        public void RunMacros(string markFilter = null, bool smoke = false, bool selectedOnly = false)
        {
            _oneAtATime = smoke || selectedOnly;
            CloseEditor("RunMacros start");
            CopyBundledMacrosToModel();
            LogSessionHeader(selectedOnly ? "selected" : (smoke ? "smoke" : "full"), markFilter);
            int sessionBefore = CountDrawings();
            MacroLog("session drawings before macros=" + sessionBefore);
            var groups = CollectMembers(markFilter, selectedOnly: selectedOnly);
            MacroLog("WALL=" + groups.Walls.Count +
                " COLUMN=" + groups.Columns.Count +
                " BEAM=" + groups.Beams.Count +
                (string.IsNullOrWhiteSpace(markFilter) ? "" : " mark=" + markFilter) +
                (selectedOnly ? " selected=yes" : "") +
                " (precast only; hollowcore/insulation/formliner skipped)");

            if (selectedOnly && groups.Marks.Count == 0)
            {
                MacroLog("FAIL selected: no precast WALL/COLUMN/BEAM in the Tekla selection");
                return;
            }

            if (smoke && !selectedOnly)
            {
                var walls = groups.Walls;
                if (walls.Count == 0)
                {
                    MacroLog("FAIL smoke: no WALL matching " + markFilter);
                    return;
                }
                // PG1–3 first (Tekla auto sheets -1/-2/-3). Sections sheet last as -4 so
                // CreateCastUnitDrawing is not blocked by an Open API sheet occupying -2.
                RunMacroInBatches(walls, Pg1Hardware, "smoke hardware CU");
                RunMacroInBatches(walls, Pg2Placing, "smoke rebar placing CU");
                RunMacroInBatches(walls, Pg3Table, "smoke rebar table CU");
                CreateSectionsSheets(walls, "smoke sections CU");
                MacroLog("smoke: skipped beams and floor GA");
                MacroLog("session drawings " + sessionBefore + " → " + CountDrawings() + " after smoke macros");
                return;
            }

            var wallsAndColumns = groups.Walls.Concat(groups.Columns).ToList();
            RunMacroInBatches(wallsAndColumns, Pg1Hardware, "hardware CU");
            RunMacroInBatches(wallsAndColumns, Pg2Placing, "rebar placing CU");
            RunMacroInBatches(wallsAndColumns, Pg3Table, "rebar table CU");
            CreateSectionsSheets(wallsAndColumns, "sections CU");
            RunMacroInBatches(groups.Beams, BeamSet, "beam hardware+placing+table+fabricator");

            if (!selectedOnly)
            {
                var all = wallsAndColumns.Concat(groups.Beams).ToList();
                CreateFloorGa(all);
            }
            else
                MacroLog("selected: skipped floor GA");

            MacroLog("session drawings " + sessionBefore + " → " + CountDrawings() + " after macros");
        }

        /// <summary>PG1–3 / BM_SET only for assemblies whose piece mark is in <paramref name="missingMarks"/>. No floor GA.</summary>
        public void RunMacrosForMissing(HashSet<string> missingMarks)
        {
            if (missingMarks == null || missingMarks.Count == 0)
            {
                MacroLog("no missing marks — skip leftover macros");
                return;
            }
            _oneAtATime = true;
            CloseEditor("leftover start");
            CopyBundledMacrosToModel();
            LogSessionHeader("leftover", null);
            var groups = CollectMembers(null, missingMarks);
            MacroLog("leftover WALL=" + groups.Walls.Count +
                " COLUMN=" + groups.Columns.Count +
                " BEAM=" + groups.Beams.Count +
                " missingMarks=" + missingMarks.Count);
            var wallsAndColumns = groups.Walls.Concat(groups.Columns).ToList();
            RunMacroInBatches(wallsAndColumns, Pg1Hardware, "leftover hardware CU");
            RunMacroInBatches(wallsAndColumns, Pg2Placing, "leftover rebar placing CU");
            RunMacroInBatches(wallsAndColumns, Pg3Table, "leftover rebar table CU");
            RunMacroInBatches(groups.Beams, BeamSet, "leftover beam set");
        }

        /// <summary>
        /// Direct Open API: CastUnitDrawing.Insert with the same SP_M attribute files, then floor GA. No RunMacro.
        /// Returns the sheets this call created, so the caller can extract only those and
        /// never pick up macro-made sheets that share the same piece mark.
        /// </summary>
        public List<Drawing> RunOpenApi(string markFilter = null, bool selectedOnly = false)
        {
            CloseEditor("OpenAPI start");
            int before = CountDrawings();
            MacroLog("OpenAPI drawings before insert=" + before);
            var groups = CollectMembers(markFilter, selectedOnly: selectedOnly);
            MacroLog("OpenAPI WALL=" + groups.Walls.Count +
                " COLUMN=" + groups.Columns.Count +
                " BEAM=" + groups.Beams.Count +
                (string.IsNullOrWhiteSpace(markFilter) ? "" : " mark=" + markFilter) +
                (selectedOnly ? " selected=yes" : "") +
                " (precast only; hollowcore/insulation/formliner skipped)");

            var created = new List<Drawing>();
            // Macro sheets already occupy W10-67 - 1/2/3. Allow parallel Open API sheets
            // (next free sheet numbers) without deleting the macro set.
            string previousCloningCheck = AllowDuplicateSheets();
            try
            {
                var wallsAndColumns = groups.Walls.Concat(groups.Columns).ToList();
                // One attribute per PG; start at next free sheet so we get 4/5/6 when 1/2/3 exist.
                InsertCastUnitOpenApi(wallsAndColumns, new[]
                {
                    AttributesForMacro(Pg1Hardware)[0],
                    AttributesForMacro(Pg2Placing)[0],
                    AttributesForMacro(Pg3Table)[0],
                }, created);
                InsertCastUnitOpenApi(groups.Beams, AttributesForMacro(BeamSet), created);

                bool singlePiece = !string.IsNullOrWhiteSpace(markFilter);
                if (!selectedOnly && !singlePiece)
                {
                    var all = wallsAndColumns.Concat(groups.Beams).ToList();
                    CreateFloorGa(all);
                }
                else
                    MacroLog("OpenAPI: skipped floor GA (" + (singlePiece ? "mark=" + markFilter : "selected") + ")");
            }
            finally
            {
                RestoreCloningCheck(previousCloningCheck);
            }

            int after = CountDrawings();
            MacroLog("OpenAPI drawings " + before + " → " + after +
                " (delta=" + (after - before) + ", tracked sheets=" + created.Count + ")");
            return created;
        }

        public MemberGroups CollectMembers(string markFilter = null, HashSet<string> onlyMarks = null, bool selectedOnly = false)
        {
            var g = new MemberGroups();
            var seenAsm = new HashSet<int>();
            try
            {
                if (selectedOnly)
                {
                    CollectFromTeklaSelection(g, seenAsm, markFilter, onlyMarks);
                    MacroLog("selected assemblies=" + seenAsm.Count + " marks=" + string.Join(",", g.Marks.OrderBy(m => m)));
                    return g;
                }

                var en = _model.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSModel.Assembly) });
                while (en != null && en.MoveNext())
                    TryAddMember(en.Current as TSModel.Assembly, g, seenAsm, markFilter, onlyMarks);
            }
            catch (Exception ex)
            {
                Fail("collect members", ex);
            }
            return g;
        }

        private void CollectFromTeklaSelection(MemberGroups g, HashSet<int> seenAsm, string markFilter, HashSet<string> onlyMarks)
        {
            var selector = new TSUI.ModelObjectSelector();
            var selected = selector.GetSelectedObjects();
            int n = 0;
            while (selected != null && selected.MoveNext())
            {
                n++;
                var asm = selected.Current as TSModel.Assembly;
                if (asm == null)
                {
                    var part = selected.Current as TSModel.Part;
                    if (part == null) continue;
                    try { asm = part.GetAssembly(); } catch { continue; }
                }
                TryAddMember(asm, g, seenAsm, markFilter, onlyMarks);
            }
            MacroLog("Tekla selection objects=" + n);
        }

        private void TryAddMember(TSModel.Assembly asm, MemberGroups g, HashSet<int> seenAsm, string markFilter, HashSet<string> onlyMarks)
        {
            if (asm == null) return;
            int aid = 0;
            try { aid = asm.Identifier.ID; } catch { }
            if (aid != 0 && !seenAsm.Add(aid)) return;
            if (!CivilDrawingSupport.IsPrecastAssembly(asm)) return;
            TSModel.Part main;
            try { main = asm.GetMainPart() as TSModel.Part; }
            catch { return; }
            if (main == null) return;
            if (CivilDrawingSupport.IsExcludedPrecast(main)) return;
            string piece = CivilDrawingSupport.PieceMark(main, null);
            if (!CivilDrawingSupport.MarksMatch(piece, markFilter)) return;
            if (onlyMarks != null && onlyMarks.Count > 0 && !ContainsMark(onlyMarks, piece)) return;
            string kind = CivilDrawingSupport.MemberKind(main);
            if (kind == "WALL") g.Walls.Add(main);
            else if (kind == "COLUMN") g.Columns.Add(main);
            else if (kind == "BEAM") g.Beams.Add(main);
            else return;
            g.Marks.Add(piece);
        }

        private static bool ContainsMark(HashSet<string> set, string piece)
        {
            if (set == null || string.IsNullOrWhiteSpace(piece)) return false;
            if (set.Contains(piece)) return true;
            string a = CivilDrawingSupport.StripMark(piece);
            foreach (var m in set)
            {
                if (a.Equals(CivilDrawingSupport.StripMark(m), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RunMacroInBatches(List<TSModel.Part> parts, string macroFile, string label)
        {
            if (parts == null || parts.Count == 0)
            {
                MacroLog("skip " + label + " — no parts");
                return;
            }

            // Pre-flight 3D solid geometry validation: guard against solkExtremaInGlobal fatal kernel aborts
            var validParts = new List<TSModel.Part>();
            foreach (var part in parts)
            {
                if (part == null) continue;
                string mark = CivilDrawingSupport.PieceMark(part, null);
                if (!CivilDrawingSupport.ValidatePartGeometry(part, out string reason))
                {
                    MacroLog($"[GeometryGuard] SKIP corrupt part mark={mark} id={part.Identifier.ID}: {reason}");
                    Fail($"{label} corrupt geometry ({mark})", new InvalidOperationException(reason));
                    continue;
                }
                validParts.Add(part);
            }

            if (validParts.Count == 0)
            {
                MacroLog("skip " + label + " — all parts failed 3D geometry validation");
                return;
            }

            int size = _oneAtATime ? 1 : MacroBatchSize;
            int batches = (validParts.Count + size - 1) / size;
            for (int i = 0; i < validParts.Count; i += size)
            {
                var slice = validParts.Skip(i).Take(size).ToList();
                int batch = (i / size) + 1;
                MacroLog(label + " batch " + batch + "/" + batches + " n=" + slice.Count);
                RunMacroOnSelection(slice, macroFile, label + " batch " + batch);
                CloseEditor("between batches");
            }
        }

        private void RunMacroOnSelection(List<TSModel.Part> parts, string macroFile, string label)
        {
            if (parts == null || parts.Count == 0)
            {
                MacroLog("skip " + label + " — no parts");
                return;
            }
            if (ForbiddenMacros.Any(f => string.Equals(f, macroFile, StringComparison.OrdinalIgnoreCase)))
            {
                MacroLog("refused forbidden macro " + macroFile);
                return;
            }

            string marks = string.Join(",", parts.Select(p => CivilDrawingSupport.PieceMark(p, null)).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct());
            CloseEditor("before " + label);
            if (!SelectParts(parts))
            {
                var miss = new InvalidOperationException("could not select " + parts.Count + " parts for " + label + " marks=" + marks);
                Fail(label + " select", miss);
                return;
            }

            int before = CountDrawings();
            MacroLog("RunMacro " + macroFile + " (" + label + ", n=" + parts.Count + ", marks=" + marks + ", drawingsBefore=" + before + ")");
            bool started = TryRunMacro(macroFile);
            if (!started)
            {
                var fail = new InvalidOperationException(
                    "RunMacro could not start " + macroFile + " — blank-template Insert fallback disabled. marks=" + marks);
                Fail(label + " RunMacro start", fail);
                return;
            }

            int after = WaitUntilDrawingsIncrease(before, macroFile);
            CloseEditor("after " + macroFile);
            after = CountDrawings();
            MacroLog("drawings " + before + " → " + after + " after " + macroFile + " fallbackInsert=no");
            if (after <= before)
            {
                var fail = new InvalidOperationException(
                    "RunMacro started but 0 new sheets for " + macroFile +
                    " (AKIT template likely loaded; CreateCastUnitDrawing did not commit). marks=" + marks);
                Fail(label + " 0 new sheets", fail);
            }
        }

        private static string[] AttributesForMacro(string macroFile)
        {
            if (string.Equals(macroFile, Pg1Hardware, StringComparison.OrdinalIgnoreCase))
                return new[] { "SP_M.CU_HARDWARE_PROPS_11X17" };
            if (string.Equals(macroFile, Pg2Placing, StringComparison.OrdinalIgnoreCase))
                return new[] { "SP_M.CU_REINFORCING_PLACING_PROPS_11X17" };
            if (string.Equals(macroFile, Pg3Table, StringComparison.OrdinalIgnoreCase))
                return new[] { "SP_M.CU_REINFORCING_TABLE_PROPS_11X17" };
            if (string.Equals(macroFile, BeamSet, StringComparison.OrdinalIgnoreCase))
                return new[]
                {
                    "SP_M.CU_HARDWARE_PROPS_11X17",
                    "SP_M.CU_REINFORCING_PLACING_PROPS_11X17",
                    "SP_M.CU_REINFORCING_TABLE_PROPS_11X17",
                    "SP_M_Beam_PG4_11x17",
                };
            return new string[0];
        }

        private static int StartSheetForMacro(string macroFile)
        {
            // PG macros: 1 hardware, 2 placing, 3 table. Sections Open API uses next free (≥4).
            if (string.Equals(macroFile, Pg2Placing, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(macroFile, Pg3Table, StringComparison.OrdinalIgnoreCase)) return 3;
            return 1;
        }

        /// <summary>
        /// Create Document Manager Sections sheet (typically -4) after PG1–3 so Tekla UI has 4 base sheets.
        /// Uses hardware props for 3D/section views, then renames to <see cref="SectionsDrawingName"/>.
        /// </summary>
        private void CreateSectionsSheets(List<TSModel.Part> parts, string label)
        {
            if (parts == null || parts.Count == 0) return;
            int before = CountDrawings();
            MacroLog(label + ": Open API sections sheet (props=" + SectionsProps + ")");
            var created = new List<Drawing>();
            try
            {
                InsertCastUnitOpenApi(parts, new[] { SectionsProps }, created);
            }
            catch (Exception ex)
            {
                Fail(label + " InsertCastUnitOpenApi", ex);
            }
            foreach (Drawing d in created)
            {
                try
                {
                    d.Name = SectionsDrawingName;
                    d.Modify();
                    MacroLog(label + ": renamed " + (d.Mark ?? "") + " → Name='" + SectionsDrawingName + "'");
                }
                catch (Exception ex)
                {
                    MacroLog(label + ": rename failed: " + ex.Message);
                }
            }
            int after = CountDrawings();
            MacroLog(label + ": drawings " + before + " → " + after + " created=" + created.Count);
        }

        private void InsertCastUnitWithAttributes(List<TSModel.Part> parts, string[] attributes, int startSheet = 1,
            List<Drawing> created = null)
        {
            if (parts == null || attributes == null || attributes.Length == 0) return;
            var seen = new HashSet<int>();
            int n = 0;
            foreach (var part in parts)
            {
                if (part == null) continue;
                if (!CivilDrawingSupport.ValidatePartGeometry(part, out string geomErr))
                {
                    string m = CivilDrawingSupport.PieceMark(part, null);
                    MacroLog($"[GeometryGuard] OpenAPI skip corrupt part mark={m} id={part.Identifier.ID}: {geomErr}");
                    continue;
                }
                TSModel.Assembly asm = null;
                try { asm = part.GetAssembly(); } catch { continue; }
                if (asm == null) continue;
                int aid = 0;
                try { aid = asm.Identifier.ID; } catch { }
                if (aid == 0 || !seen.Add(aid)) continue;
                int sheet = startSheet < 1 ? 1 : startSheet;
                foreach (var attr in attributes)
                {
                    try
                    {
                        var d = new CastUnitDrawing(asm.Identifier, sheet, attr);
                        if (d.Insert())
                        {
                            n++;
                            if (created != null) created.Add(d);
                            if (n % 25 == 0)
                                MacroLog("OpenAPI CastUnit Insert created=" + n + " last=" + attr + " sheet=" + sheet);
                        }
                    }
                    catch (Exception ex)
                    {
                        Fail("OpenAPI CastUnit " + attr + " id=" + aid + " sheet=" + sheet, ex);
                    }
                    sheet++;
                }
            }
            MacroLog("OpenAPI CastUnit Insert created=" + n + " attributes=" + string.Join(",", attributes ?? new string[0]));
        }

        /// <summary>
        /// Insert cast-unit sheets starting at the next free sheet number for each assembly,
        /// so macro sheets (1/2/3) stay untouched and Open API gets 4/5/6.
        /// </summary>
        private void InsertCastUnitOpenApi(List<TSModel.Part> parts, string[] attributes, List<Drawing> created)
        {
            if (parts == null || attributes == null || attributes.Length == 0) return;
            var seen = new HashSet<int>();
            int n = 0;
            foreach (var part in parts)
            {
                if (part == null) continue;
                if (!CivilDrawingSupport.ValidatePartGeometry(part, out string geomErr))
                {
                    string m = CivilDrawingSupport.PieceMark(part, null);
                    MacroLog($"[GeometryGuard] OpenAPI skip corrupt part mark={m} id={part.Identifier.ID}: {geomErr}");
                    continue;
                }
                TSModel.Assembly asm = null;
                try { asm = part.GetAssembly(); } catch { continue; }
                if (asm == null) continue;
                int aid = 0;
                try { aid = asm.Identifier.ID; } catch { }
                if (aid == 0 || !seen.Add(aid)) continue;

                int sheet = NextFreeSheetNumber(asm.Identifier);
                MacroLog("OpenAPI next free sheet for id=" + aid + " → " + sheet);
                foreach (var attr in attributes)
                {
                    try
                    {
                        var d = new CastUnitDrawing(asm.Identifier, sheet, attr);
                        if (d.Insert())
                        {
                            n++;
                            if (created != null) created.Add(d);
                            MacroLog("OpenAPI CastUnit Insert OK attr=" + attr + " sheet=" + sheet);
                        }
                        else
                            MacroLog("OpenAPI CastUnit Insert FALSE attr=" + attr + " sheet=" + sheet + " id=" + aid);
                    }
                    catch (Exception ex)
                    {
                        Fail("OpenAPI CastUnit " + attr + " id=" + aid + " sheet=" + sheet, ex);
                    }
                    sheet++;
                }
            }
            MacroLog("OpenAPI CastUnit Insert created=" + n + " attributes=" + string.Join(",", attributes ?? new string[0]));
        }

        private int NextFreeSheetNumber(Tekla.Structures.Identifier assemblyId)
        {
            int max = 0;
            try
            {
                int targetId = assemblyId != null ? assemblyId.ID : 0;
                if (targetId == 0) return 1;
                var en = _handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var cu = en.Current as CastUnitDrawing;
                    if (cu == null) continue;
                    try
                    {
                        var cuid = cu.CastUnitIdentifier;
                        if (cuid == null || cuid.ID != targetId) continue;
                        if (cu.SheetNumber > max) max = cu.SheetNumber;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Fail("NextFreeSheetNumber", ex);
            }
            return max + 1;
        }

        /// <summary>
        /// Temporarily allow a second set of drawings for an assembly that already has macro sheets.
        /// Returns the previous string value so RestoreCloningCheck can put it back.
        /// </summary>
        private string AllowDuplicateSheets()
        {
            const string opt = "XS_DRAWING_CLONING_IGNORE_CHECK";
            string previous = null;
            try
            {
                Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption(opt, ref previous);
                MacroLog("OpenAPI " + opt + " was=" + (previous ?? "(null)"));
            }
            catch (Exception ex)
            {
                MacroLog("OpenAPI get " + opt + " failed: " + ex.Message);
            }

            try
            {
                // Public SetAdvancedOption is not exposed; ModelInternal.Operation is.
                bool ok = Tekla.Structures.ModelInternal.Operation.dotSetAdvancedOption(opt, true);
                MacroLog("OpenAPI set " + opt + "=TRUE ok=" + ok);
            }
            catch (Exception ex)
            {
                MacroLog("OpenAPI set " + opt + " failed: " + ex.Message);
            }
            return previous;
        }

        private void RestoreCloningCheck(string previous)
        {
            const string opt = "XS_DRAWING_CLONING_IGNORE_CHECK";
            try
            {
                if (previous == null)
                    return;
                bool asBool;
                if (bool.TryParse(previous, out asBool))
                    Tekla.Structures.ModelInternal.Operation.dotSetAdvancedOption(opt, asBool);
                else
                    Tekla.Structures.ModelInternal.Operation.dotSetAdvancedOption(opt, previous);
                MacroLog("OpenAPI restored " + opt + "=" + previous);
            }
            catch (Exception ex)
            {
                MacroLog("OpenAPI restore " + opt + " failed: " + ex.Message);
            }
        }

        public int CountDrawings()
        {
            int n = 0;
            try
            {
                var en = _handler.GetDrawings();
                while (en != null && en.MoveNext()) n++;
            }
            catch (Exception ex)
            {
                MacroLog("CountDrawings: " + ex.GetType().Name + ": " + ex.Message);
            }
            return n;
        }

        private bool TryRunMacro(string macroFile)
        {
            string modelDir = "";
            try { modelDir = _model.GetInfo()?.ModelPath ?? ""; } catch { }
            var attempts = new List<string>
            {
                macroFile,
                Path.Combine(modelDir, "macros", "modeling", macroFile),
                Path.Combine("modeling", macroFile),
                @"..\modeling\" + macroFile,
            };

            // Dynamic macro directories from XS_MACRO_DIRECTORY and XS_FIRM
            try
            {
                string xsMacroDir = "";
                Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref xsMacroDir);
                string xsDataDir = "";
                Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XSDATADIR", ref xsDataDir);

                if (!string.IsNullOrWhiteSpace(xsMacroDir))
                {
                    var splitDirs = xsMacroDir.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawDir in splitDirs)
                    {
                        string dir = rawDir.Trim();
                        if (string.IsNullOrEmpty(dir)) continue;

                        if (!string.IsNullOrEmpty(xsDataDir) && dir.IndexOf("%XSDATADIR%", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string normData = xsDataDir.TrimEnd('\\', '/');
                            dir = dir.Replace("%XSDATADIR%\\", normData + "\\")
                                     .Replace("%XSDATADIR%", normData + "\\");
                        }
                        dir = Environment.ExpandEnvironmentVariables(dir);

                        var subCandidates = new[]
                        {
                            Path.Combine(dir, "modeling", macroFile),
                            Path.Combine(dir, "drawings", macroFile),
                            Path.Combine(dir, macroFile)
                        };
                        foreach (var sc in subCandidates)
                        {
                            if (File.Exists(sc) && !attempts.Contains(sc))
                            {
                                attempts.Add(sc);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MacroLog("Dynamic macro path expansion note: " + ex.Message);
            }

            foreach (var path in attempts)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    MacroLog("trying " + path);
                    bool ok = Operation.RunMacro(path);
                    MacroLog("RunMacro bool=" + ok + " path=" + path);
                    if (ok)
                    {
                        MacroLog("started " + path);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Fail("RunMacro '" + path + "'", ex);
                }
            }
            MacroLog("RunMacro bool=false after " + attempts.Count + " paths");
            return false;
        }

        private bool SelectParts(List<TSModel.Part> parts)
        {
            var list = new ArrayList();
            var seen = new HashSet<int>();
            foreach (var part in parts)
            {
                if (part == null) continue;
                try
                {
                    var asm = part.GetAssembly();
                    if (asm != null)
                    {
                        int id = asm.Identifier.ID;
                        if (id != 0 && !seen.Add(id)) continue;
                        list.Add(asm);
                        continue;
                    }
                }
                catch { }
                try
                {
                    int id = part.Identifier.ID;
                    if (id != 0 && !seen.Add(id)) continue;
                    list.Add(part);
                }
                catch { }
            }
            if (list.Count == 0) return false;
            try
            {
                var ui = new TSUI.ModelObjectSelector();
                bool ok = ui.Select(list);
                MacroLog("Select n=" + list.Count + " ok=" + ok);
                try
                {
                    _model.CommitChanges();
                    MacroLog("CommitChanges after select");
                }
                catch (Exception ex)
                {
                    Fail("CommitChanges after select", ex);
                }
                Thread.Sleep(400);
                return ok;
            }
            catch (Exception ex)
            {
                Fail("Select", ex);
                return false;
            }
        }

        /// <summary>
        /// Wait until drawing count increases (CreateCastUnitDrawing committed),
        /// not merely until IsMacroRunning() is false (AKIT script end).
        /// </summary>
        private int WaitUntilDrawingsIncrease(int before, string name)
        {
            const int slice = 1000;
            const int maxMs = 5 * 60 * 1000;
            const int idleAfterScriptSec = 20;
            int waited = 0;
            int idle = 0;
            while (waited < maxMs)
            {
                bool running = false;
                try { running = Operation.IsMacroRunning(); } catch { }
                int now = CountDrawings();
                if (waited == 0 || waited % 15000 == 0)
                    MacroLog("wait " + name + " t=" + (waited / 1000) + "s IsMacroRunning=" + running + " drawings=" + now + " (need > " + before + ")");
                if (now > before)
                {
                    Thread.Sleep(1500);
                    CloseEditor("new sheet from " + name);
                    int settled = CountDrawings();
                    MacroLog("drawing-count delta " + before + " → " + settled + " after " + (waited / 1000) + "s");
                    return settled;
                }
                if (running) idle = 0;
                else idle++;
                if (!running && idle >= idleAfterScriptSec)
                {
                    MacroLog("script ended with no drawing-count increase after " + idle + "s idle (" + name + ")");
                    break;
                }
                Thread.Sleep(slice);
                waited += slice;
            }
            int after = CountDrawings();
            MacroLog("timeout waiting for drawing-count increase on " + name +
                " after " + (waited / 1000) + "s drawings=" + after);
            return after;
        }

        private void CloseEditor(string reason)
        {
            try
            {
                _handler.CloseActiveDrawing(false);
                MacroLog("CloseActiveDrawing (" + reason + ")");
            }
            catch (Exception ex)
            {
                MacroLog("CloseActiveDrawing (" + reason + "): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void CreateFloorGa(List<TSModel.Part> parts)
        {
            try
            {
                int n = new GaDrawingBuilder(_model).CreateMissingFloorPlans(parts);
                MacroLog("GA floor plans (EXP_GA_STD): " + n);
            }
            catch (Exception ex)
            {
                Fail("GA create", ex);
            }
        }

        private void CopyBundledMacrosToModel()
        {
            string modelDir = "";
            try { modelDir = _model.GetInfo()?.ModelPath ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
                return;
            string dest = Path.Combine(modelDir, "macros", "modeling");
            try { Directory.CreateDirectory(dest); } catch { return; }

            string src = FindBundledMacros();
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
            {
                MacroLog("bundled LocalMacros not found");
                return;
            }
            MacroLog("bundled macros src=" + src + " dest=" + dest);
            foreach (var file in Directory.GetFiles(src, "*.cs"))
            {
                string name = Path.GetFileName(file);
                string target = Path.Combine(dest, name);
                try
                {
                    File.Copy(file, target, true);
                    MacroLog("synced " + name + " → " + dest);
                }
                catch (Exception ex)
                {
                    Fail("copy " + name, ex);
                }
            }

            // Also synchronize with Firm macro folder if configured
            try
            {
                string firmDir = "";
                Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_FIRM", ref firmDir);
                if (!string.IsNullOrWhiteSpace(firmDir) && Directory.Exists(firmDir))
                {
                    string firmDest = Path.Combine(firmDir, "macros", "modeling");
                    Directory.CreateDirectory(firmDest);
                    foreach (var file in Directory.GetFiles(src, "*.cs"))
                    {
                        string name = Path.GetFileName(file);
                        string firmTarget = Path.Combine(firmDest, name);
                        File.Copy(file, firmTarget, true);
                    }
                    MacroLog("synced bundled macros → firm: " + firmDest);
                }
            }
            catch (Exception ex)
            {
                MacroLog("Firm macro sync note: " + ex.Message);
            }
        }

        private static string FindBundledMacros()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            var candidates = new[]
            {
                Path.Combine(baseDir, "LocalMacros"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "LocalMacros")),
                Path.Combine(Directory.GetCurrentDirectory(), "LocalMacros"),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
            return "";
        }

        private void LogSessionHeader(string mode, string markFilter)
        {
            string modelPath = "";
            string modelName = "";
            try
            {
                var info = _model.GetInfo();
                modelPath = info != null ? (info.ModelPath ?? "") : "";
                modelName = info != null ? (info.ModelName ?? "") : "";
            }
            catch { }
            string teklaLogs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trimble", "TeklaStructures", "2026.0", "logs");
            MacroLog("=== session mode=" + mode +
                (string.IsNullOrWhiteSpace(markFilter) ? "" : " mark=" + markFilter) + " ===");
            MacroLog("model=" + modelName + " path=" + modelPath);
            MacroLog("macros.log=" + (_logPath ?? "(none)"));
            MacroLog("if AKIT dies inside Tekla, check " + teklaLogs + " and the Tekla session log");
        }

        private void Fail(string context, Exception ex)
        {
            string type = ex != null ? ex.GetType().FullName : "Exception";
            string msg = ex != null ? (ex.Message ?? "") : "";
            string stack = ex != null ? (ex.StackTrace ?? "") : "";
            if (string.IsNullOrEmpty(stack))
                stack = Environment.StackTrace;
            MacroLog("FAIL [" + context + "] " + type + ": " + msg);
            if (!string.IsNullOrEmpty(stack))
                MacroLog(stack);
            if (!string.IsNullOrWhiteSpace(_outputRoot))
                CivilDrawingSupport.LogError(_outputRoot, context, ex);
        }

        private void MacroLog(string msg)
        {
            Console.WriteLine("[Macros] " + msg);
            if (string.IsNullOrWhiteSpace(_logPath)) return;
            try
            {
                string dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("s") + " " + msg + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        public class MemberGroups
        {
            public List<TSModel.Part> Walls { get; set; } = new List<TSModel.Part>();
            public List<TSModel.Part> Columns { get; set; } = new List<TSModel.Part>();
            public List<TSModel.Part> Beams { get; set; } = new List<TSModel.Part>();
            public HashSet<string> Marks { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

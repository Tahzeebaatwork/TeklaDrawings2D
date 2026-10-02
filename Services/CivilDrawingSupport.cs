using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using TSModel = Tekla.Structures.Model;
using TSDrawing = Tekla.Structures.Drawing;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Shared Tekla Structures 2026 Open API helpers for civil drawing extractors.
    /// All lengths/volumes are raw model units (mm / mm³). Grades are raw strings.
    /// </summary>
    public static class CivilDrawingSupport
    {
        public static readonly Type[] PartTypes =
        {
            typeof(TSModel.Beam),
            typeof(TSModel.ContourPlate),
            typeof(TSModel.PolyBeam),
        };

        public static readonly string[] FloorUdaNames =
        {
            "FLOOR_LEVEL", "FLOOR", "FLOOR_NAME", "STOREY", "STORY", "LEVEL", "FLOORLEVEL"
        };

        public static string ProjectCode(TSModel.Model model)
        {
            try
            {
                var dummy = model.GetProjectInfo();
                string code = FirstNonEmpty(dummy != null ? dummy.ProjectNumber : "", dummy != null ? dummy.Name : "");
                if (!string.IsNullOrWhiteSpace(code)) return Sanitize(code);
            }
            catch { /* report fallback */ }

            try
            {
                var info = model.GetInfo();
                return Sanitize(Path.GetFileNameWithoutExtension(info?.ModelName ?? "PROJECT"));
            }
            catch { return "PROJECT"; }
        }

        public static string ProjectName(TSModel.Model model)
        {
            try
            {
                var dummy = model.GetProjectInfo();
                return dummy != null ? (dummy.Name ?? "") : "";
            }
            catch { return ""; }
        }

        public static CivilDrawingHeader Header(
            TSModel.Model model, Drawing drawing, string drawingType, string pieceMark, bool hasSheet)
        {
            var h = new CivilDrawingHeader
            {
                DrawingType = drawingType,
                ProjectCode = ProjectCode(model),
                ProjectName = ProjectName(model),
                PieceMark = pieceMark ?? "",
                HasDrawingSheet = hasSheet,
                Units = CivilDrawingTypes.UnitsMm,
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
            };
            try { h.ModelName = model.GetInfo()?.ModelName ?? ""; } catch { /* optional */ }
            if (drawing != null)
            {
                try { h.DrawingName = drawing.Name ?? ""; } catch { }
                try { h.DrawingMark = drawing.Mark ?? ""; } catch { }
                try { h.Title1 = drawing.Title1 ?? ""; } catch { }
                try { h.Title2 = drawing.Title2 ?? ""; } catch { }
                try { h.Title3 = drawing.Title3 ?? ""; } catch { }
                try { h.TeklaDrawingType = drawing.GetType().Name; } catch { }
                try { h.Date = drawing.CreationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); } catch { }
                try { h.Revision = drawing.UpToDateStatus.ToString(); } catch { }
                try { h.Scale = drawing.Title3 ?? ""; } catch { }
            }
            if (string.IsNullOrWhiteSpace(h.PieceMark))
                h.PieceMark = FirstNonEmpty(StripBrackets(h.DrawingMark), h.DrawingName, "UNKNOWN");
            return h;
        }

        public static List<string> Classify(Drawing drawing)
        {
            var types = new List<string>();
            string blob = DrawingBlob(drawing);

            bool isGa = false, isErect = false, isShop = false, isConn = false, isBbs = false;
            bool isHardware = false, isPlace = false, isTable = false, isFab = false;
            try { isGa = drawing is GADrawing; } catch { }
            try { isShop = drawing is CastUnitDrawing || drawing is SinglePartDrawing; } catch { }
            try { isErect = drawing is AssemblyDrawing; } catch { }

            isHardware = ContainsAny(blob, "HARDWARE", "HARDWARE_PROPS", "CU_HARDWARE");
            isPlace = ContainsAny(blob, "PLACING", "REINFORCING_PLACING");
            isTable = ContainsAny(blob, "REINFORCING_TABLE", "REBAR_TABLE", "BAR TABLE", "BAR-TABLE");
            isFab = ContainsAny(blob, "FABRICAT", "BEAM_PG4", "PG4");

            if (ContainsAny(blob, "GA", "GAD", "GENERAL ARRANGEMENT", "GENERAL-ARRANGEMENT")) isGa = true;
            if (ContainsAny(blob, "ERECT", "SITE PLAN", "SETTING OUT", "SETTING-OUT")) isErect = true;
            if (ContainsAny(blob, "SHOP", "PIECE", "PRODUCTION") || isHardware || isFab) isShop = true;
            if (ContainsAny(blob, "CONN", "DETAIL", "JOINT", "HAUNCH", "CORBEL") || isHardware) isConn = true;
            if (ContainsAny(blob, "REBAR", "BBS", "BAR BEND", "BAR-BEND", "REINFORC") || isPlace || isTable) isBbs = true;
            try { if (drawing is MultiDrawing && !isConn && !isBbs) isGa = true; } catch { }

            // Macro-specific CU pages: extract only the matching civil type.
            if (isHardware)
            {
                types.Add(CivilDrawingTypes.Shop);
                types.Add(CivilDrawingTypes.Connection);
                return types;
            }
            if (isPlace || isTable)
            {
                types.Add(CivilDrawingTypes.RebarBbs);
                return types;
            }
            if (isFab)
            {
                types.Add(CivilDrawingTypes.Shop);
                return types;
            }

            try { if (drawing is CastUnitDrawing) isErect = true; } catch { }

            if (isGa) types.Add(CivilDrawingTypes.GA);
            if (isErect) types.Add(CivilDrawingTypes.Erection);
            if (isShop) types.Add(CivilDrawingTypes.Shop);
            if (isConn) types.Add(CivilDrawingTypes.Connection);
            if (isBbs) types.Add(CivilDrawingTypes.RebarBbs);

            if (types.Count == 0)
                types.Add(CivilDrawingTypes.Shop);

            // Generic piece shop sheets also carry BOM hardware + rebar for civil regeneration.
            if (types.Contains(CivilDrawingTypes.Shop) && drawing is CastUnitDrawing && !isHardware && !isFab)
            {
                if (!types.Contains(CivilDrawingTypes.RebarBbs)) types.Add(CivilDrawingTypes.RebarBbs);
                if (!types.Contains(CivilDrawingTypes.Connection)) types.Add(CivilDrawingTypes.Connection);
            }
            return types;
        }

        public static string DrawingBlob(Drawing drawing)
        {
            try
            {
                return ((drawing.Name ?? "") + " " + (drawing.Mark ?? "") + " " +
                        (drawing.Title1 ?? "") + " " + (drawing.Title2 ?? "") + " " +
                        (drawing.Title3 ?? "")).ToUpperInvariant();
            }
            catch { return ""; }
        }

        public static bool IsHardwareSheet(Drawing drawing)
        {
            if (drawing == null) return false;
            string blob = DrawingBlob(drawing);
            if (ContainsAny(blob, "HARDWARE", "HARDWARE_PROPS", "CU_HARDWARE")) return true;
            try { return drawing is CastUnitDrawing && !ContainsAny(blob, "PLACING", "TABLE", "FABRICAT", "PG4"); }
            catch { return false; }
        }

        public static bool Preflight(TSModel.Model model, DrawingHandler handler)
        {
            Console.WriteLine("=== Civil pre-flight ===");
            int tekla = 0;
            try
            {
                tekla = Process.GetProcessesByName("TeklaStructures").Length;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Process check error: " + ex.Message);
            }
            Console.WriteLine("TeklaStructures process count: " + tekla);

            int objects = 0;
            try
            {
                var en = model.GetModelObjectSelector().GetAllObjects();
                while (en != null && en.MoveNext()) objects++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetAllObjects error: " + ex.Message);
            }
            Console.WriteLine("Model.GetModelObjectSelector().GetAllObjects() count: " + objects);

            int drawings = 0;
            try
            {
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext()) drawings++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetDrawings error: " + ex.Message);
            }
            Console.WriteLine("DrawingHandler.GetDrawings() count: " + drawings);

            if (tekla == 0)
            {
                Console.WriteLine("PRE-FLIGHT FAILED: Tekla Structures 2026 process is not running.");
                return false;
            }
            if (objects == 0)
            {
                Console.WriteLine("PRE-FLIGHT FAILED: model is empty (GetAllObjects returned 0).");
                return false;
            }
            if (drawings == 0)
            {
                Console.WriteLine("PRE-FLIGHT NOTE: no drawings yet — will create WALL/COLUMN/BEAM sheets then extract.");
            }
            Console.WriteLine("PRE-FLIGHT PASSED");
            return true;
        }

        public static List<TSModel.Part> PartsFromDrawing(TSModel.Model model, DrawingHandler handler, Drawing drawing)
        {
            var parts = new List<TSModel.Part>();
            var seen = new HashSet<int>();
            foreach (var id in Identifiers(handler, drawing))
                Expand(model, id, parts, seen);
            var snapshot = parts.ToList();
            foreach (var part in snapshot)
                ExpandPartChildren(part, parts, seen);
            return parts;
        }

        public static void MergeSheetParts(TSModel.Model model, CivilSheetCapture capture, List<TSModel.Part> parts)
        {
            if (model == null || capture == null || parts == null) return;
            var seen = new HashSet<int>();
            foreach (var p in parts)
            {
                try { seen.Add(p.Identifier.ID); } catch { }
            }
            foreach (var id in capture.SheetModelObjects)
            {
                if (id == null || id.Id == 0) continue;
                try
                {
                    var ident = new Identifier(id.Id);
                    Expand(model, ident, parts, seen);
                }
                catch { }
            }
            var snapshot = parts.ToList();
            foreach (var part in snapshot)
                ExpandPartChildren(part, parts, seen);
        }

        private static Dictionary<string, List<Identifier>> _cuIndex;
        private static int _cuIndexModel;

        public static void MergeCastUnitIndex(TSModel.Model model, TSModel.Part main, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (model == null || main == null || parts == null) return;
            string piece = FirstNonEmpty(Report(main, "CAST_UNIT_POS"), Report(main, "ASSEMBLY_POS"));
            if (string.IsNullOrWhiteSpace(piece)) return;
            var index = CastUnitIndex(model);
            List<Identifier> ids;
            if (!index.TryGetValue(piece, out ids) && !index.TryGetValue(piece.Trim(), out ids))
                return;
            foreach (var id in ids)
                Expand(model, id, parts, seen);
        }

        private static Dictionary<string, List<Identifier>> CastUnitIndex(TSModel.Model model)
        {
            int key = 0;
            try { key = model.GetHashCode(); } catch { }
            if (_cuIndex != null && _cuIndexModel == key) return _cuIndex;
            _cuIndex = new Dictionary<string, List<Identifier>>(StringComparer.OrdinalIgnoreCase);
            _cuIndexModel = key;
            int n = 0;
            try
            {
                var en = model.GetModelObjectSelector().GetAllObjectsWithType(PartTypes);
                while (en != null && en.MoveNext())
                {
                    var p = en.Current as TSModel.Part;
                    if (p == null) continue;
                    string cu = FirstNonEmpty(Report(p, "CAST_UNIT_POS"), Report(p, "ASSEMBLY_POS"));
                    if (string.IsNullOrWhiteSpace(cu)) continue;
                    string mark = FirstNonEmpty(Report(p, "PART_POS"), Safe(() => p.Name));
                    if (!LooksLikeHardwarePart(p) && !LooksLikeHardwareMark(mark)) continue;
                    List<Identifier> list;
                    if (!_cuIndex.TryGetValue(cu, out list))
                    {
                        list = new List<Identifier>();
                        _cuIndex[cu] = list;
                    }
                    try { list.Add(p.Identifier); n++; } catch { }
                }
            }
            catch { }
            Console.WriteLine("[Civil] CAST_UNIT hardware index: " + n + " parts in " + _cuIndex.Count + " units");
            return _cuIndex;
        }

        private static bool LooksLikeHardwareMark(string mark)
        {
            string u = (mark ?? "").ToUpperInvariant();
            return u.StartsWith("P-") || u.StartsWith("GT") || u.StartsWith("SLV") ||
                   u.StartsWith("SP") || u.StartsWith("CNDT") || u.StartsWith("EB-");
        }

        /// <summary>
        /// Pre-flight validation of 3D solid geometry to prevent solkExtremaInGlobal fatal kernel aborts
        /// and int_dr_layout_attributes zero-dimension crashes on assemblies with broken boolean cuts (e.g. W10-50).
        /// </summary>
        public static bool ValidatePartGeometry(TSModel.Part part, out string errorReason)
        {
            errorReason = null;
            if (part == null)
            {
                errorReason = "Part is null";
                return false;
            }

            try
            {
                var solid = part.GetSolid();
                if (solid == null)
                {
                    errorReason = "Solid kernel returned null for part " + part.Identifier.ID;
                    return false;
                }

                Point min = solid.MinimumPoint;
                Point max = solid.MaximumPoint;
                if (min == null || max == null)
                {
                    errorReason = "Solid bounding point is null for part " + part.Identifier.ID;
                    return false;
                }

                double dx = Math.Abs(max.X - min.X);
                double dy = Math.Abs(max.Y - min.Y);
                double dz = Math.Abs(max.Z - min.Z);

                if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsNaN(dz) ||
                    double.IsInfinity(dx) || double.IsInfinity(dy) || double.IsInfinity(dz))
                {
                    errorReason = "Solid dimensions are NaN/Infinity for part " + part.Identifier.ID;
                    return false;
                }

                if (dx < 1e-4 && dy < 1e-4 && dz < 1e-4)
                {
                    errorReason = "Solid bounding box has zero volume (degenerate geometry) for part " + part.Identifier.ID;
                    return false;
                }

                // Check boolean cuts on this part
                try
                {
                    var booleans = part.GetBooleans();
                    while (booleans != null && booleans.MoveNext())
                    {
                        if (booleans.Current is TSModel.BooleanPart bp)
                        {
                            try
                            {
                                var cutPart = bp.OperativePart;
                                if (cutPart != null)
                                {
                                    var cutSolid = cutPart.GetSolid();
                                    if (cutSolid == null)
                                    {
                                        errorReason = "Invalid boolean cut solid on part " + part.Identifier.ID + " (cut ID: " + bp.Identifier.ID + ")";
                                        return false;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                errorReason = "Boolean cut solkExtremaInGlobal failure on part " + part.Identifier.ID + ": " + ex.Message;
                                return false;
                            }
                        }
                    }
                }
                catch { /* boolean enumeration best-effort */ }

                // Check assembly secondaries if this is the assembly main part
                try
                {
                    var assy = part.GetAssembly();
                    if (assy != null)
                    {
                        var secondaries = assy.GetSecondaries();
                        if (secondaries != null)
                        {
                            foreach (var obj in secondaries)
                            {
                                if (obj is TSModel.Part secPart)
                                {
                                    try
                                    {
                                        var secSolid = secPart.GetSolid();
                                        if (secSolid == null)
                                        {
                                            errorReason = "Invalid secondary solid in assembly " + assy.Identifier.ID + " on part " + secPart.Identifier.ID;
                                            return false;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        errorReason = "Secondary solid kernel failure in assembly " + assy.Identifier.ID + ": " + ex.Message;
                                        return false;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { /* assembly secondaries best-effort */ }

                return true;
            }
            catch (Exception ex)
            {
                errorReason = "solkExtremaInGlobal / solid kernel exception on part " + part.Identifier.ID + ": " + ex.Message;
                return false;
            }
        }

        public static List<Identifier> Identifiers(DrawingHandler handler, Drawing drawing)
        {
            var list = new List<Identifier>();
            try
            {
                if (drawing is SinglePartDrawing spd && spd.PartIdentifier != null)
                    list.Add(spd.PartIdentifier);
                else if (drawing is AssemblyDrawing ad && ad.AssemblyIdentifier != null)
                    list.Add(ad.AssemblyIdentifier);
                else if (drawing is CastUnitDrawing cud && cud.CastUnitIdentifier != null)
                    list.Add(cud.CastUnitIdentifier);
            }
            catch { /* optional */ }

            try
            {
                var fromApi = handler.GetModelObjectIdentifiers(drawing);
                if (fromApi != null)
                {
                    foreach (var id in fromApi)
                        if (id != null) list.Add(id);
                }
            }
            catch { /* optional */ }
            return list;
        }

        public static void Expand(TSModel.Model model, Identifier id, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (id == null) return;
            TSModel.ModelObject mo = null;
            try { mo = model.SelectModelObject(id); } catch { return; }
            ExpandObject(mo, parts, seen);
        }

        public static void ExpandObject(TSModel.ModelObject mo, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (mo == null) return;
            if (!(mo is TSModel.Part))
            {
                int oid = 0;
                try { oid = mo.Identifier.ID; } catch { }
                if (oid != 0 && !seen.Add(oid)) return;
            }
            if (mo is TSModel.Part part)
            {
                int id = 0;
                try
                {
                    id = part.Identifier.ID;
                    if (!part.Identifier.IsValid()) return;
                }
                catch { return; }
                if (id != 0 && !seen.Add(id)) return;
                parts.Add(part);
                ExpandPartChildren(part, parts, seen);
                return;
            }
            var assembly = mo as TSModel.Assembly;
            if (assembly != null)
            {
                try { assembly.Select(); } catch { }
                try
                {
                    var main = assembly.GetMainPart() as TSModel.Part;
                    if (main != null)
                    {
                        AddPart(main, parts, seen);
                        ExpandPartChildren(main, parts, seen);
                    }
                }
                catch { }
                try
                {
                    var seconds = assembly.GetSecondaries();
                    if (seconds != null)
                    {
                        foreach (var obj in seconds)
                        {
                            if (obj is TSModel.Part p)
                            {
                                AddPart(p, parts, seen);
                                ExpandPartChildren(p, parts, seen);
                            }
                            else ExpandObject(obj as TSModel.ModelObject, parts, seen);
                        }
                    }
                }
                catch { }
                try
                {
                    var subs = assembly.GetSubAssemblies();
                    if (subs != null)
                    {
                        foreach (var obj in subs)
                            ExpandObject(obj as TSModel.ModelObject, parts, seen);
                    }
                }
                catch { }
            }
            ExpandChildren(mo, parts, seen);
        }

        public static void ExpandPartChildren(TSModel.Part part, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (part == null) return;
            try
            {
                var comps = part.GetComponents();
                while (comps != null && comps.MoveNext())
                    ExpandObject(comps.Current as TSModel.ModelObject, parts, seen);
            }
            catch { }
            ExpandChildren(part, parts, seen);
        }

        private static void ExpandChildren(TSModel.ModelObject mo, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (mo == null) return;
            try
            {
                var kids = mo.GetChildren();
                while (kids != null && kids.MoveNext())
                {
                    var child = kids.Current as TSModel.ModelObject;
                    if (child == null) continue;
                    if (child is TSModel.Part p)
                    {
                        AddPart(p, parts, seen);
                        continue;
                    }
                    if (child is TSModel.Assembly || child is TSModel.Component || child is TSModel.Connection)
                        ExpandObject(child, parts, seen);
                }
            }
            catch { }
        }

        public static void ExpandNearbyHardware(TSModel.Model model, TSModel.Part main, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (model == null || main == null) return;
            Point min, max;
            try
            {
                var solid = main.GetSolid();
                if (solid == null) return;
                min = solid.MinimumPoint;
                max = solid.MaximumPoint;
            }
            catch { return; }
            if (min == null || max == null) return;
            const double pad = 200;
            var lo = new Point(min.X - pad, min.Y - pad, min.Z - pad);
            var hi = new Point(max.X + pad, max.Y + pad, max.Z + pad);
            try
            {
                var en = model.GetModelObjectSelector().GetObjectsByBoundingBox(lo, hi);
                while (en != null && en.MoveNext())
                {
                    var p = en.Current as TSModel.Part;
                    if (p == null) continue;
                    if (!LooksLikeHardwarePart(p)) continue;
                    AddPart(p, parts, seen);
                }
            }
            catch { }
        }

        public static TSModel.Part MainPart(IList<TSModel.Part> parts)
        {
            if (parts == null || parts.Count == 0) return null;
            foreach (var p in parts)
            {
                try
                {
                    var asm = p.GetAssembly();
                    var main = asm != null ? asm.GetMainPart() as TSModel.Part : null;
                    if (main != null && main.Identifier.ID == p.Identifier.ID)
                        return p;
                }
                catch { }
            }
            return parts[0];
        }

        /// <summary>
        /// WALL / COLUMN / BEAM for local macros. Precast contour-plate walls stay WALL
        /// (name / cast-unit mark wins). Hollowcore / insulation / formliner return empty.
        /// </summary>
        public static string MemberKind(TSModel.Part part)
        {
            if (part == null) return "";
            if (IsExcludedPrecast(part)) return "";
            string name = (Safe(() => part.Name) ?? "").ToUpperInvariant();
            string cls = (Safe(() => part.Class) ?? "").Trim();
            string pos = (FirstNonEmpty(Report(part, "CAST_UNIT_POS"), Report(part, "ASSEMBLY_POS"), name) ?? "").ToUpperInvariant();
            if (LooksLikeHardwareMark(pos) || LooksLikeHardwareMark(name)) return "";
            if (name.Contains("WALL") || pos.Contains("WALL") || Regex.IsMatch(pos, @"^W\d") || Regex.IsMatch(name, @"^W\d"))
                return "WALL";
            if (pos.StartsWith("P6") || name.StartsWith("P6") || name.Contains("PANEL"))
                return "WALL";
            if (cls == "1" || name.Contains("COL") || pos.Contains("COL")) return "COLUMN";
            if (cls == "2" || name.Contains("BEAM") || pos.Contains("BEAM")) return "BEAM";
            if (cls == "5" || cls == "6" || cls == "3") return "";
            if (name.Contains("SLAB") || name.Contains("DECK") || name.Contains("FOOT") || name.Contains("FOUND"))
                return "";
            string dir = PartDirection(part);
            if (dir == "VERTICAL") return "COLUMN";
            try { if (part is TSModel.Beam) return "BEAM"; } catch { }
            return "";
        }

        public static bool IsPrecastAssembly(TSModel.Assembly asm)
        {
            if (asm == null) return false;
            try { return asm.GetAssemblyType() == TSModel.Assembly.AssemblyTypeEnum.PRECAST_ASSEMBLY; }
            catch { return false; }
        }

        public static bool IsWallColumnBeam(TSModel.Part part)
        {
            string k = MemberKind(part);
            return k == "WALL" || k == "COLUMN" || k == "BEAM";
        }

        public static bool IsExcludedPrecast(TSModel.Part part)
        {
            string blob = ((Safe(() => part.Name) ?? "") + " " + (Safe(() => part.Profile != null ? part.Profile.ProfileString : "") ?? "")).ToUpperInvariant();
            return blob.Contains("HOLLOWCORE") || blob.Contains("HOLLOW CORE") ||
                   blob.Contains("FORMLINER") || blob.Contains("FORM LINER") ||
                   blob.Contains("INSULAT") || Regex.IsMatch(blob, @"\bHC\b");
        }

        public static List<TSModel.Part> CollectWallColumnBeamMains(TSModel.Model model)
        {
            var parts = new List<TSModel.Part>();
            var seen = new HashSet<int>();
            if (model == null) return parts;
            try
            {
                var en = model.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSModel.Assembly) });
                while (en != null && en.MoveNext())
                {
                    var asm = en.Current as TSModel.Assembly;
                    if (asm == null) continue;
                    if (!IsPrecastAssembly(asm)) continue;
                    TSModel.Part main;
                    try { main = asm.GetMainPart() as TSModel.Part; }
                    catch { continue; }
                    if (main == null || !IsWallColumnBeam(main)) continue;
                    int id = 0;
                    try { id = main.Identifier.ID; } catch { }
                    if (id != 0 && !seen.Add(id)) continue;
                    parts.Add(main);
                }
            }
            catch { }
            return parts;
        }

        public static double? PlausibleCoverMm(double v)
        {
            if (v >= 5 && v <= 150) return Round(v, 1);
            return null;
        }

        private static string PartDirection(TSModel.Part part)
        {
            try
            {
                Point s, e;
                Ends(part, out s, out e);
                if (s == null || e == null) return "";
                double dx = e.X - s.X, dy = e.Y - s.Y, dz = e.Z - s.Z;
                double adx = Math.Abs(dx), ady = Math.Abs(dy), adz = Math.Abs(dz);
                if (adz > adx * 1.2 && adz > ady * 1.2) return "VERTICAL";
                if (adz < adx * 0.35 && adz < ady * 0.35) return "HORIZONTAL";
                return adz >= adx && adz >= ady ? "VERTICAL" : "HORIZONTAL";
            }
            catch { return ""; }
        }

        public static bool MarksMatch(string value, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string a = StripMark(value);
            string f = StripMark(filter);
            if (a.Length == 0 || f.Length == 0) return false;
            if (a.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith(f, StringComparison.OrdinalIgnoreCase) && a.Length > f.Length)
            {
                char n = a[f.Length];
                if (n == '_' || n == '-' || n == '[' || n == ' ') return true;
            }
            return false;
        }

        public static string StripMark(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return s.Trim().Trim('[', ']').Trim();
        }

        public static string PieceMark(TSModel.Part part, Drawing drawing)
        {
            string mark = FirstNonEmpty(
                Report(part, "CAST_UNIT_POS"),
                Report(part, "ASSEMBLY_POS"),
                Report(part, "PART_POS"),
                Safe(() => part.Name));
            if (string.IsNullOrWhiteSpace(mark) && drawing != null)
            {
                try { mark = StripBrackets(drawing.Mark); } catch { }
            }
            return string.IsNullOrWhiteSpace(mark) ? "UNKNOWN" : mark.Trim();
        }

        public static CivilPieceNotes ReadNotes(TSModel.Part part)
        {
            var n = new CivilPieceNotes();
            if (part == null) return n;
            n.Name = Safe(() => part.Name);
            n.Class = Safe(() => part.Class);
            n.Finish = Safe(() => part.Finish);
            n.Profile = FirstNonEmpty(Safe(() => part.Profile != null ? part.Profile.ProfileString : ""), Report(part, "PROFILE"));
            n.Material = FirstNonEmpty(Safe(() => part.Material != null ? part.Material.MaterialString : ""), Report(part, "MATERIAL"));
            n.ConcreteGrade = FirstNonEmpty(
                Report(part, "CONCRETE_GRADE"),
                Report(part, "GRADE"),
                LooksConcrete(n.Material) ? n.Material : null);
            n.Strength28DayMPa = FirstNonEmpty(
                Uda(part, "STRENGTH_28DAY"),
                Uda(part, "FCK"),
                Uda(part, "FC_PRIME"),
                Uda(part, "CONCRETE_STRENGTH"),
                ExtractMpa(n.ConcreteGrade),
                PsiToMpa(n.Material));
            n.StrippingStrength = FirstNonEmpty(
                Uda(part, "STRIPPING_STRENGTH"),
                Uda(part, "STRIP_STRENGTH"),
                Uda(part, "RELEASE_STRENGTH"),
                Uda(part, "STRIPPING"));
            n.AirEntrainment = FirstNonEmpty(Uda(part, "AIR_ENTRAINMENT"), Uda(part, "AIR"), Uda(part, "AIR_CONTENT"));
            if (string.IsNullOrWhiteSpace(n.ConcreteGrade) && !string.IsNullOrWhiteSpace(n.Strength28DayMPa))
                n.ConcreteGrade = n.Strength28DayMPa + " MPa";
            if (string.IsNullOrWhiteSpace(n.ConcreteGrade) && LooksNumericPsi(n.Material))
                n.ConcreteGrade = n.Material + " psi";
            double w = ReportDouble(part, "WEIGHT");
            if (w > 0) { n.WeightKg = Round(w, 2); n.WeightLbs = Round(w * 2.2046226218, 1); }
            double vol = ReportDouble(part, "VOLUME");
            if (vol > 0) { n.VolumeMm3 = vol; n.VolumeM3 = Round(vol / 1e9, 4); }

            n.CoverTopMm = PlausibleCoverMm(ReportDouble(part, "COVER_TOP"));
            n.CoverBottomMm = PlausibleCoverMm(ReportDouble(part, "COVER_BOTTOM"));
            n.CoverSideMm = PlausibleCoverMm(ReportDouble(part, "COVER_SIDE"));
            var covers = new List<double>();
            foreach (var c in new[] { n.CoverTopMm, n.CoverBottomMm, n.CoverSideMm,
                         PlausibleCoverMm(ReportDouble(part, "COVER")),
                         PlausibleCoverMm(ReportDouble(part, "COVER_THICKNESS")),
                         PlausibleCoverMm(ReportDouble(part, "CONCRETE_COVER")) })
            {
                if (c != null && c.Value > 0) covers.Add(c.Value);
            }
            if (covers.Count > 0) n.MinCoverMm = Round(covers.Min(), 1);
            return n;
        }

        public static void EnrichNotesFromSheetAndRebar(CivilPieceNotes n, CivilSheetCapture capture, IList<TSModel.Part> parts)
        {
            if (n == null) return;
            if ((n.MinCoverMm == null || n.MinCoverMm <= 0) && parts != null)
            {
                double minCover = double.MaxValue;
                foreach (var part in parts)
                {
                    try
                    {
                        var en = part.GetReinforcements();
                        while (en != null && en.MoveNext())
                        {
                            var reinf = en.Current as TSModel.Reinforcement;
                            if (reinf == null) continue;
                            foreach (var prop in new[] { "COVER", "COVER_ON_PLANE", "COVER_ON_PLANE_START", "COVER_ON_EDGE_START", "COVER_ON_EDGE", "COVER_TOP", "COVER_BOTTOM", "COVER_SIDE" })
                            {
                                double v = ReportDouble(reinf, prop);
                                var ok = PlausibleCoverMm(v);
                                if (ok != null && ok.Value < minCover) minCover = ok.Value;
                            }
                            try
                            {
                                var offs = reinf.OnPlaneOffsets;
                                if (offs != null)
                                {
                                    foreach (var o in offs)
                                    {
                                        double v = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                                        var ok = PlausibleCoverMm(v);
                                        if (ok != null && ok.Value < minCover) minCover = ok.Value;
                                    }
                                }
                            }
                            catch { }
                            try
                            {
                                if (reinf.StartPointOffsetType == TSModel.Reinforcement.RebarOffsetTypeEnum.OFFSET_TYPE_COVER_THICKNESS)
                                {
                                    var ok = PlausibleCoverMm(reinf.StartPointOffsetValue);
                                    if (ok != null && ok.Value < minCover) minCover = ok.Value;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                if (minCover < double.MaxValue) n.MinCoverMm = Round(minCover, 1);
            }

            string blob = capture != null ? string.Join(" ", capture.Texts ?? new List<string>()) : "";
            if (string.IsNullOrWhiteSpace(n.Strength28DayMPa))
            {
                var m = Regex.Match(blob, @"(\d+(?:\.\d+)?)\s*(?:MPa|MPA).*?(?:28|f'?c)|(?:28|f'?c).*?(\d+(?:\.\d+)?)\s*(?:MPa|MPA)", RegexOptions.IgnoreCase);
                if (m.Success)
                    n.Strength28DayMPa = FirstNonEmpty(m.Groups[1].Value, m.Groups[2].Value);
            }
            if (string.IsNullOrWhiteSpace(n.StrippingStrength))
            {
                var m = Regex.Match(blob, @"(?:stripp|release)[^\d]{0,24}(\d+(?:\.\d+)?)\s*(?:MPa|psi|MPA)?", RegexOptions.IgnoreCase);
                if (m.Success) n.StrippingStrength = m.Groups[1].Value;
            }
            if ((n.MinCoverMm == null || n.MinCoverMm <= 0) && !string.IsNullOrWhiteSpace(blob))
            {
                var m = Regex.Match(blob, @"(?:min(?:imum)?\s*)?cover[^\d]{0,16}(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                double v;
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                {
                    var ok = PlausibleCoverMm(v);
                    if (ok != null) n.MinCoverMm = ok;
                }
            }
        }

        public static List<CivilBbsBar> ReadRebar(TSModel.Part part, string pieceMark)
        {
            var list = new List<CivilBbsBar>();
            if (part == null) return list;
            var seen = new HashSet<int>();

            try
            {
                var en = part.GetReinforcements();
                while (en != null && en.MoveNext())
                {
                    try
                    {
                        if (en.Current is TSModel.RebarSet set)
                            AddRebarSet(set, pieceMark, list, seen);
                        else if (en.Current is TSModel.Reinforcement reinf)
                            AddBar(ReadReinforcement(reinf, pieceMark, reinf.GetType().Name, null), list, seen);
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* optional */ }

            return list;
        }

        public static List<CivilBbsBar> ReadRebarAllParts(TSModel.Model model, IList<TSModel.Part> parts, string pieceMark)
        {
            var list = new List<CivilBbsBar>();
            var seen = new HashSet<int>();
            var main = MainPart(parts);
            if (main != null)
            {
                foreach (var bar in ReadRebarSetsOnPart(model, main, pieceMark))
                    AddBar(bar, list, seen);
            }
            if (parts == null) return list;
            foreach (var part in parts)
            {
                if (main != null)
                {
                    try { if (part.Identifier.ID == main.Identifier.ID) continue; } catch { }
                }
                foreach (var bar in ReadRebar(part, pieceMark))
                    AddBar(bar, list, seen);
            }
            return list;
        }

        private static List<TSModel.RebarSet> _rebarSetCache;
        private static int _rebarSetCacheModel;

        public static List<CivilBbsBar> ReadRebarSetsOnPart(TSModel.Model model, TSModel.Part part, string pieceMark)
        {
            var list = ReadRebar(part, pieceMark);
            var seen = new HashSet<int>(list.Select(b => SafeIdFromGuid(b.Guid)));
            int partId = 0, asmId = 0;
            try { partId = part.Identifier.ID; } catch { }
            try { var a = part.GetAssembly(); if (a != null) asmId = a.Identifier.ID; } catch { }

            foreach (var set in CachedRebarSets(model))
            {
                bool mine = false;
                try
                {
                    if (set.FatherPart != null && set.FatherPart.Identifier.ID == partId)
                        mine = true;
                }
                catch { }
                if (!mine && asmId != 0)
                {
                    try
                    {
                        var asm = set.GetAssembly();
                        if (asm != null && asm.Identifier.ID == asmId) mine = true;
                    }
                    catch { }
                }
                if (mine) AddRebarSet(set, pieceMark, list, seen);
            }
            return list;
        }

        private static List<TSModel.RebarSet> CachedRebarSets(TSModel.Model model)
        {
            int key = 0;
            try { key = model.GetHashCode(); } catch { }
            if (_rebarSetCache != null && _rebarSetCacheModel == key) return _rebarSetCache;
            _rebarSetCache = new List<TSModel.RebarSet>();
            _rebarSetCacheModel = key;
            try
            {
                var en = model.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSModel.RebarSet) });
                while (en != null && en.MoveNext())
                {
                    var set = en.Current as TSModel.RebarSet;
                    if (set != null) _rebarSetCache.Add(set);
                }
            }
            catch { }
            Console.WriteLine("[Civil] cached " + _rebarSetCache.Count + " RebarSet objects");
            return _rebarSetCache;
        }

        public static void AddRebarSet(TSModel.RebarSet set, string pieceMark, List<CivilBbsBar> list, HashSet<int> seen)
        {
            if (set == null) return;
            string frozen = "";
            try { frozen = set.FrozenState.ToString(); } catch { }
            int before = list.Count;
            try
            {
                var en = set.GetReinforcements();
                while (en != null && en.MoveNext())
                {
                    var reinf = en.Current as TSModel.Reinforcement;
                    if (reinf == null) continue;
                    AddBar(ReadReinforcement(reinf, pieceMark, "RebarSet/" + reinf.GetType().Name, frozen), list, seen);
                }
            }
            catch { /* fall through */ }

            if (list.Count == before)
            {
                var rec = new CivilBbsBar
                {
                    PieceMark = pieceMark,
                    SourceType = "RebarSet",
                    FrozenState = frozen,
                    Guid = Safe(() => set.Identifier.GUID.ToString()),
                };
                try
                {
                    var p = set.RebarProperties;
                    rec.Mark = p.Name ?? "";
                    rec.Size = p.Size ?? "";
                    rec.Grade = p.Grade ?? "";
                    rec.PinRadiusMm = p.BendingRadius;
                    rec.DiameterMm = ParseDia(rec.Size);
                }
                catch { }
                rec.Quantity = Math.Max(1, rec.Quantity);
                AddBar(rec, list, seen);
            }
        }

        public static CivilBbsBar ReadReinforcement(TSModel.Reinforcement reinf, string pieceMark, string source, string frozen)
        {
            var rec = new CivilBbsBar
            {
                PieceMark = pieceMark,
                SourceType = source,
                FrozenState = frozen,
            };
            try { rec.Guid = reinf.Identifier.GUID.ToString(); } catch { }
            rec.Mark = FirstNonEmpty(Report(reinf, "REBAR_POS"), rec.Mark, Safe(() => reinf.Name));
            rec.Grade = FirstNonEmpty(Safe(() => reinf.Grade), Report(reinf, "GRADE"));
            try { rec.Quantity = reinf.GetNumberOfRebars(); } catch { rec.Quantity = 1; }

            var group = reinf as TSModel.RebarGroup;
            var single = reinf as TSModel.SingleRebar;
            rec.Size = group != null ? (group.Size ?? "") : (single != null ? (single.Size ?? "") : Report(reinf, "SIZE"));
            rec.DiameterMm = ParseDia(rec.Size);
            rec.ShapeCode = FirstNonEmpty(Report(reinf, "SHAPE"), Report(reinf, "SHAPE_CODE"));
            rec.TotalLengthMm = Positive(ReportDouble(reinf, "LENGTH"));
            rec.TotalAreaMm2 = Positive(ReportDouble(reinf, "AREA"));
            rec.WeightKg = Positive(ReportDouble(reinf, "WEIGHT"));
            rec.A = Positive(ReportDouble(reinf, "DIM_A"));
            rec.B = Positive(ReportDouble(reinf, "DIM_B"));
            rec.C = Positive(ReportDouble(reinf, "DIM_C"));
            rec.D = Positive(ReportDouble(reinf, "DIM_D"));
            rec.E = Positive(ReportDouble(reinf, "DIM_E"));
            rec.F = Positive(ReportDouble(reinf, "DIM_F"));
            rec.G = Positive(ReportDouble(reinf, "DIM_G"));
            rec.H = Positive(ReportDouble(reinf, "DIM_H"));
            rec.H2 = Positive(ReportDouble(reinf, "DIM_H2"));
            rec.J = Positive(ReportDouble(reinf, "DIM_J"));
            rec.K = Positive(ReportDouble(reinf, "DIM_K"));
            rec.K2 = Positive(ReportDouble(reinf, "DIM_K2"));
            rec.O = Positive(ReportDouble(reinf, "DIM_O"));

            if (group?.Spacings != null)
                rec.Spacing = string.Join("*", group.Spacings.Cast<object>().Select(s => Convert.ToString(s, CultureInfo.InvariantCulture)));

            var startHook = group != null ? group.StartHook : single?.StartHook;
            if (startHook != null)
            {
                try { rec.PinRadiusMm = startHook.Radius; } catch { }
            }

            FillGeometry(reinf, rec);
            rec.ShapeType = InferShape(rec);
            if (rec.Quantity <= 0) rec.Quantity = 1;
            return rec;
        }

        public static List<CivilHardwareRow> ReadHardware(IList<TSModel.Part> parts, string pieceMark)
        {
            var rows = new List<CivilHardwareRow>();
            if (parts == null) return rows;
            TSModel.Part main = MainPart(parts);
            int mainId = 0;
            try { if (main != null) mainId = main.Identifier.ID; } catch { }

            foreach (var part in parts)
            {
                int id = 0;
                try { id = part.Identifier.ID; } catch { continue; }
                bool embed = mainId != 0 && id != mainId;
                string name = FirstNonEmpty(Safe(() => part.Name), Report(part, "NAME"));
                string mark = FirstNonEmpty(Report(part, "PART_POS"), name);
                string kind = HardwareKind(name, Safe(() => part.Class), Safe(() => part.Profile != null ? part.Profile.ProfileString : ""), mark);
                if (!embed && kind == "PART") continue;

                var row = new CivilHardwareRow
                {
                    PieceMark = pieceMark,
                    Mark = mark,
                    Description = name,
                    Kind = embed ? (kind == "PART" ? "EMBED" : kind) : kind,
                    Profile = FirstNonEmpty(Safe(() => part.Profile != null ? part.Profile.ProfileString : ""), Report(part, "PROFILE")),
                    Material = FirstNonEmpty(Safe(() => part.Material != null ? part.Material.MaterialString : ""), Report(part, "MATERIAL")),
                    Quantity = 1,
                    Guid = Safe(() => part.Identifier.GUID.ToString()),
                    Id = id,
                };
                row.Position = Origin(part);
                rows.Add(row);

                try
                {
                    var bolts = part.GetBolts();
                    while (bolts != null && bolts.MoveNext())
                    {
                        var bg = bolts.Current as TSModel.BoltGroup;
                        if (bg == null) continue;
                        ArrayList positions = null;
                        try { positions = bg.BoltPositions; } catch { }
                        int qty = positions != null ? positions.Count : 1;
                        var h = new CivilHardwareRow
                        {
                            PieceMark = pieceMark,
                            Mark = FirstNonEmpty(bg.BoltStandard, "BOLT"),
                            Description = (bg.BoltStandard ?? "BOLT") + " " + bg.BoltSize.ToString(CultureInfo.InvariantCulture),
                            Kind = ContainsAny((bg.BoltStandard ?? "").ToUpperInvariant(), "ANCHOR") ? "ANCHOR" : "BOLT",
                            Quantity = Math.Max(1, qty),
                            Guid = Safe(() => bg.Identifier.GUID.ToString()),
                            Id = Safe(() => bg.Identifier.ID),
                        };
                        if (positions != null && positions.Count > 0 && positions[0] is Point p)
                            h.Position = Xyz(p);
                        rows.Add(h);
                    }
                }
                catch { }
            }
            return rows;
        }

        public static List<CivilBomRow> BomFromHardware(IList<CivilHardwareRow> hardware)
        {
            var map = new Dictionary<string, CivilBomRow>(StringComparer.OrdinalIgnoreCase);
            if (hardware == null) return new List<CivilBomRow>();
            foreach (var h in hardware)
            {
                string family = FamilyMark(h.Mark);
                string kind = h.Kind ?? "PART";
                if (string.Equals(kind, "EMBED", StringComparison.OrdinalIgnoreCase) &&
                    map.ContainsKey(family))
                    continue;
                if (!map.TryGetValue(family, out var row))
                {
                    row = new CivilBomRow
                    {
                        PieceMark = h.PieceMark,
                        Mark = family,
                        Description = h.Description,
                        Kind = kind,
                        Profile = h.Profile,
                        Material = h.Material,
                        Guid = h.Guid,
                        Id = h.Id,
                    };
                    map[family] = row;
                }
                else if (string.Equals(row.Kind, "EMBED", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(kind, "EMBED", StringComparison.OrdinalIgnoreCase))
                {
                    row.Kind = kind;
                    if (!string.IsNullOrWhiteSpace(h.Description)) row.Description = h.Description;
                }
                if (!string.Equals(kind, "EMBED", StringComparison.OrdinalIgnoreCase))
                    row.Quantity += Math.Max(1, h.Quantity);
                else if (row.Quantity <= 0)
                    row.Quantity += Math.Max(1, h.Quantity);
            }
            return map.Values.ToList();
        }

        public static string FamilyMark(string mark)
        {
            if (string.IsNullOrWhiteSpace(mark)) return "";
            string m = mark.Trim();
            // Tekla assembly prefixes only (-11, -21, -31, …), not size suffixes (-686, -203, -457).
            m = Regex.Replace(m, @"-(1[1-9]|2[1-9]|3[1-9]|4[1-9]|5[1-9])$", "");
            if (Regex.IsMatch(m, @"^CNDT\d+$", RegexOptions.IgnoreCase)) return "CNDT";
            if (Regex.IsMatch(m, @"^EB-S-PL\d*$", RegexOptions.IgnoreCase)) return "EB-S-PL";
            return m;
        }

        public static CivilPiecePlacement Placement(TSModel.Part part, string pieceMark)
        {
            var rec = new CivilPiecePlacement
            {
                PieceMark = pieceMark,
                Name = Safe(() => part.Name),
                Profile = FirstNonEmpty(Safe(() => part.Profile != null ? part.Profile.ProfileString : ""), Report(part, "PROFILE")),
                Material = FirstNonEmpty(Safe(() => part.Material != null ? part.Material.MaterialString : ""), Report(part, "MATERIAL")),
                Floor = ReadFloor(part),
            };
            try { rec.Id = part.Identifier.ID; rec.Guid = part.Identifier.GUID.ToString(); } catch { }
            Point s, e;
            Ends(part, out s, out e);
            rec.StartPoint = Xyz(s);
            rec.EndPoint = Xyz(e);
            rec.Position = new CivilXyz
            {
                X = Round((s.X + e.X) / 2, 3),
                Y = Round((s.Y + e.Y) / 2, 3),
                Z = Round((s.Z + e.Z) / 2, 3),
            };
            rec.LengthMm = Round(Math.Sqrt(
                Math.Pow(e.X - s.X, 2) + Math.Pow(e.Y - s.Y, 2) + Math.Pow(e.Z - s.Z, 2)), 1);
            try
            {
                var cs = part.GetCoordinateSystem();
                rec.AxisX = Vec(cs.AxisX);
                rec.AxisY = Vec(cs.AxisY);
                rec.AxisZ = cs.AxisX != null && cs.AxisY != null
                    ? new CivilXyz
                    {
                        X = Round(cs.AxisX.Y * cs.AxisY.Z - cs.AxisX.Z * cs.AxisY.Y, 3),
                        Y = Round(cs.AxisX.Z * cs.AxisY.X - cs.AxisX.X * cs.AxisY.Z, 3),
                        Z = Round(cs.AxisX.X * cs.AxisY.Y - cs.AxisX.Y * cs.AxisY.X, 3),
                    }
                    : new CivilXyz { Z = 1 };
            }
            catch { }
            rec.Grid = InferGrid(rec.Position);
            return rec;
        }

        public static List<CivilGridLine> ReadGrids(TSModel.Model model)
        {
            var grids = new List<CivilGridLine>();
            try
            {
                var en = model.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSModel.Grid) });
                while (en != null && en.MoveNext())
                {
                    var grid = en.Current as TSModel.Grid;
                    if (grid == null) continue;
                    double[] xs = ParseCoords(grid.CoordinateX);
                    double[] ys = ParseCoords(grid.CoordinateY);
                    string[] lx = ParseLabels(grid.LabelX, xs.Length);
                    string[] ly = ParseLabels(grid.LabelY, ys.Length);
                    double xmin = xs.Length > 0 ? xs.Min() : 0;
                    double xmax = xs.Length > 0 ? xs.Max() : 0;
                    double ymin = ys.Length > 0 ? ys.Min() : 0;
                    double ymax = ys.Length > 0 ? ys.Max() : 0;
                    for (int i = 0; i < xs.Length; i++)
                    {
                        grids.Add(new CivilGridLine
                        {
                            Name = grid.Name ?? "",
                            Label = i < lx.Length ? lx[i] : "X" + (i + 1),
                            Axis = "X",
                            StartPoint = new CivilXyz { X = xs[i], Y = ymin, Z = 0 },
                            EndPoint = new CivilXyz { X = xs[i], Y = ymax, Z = 0 },
                            SpacingMm = i > 0 ? Round(xs[i] - xs[i - 1], 1) : (double?)null,
                        });
                    }
                    for (int i = 0; i < ys.Length; i++)
                    {
                        grids.Add(new CivilGridLine
                        {
                            Name = grid.Name ?? "",
                            Label = i < ly.Length ? ly[i] : "Y" + (i + 1),
                            Axis = "Y",
                            StartPoint = new CivilXyz { X = xmin, Y = ys[i], Z = 0 },
                            EndPoint = new CivilXyz { X = xmax, Y = ys[i], Z = 0 },
                            SpacingMm = i > 0 ? Round(ys[i] - ys[i - 1], 1) : (double?)null,
                        });
                    }
                }
            }
            catch { }
            return grids;
        }

        public static CivilSheetCapture CaptureActiveSheet(DrawingHandler handler, Drawing drawing, string pieceMark, string outputRoot)
        {
            var cap = new CivilSheetCapture();
            if (drawing == null)
            {
                cap.Error = "no drawing";
                LogSkip(outputRoot, "Drawing", "CaptureActiveSheet skipped: drawing is null");
                return cap;
            }

            bool opened = false;
            try
            {
                try { handler.UpdateDrawing(drawing); } catch { }
                try { opened = handler.SetActiveDrawing(drawing, false); }
                catch (Exception ex)
                {
                    cap.Error = ex.Message;
                    LogSkip(outputRoot, drawing.GetType().Name, "SetActiveDrawing failed: " + ex.Message);
                }
                cap.Opened = opened;

                ContainerView sheet = null;
                try { sheet = drawing.GetSheet(); }
                catch (Exception ex)
                {
                    cap.Error = ex.Message;
                    LogSkip(outputRoot, "Sheet", "GetSheet failed for '" + pieceMark + "': " + ex.Message);
                }
                if (sheet == null)
                {
                    if (string.IsNullOrEmpty(cap.Error)) cap.Error = "GetSheet returned null";
                    LogSkip(outputRoot, "Sheet", "GetSheet returned null for '" + pieceMark + "'");
                    return cap;
                }

                DrawingObjectEnumerator views = null;
                try { views = sheet.GetAllViews(); }
                catch
                {
                    try { views = sheet.GetViews(); } catch { }
                }

                int[] slots = new int[2];
                while (views != null && views.MoveNext())
                {
                    var view = views.Current as View;
                    if (view == null) continue;
                    var rec = new CivilViewRef();
                    try { rec.Name = view.Name ?? ""; } catch { }
                    try { rec.ViewType = view.ViewType.ToString(); } catch { }
                    try { rec.Scale = view.Attributes.Scale; } catch { }
                    rec.Name = MapShopViewName(rec.Name, rec.ViewType, slots);
                    try { rec.PluginInputs = ReadPluginInputs(view); } catch { }
                    cap.Views.Add(rec);
                    if (rec.Scale > 0 && cap.Scale <= 0) cap.Scale = rec.Scale;
                    CaptureViewObjects(view, rec.Name, pieceMark, cap, outputRoot, slots);
                }

                try { CaptureViewObjects(sheet, "Sheet", pieceMark, cap, outputRoot, slots); }
                catch (Exception ex) { LogSkip(outputRoot, "SheetObjects", ex.Message); }

                cap.Features.AddRange(FeaturesFromTexts(cap.Texts, ""));
                cap.Features.AddRange(FeaturesFromTexts(cap.Views.Select(v => v.Name), ""));
                EnsureShopViewSlots(cap.Views);
            }
            catch (Exception ex)
            {
                cap.Error = ex.Message;
                LogError(outputRoot, pieceMark + " CaptureActiveSheet", ex);
            }
            finally
            {
                if (opened)
                {
                    try { handler.CloseActiveDrawing(false); } catch { }
                }
            }
            return cap;
        }

        public static List<CivilProfileFeature> ReadCuts(IList<TSModel.Part> parts)
        {
            var list = new List<CivilProfileFeature>();
            if (parts == null) return list;
            foreach (var part in parts)
            {
                try
                {
                    var cuts = part.GetBooleans();
                    while (cuts != null && cuts.MoveNext())
                    {
                        string kind = "cut";
                        string text = "";
                        try
                        {
                            text = cuts.Current.GetType().Name;
                            var bp = cuts.Current as TSModel.BooleanPart;
                            if (bp != null)
                            {
                                var op = bp.OperativePart;
                                string name = op != null ? FirstNonEmpty(Safe(() => op.Name), Safe(() => op.Profile != null ? op.Profile.ProfileString : "")) : "";
                                text = FirstNonEmpty(name, text);
                            }
                        }
                        catch { }
                        string u = (text ?? "").ToUpperInvariant();
                        if (cuts.Current is TSModel.Fitting) kind = "chamfer";
                        else if (u.Contains("CHAMFER") || u.Contains("CHAM") || u.StartsWith("PL")) kind = "chamfer";
                        else if (u.Contains("RECESS") || u.Contains("REVEAL") || u.Contains("TRIANGLE")) kind = "recess";
                        else if (u.Contains("NOTCH") || u.Contains("CUTPLANE")) kind = "notch";
                        else if (u.Contains("CORBEL") || u.Contains("HAUNCH")) kind = "corbel";
                        else if (u.Contains("CUT") || u.Contains("BOOLEAN")) kind = "cut";
                        list.Add(new CivilProfileFeature { Kind = kind, Source = "GetBooleans", Text = text });
                    }
                }
                catch { }
            }
            return list;
        }

        public static List<CivilViewRef> EnsureShopViewSlots(List<CivilViewRef> views)
        {
            if (views == null) views = new List<CivilViewRef>();
            foreach (var name in new[] { "End1", "End2", "Side A", "Side B" })
            {
                if (!views.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
                    views.Add(new CivilViewRef { Name = name, ViewType = name.StartsWith("End") ? "EndView" : "FrontView" });
            }
            return views;
        }

        public static List<CivilDimensionRow> ReadDimensions(Drawing drawing, string pieceMark)
        {
            var rows = new List<CivilDimensionRow>();
            if (drawing == null) return rows;
            try
            {
                var sheet = drawing.GetSheet();
                if (sheet == null) return rows;
                var views = sheet.GetAllViews();
                while (views != null && views.MoveNext())
                {
                    var view = views.Current as View;
                    if (view == null) continue;
                    string viewName = "";
                    try { viewName = view.Name ?? ""; } catch { }
                    var objs = view.GetAllObjects();
                    while (objs != null && objs.MoveNext())
                    {
                        try
                        {
                            if (objs.Current is StraightDimensionSet set)
                            {
                                var kids = set.GetObjects();
                                while (kids != null && kids.MoveNext())
                                {
                                    var dim = kids.Current as StraightDimension;
                                    if (dim == null) continue;
                                    rows.Add(DimFrom(pieceMark, dim, viewName));
                                }
                            }
                            else if (objs.Current is StraightDimension sdim)
                            {
                                rows.Add(DimFrom(pieceMark, sdim, viewName));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return rows;
        }

        public static List<string> ReadPluginInputs(View view)
        {
            var list = new List<string>();
            if (view == null) return list;
            try
            {
                var objs = view.GetAllObjects();
                while (objs != null && objs.MoveNext())
                {
                    var plugin = objs.Current as Plugin;
                    if (plugin == null) continue;
                    try
                    {
                        // TS2026: Plugin.GetPluginInput()
                        var input = plugin.GetPluginInput();
                        if (input == null) continue;
                        foreach (var item in input)
                            list.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public static string MapShopViewName(string name, string viewType)
        {
            return MapShopViewName(name, viewType, null);
        }

        public static string MapShopViewName(string name, string viewType, int[] slots)
        {
            string n = (name ?? "").ToUpperInvariant();
            string v = (viewType ?? "").ToUpperInvariant();
            if (n.Contains("END") && (n.Contains("2") || n.Contains("RIGHT"))) return "End2";
            if (n.Contains("END") && (n.Contains("1") || n.Contains("LEFT") || n.Contains("START"))) return "End1";
            if (n.Contains("SIDE") && n.Contains("B")) return "Side B";
            if (n.Contains("SIDE") && n.Contains("A")) return "Side A";
            if (v.Contains("END"))
            {
                if (slots != null)
                {
                    slots[0]++;
                    return slots[0] <= 1 ? "End1" : "End2";
                }
                return string.IsNullOrWhiteSpace(name) ? "End1" : name;
            }
            if (v.Contains("BACK")) return "Side B";
            if (v.Contains("FRONT"))
            {
                if (slots != null)
                {
                    slots[1]++;
                    return slots[1] <= 1 ? "Side A" : "Side B";
                }
                return "Side A";
            }
            return string.IsNullOrWhiteSpace(name) ? viewType : name;
        }

        public static void AddOverallDimensions(CivilSheetCapture cap, TSModel.Part main, string pieceMark)
        {
            if (cap == null || main == null) return;
            double dx = 0, dy = 0, dz = 0;
            try
            {
                var solid = main.GetSolid();
                if (solid == null) return;
                dx = Round(Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X), 1);
                dy = Round(Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y), 1);
                dz = Round(Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z), 1);
            }
            catch { return; }
            var named = new[]
            {
                new { Name = "End1", Val = Math.Min(dx, Math.Min(dy, dz)), Kind = "OverallThickness" },
                new { Name = "End2", Val = Math.Min(dx, Math.Min(dy, dz)), Kind = "OverallThickness" },
                new { Name = "Side A", Val = Math.Max(dx, Math.Max(dy, dz)), Kind = "OverallLength" },
                new { Name = "Side B", Val = new[] { dx, dy, dz }.OrderBy(v => v).ElementAt(1), Kind = "OverallWidth" },
            };
            foreach (var slot in named)
            {
                bool has = cap.Dimensions.Any(d => ViewMatchName(d.ViewName, slot.Name));
                if (has) continue;
                cap.Dimensions.Add(new CivilDimensionRow
                {
                    PieceMark = pieceMark,
                    Kind = slot.Kind,
                    Text = slot.Val.ToString("0.#", CultureInfo.InvariantCulture),
                    Value = slot.Val,
                    ViewName = slot.Name,
                });
            }
        }

        private static bool ViewMatchName(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            return actual.Replace(" ", "").Equals(expected.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        public static string JointType(IList<CivilHardwareRow> hardware, TSModel.Part main)
        {
            string blob = string.Join(" ", (hardware ?? new List<CivilHardwareRow>()).Select(h => h.Kind + " " + h.Description)).ToUpperInvariant();
            string name = (Safe(() => main.Name) ?? "").ToUpperInvariant();
            if (ContainsAny(blob + " " + name, "FOOT", "FOUND", "PILE", "PEDESTAL")) return "piece-to-foundation";
            if ((hardware ?? new List<CivilHardwareRow>()).Any(h => h.Kind == "BOLT" || h.Kind == "ANCHOR" || h.Kind == "WELD"))
                return "piece-to-piece";
            if ((hardware ?? new List<CivilHardwareRow>()).Count > 0) return "piece-to-piece";
            return "";
        }

        public static string FileStem(string project, string pieceMark, string drawingType)
        {
            return Sanitize(project) + "_" + Sanitize(pieceMark) + "_" + Sanitize(drawingType);
        }

        public static void WriteJson(object doc, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonConvert.SerializeObject(doc, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include }), Encoding.UTF8);
        }

        public static void AppendCsv(string path, string header, IEnumerable<string> rows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            bool exists = File.Exists(path);
            using (var sw = new StreamWriter(path, true, Encoding.UTF8))
            {
                if (!exists) sw.WriteLine(header);
                foreach (var row in rows) sw.WriteLine(row);
            }
        }

        public static string Csv(params object[] cells)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(CsvCell(cells[i]));
            }
            return sb.ToString();
        }

        public static string LogPath(string outputRoot)
        {
            return Path.Combine(outputRoot, "extract_errors.log");
        }

        public static void LogSkip(string outputRoot, string objectType, string reason)
        {
            try
            {
                Directory.CreateDirectory(outputRoot);
                File.AppendAllText(LogPath(outputRoot),
                    DateTime.Now.ToString("s") + " SKIP [" + objectType + "] " + reason + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
            Console.WriteLine("[Civil] SKIP " + objectType + ": " + reason);
        }

        public static void LogError(string outputRoot, string context, Exception ex)
        {
            string type = ex != null ? ex.GetType().FullName : "Exception";
            string msg = ex != null ? (ex.Message ?? "") : "";
            string stack = ex != null ? (ex.StackTrace ?? "") : "";
            if (string.IsNullOrEmpty(stack))
                stack = Environment.StackTrace;
            try
            {
                Directory.CreateDirectory(outputRoot);
                File.AppendAllText(LogPath(outputRoot),
                    DateTime.Now.ToString("s") + " FAIL [" + context + "] " + type + ": " + msg +
                    Environment.NewLine + stack + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
            Console.WriteLine("[Civil] FAIL " + context + ": " + type + ": " + msg);
            if (!string.IsNullOrEmpty(stack))
                Console.WriteLine(stack);
        }

        public static string Report(TSModel.ModelObject obj, string name)
        {
            if (obj == null) return "";
            string v = "";
            try { obj.GetReportProperty(name, ref v); } catch { }
            return v ?? "";
        }

        public static double ReportDouble(TSModel.ModelObject obj, string name)
        {
            double v = 0;
            try { if (obj.GetReportProperty(name, ref v)) return v; } catch { }
            double.TryParse(Report(obj, name), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
            return v;
        }

        public static string Uda(TSModel.ModelObject obj, string name)
        {
            if (obj == null) return "";
            string v = "";
            try { if (obj.GetUserProperty(name, ref v) && !string.IsNullOrWhiteSpace(v)) return v.Trim(); } catch { }
            return Report(obj, name);
        }

        public static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        public static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        public static int Safe(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }

        public static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "UNKNOWN";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ' || c == '/' || c == '\\' || c == '[' || c == ']' || c == '.') sb.Append('_');
            }
            string t = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(t) ? "UNKNOWN" : t;
        }

        public static string StripBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("[", "").Replace("]", "").Trim();
        }

        public static CivilXyz Xyz(Point p)
        {
            if (p == null) return new CivilXyz();
            return new CivilXyz { X = Round(p.X, 3), Y = Round(p.Y, 3), Z = Round(p.Z, 3) };
        }

        public static CivilXyz Vec(Vector v)
        {
            if (v == null) return new CivilXyz();
            return new CivilXyz { X = Round(v.X, 3), Y = Round(v.Y, 3), Z = Round(v.Z, 3) };
        }

        public static double Round(double v, int d)
        {
            return Math.Round(v, d);
        }

        private static void AddPart(TSModel.Part part, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (part == null) return;
            int id;
            try { id = part.Identifier.ID; if (!part.Identifier.IsValid()) return; }
            catch { return; }
            if (id != 0 && !seen.Add(id)) return;
            parts.Add(part);
        }

        private static void AddBar(CivilBbsBar rec, List<CivilBbsBar> list, HashSet<int> seen)
        {
            if (rec == null) return;
            int id = SafeIdFromGuid(rec.Guid);
            if (id != 0 && !seen.Add(id)) return;
            list.Add(rec);
        }

        private static int SafeIdFromGuid(string guid)
        {
            return string.IsNullOrEmpty(guid) ? 0 : guid.GetHashCode();
        }

        private static void FillGeometry(TSModel.Reinforcement reinf, CivilBbsBar rec)
        {
            try
            {
                ArrayList geoms = reinf.GetRebarGeometries(true);
                if (geoms == null || geoms.Count == 0) return;
                var geom = geoms[0] as TSModel.RebarGeometry;
                if (geom?.Shape?.Points == null) return;
                Point prev = null;
                var legs = new List<double>();
                foreach (Point p in geom.Shape.Points)
                {
                    rec.Geometry.Add(Xyz(p));
                    if (prev != null)
                    {
                        double len = Distance(prev, p);
                        if (len > 1) legs.Add(Round(len, 1));
                    }
                    prev = p;
                }
                ApplyLegs(rec, legs);
            }
            catch { }
        }

        private static void ApplyLegs(CivilBbsBar rec, List<double> legs)
        {
            if (rec.A == null && legs.Count > 0) rec.A = legs[0];
            if (rec.B == null && legs.Count > 1) rec.B = legs[1];
            if (rec.C == null && legs.Count > 2) rec.C = legs[2];
            if (rec.D == null && legs.Count > 3) rec.D = legs[3];
            if (rec.E == null && legs.Count > 4) rec.E = legs[4];
            if (rec.F == null && legs.Count > 5) rec.F = legs[5];
            if (rec.G == null && legs.Count > 6) rec.G = legs[6];
            if (rec.H == null && legs.Count > 7) rec.H = legs[7];
        }

        private static string InferShape(CivilBbsBar rec)
        {
            int legs = rec.Geometry.Count > 1 ? rec.Geometry.Count - 1 : 0;
            string code = (rec.ShapeCode ?? "").ToUpperInvariant();
            if (code.Contains("STIR") || code.Contains("LINK") || legs >= 4) return "stirrup";
            if (code.Contains("BENT") || legs >= 2) return "bent";
            return "straight";
        }

        private static string HardwareKind(string name, string cls, string profile, string mark = "")
        {
            string blob = ((name ?? "") + " " + (profile ?? "") + " " + (mark ?? "")).ToUpperInvariant();
            if (ContainsAny(blob, "ANCHOR")) return "ANCHOR";
            if (ContainsAny(blob, "GROUT", "GT75", "GT100") || Regex.IsMatch(blob, @"\bGT\d")) return "GROUT_TUBE";
            if (ContainsAny(blob, "SLEEVE") || blob.Contains("SLV-")) return "SLEEVE";
            if (ContainsAny(blob, "INSERT", "WILLIAMS", "SP15", "SPLICE")) return "INSERT";
            if (ContainsAny(blob, "CONDUIT") || blob.Contains("CNDT")) return "CONDUIT";
            if (ContainsAny(blob, "PLATE") || blob.Contains("EB-S-PL") || blob.Contains("P-705")) return "PLATE";
            if (ContainsAny(blob, "EMBED")) return "EMBED";
            if (cls == "104" || cls == "100" || cls == "101" || cls == "102") return "EMBED";
            return "PART";
        }

        private static bool LooksLikeHardwarePart(TSModel.Part part)
        {
            string mark = FirstNonEmpty(Report(part, "PART_POS"), Safe(() => part.Name));
            string kind = HardwareKind(Safe(() => part.Name), Safe(() => part.Class),
                Safe(() => part.Profile != null ? part.Profile.ProfileString : ""), mark);
            if (kind != "PART") return true;
            string u = (mark ?? "").ToUpperInvariant();
            return u.StartsWith("P-") || u.StartsWith("GT") || u.StartsWith("SLV") ||
                   u.StartsWith("SP") || u.StartsWith("CNDT") || u.StartsWith("EB-");
        }

        private static string PsiToMpa(string material)
        {
            if (!LooksNumericPsi(material)) return "";
            double psi;
            if (!double.TryParse(material, NumberStyles.Any, CultureInfo.InvariantCulture, out psi)) return "";
            return Round(psi / 145.03773773, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static bool LooksNumericPsi(string s)
        {
            double v;
            return !string.IsNullOrWhiteSpace(s) &&
                   double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v) &&
                   v >= 1000 && v <= 20000;
        }

        private static void CaptureViewObjects(ViewBase container, string viewName, string pieceMark, CivilSheetCapture cap, string outputRoot, int[] slots)
        {
            if (container == null) return;
            DrawingObjectEnumerator objs = null;
            try { objs = container.GetAllObjects(); }
            catch
            {
                try { objs = container.GetObjects(); }
                catch (Exception ex)
                {
                    LogSkip(outputRoot, "ViewObjects", viewName + ": " + ex.Message);
                    return;
                }
            }
            while (objs != null && objs.MoveNext())
            {
                var obj = objs.Current;
                if (obj == null) continue;
                string vn = viewName;
                if (string.Equals(viewName, "Sheet", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var pv = obj.GetView() as View;
                        if (pv != null)
                            vn = MapShopViewName(pv.Name ?? "", pv.ViewType.ToString(), slots);
                    }
                    catch { }
                }
                try
                {
                    if (obj is ViewBase) continue;
                    if (obj is StraightDimensionSet set)
                    {
                        var kids = set.GetObjects();
                        int n = 0;
                        while (kids != null && kids.MoveNext())
                        {
                            var dim = kids.Current as StraightDimension;
                            if (dim == null) continue;
                            cap.Dimensions.Add(DimFrom(pieceMark, dim, vn));
                            n++;
                        }
                        if (n == 0)
                            LogSkip(outputRoot, "StraightDimensionSet", vn + " had no StraightDimension children");
                    }
                    else if (obj is StraightDimension sdim)
                    {
                        cap.Dimensions.Add(DimFrom(pieceMark, sdim, vn));
                    }
                    else if (obj is TSDrawing.Text text)
                    {
                        string t = text.TextString ?? "";
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            cap.Texts.Add(t);
                            if (LooksDrawnBy(t) && string.IsNullOrWhiteSpace(cap.DrawnBy)) cap.DrawnBy = t;
                            cap.Features.AddRange(FeaturesFromTexts(new[] { t }, vn));
                        }
                    }
                    else if (obj is MarkBase mark)
                    {
                        string t = FlattenRelated(mark);
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            cap.Texts.Add(t);
                            cap.Features.AddRange(FeaturesFromTexts(new[] { t }, vn));
                        }
                    }
                    else if (obj is TSDrawing.Part dpart)
                    {
                        try
                        {
                            var mid = dpart.ModelIdentifier;
                            if (mid != null && mid.ID != 0)
                                cap.SheetModelObjects.Add(new IdentifierRef { Id = mid.ID, Guid = mid.GUID.ToString() });
                        }
                        catch { }
                    }
                    else if (obj is ReinforcementBase rebar)
                    {
                        try
                        {
                            var mid = rebar.ModelIdentifier;
                            if (mid != null && mid.ID != 0)
                                cap.SheetModelObjects.Add(new IdentifierRef { Id = mid.ID, Guid = mid.GUID.ToString() });
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    LogSkip(outputRoot, obj.GetType().Name, viewName + ": " + ex.Message);
                }
            }
        }

        private static IEnumerable<CivilProfileFeature> FeaturesFromTexts(IEnumerable<string> texts, string viewName)
        {
            var list = new List<CivilProfileFeature>();
            if (texts == null) return list;
            foreach (var t in texts)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                string u = t.ToUpperInvariant();
                if (u.Contains("CHAMFER")) list.Add(new CivilProfileFeature { Kind = "chamfer", Source = "drawing-text", ViewName = viewName, Text = t });
                if (u.Contains("RECESS") || u.Contains("REVEAL")) list.Add(new CivilProfileFeature { Kind = "recess", Source = "drawing-text", ViewName = viewName, Text = t });
                if (u.Contains("NOTCH")) list.Add(new CivilProfileFeature { Kind = "notch", Source = "drawing-text", ViewName = viewName, Text = t });
                if (u.Contains("CORBEL") || u.Contains("HAUNCH")) list.Add(new CivilProfileFeature { Kind = "corbel", Source = "drawing-text", ViewName = viewName, Text = t });
            }
            return list;
        }

        private static bool LooksDrawnBy(string t)
        {
            string u = (t ?? "").ToUpperInvariant();
            return u.Contains("DRAWN") || u.StartsWith("BY ") || u.Contains("DRAFTED");
        }

        private static string FlattenRelated(DrawingObject obj)
        {
            try
            {
                var en = obj.GetRelatedObjects();
                var sb = new StringBuilder();
                while (en != null && en.MoveNext())
                {
                    if (en.Current is TSDrawing.Text t) sb.Append(t.TextString);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static CivilXyz Origin(TSModel.Part part)
        {
            try { return Xyz(part.GetCoordinateSystem().Origin); }
            catch { return new CivilXyz(); }
        }

        private static void Ends(TSModel.Part part, out Point start, out Point end)
        {
            start = new Point();
            end = new Point();
            try
            {
                if (part is TSModel.Beam beam) { start = beam.StartPoint; end = beam.EndPoint; return; }
            }
            catch { }
            try
            {
                if (part is TSModel.PolyBeam poly && poly.Contour?.ContourPoints != null && poly.Contour.ContourPoints.Count >= 2)
                {
                    start = poly.Contour.ContourPoints[0] as Point ?? start;
                    end = poly.Contour.ContourPoints[poly.Contour.ContourPoints.Count - 1] as Point ?? end;
                    return;
                }
            }
            catch { }
            try { start = end = part.GetCoordinateSystem().Origin; } catch { }
        }

        private static string ReadFloor(TSModel.Part part)
        {
            foreach (var name in FloorUdaNames)
            {
                string v = Uda(part, name);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return Report(part, "FLOOR");
        }

        private static string InferGrid(CivilXyz p)
        {
            if (p == null) return "";
            return "X" + Math.Round(p.X) + "/Y" + Math.Round(p.Y);
        }

        private static bool LooksConcrete(string material)
        {
            string u = (material ?? "").ToUpperInvariant();
            return u.Contains("CONCRETE") || u.Contains("C20") || u.Contains("C25") || u.Contains("C30")
                || u.Contains("C35") || u.Contains("C40") || u.Contains("C45") || u.Contains("C50")
                || (material != null && material.Length >= 2 && (material[0] == 'C' || material[0] == 'c') && char.IsDigit(material[1]));
        }

        private static string ExtractMpa(string grade)
        {
            if (string.IsNullOrWhiteSpace(grade)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(grade, @"(\d+)\s*MPa", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = System.Text.RegularExpressions.Regex.Match(grade, @"C\s*(\d+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static double? ParseDia(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return null;
            string t = size.ToUpperInvariant().Replace("M", "").Replace("Ø", "").Replace("MM", "").Trim();
            double v;
            if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v > 0)
                return v;
            return null;
        }

        private static double? Positive(double v)
        {
            return v > 0.0001 ? (double?)Round(v, 1) : null;
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static CivilDimensionRow DimFrom(string piece, StraightDimension dim, string view)
        {
            double measured = 0;
            try
            {
                measured = Math.Sqrt(
                    Math.Pow(dim.EndPoint.X - dim.StartPoint.X, 2) +
                    Math.Pow(dim.EndPoint.Y - dim.StartPoint.Y, 2) +
                    Math.Pow(dim.EndPoint.Z - dim.StartPoint.Z, 2));
            }
            catch { }
            string text = Convert.ToString(dim.Value, CultureInfo.InvariantCulture) ?? "";
            return Dim(piece, "StraightDimension", text, Round(measured, 1), dim.StartPoint, dim.EndPoint, view);
        }

        private static CivilDimensionRow Dim(string piece, string kind, string text, double value, Point a, Point b, string view)
        {
            return new CivilDimensionRow
            {
                PieceMark = piece,
                Kind = kind,
                Text = text,
                Value = value,
                StartPoint = Xyz(a),
                EndPoint = Xyz(b),
                ViewName = view,
            };
        }

        private static string CsvCell(object v)
        {
            if (v == null) return "";
            string s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static bool ContainsAny(string hay, params string[] needles)
        {
            foreach (var n in needles)
                if (hay.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static double[] ParseCoords(string raw)
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(raw)) return list.ToArray();
            foreach (var token in raw.Split(new[] { ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (double.TryParse(token.Replace("mm", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                    list.Add(v);
            }
            return list.ToArray();
        }

        private static string[] ParseLabels(string raw, int count)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new string[0];
            var parts = raw.Split(new[] { ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= count) return parts;
            return parts;
        }
    }
}

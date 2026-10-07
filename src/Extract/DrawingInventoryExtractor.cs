// DrawingInventoryExtractor.cs
// Read-only: every drawing type + every model part → JSON/CSV.
// Tekla Structures 2026 Open API (DrawingHandler + Model).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public class InventoryPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class InventoryGeometry
    {
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public double DeltaZ { get; set; }
        public double Length { get; set; }
    }

    public class DrawingInventoryRecord
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string Type { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Class { get; set; } = "";
        public string Finish { get; set; } = "";
        public InventoryPoint StartPoint { get; set; } = new InventoryPoint();
        public InventoryPoint EndPoint { get; set; } = new InventoryPoint();
        public InventoryGeometry Geometry { get; set; } = new InventoryGeometry();
        public string DrawingType { get; set; } = "";
        public string DrawingName { get; set; } = "";
        public string DrawingMark { get; set; } = "";
        public string Category { get; set; } = "";
        public string Floor { get; set; } = "";
        public bool HasDrawing { get; set; }
    }

    public class DrawingTypeRecord
    {
        public string DrawingType { get; set; } = "";
        public string DrawingName { get; set; } = "";
        public string DrawingMark { get; set; } = "";
        public string Category { get; set; } = "";
        public int ObjectCount { get; set; }
    }

    public class DrawingInventoryExtractor
    {
        private static readonly string[] FloorUdaNames =
        {
            "FLOOR_LEVEL", "FLOOR", "FLOOR_NAME", "STOREY", "STORY", "LEVEL", "FLOORLEVEL"
        };

        private static readonly string[] SteelProfileHints =
        {
            "IPE", "HEA", "HEB", "HEM", "UB", "UC", "PFC", "UBP", "SHS", "RHS", "CHS",
            "UNP", "UPE", "UPN", "L50", "L60", "L70", "L80", "L90", "L100", "PL", "FL", "WELDED"
        };

        private static readonly Type[] PartTypes =
        {
            typeof(TSModel.Beam),
            typeof(TSModel.ContourPlate),
            typeof(TSModel.PolyBeam),
        };

        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public DrawingInventoryExtractor(TSModel.Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
        }

        public List<DrawingInventoryRecord> Run(string baseDir)
        {
            var records = new List<DrawingInventoryRecord>();
            var typeRows = new List<DrawingTypeRecord>();

            try
            {
                if (_model == null || !_model.GetConnectionStatus())
                {
                    Console.WriteLine("[Inventory] Model is not connected. Open Tekla with a model.");
                    return records;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] Model connection check failed: " + ex.Message);
                return records;
            }

            try
            {
                if (_handler == null || !_handler.GetConnectionStatus())
                {
                    Console.WriteLine("[Inventory] DrawingHandler is not connected.");
                    return records;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] DrawingHandler check failed: " + ex.Message);
                return records;
            }

            var onDrawing = new HashSet<int>();
            int drawingCount = ExtractFromDrawings(records, typeRows, onDrawing);
            Console.WriteLine("[Inventory] " + drawingCount + " drawings, " + records.Count +
                              " linked objects. Scanning remaining model parts...");

            ExtractRemainingModelParts(records, onDrawing);

            WriteOutputs(records, typeRows, baseDir);
            PrintSummary(records, typeRows, drawingCount);
            return records;
        }

        private int ExtractFromDrawings(
            List<DrawingInventoryRecord> records,
            List<DrawingTypeRecord> typeRows,
            HashSet<int> onDrawing)
        {
            System.Collections.IEnumerator en = null;
            try { en = _handler.GetDrawings(); }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] GetDrawings failed: " + ex.Message);
                return 0;
            }

            int drawingCount = 0;
            while (true)
            {
                bool more;
                try { more = en != null && en.MoveNext(); }
                catch (Exception ex)
                {
                    Console.WriteLine("[Inventory] enumerator MoveNext failed: " + ex.Message);
                    break;
                }
                if (!more) break;

                Drawing drawing = null;
                try { drawing = en.Current as Drawing; }
                catch (Exception ex)
                {
                    Console.WriteLine("[Inventory] skipped drawing Current: " + ex.Message);
                    continue;
                }
                if (drawing == null) continue;

                drawingCount++;
                string drawingType = Classify(drawing);
                string drawingName = "";
                string drawingMark = "";
                try { drawingName = drawing.Name ?? ""; } catch { /* optional */ }
                try { drawingMark = drawing.Mark ?? ""; } catch { /* optional */ }

                int before = records.Count;
                try
                {
                    var seenPart = new HashSet<int>();
                    foreach (var part in CollectParts(drawing))
                    {
                        int pid = 0;
                        try { pid = part.Identifier.ID; } catch { continue; }
                        if (pid != 0 && !seenPart.Add(pid)) continue;

                        try
                        {
                            var rec = ReadPart(part, drawingType, drawingName, drawingMark, hasDrawing: true);
                            if (rec == null) continue;
                            records.Add(rec);
                            onDrawing.Add(pid);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[Inventory] skipped object " + pid + " on '" + drawingMark + "': " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Inventory] skipped drawing '" + SafeMark(drawing) + "': " + ex.Message);
                }

                int objectCount = records.Count - before;
                if (objectCount == 0)
                {
                    records.Add(EmptyDrawingRow(drawingType, drawingName, drawingMark));
                    objectCount = 1;
                }

                string category = records.Skip(before).Select(r => r.Category).FirstOrDefault(c => !string.IsNullOrEmpty(c))
                    ?? InferCategoryFromDrawingType(drawingType);
                typeRows.Add(new DrawingTypeRecord
                {
                    DrawingType = drawingType,
                    DrawingName = drawingName,
                    DrawingMark = drawingMark,
                    Category = category,
                    ObjectCount = objectCount,
                });

                Console.WriteLine("[Inventory] " + drawingType + " '" + drawingMark + "' objects=" + objectCount);
            }

            return drawingCount;
        }

        private void ExtractRemainingModelParts(List<DrawingInventoryRecord> records, HashSet<int> onDrawing)
        {
            System.Collections.IEnumerator en = null;
            try { en = _model.GetModelObjectSelector().GetAllObjectsWithType(PartTypes); }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] GetAllObjectsWithType failed: " + ex.Message);
                return;
            }

            int n = 0, added = 0;
            while (en != null)
            {
                bool more;
                try { more = en.MoveNext(); }
                catch (Exception ex)
                {
                    Console.WriteLine("[Inventory] model enumerator failed: " + ex.Message);
                    break;
                }
                if (!more) break;

                n++;
                if (!(en.Current is TSModel.Part part)) continue;

                int pid = 0;
                try { pid = part.Identifier.ID; } catch { continue; }
                if (pid != 0 && onDrawing.Contains(pid)) continue;

                try
                {
                    TSModel.Assembly assembly = null;
                    try { assembly = part.GetAssembly(); } catch { /* optional */ }
                    string drawingType = InferDrawingType(assembly, part);
                    var rec = ReadPart(part, drawingType, "", "", hasDrawing: false);
                    if (rec == null) continue;
                    records.Add(rec);
                    added++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Inventory] skipped model part " + pid + ": " + ex.Message);
                }

                if (n % 5000 == 0)
                    Console.WriteLine("[Inventory] scanned " + n + " model parts, extra rows=" + added);
            }

            Console.WriteLine("[Inventory] model scan done: " + n + " parts, " + added + " without a drawing sheet.");
        }

        private List<TSModel.Part> CollectParts(Drawing drawing)
        {
            var parts = new List<TSModel.Part>();
            var seen = new HashSet<int>();

            foreach (var id in CollectIdentifiers(drawing))
                ExpandIdentifier(id, parts, seen);

            return parts;
        }

        private IEnumerable<Identifier> CollectIdentifiers(Drawing drawing)
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
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] type identifier: " + ex.Message);
            }

            try
            {
                var fromApi = _handler.GetModelObjectIdentifiers(drawing);
                if (fromApi != null)
                {
                    foreach (var id in fromApi)
                    {
                        if (id != null) list.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] GetModelObjectIdentifiers '" + SafeMark(drawing) + "': " + ex.Message);
            }

            return list;
        }

        private void ExpandIdentifier(Identifier id, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (id == null) return;
            TSModel.ModelObject mo = null;
            try { mo = _model.SelectModelObject(id); }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] SelectModelObject " + SafeId(id) + ": " + ex.Message);
                return;
            }
            if (mo == null) return;
            ExpandModelObject(mo, parts, seen);
        }

        private void ExpandModelObject(TSModel.ModelObject mo, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (mo == null) return;

            if (mo is TSModel.Part part)
            {
                AddPart(part, parts, seen);
                return;
            }

            var assembly = mo as TSModel.Assembly;
            if (assembly == null) return;

            try { assembly.Select(); } catch { /* best effort */ }

            try { AddPart(assembly.GetMainPart() as TSModel.Part, parts, seen); }
            catch (Exception ex) { Console.WriteLine("[Inventory] GetMainPart: " + ex.Message); }

            try
            {
                ArrayList seconds = assembly.GetSecondaries();
                if (seconds != null)
                {
                    foreach (var obj in seconds)
                    {
                        if (obj is TSModel.Part p) AddPart(p, parts, seen);
                        else if (obj is TSModel.Assembly nested) ExpandModelObject(nested, parts, seen);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[Inventory] GetSecondaries: " + ex.Message); }

            try
            {
                ArrayList subs = assembly.GetSubAssemblies();
                if (subs != null)
                {
                    foreach (var obj in subs)
                        ExpandModelObject(obj as TSModel.ModelObject, parts, seen);
                }
            }
            catch { /* optional on some assembly types */ }
        }

        private static void AddPart(TSModel.Part part, List<TSModel.Part> parts, HashSet<int> seen)
        {
            if (part == null) return;
            int pid = 0;
            try
            {
                if (!part.Identifier.IsValid()) return;
                pid = part.Identifier.ID;
            }
            catch { return; }
            if (pid != 0 && !seen.Add(pid)) return;
            parts.Add(part);
        }

        private DrawingInventoryRecord ReadPart(
            TSModel.Part part, string drawingType, string drawingName, string drawingMark, bool hasDrawing)
        {
            if (part == null) return null;

            TSModel.Assembly assembly = null;
            try { assembly = part.GetAssembly(); } catch { /* optional */ }

            var rec = new DrawingInventoryRecord
            {
                DrawingType = drawingType,
                DrawingName = drawingName,
                DrawingMark = drawingMark,
                HasDrawing = hasDrawing,
            };

            try { rec.Id = part.Identifier.ID; } catch { /* optional */ }
            try { rec.Guid = part.Identifier.GUID.ToString(); } catch { /* optional */ }
            try { rec.Name = FirstNonEmpty(part.Name, Report(part, "NAME")); } catch { rec.Name = Report(part, "NAME"); }
            try { rec.Profile = FirstNonEmpty(part.Profile != null ? part.Profile.ProfileString : "", Report(part, "PROFILE")); }
            catch { rec.Profile = Report(part, "PROFILE"); }
            try { rec.Material = FirstNonEmpty(part.Material != null ? part.Material.MaterialString : "", Report(part, "MATERIAL")); }
            catch { rec.Material = Report(part, "MATERIAL"); }
            try { rec.Class = part.Class ?? ""; } catch { rec.Class = ""; }
            try { rec.Finish = part.Finish ?? ""; } catch { rec.Finish = ""; }

            Point start = null, end = null;
            ReadEnds(part, out start, out end);
            if (start == null) start = new Point();
            if (end == null) end = new Point();

            double dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.01)
            {
                double reported = ReportDouble(part, "LENGTH");
                if (reported > 0) len = reported;
            }

            rec.StartPoint = new InventoryPoint { X = start.X, Y = start.Y, Z = start.Z };
            rec.EndPoint = new InventoryPoint { X = end.X, Y = end.Y, Z = end.Z };
            rec.Direction = InferDirection(dx, dy, dz);
            rec.Type = InferMemberType(part, rec.Direction, rec.Class, rec.Name, rec.Profile);
            rec.Geometry = new InventoryGeometry
            {
                DeltaX = dx,
                DeltaY = dy,
                DeltaZ = dz,
                Length = Math.Round(len, 1),
            };
            rec.Floor = ReadFloor(part, assembly);
            rec.Category = ClassifyCategory(drawingType, assembly, rec.Material, rec.Profile, rec.Class);
            return rec;
        }

        private static void ReadEnds(TSModel.Part part, out Point start, out Point end)
        {
            start = null;
            end = null;
            try
            {
                if (part is TSModel.Beam beam)
                {
                    start = beam.StartPoint;
                    end = beam.EndPoint;
                    return;
                }
            }
            catch { /* contour fallback */ }

            try
            {
                if (part is TSModel.PolyBeam poly)
                {
                    var pts = poly.Contour != null ? poly.Contour.ContourPoints : null;
                    if (pts != null && pts.Count >= 2)
                    {
                        start = pts[0] as Point;
                        end = pts[pts.Count - 1] as Point;
                        return;
                    }
                }
            }
            catch { /* contour plate fallback */ }

            try
            {
                if (part is TSModel.ContourPlate plate)
                {
                    var pts = plate.Contour != null ? plate.Contour.ContourPoints : null;
                    if (pts != null && pts.Count > 0)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                        foreach (var raw in pts)
                        {
                            var p = raw as Point;
                            if (p == null) continue;
                            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
                        }
                        if (minX < double.MaxValue)
                        {
                            start = new Point(minX, minY, minZ);
                            end = new Point(maxX, maxY, maxZ);
                            return;
                        }
                    }
                }
            }
            catch { /* CS origin */ }

            try
            {
                var o = part.GetCoordinateSystem().Origin;
                start = o;
                end = o;
            }
            catch { /* leave null */ }
        }

        private static string Classify(Drawing drawing)
        {
            try
            {
                if (drawing is SinglePartDrawing) return "SINGLE_PART";
                if (drawing is AssemblyDrawing) return "ASSEMBLY";
                if (drawing is CastUnitDrawing) return "CAST_UNIT";
                if (drawing is GADrawing) return "GA";
                if (drawing is MultiDrawing) return "MULTIDRAWING";
            }
            catch { /* fall through */ }
            try { return drawing.GetType().Name; }
            catch { return "UNKNOWN"; }
        }

        private static string InferDrawingType(TSModel.Assembly assembly, TSModel.Part part)
        {
            try
            {
                if (assembly != null)
                {
                    var t = assembly.GetAssemblyType();
                    if (t == TSModel.Assembly.AssemblyTypeEnum.PRECAST_ASSEMBLY) return "CAST_UNIT";
                    if (t == TSModel.Assembly.AssemblyTypeEnum.STEEL_ASSEMBLY) return "ASSEMBLY";
                    if (t == TSModel.Assembly.AssemblyTypeEnum.TIMBER_ASSEMBLY) return "ASSEMBLY";
                }
            }
            catch { /* heuristic */ }

            string material = "";
            string profile = "";
            try { material = part.Material != null ? part.Material.MaterialString ?? "" : ""; } catch { }
            try { profile = part.Profile != null ? part.Profile.ProfileString ?? "" : ""; } catch { }
            string up = (material ?? "").ToUpperInvariant();
            if (LooksConcrete(material, up)) return "GA";
            if (LooksSteel(up, (profile ?? "").ToUpperInvariant(), "")) return "ASSEMBLY";
            return "GA";
        }

        private static string ClassifyCategory(
            string drawingType, TSModel.Assembly assembly, string material, string profile, string cls)
        {
            try
            {
                if (assembly != null)
                {
                    var t = assembly.GetAssemblyType();
                    if (t == TSModel.Assembly.AssemblyTypeEnum.PRECAST_ASSEMBLY) return "Precast";
                    if (t == TSModel.Assembly.AssemblyTypeEnum.STEEL_ASSEMBLY) return "Fabrication";
                    if (t == TSModel.Assembly.AssemblyTypeEnum.TIMBER_ASSEMBLY) return "Fabrication";
                }
            }
            catch { /* heuristic */ }

            string mat = (material ?? "").Trim();
            string up = mat.ToUpperInvariant();
            string prf = (profile ?? "").ToUpperInvariant();
            bool concrete = LooksConcrete(mat, up);
            bool steel = LooksSteel(up, prf, cls);

            if (drawingType == "CAST_UNIT" && concrete) return "Precast";
            if (concrete && !steel) return drawingType == "CAST_UNIT" ? "Precast" : "RCC";
            if (steel) return "Fabrication";
            if (drawingType == "CAST_UNIT") return "Precast";
            if (drawingType == "ASSEMBLY" || drawingType == "SINGLE_PART") return "Fabrication";
            return concrete ? "RCC" : "Fabrication";
        }

        private static string InferCategoryFromDrawingType(string drawingType)
        {
            if (drawingType == "CAST_UNIT") return "Precast";
            if (drawingType == "GA") return "RCC";
            return "Fabrication";
        }

        private static bool LooksConcrete(string mat, string up)
        {
            if (string.IsNullOrWhiteSpace(mat)) return false;
            if (up.Contains("CONCRETE") || up.Contains("RCC") || up.StartsWith("C20") || up.StartsWith("C25")
                || up.StartsWith("C30") || up.StartsWith("C35") || up.StartsWith("C40") || up.StartsWith("C45")
                || up.StartsWith("C50"))
                return true;
            if (mat.Length >= 2 && (mat[0] == 'C' || mat[0] == 'c') && char.IsDigit(mat[1]))
                return true;
            return false;
        }

        private static bool LooksSteel(string materialUp, string profileUp, string cls)
        {
            if (materialUp.StartsWith("S23") || materialUp.StartsWith("S27") || materialUp.StartsWith("S35")
                || materialUp.StartsWith("S42") || materialUp.Contains("STEEL") || materialUp.StartsWith("FE"))
                return true;
            foreach (var h in SteelProfileHints)
            {
                if (profileUp.StartsWith(h) || profileUp.Contains(h)) return true;
            }
            return false;
        }

        private static string InferDirection(double dx, double dy, double dz)
        {
            double adx = Math.Abs(dx), ady = Math.Abs(dy), adz = Math.Abs(dz);
            if (adz > adx * 1.2 && adz > ady * 1.2) return "VERTICAL";
            if (adz < adx * 0.35 && adz < ady * 0.35) return "HORIZONTAL";
            if (adz > 80 && (adx > 80 || ady > 80)) return "DIAGONAL";
            return adz >= adx && adz >= ady ? "VERTICAL" : "HORIZONTAL";
        }

        private static string InferMemberType(
            TSModel.Part part, string dir, string cls, string name, string profile)
        {
            try
            {
                if (part is TSModel.ContourPlate) return "CONTOURPLATE";
                if (part is TSModel.PolyBeam) return "POLYBEAM";
            }
            catch { /* name/class rules */ }

            var c = (cls ?? "").Trim();
            if (c == "6") return "CONNECTION";
            if (c == "5") return "SLAB";
            if (c == "3") return "SECONDARY";
            if (c == "2") return "BEAM";
            if (c == "1") return "COLUMN";

            var n = (name ?? "").ToUpperInvariant();
            if (n.Contains("COL")) return "COLUMN";
            if (n.Contains("WALL")) return "WALL";
            if (n.Contains("SLAB") || n.Contains("DECK")) return "SLAB";
            if (n.Contains("FOOT") || n.Contains("FOUND")) return "FOUNDATION";
            if (dir == "VERTICAL") return "COLUMN";
            if (part is TSModel.Beam) return "BEAM";
            try { return part.GetType().Name.ToUpperInvariant(); }
            catch { return "BEAM"; }
        }

        private static string ReadFloor(TSModel.Part part, TSModel.Assembly assembly)
        {
            string floor = ReadFloorUda(part);
            if (string.IsNullOrWhiteSpace(floor)) floor = ReadFloorUda(assembly);
            if (string.IsNullOrWhiteSpace(floor)) floor = Report(part, "FLOOR");
            return floor ?? "";
        }

        private static string ReadFloorUda(TSModel.ModelObject obj)
        {
            if (obj == null) return "";
            foreach (var name in FloorUdaNames)
            {
                string v = "";
                try
                {
                    if (obj.GetUserProperty(name, ref v) && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                catch { /* report */ }
                try
                {
                    if (obj.GetReportProperty(name, ref v) && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                catch { /* next */ }
            }
            return "";
        }

        private static DrawingInventoryRecord EmptyDrawingRow(string drawingType, string name, string mark)
        {
            return new DrawingInventoryRecord
            {
                DrawingType = drawingType,
                DrawingName = name,
                DrawingMark = mark,
                HasDrawing = true,
                Category = InferCategoryFromDrawingType(drawingType),
            };
        }

        private static void WriteOutputs(
            List<DrawingInventoryRecord> records,
            List<DrawingTypeRecord> typeRows,
            string baseDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseDir))
                    baseDir = Directory.GetCurrentDirectory();
                Directory.CreateDirectory(baseDir);

                string jsonPath = Path.Combine(baseDir, "drawings_inventory.json");
                string csvPath = Path.Combine(baseDir, "drawings_inventory.csv");
                string typesJson = Path.Combine(baseDir, "drawings_types.json");
                string typesCsv = Path.Combine(baseDir, "drawings_types.csv");

                File.WriteAllText(jsonPath,
                    JsonConvert.SerializeObject(records, Formatting.Indented),
                    Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine("DrawingType,Category,HasDrawing,Guid,Id,Name,Type,Profile,Material,Class,Floor,DrawingName,DrawingMark,StartX,StartY,StartZ,EndX,EndY,EndZ,Length");
                foreach (var r in records)
                {
                    var sp = r.StartPoint ?? new InventoryPoint();
                    var ep = r.EndPoint ?? new InventoryPoint();
                    var geo = r.Geometry ?? new InventoryGeometry();
                    sb.AppendLine(string.Join(",",
                        Csv(r.DrawingType), Csv(r.Category), r.HasDrawing ? "true" : "false",
                        Csv(r.Guid), r.Id, Csv(r.Name), Csv(r.Type),
                        Csv(r.Profile), Csv(r.Material), Csv(r.Class), Csv(r.Floor),
                        Csv(r.DrawingName), Csv(r.DrawingMark),
                        N(sp.X), N(sp.Y), N(sp.Z), N(ep.X), N(ep.Y), N(ep.Z), N(geo.Length)));
                }
                File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);

                File.WriteAllText(typesJson,
                    JsonConvert.SerializeObject(typeRows, Formatting.Indented),
                    Encoding.UTF8);

                var tb = new StringBuilder();
                tb.AppendLine("DrawingType,DrawingName,DrawingMark,Category,ObjectCount");
                foreach (var d in typeRows)
                {
                    tb.AppendLine(string.Join(",",
                        Csv(d.DrawingType), Csv(d.DrawingName), Csv(d.DrawingMark),
                        Csv(d.Category), d.ObjectCount));
                }
                File.WriteAllText(typesCsv, tb.ToString(), Encoding.UTF8);

                Console.WriteLine("[Inventory] " + records.Count + " objects → " + jsonPath);
                Console.WriteLine("[Inventory] " + records.Count + " objects → " + csvPath);
                Console.WriteLine("[Inventory] " + typeRows.Count + " drawings → " + typesJson);
                Console.WriteLine("[Inventory] " + typeRows.Count + " drawings → " + typesCsv);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Inventory] write failed: " + ex.Message);
            }
        }

        private static void PrintSummary(
            List<DrawingInventoryRecord> records,
            List<DrawingTypeRecord> typeRows,
            int drawingCount)
        {
            Console.WriteLine();
            Console.WriteLine("[Inventory] drawings scanned : " + drawingCount);
            Console.WriteLine("[Inventory] objects written  : " + records.Count);
            Console.WriteLine("[Inventory] drawings by type:");
            foreach (var g in typeRows.GroupBy(r => r.DrawingType ?? "").OrderBy(g => g.Key))
                Console.WriteLine("    " + Label(g.Key) + " sheets = " + g.Count() + "  objects = " + g.Sum(x => x.ObjectCount));
            Console.WriteLine("[Inventory] objects by DrawingType:");
            foreach (var g in records.GroupBy(r => r.DrawingType ?? "").OrderBy(g => g.Key))
                Console.WriteLine("    " + Label(g.Key) + " = " + g.Count());
            Console.WriteLine("[Inventory] objects by Category:");
            foreach (var g in records.GroupBy(r => r.Category ?? "").OrderBy(g => g.Key))
                Console.WriteLine("    " + Label(g.Key) + " = " + g.Count());
            int withSheet = records.Count(r => r.HasDrawing);
            Console.WriteLine("[Inventory] HasDrawing true=" + withSheet + "  false=" + (records.Count - withSheet));
        }

        private static string Report(TSModel.ModelObject obj, string name)
        {
            if (obj == null) return "";
            string v = "";
            try { obj.GetReportProperty(name, ref v); } catch { /* optional */ }
            return v ?? "";
        }

        private static double ReportDouble(TSModel.ModelObject obj, string name)
        {
            double v = 0;
            try
            {
                if (obj.GetReportProperty(name, ref v)) return v;
            }
            catch { /* string fallback */ }
            double.TryParse(Report(obj, name), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        private static string SafeMark(Drawing drawing)
        {
            try { return drawing.Mark ?? drawing.Name ?? "?"; }
            catch { return "?"; }
        }

        private static string SafeId(Identifier id)
        {
            try { return id.ID.ToString(); } catch { return "?"; }
        }

        private static string Label(string s)
        {
            return string.IsNullOrEmpty(s) ? "(blank)" : s;
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        private static string N(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}

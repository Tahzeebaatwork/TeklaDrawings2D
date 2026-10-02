// FloorWiseDrawingExtractor.cs
// ─────────────────────────────────────────────────────────────────
// 3D model → FLOOR UDA or Z-band → group every drawing
//   GADrawing / CastUnitDrawing / AssemblyDrawing /
//   SinglePartDrawing / MultiDrawing
//   → PrintDrawing (DPMPrinterAttributes, PDF) into Export/<floor>/
//   → nested 2D digital-twin JSON (sheet, views, CS, dims, marks,
//      linework, hide flags, 3D GUIDs, AABB overlaps)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using TSDrawing = Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public class FloorWiseMeta
    {
        public string ModelName { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public string GroupingRule { get; set; } =
            "FLOOR / FLOOR_NAME / STOREY / LEVEL UDA if set; otherwise Z-band from solid AABB (or CS origin).";
        public int DrawingCount { get; set; }
        public int PdfCount { get; set; }
        public Dictionary<string, int> CountsByFloor { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CountsByType { get; set; } = new Dictionary<string, int>();
        public string ExportRoot { get; set; } = "";
    }

    public class FloorBucket
    {
        public string FloorName { get; set; } = "";
        public string Source { get; set; } = "";
        public double ElevationZmm { get; set; }
        public string PdfFolder { get; set; } = "";
        public int DrawingCount { get; set; }
        public List<FloorWiseSheet> Drawings { get; set; } = new List<FloorWiseSheet>();
    }

    public class Linked3dObject
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string TeklaType { get; set; } = "";
        public string Mark { get; set; } = "";
        public string AssemblyMark { get; set; } = "";
        public string Profile { get; set; } = "";
        public string FloorUda { get; set; } = "";
        public string CastUnitMark { get; set; } = "";
        public double Zmin { get; set; }
        public double Zmax { get; set; }
        public double Zcenter { get; set; }
    }

    public class OverlapHit
    {
        public int IndexA { get; set; }
        public int IndexB { get; set; }
        public string KindA { get; set; } = "";
        public string KindB { get; set; } = "";
        public string TextA { get; set; } = "";
        public string TextB { get; set; } = "";
    }

    public class FloorWiseSheet
    {
        public string Type { get; set; } = "";
        public string TeklaDrawingType { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Name { get; set; } = "";
        public string Title1 { get; set; } = "";
        public string Title2 { get; set; } = "";
        public string Title3 { get; set; } = "";
        public string UpToDateStatus { get; set; } = "";
        public DateTime CreationDate { get; set; }
        public DateTime ModificationDate { get; set; }
        public string Floor { get; set; } = "";
        public string FloorSource { get; set; } = "";
        public double FloorZmm { get; set; }
        /// <summary>One-line identity: which 3D part/assembly this sheet is for.</summary>
        public string Identity { get; set; } = "";
        public string PartMark { get; set; } = "";
        public string AssemblyMark { get; set; } = "";
        public string CastUnitMark { get; set; } = "";
        public string Profile { get; set; } = "";
        public string PdfPath { get; set; } = "";
        public string JsonPath { get; set; } = "";
        public string ReadablePath { get; set; } = "";
        public bool PdfExported { get; set; }
        public string LinkedModelObject { get; set; } = "";
        public int LinkedModelId { get; set; }
        public string LinkedModelGuid { get; set; } = "";
        public List<Linked3dObject> Linked3d { get; set; } = new List<Linked3dObject>();
        public double SheetWidth { get; set; }
        public double SheetHeight { get; set; }
        public double[] SheetOrigin { get; set; }
        public double[] SheetSize { get; set; }
        public List<FloorWiseView> Views { get; set; } = new List<FloorWiseView>();
        public List<FloorWiseObject> SheetObjects { get; set; } = new List<FloorWiseObject>();
        /// <summary>3D members on this floor projected to plan (X,Y) — start/end/profile/GUID.</summary>
        public List<FloorMember> Members { get; set; } = new List<FloorMember>();
        public List<FloorGridLine> GridLines { get; set; } = new List<FloorGridLine>();
        public double[] PlanBboxMin { get; set; }
        public double[] PlanBboxMax { get; set; }
        public int MemberCount { get; set; }
        public PdfReadableDump Pdf { get; set; }
        public string Error { get; set; }
    }

    public class FloorMember
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string TeklaType { get; set; } = "";
        public string PartMark { get; set; } = "";
        public string AssemblyMark { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Class { get; set; } = "";
        public double Length { get; set; }
        public double Thickness { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] PlanStart { get; set; }
        public double[] PlanEnd { get; set; }
        public double Zcenter { get; set; }
    }

    public class FloorGridLine
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] PlanStart { get; set; }
        public double[] PlanEnd { get; set; }
        public double Spacing { get; set; }
        public string Direction { get; set; } = "";
        public int ModelObjectId { get; set; }
        public string ModelObjectGuid { get; set; } = "";
    }

    public class FloorWiseView
    {
        public string Name { get; set; } = "";
        public string ViewType { get; set; } = "";
        public double Scale { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double[] Origin { get; set; }
        public double[] FrameOrigin { get; set; }
        public double[] ExtremaCenter { get; set; }
        public double[] ViewCsOrigin { get; set; }
        public double[] ViewCsX { get; set; }
        public double[] ViewCsY { get; set; }
        public double[] DisplayCsOrigin { get; set; }
        public double[] DisplayCsX { get; set; }
        public double[] DisplayCsY { get; set; }
        public double[] RestrictionBoxMin { get; set; }
        public double[] RestrictionBoxMax { get; set; }
        public bool Unfolded { get; set; }
        public bool Undeformed { get; set; }
        public int OverlapCount { get; set; }
        public List<OverlapHit> Overlaps { get; set; } = new List<OverlapHit>();
        public List<FloorWiseObject> Objects { get; set; } = new List<FloorWiseObject>();
    }

    public class FloorWiseObject
    {
        public string Kind { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Text { get; set; }
        public double[] InsertionPoint { get; set; }
        public double[] LabelPoint { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] CenterPoint { get; set; }
        public double[] Point1 { get; set; }
        public double[] Point2 { get; set; }
        public double[] Point3 { get; set; }
        public List<double[]> Points { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public double? Distance { get; set; }
        public double? Radius { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? Angle { get; set; }
        public int ModelObjectId { get; set; }
        public string ModelObjectGuid { get; set; }
        public bool Hidden { get; set; }
        public string HiddenFlags { get; set; }
        public string ShouldBeHiddenFlags { get; set; }
        public string DetailBoundaryShape { get; set; }
        public string DetailMarkName { get; set; }
        public Dictionary<string, string> Extra { get; set; }
    }

    public class FloorWiseDrawingExtractor
    {
        private static readonly string[] FloorUdaNames =
            { "FLOOR", "FLOOR_NAME", "STOREY", "STORY", "LEVEL", "FLOORLEVEL" };

        private readonly DrawingHandler _handler;
        private readonly TSModel.Model _model;
        private readonly DrawingGenerator _printer;
        private readonly string _exportRoot;
        private Dictionary<string, List<FloorMember>> _membersByFloor;
        private List<FloorGridLine> _grids;
        private static readonly FieldInfo HiddenFlagsField =
            typeof(Hideable).GetField("_HiddenFlags", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ShouldHideField =
            typeof(Hideable).GetField("_ShouldBeHiddenFlags", BindingFlags.Instance | BindingFlags.NonPublic);

        public FloorWiseDrawingExtractor(TSModel.Model model, string baseDir, string exportFolder = "Export")
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
            _printer = new DrawingGenerator();
            _exportRoot = Path.Combine(baseDir ?? ".", exportFolder ?? "Export");
        }

        public bool IsConnected => _handler != null && _handler.GetConnectionStatus();

        /// <param name="typeFilter">
        /// Null = all sheets. "GA" / "GAD" = only GADrawing (General Arrangement).
        /// Also accepts Assembly, CastUnit, SinglePart, Multi.
        /// </param>
        public JObject Run(string typeFilter = null)
        {
            Directory.CreateDirectory(_exportRoot);
            var meta = new FloorWiseMeta
            {
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
                ExportRoot = _exportRoot,
            };
            try { meta.ModelName = _model.GetInfo()?.ModelName ?? ""; } catch { /* optional */ }

            if (!IsConnected)
            {
                Console.WriteLine("[FloorWise] DrawingHandler is not connected.");
                return BuildRoot(meta, new Dictionary<string, FloorBucket>());
            }

            var drawings = new List<Drawing>();
            var en = _handler.GetDrawings();
            while (en != null && en.MoveNext())
            {
                if (en.Current == null) continue;
                if (!MatchesType(en.Current, typeFilter)) continue;
                drawings.Add(en.Current);
            }

            string filterLabel = string.IsNullOrWhiteSpace(typeFilter) ? "all types" : typeFilter;
            if (IsGaFilter(typeFilter) || drawings.Any(d => d is GADrawing))
                BuildGaCatalog();
            Console.WriteLine($"[FloorWise] {drawings.Count} drawings ({filterLabel}) → floor-wise PDF + JSON...");
            if (drawings.Count == 0 && IsGaFilter(typeFilter))
            {
                Console.WriteLine("[GAD] No GADrawing sheets in this model.");
                Console.WriteLine("[GAD] In Tekla: create General Arrangement drawings, then re-run --gad.");
            }

            var floors = new Dictionary<string, FloorBucket>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var drawing in drawings)
            {
                n++;
                FloorWiseSheet sheet = null;
                try
                {
                    sheet = ExtractOne(drawing);
                }
                catch (Exception ex)
                {
                    sheet = new FloorWiseSheet
                    {
                        Mark = SafeMark(drawing),
                        Type = Classify(drawing),
                        Error = ex.Message,
                        Floor = "Unknown",
                        FloorSource = "Error",
                    };
                    Console.WriteLine($"[FloorWise] '{sheet.Mark}' failed: {ex.Message}");
                }

                string key = string.IsNullOrWhiteSpace(sheet.Floor) ? "Unknown" : sheet.Floor;
                if (!floors.TryGetValue(key, out var bucket))
                {
                    bucket = new FloorBucket
                    {
                        FloorName = key,
                        Source = sheet.FloorSource,
                        ElevationZmm = sheet.FloorZmm,
                        PdfFolder = Path.Combine(_exportRoot, SafeFolder(key)),
                    };
                    floors[key] = bucket;
                }
                bucket.Drawings.Add(sheet);
                bucket.DrawingCount = bucket.Drawings.Count;
                if (sheet.PdfExported) meta.PdfCount++;

                if (!meta.CountsByType.ContainsKey(sheet.Type))
                    meta.CountsByType[sheet.Type] = 0;
                meta.CountsByType[sheet.Type]++;

                if (n % 5 == 0)
                    Console.WriteLine($"[FloorWise] {n}/{drawings.Count}...");
            }

            meta.DrawingCount = drawings.Count;
            foreach (var kv in floors)
                meta.CountsByFloor[kv.Key] = kv.Value.DrawingCount;

            WriteOutputs(meta, floors);
            return BuildRoot(meta, floors);
        }

        private FloorWiseSheet ExtractOne(Drawing drawing)
        {
            var sheet = new FloorWiseSheet
            {
                Type = Classify(drawing),
                TeklaDrawingType = drawing.DrawingTypeStr ?? drawing.GetType().Name,
                Mark = drawing.Mark ?? "",
                Name = drawing.Name ?? "",
                Title1 = drawing.Title1 ?? "",
                Title2 = drawing.Title2 ?? "",
                Title3 = drawing.Title3 ?? "",
                UpToDateStatus = drawing.UpToDateStatus.ToString(),
                CreationDate = drawing.CreationDate,
                ModificationDate = drawing.ModificationDate,
            };

            FillDirectLink(drawing, sheet);
            sheet.Linked3d = ResolveLinked3d(drawing, sheet);
            AssignFloor(sheet);

            string floorDir = Path.Combine(_exportRoot, SafeFolder(sheet.Floor));
            string jsonDir = Path.Combine(floorDir, "json");
            string txtDir = Path.Combine(floorDir, "human_readable");
            Directory.CreateDirectory(floorDir);
            Directory.CreateDirectory(jsonDir);
            Directory.CreateDirectory(txtDir);

            bool opened = false;
            bool isGa = drawing is GADrawing;
            try
            {
                if (!isGa)
                {
                    try { _handler.UpdateDrawing(drawing); } catch { /* optional */ }
                    try { opened = _handler.SetActiveDrawing(drawing, false); }
                    catch { opened = false; }
                }

                var container = drawing.GetSheet();
                if (container != null)
                {
                    sheet.SheetWidth = R(container.Width);
                    sheet.SheetHeight = R(container.Height);
                    sheet.SheetOrigin = Xyz(container.Origin);
                    try
                    {
                        var size = drawing.Layout?.SheetSize;
                        if (size != null)
                            sheet.SheetSize = new[] { R(size.Width), R(size.Height) };
                    }
                    catch { /* layout optional */ }

                    CollectViews(container, sheet);
                    sheet.SheetObjects = CollectObjects(container, skipViews: true);
                }
            }
            finally
            {
                if (opened)
                {
                    try { _handler.CloseActiveDrawing(false); } catch { /* best effort */ }
                }
            }

            AttachGaGeometry(sheet);

            string stem = isGa ? ("GAD_" + SafeFolder(sheet.Floor)) : IdentityStem(sheet);
            string pdfPath = Path.Combine(floorDir, stem + ".pdf");
            try
            {
                sheet.PdfExported = _printer.ExportToPdf(drawing, pdfPath);
                if (sheet.PdfExported)
                    sheet.PdfPath = pdfPath;
            }
            catch (Exception ex)
            {
                sheet.Error = (sheet.Error ?? "") + " PDF: " + ex.Message;
            }

            if (!string.IsNullOrEmpty(sheet.PdfPath) && File.Exists(sheet.PdfPath))
            {
                try { sheet.Pdf = DrawingJsonExtractor.ExtractPdf(sheet.PdfPath); }
                catch { /* optional */ }
            }

            sheet.JsonPath = Path.Combine(jsonDir, stem + ".json");
            sheet.ReadablePath = Path.Combine(txtDir, stem + ".txt");
            File.WriteAllText(sheet.JsonPath, JsonConvert.SerializeObject(sheet, JsonSettings()), Encoding.UTF8);
            File.WriteAllText(sheet.ReadablePath, BuildReadable(sheet), Encoding.UTF8);

            Console.WriteLine($"[FloorWise] {sheet.Identity}");
            Console.WriteLine($"            pdf={(sheet.PdfExported ? "ok" : "fail")}  json={Path.GetFileName(sheet.JsonPath)}");

            return sheet;
        }

        private void CollectViews(ContainerView sheet, FloorWiseSheet dump)
        {
            DrawingObjectEnumerator views = null;
            try { views = sheet.GetAllViews(); }
            catch
            {
                try { views = sheet.GetViews(); } catch { return; }
            }
            if (views == null) return;

            while (views.MoveNext())
            {
                try
                {
                    if (views.Current is View view)
                        dump.Views.Add(ReadView(view));
                }
                catch { /* skip bad view */ }
            }
        }

        private FloorWiseView ReadView(View view)
        {
            var rec = new FloorWiseView
            {
                Name = view.Name ?? "",
                ViewType = view.ViewType.ToString(),
                Width = R(view.Width),
                Height = R(view.Height),
                Origin = Xyz(view.Origin),
                FrameOrigin = Xyz(view.FrameOrigin),
                ExtremaCenter = Xyz(view.ExtremaCenter),
            };

            try
            {
                rec.Scale = R(view.Attributes.Scale, 4);
                rec.Unfolded = view.Attributes.UnfoldedView;
                rec.Undeformed = view.Attributes.UndeformedView;
            }
            catch { /* attributes optional */ }

            try
            {
                var vcs = view.ViewCoordinateSystem;
                rec.ViewCsOrigin = Xyz(vcs.Origin);
                rec.ViewCsX = Xyz(vcs.AxisX);
                rec.ViewCsY = Xyz(vcs.AxisY);
            }
            catch { /* CS optional */ }

            try
            {
                var dcs = view.DisplayCoordinateSystem;
                rec.DisplayCsOrigin = Xyz(dcs.Origin);
                rec.DisplayCsX = Xyz(dcs.AxisX);
                rec.DisplayCsY = Xyz(dcs.AxisY);
            }
            catch { /* CS optional */ }

            try
            {
                rec.RestrictionBoxMin = Xyz(view.RestrictionBox.MinPoint);
                rec.RestrictionBoxMax = Xyz(view.RestrictionBox.MaxPoint);
            }
            catch { /* box optional */ }

            rec.Objects = CollectObjects(view, skipViews: false);
            rec.Overlaps = FindOverlaps(rec.Objects);
            rec.OverlapCount = rec.Overlaps.Count;
            return rec;
        }

        private List<FloorWiseObject> CollectObjects(ViewBase container, bool skipViews)
        {
            var list = new List<FloorWiseObject>();
            DrawingObjectEnumerator en;
            try { en = container.GetAllObjects(); }
            catch
            {
                try { en = container.GetObjects(); }
                catch { return list; }
            }
            if (en == null) return list;

            while (en.MoveNext())
            {
                var obj = en.Current;
                if (obj == null) continue;
                if (skipViews && obj is ViewBase) continue;
                try { list.Add(ReadObject(obj)); }
                catch { /* enumerator can yield uninitialized objects */ }
            }
            return list;
        }

        private FloorWiseObject ReadObject(DrawingObject obj)
        {
            var rec = new FloorWiseObject
            {
                TypeName = obj.GetType().Name,
                Kind = KindOf(obj),
            };
            FillHideable(obj, rec);
            FillAabb(obj, rec);

            if (obj is TSDrawing.Text text)
            {
                rec.Text = text.TextString ?? "";
                rec.InsertionPoint = Xyz(text.InsertionPoint);
            }
            else if (obj is DetailMark detail)
            {
                rec.CenterPoint = Xyz(detail.CenterPoint);
                rec.Point1 = Xyz(detail.BoundaryPoint);
                rec.LabelPoint = Xyz(detail.LabelPoint);
                rec.InsertionPoint = rec.LabelPoint;
                rec.Text = FlattenRelated(detail);
                try
                {
                    rec.DetailBoundaryShape = detail.Attributes.BoundaryShape.ToString();
                    rec.DetailMarkName = detail.Attributes.MarkName;
                }
                catch { /* attributes optional */ }
            }
            else if (obj is SectionMark section)
            {
                rec.StartPoint = Xyz(section.LeftPoint);
                rec.EndPoint = Xyz(section.RightPoint);
                rec.Text = FlattenRelated(section);
            }
            else if (obj is LevelMark level)
            {
                rec.InsertionPoint = Xyz(level.InsertionPoint);
                rec.StartPoint = Xyz(level.BasePoint);
                rec.Text = level.SubType.ToString();
                FillModelId(rec, level.ModelObjectIdentifier);
            }
            else if (obj is MarkBase mark)
            {
                rec.InsertionPoint = Xyz(mark.InsertionPoint);
                rec.Text = FlattenRelated(mark);
            }
            else if (obj is StraightDimension dim)
            {
                rec.StartPoint = Xyz(dim.StartPoint);
                rec.EndPoint = Xyz(dim.EndPoint);
                rec.Distance = R(dim.Distance);
                rec.Text = FlattenContainer(dim.Value);
                rec.Extra = Extra("UpDirection", Format(dim.UpDirection));
                GuessBox(rec, rec.StartPoint, rec.EndPoint);
            }
            else if (obj is StraightDimensionSet set)
            {
                rec.Distance = R(set.Distance);
                rec.Text = FlattenRelated(set);
            }
            else if (obj is AngleDimension ang)
            {
                rec.CenterPoint = Xyz(ang.Origin);
                rec.Point1 = Xyz(ang.Point1);
                rec.Point2 = Xyz(ang.Point2);
                rec.Distance = R(ang.Distance);
                rec.Text = FlattenRelated(ang);
            }
            else if (obj is RadiusDimension rad)
            {
                rec.Point1 = Xyz(rad.ArcPoint1);
                rec.Point2 = Xyz(rad.ArcPoint2);
                rec.Point3 = Xyz(rad.ArcPoint3);
                rec.Distance = R(rad.Distance);
                rec.Text = FlattenRelated(rad);
            }
            else if (obj is CurvedDimensionBase curved)
            {
                rec.StartPoint = Xyz(curved.StartPoint);
                rec.EndPoint = Xyz(curved.EndPoint);
                rec.Point1 = Xyz(curved.ArcPoint1);
                rec.Point2 = Xyz(curved.ArcPoint2);
                rec.Point3 = Xyz(curved.ArcPoint3);
                rec.Distance = R(curved.Distance);
                rec.Text = FlattenRelated(curved);
            }
            else if (obj is TSDrawing.Line line)
            {
                rec.StartPoint = Xyz(line.StartPoint);
                rec.EndPoint = Xyz(line.EndPoint);
                GuessBox(rec, rec.StartPoint, rec.EndPoint);
            }
            else if (obj is TSDrawing.Arc arc)
            {
                rec.StartPoint = Xyz(arc.StartPoint);
                rec.EndPoint = Xyz(arc.EndPoint);
                rec.Radius = R(arc.Radius);
            }
            else if (obj is Circle circle)
            {
                rec.CenterPoint = Xyz(circle.CenterPoint);
                rec.Radius = R(circle.Radius);
            }
            else if (obj is TSDrawing.Rectangle rect)
            {
                rec.StartPoint = Xyz(rect.StartPoint);
                rec.EndPoint = Xyz(rect.EndPoint);
                rec.Width = R(rect.Width);
                rec.Height = R(rect.Height);
                rec.Angle = R(rect.Angle);
                GuessBox(rec, rec.StartPoint, rec.EndPoint);
            }
            else if (obj is RectangularCloud rcloud)
            {
                rec.StartPoint = Xyz(rcloud.StartPoint);
                rec.EndPoint = Xyz(rcloud.EndPoint);
                rec.Width = R(rcloud.Width);
                rec.Height = R(rcloud.Height);
                rec.Angle = R(rcloud.Angle);
            }
            else if (obj is Cloud cloud)
            {
                rec.Points = PointsOf(cloud.Points);
            }
            else if (obj is Polyline poly)
            {
                rec.Points = PointsOf(poly.Points);
            }
            else if (obj is Polygon polygon)
            {
                rec.Points = PointsOf(polygon.Points);
            }
            else if (obj is TSDrawing.Part part)
            {
                FillModelId(rec, part.ModelIdentifier);
            }
            else if (obj is TSDrawing.Bolt bolt)
            {
                FillModelId(rec, bolt.ModelIdentifier);
            }
            else if (obj is TSDrawing.Weld weld)
            {
                FillModelId(rec, weld.ModelIdentifier);
            }
            else if (obj is ReinforcementBase rebar)
            {
                FillModelId(rec, rebar.ModelIdentifier);
            }
            else if (obj is Grid grid)
            {
                FillModelId(rec, grid.ModelIdentifier);
            }

            return rec;
        }

        // =====================================================================
        // Floor grouping
        // =====================================================================
        private List<Linked3dObject> ResolveLinked3d(Drawing drawing, FloorWiseSheet sheet)
        {
            var list = new List<Linked3dObject>();
            var seen = new HashSet<int>();

            IEnumerable<Identifier> ids = Enumerable.Empty<Identifier>();
            try
            {
                var fromApi = _handler.GetModelObjectIdentifiers(drawing);
                if (fromApi != null && fromApi.Count > 0)
                    ids = fromApi;
            }
            catch { /* fall back to drawing-type identifiers */ }

            var fallback = new List<Identifier>();
            try
            {
                if (drawing is SinglePartDrawing spd && spd.PartIdentifier != null)
                    fallback.Add(spd.PartIdentifier);
                else if (drawing is AssemblyDrawing ad && ad.AssemblyIdentifier != null)
                    fallback.Add(ad.AssemblyIdentifier);
                else if (drawing is CastUnitDrawing cud && cud.CastUnitIdentifier != null)
                    fallback.Add(cud.CastUnitIdentifier);
            }
            catch { /* optional */ }

            foreach (var id in ids.Concat(fallback))
            {
                if (id == null || id.ID == 0 || !seen.Add(id.ID)) continue;
                var rec = Describe3d(id);
                if (rec != null) list.Add(rec);
            }
            return list;
        }

        private Linked3dObject Describe3d(Identifier id)
        {
            try
            {
                TSModel.ModelObject mo = _model.SelectModelObject(id);
                if (mo == null) return null;

                if (mo is TSModel.Assembly asm)
                {
                    try { mo = asm.GetMainPart() as TSModel.ModelObject ?? mo; } catch { /* keep assembly */ }
                }

                var rec = new Linked3dObject
                {
                    Id = id.ID,
                    Guid = id.GUID != Guid.Empty ? id.GUID.ToString() : mo.Identifier.GUID.ToString(),
                    TeklaType = mo.GetType().Name,
                };

                if (mo is TSModel.Part part)
                {
                    rec.Mark = Report(part, "PART_POS");
                    rec.AssemblyMark = Report(part, "ASSEMBLY_POS");
                    rec.CastUnitMark = Report(part, "CAST_UNIT_POS");
                    rec.Profile = Report(part, "PROFILE");
                    rec.FloorUda = ReadFloorUda(part);
                    FillZ(part, rec);
                }
                else
                {
                    rec.FloorUda = ReadFloorUda(mo);
                    try
                    {
                        var cs = mo.GetCoordinateSystem();
                        rec.Zcenter = cs.Origin.Z;
                        rec.Zmin = rec.Zmax = rec.Zcenter;
                    }
                    catch { /* optional */ }
                }
                return rec;
            }
            catch
            {
                return new Linked3dObject { Id = id.ID, Guid = id.GUID.ToString(), TeklaType = "Unresolved" };
            }
        }

        private static void FillZ(TSModel.Part part, Linked3dObject rec)
        {
            try
            {
                var solid = part.GetSolid();
                if (solid != null)
                {
                    rec.Zmin = solid.MinimumPoint.Z;
                    rec.Zmax = solid.MaximumPoint.Z;
                    rec.Zcenter = (rec.Zmin + rec.Zmax) / 2.0;
                    return;
                }
            }
            catch { /* solid optional */ }
            try
            {
                rec.Zcenter = part.GetCoordinateSystem().Origin.Z;
                rec.Zmin = rec.Zmax = rec.Zcenter;
            }
            catch { /* optional */ }
        }

        private static string ReadFloorUda(TSModel.ModelObject obj)
        {
            foreach (var name in FloorUdaNames)
            {
                string v = "";
                try { if (obj.GetUserProperty(name, ref v) && !string.IsNullOrWhiteSpace(v)) return v.Trim(); } catch { }
                try { if (obj.GetReportProperty(name, ref v) && !string.IsNullOrWhiteSpace(v)) return v.Trim(); } catch { }
            }
            return "";
        }

        private static void AssignFloor(FloorWiseSheet sheet)
        {
            var uda = sheet.Linked3d
                .Select(p => p.FloorUda)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            double z = 0;
            var zs = sheet.Linked3d.Where(p => Math.Abs(p.Zcenter) + Math.Abs(p.Zmax) + Math.Abs(p.Zmin) > 0.01).ToList();
            if (zs.Count > 0)
                z = zs.OrderBy(p => p.Zcenter).Skip(zs.Count / 2).First().Zcenter;
            else if (TryParseTitleZ(sheet.Title3, out double zTitle))
                z = zTitle;

            sheet.FloorZmm = Math.Round(z, 1);

            string fromTitle = InferFloorFromText(
                (sheet.Name ?? "") + " " + (sheet.Title1 ?? "") + " " +
                (sheet.Title2 ?? "") + " " + (sheet.Title3 ?? "") + " " + (sheet.Mark ?? ""));

            if (uda != null)
            {
                sheet.Floor = uda.Key;
                sheet.FloorSource = "UDA";
            }
            else if (!string.IsNullOrWhiteSpace(fromTitle))
            {
                sheet.Floor = fromTitle;
                sheet.FloorSource = "DrawingTitle";
            }
            else
            {
                sheet.Floor = FloorBand(z);
                sheet.FloorSource = "ZBand";
            }

            FillIdentity(sheet);
        }

        /// <summary>
        /// GA sheets are often named "Level 2 Plan" / "1ST FLOOR" even when
        /// the FLOOR UDA on parts is blank.
        /// </summary>
        internal static string InferFloorFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string t = text.ToUpperInvariant();

            if (t.Contains("ROOF")) return "Upper / roof";
            if (t.Contains("PODIUM")) return "Level 1 / podium";
            if (t.Contains("GRADE") || t.Contains("GROUND")) return "Grade / on-grade (~0 m)";

            var m = System.Text.RegularExpressions.Regex.Match(
                t, @"(?:LEVEL|LVL|FL(?:OOR)?|STOREY|STORY)\s*[-:]?\s*(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
                return n <= 0 ? "Grade / on-grade (~0 m)" : "Level " + n;

            m = System.Text.RegularExpressions.Regex.Match(t, @"\b(\d+)(?:ST|ND|RD|TH)\s+FLOOR");
            if (m.Success && int.TryParse(m.Groups[1].Value, out n))
                return "Level " + n;

            m = System.Text.RegularExpressions.Regex.Match(t, @"\bL(\d+)\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out n) && n >= 0 && n <= 20)
                return n == 0 ? "Grade / on-grade (~0 m)" : "Level " + n;

            return "";
        }

        private static void FillIdentity(FloorWiseSheet sheet)
        {
            var main = sheet.Linked3d.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Mark))
                       ?? sheet.Linked3d.FirstOrDefault();
            if (main != null)
            {
                sheet.PartMark = main.Mark ?? "";
                sheet.AssemblyMark = main.AssemblyMark ?? "";
                sheet.CastUnitMark = main.CastUnitMark ?? "";
                sheet.Profile = main.Profile ?? "";
            }

            string owner = !string.IsNullOrWhiteSpace(sheet.CastUnitMark)
                ? "CU " + sheet.CastUnitMark
                : (!string.IsNullOrWhiteSpace(sheet.AssemblyMark) ? "ASM " + sheet.AssemblyMark : sheet.Mark);

            if (string.Equals(sheet.Type, "GA", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Identity =
                    $"GAD {sheet.Mark}  name='{sheet.Name}'  floor={sheet.Floor}  " +
                    $"Z={sheet.FloorZmm:0} mm  members={sheet.MemberCount}  grids={sheet.GridLines.Count}";
            }
            else
            {
                sheet.Identity =
                    $"{sheet.Type} drawing {sheet.Mark}  →  {owner}" +
                    (string.IsNullOrWhiteSpace(sheet.PartMark) ? "" : "  part " + sheet.PartMark) +
                    (string.IsNullOrWhiteSpace(sheet.Profile) ? "" : "  profile " + sheet.Profile) +
                    $"  floor {sheet.Floor} (Z={sheet.FloorZmm:0} mm)";
            }
        }

        private static bool IsGaFilter(string typeFilter)
        {
            if (string.IsNullOrWhiteSpace(typeFilter)) return false;
            string t = typeFilter.Trim().ToUpperInvariant();
            return t == "GA" || t == "GAD" || t == "GENERAL" || t == "GENERALARRANGEMENT";
        }

        internal static bool TryParseTitleZ(string title3, out double zMid)
        {
            zMid = 0;
            if (string.IsNullOrWhiteSpace(title3)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(
                title3, @"Z\s*([-+]?\d+(?:\.\d+)?)\s*[–\-]\s*([-+]?\d+(?:\.\d+)?)");
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double a))
                return false;
            if (!double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double b))
                return false;
            zMid = (a + b) / 2.0;
            return true;
        }

        private void BuildGaCatalog()
        {
            _membersByFloor = new Dictionary<string, List<FloorMember>>(StringComparer.OrdinalIgnoreCase);
            _grids = new List<FloorGridLine>();
            Console.WriteLine("[GAD] Building floor catalog from live 3D model (parts + grids)...");

            var types = new[] { typeof(TSModel.Beam), typeof(TSModel.ContourPlate), typeof(TSModel.PolyBeam) };
            var en = _model.GetModelObjectSelector().GetAllObjectsWithType(types);
            int n = 0;
            while (en != null && en.MoveNext())
            {
                if (!(en.Current is TSModel.Part part)) continue;
                n++;
                try
                {
                    var member = ReadFloorMember(part);
                    if (member == null) continue;
                    string floor = FloorBand(member.Zcenter);
                    if (!_membersByFloor.TryGetValue(floor, out var list))
                    {
                        list = new List<FloorMember>();
                        _membersByFloor[floor] = list;
                    }
                    list.Add(member);
                }
                catch { /* skip bad part */ }

                if (n % 5000 == 0)
                    Console.WriteLine($"[GAD] catalog {n} parts...");
            }

            try { CollectGrids(); }
            catch (Exception ex) { Console.WriteLine("[GAD] grids: " + ex.Message); }

            int members = _membersByFloor.Values.Sum(v => v.Count);
            Console.WriteLine($"[GAD] catalog ready: {members} members, {_grids.Count} grid lines, {_membersByFloor.Count} floors.");
        }

        private FloorMember ReadFloorMember(TSModel.Part part)
        {
            Point start = null, end = null;
            if (part is TSModel.Beam beam)
            {
                start = beam.StartPoint;
                end = beam.EndPoint;
            }
            else if (part is TSModel.PolyBeam poly)
            {
                try
                {
                    var pts = poly.Contour?.ContourPoints;
                    if (pts != null && pts.Count >= 2)
                    {
                        start = pts[0] as Point;
                        end = pts[pts.Count - 1] as Point;
                    }
                }
                catch { /* fallback CS */ }
            }
            else if (part is TSModel.ContourPlate plate)
            {
                try
                {
                    var pts = plate.Contour?.ContourPoints;
                    if (pts != null && pts.Count > 0)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                        foreach (var raw in pts)
                        {
                            if (!(raw is Point p)) continue;
                            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
                        }
                        if (minX < double.MaxValue)
                        {
                            start = new Point(minX, minY, minZ);
                            end = new Point(maxX, maxY, maxZ);
                        }
                    }
                }
                catch { /* fallback CS */ }
            }

            if (start == null || end == null)
            {
                try
                {
                    var o = part.GetCoordinateSystem().Origin;
                    start = start ?? o;
                    end = end ?? o;
                }
                catch { return null; }
            }

            double z = (start.Z + end.Z) / 2.0;
            double len = Math.Sqrt(
                Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2));
            if (len < 0.01)
            {
                double reported = 0;
                try { part.GetReportProperty("LENGTH", ref reported); } catch { /* optional */ }
                if (reported > 0) len = reported;
            }

            return new FloorMember
            {
                Id = part.Identifier.ID,
                Guid = part.Identifier.GUID.ToString(),
                TeklaType = part.GetType().Name,
                PartMark = Report(part, "PART_POS"),
                AssemblyMark = Report(part, "ASSEMBLY_POS"),
                Profile = Report(part, "PROFILE"),
                Material = Report(part, "MATERIAL"),
                Class = SafeClass(part),
                Length = Math.Round(len, 1),
                StartPoint = Xyz(start),
                EndPoint = Xyz(end),
                PlanStart = new[] { R(start.X), R(start.Y) },
                PlanEnd = new[] { R(end.X), R(end.Y) },
                Zcenter = Math.Round(z, 1),
            };
        }

        private void CollectGrids()
        {
            var en = _model.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSModel.Grid) });
            while (en != null && en.MoveNext())
            {
                if (!(en.Current is TSModel.Grid grid)) continue;
                double[] xs = ParseCoords(grid.CoordinateX);
                double[] ys = ParseCoords(grid.CoordinateY);
                string[] lx = ParseLabels(grid.LabelX, xs.Length);
                string[] ly = ParseLabels(grid.LabelY, ys.Length);
                if (xs.Length == 0 && ys.Length == 0) continue;

                double xmin = xs.Length > 0 ? xs.Min() : 0;
                double xmax = xs.Length > 0 ? xs.Max() : 0;
                double ymin = ys.Length > 0 ? ys.Min() : 0;
                double ymax = ys.Length > 0 ? ys.Max() : 0;
                if (_membersByFloor != null)
                {
                    foreach (var m in _membersByFloor.Values.SelectMany(v => v))
                    {
                        if (m.PlanStart == null || m.PlanEnd == null) continue;
                        xmin = Math.Min(xmin, Math.Min(m.PlanStart[0], m.PlanEnd[0]));
                        xmax = Math.Max(xmax, Math.Max(m.PlanStart[0], m.PlanEnd[0]));
                        ymin = Math.Min(ymin, Math.Min(m.PlanStart[1], m.PlanEnd[1]));
                        ymax = Math.Max(ymax, Math.Max(m.PlanStart[1], m.PlanEnd[1]));
                    }
                }

                for (int i = 0; i < xs.Length; i++)
                {
                    double spacing = i > 0 ? Math.Round(xs[i] - xs[i - 1], 1) : 0;
                    _grids.Add(new FloorGridLine
                    {
                        Name = grid.Name ?? "",
                        Label = i < lx.Length ? lx[i] : "X" + (i + 1),
                        StartPoint = new[] { R(xs[i]), R(ymin), 0 },
                        EndPoint = new[] { R(xs[i]), R(ymax), 0 },
                        PlanStart = new[] { R(xs[i]), R(ymin) },
                        PlanEnd = new[] { R(xs[i]), R(ymax) },
                        ModelObjectId = grid.Identifier.ID,
                        ModelObjectGuid = grid.Identifier.GUID.ToString(),
                        Spacing = spacing,
                    });
                }
                for (int i = 0; i < ys.Length; i++)
                {
                    double spacing = i > 0 ? Math.Round(ys[i] - ys[i - 1], 1) : 0;
                    _grids.Add(new FloorGridLine
                    {
                        Name = grid.Name ?? "",
                        Label = i < ly.Length ? ly[i] : "Y" + (i + 1),
                        StartPoint = new[] { R(xmin), R(ys[i]), 0 },
                        EndPoint = new[] { R(xmax), R(ys[i]), 0 },
                        PlanStart = new[] { R(xmin), R(ys[i]) },
                        PlanEnd = new[] { R(xmax), R(ys[i]) },
                        ModelObjectId = grid.Identifier.ID,
                        ModelObjectGuid = grid.Identifier.GUID.ToString(),
                        Spacing = spacing,
                    });
                }
            }
        }

        private void AttachGaGeometry(FloorWiseSheet sheet)
        {
            if (!string.Equals(sheet.Type, "GA", StringComparison.OrdinalIgnoreCase))
                return;
            if (_membersByFloor == null)
                return;

            if (_membersByFloor.TryGetValue(sheet.Floor ?? "", out var members))
                sheet.Members = members;
            else
            {
                string band = FloorBand(sheet.FloorZmm);
                if (_membersByFloor.TryGetValue(band, out members))
                    sheet.Members = members;
            }

            sheet.GridLines = _grids ?? new List<FloorGridLine>();
            sheet.MemberCount = sheet.Members.Count;

            if (sheet.Members.Count > 0)
            {
                double minX = sheet.Members.Min(m => Math.Min(m.PlanStart?[0] ?? 0, m.PlanEnd?[0] ?? 0));
                double minY = sheet.Members.Min(m => Math.Min(m.PlanStart?[1] ?? 0, m.PlanEnd?[1] ?? 0));
                double maxX = sheet.Members.Max(m => Math.Max(m.PlanStart?[0] ?? 0, m.PlanEnd?[0] ?? 0));
                double maxY = sheet.Members.Max(m => Math.Max(m.PlanStart?[1] ?? 0, m.PlanEnd?[1] ?? 0));
                sheet.PlanBboxMin = new[] { R(minX), R(minY) };
                sheet.PlanBboxMax = new[] { R(maxX), R(maxY) };
                var zs = sheet.Members.Select(m => m.Zcenter).OrderBy(z => z).ToList();
                sheet.FloorZmm = Math.Round(zs[zs.Count / 2], 1);
            }

            bool viewsEmpty = sheet.Views == null || sheet.Views.Count == 0
                || sheet.Views.All(v => v.Objects == null || v.Objects.Count == 0);
            if (viewsEmpty)
                SynthesizePlanView(sheet);

            FillIdentity(sheet);
        }

        private static void SynthesizePlanView(FloorWiseSheet sheet)
        {
            var view = new FloorWiseView
            {
                Name = (sheet.Floor ?? "floor") + " plan (from 3D model)",
                ViewType = "Plan",
                Scale = 100,
                RestrictionBoxMin = sheet.PlanBboxMin,
                RestrictionBoxMax = sheet.PlanBboxMax,
            };

            foreach (var g in sheet.GridLines)
            {
                view.Objects.Add(new FloorWiseObject
                {
                    Kind = "GridLine",
                    TypeName = "Grid",
                    Text = g.Label,
                    StartPoint = g.PlanStart,
                    EndPoint = g.PlanEnd,
                    ModelObjectId = g.ModelObjectId,
                    ModelObjectGuid = g.ModelObjectGuid,
                    Extra = Extra("Spacing", g.Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                });
            }

            foreach (var m in sheet.Members)
            {
                view.Objects.Add(new FloorWiseObject
                {
                    Kind = "Part",
                    TypeName = m.TeklaType,
                    Text = (m.PartMark + " " + m.Profile).Trim(),
                    StartPoint = m.PlanStart,
                    EndPoint = m.PlanEnd,
                    ModelObjectId = m.Id,
                    ModelObjectGuid = m.Guid,
                    Extra = Extra("Profile", m.Profile),
                });
            }

            if (sheet.SheetObjects == null || sheet.SheetObjects.Count == 0)
            {
                sheet.SheetObjects = new List<FloorWiseObject>
                {
                    new FloorWiseObject
                    {
                        Kind = "Text",
                        TypeName = "Title",
                        Text = sheet.Name,
                        InsertionPoint = new[] { 20.0, 20.0 },
                    }
                };
            }

            sheet.Views = new List<FloorWiseView> { view };
        }

        private static double[] ParseCoords(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new double[0];
            return raw.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s =>
                {
                    double.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v);
                    return v;
                })
                .ToArray();
        }

        private static string[] ParseLabels(string raw, int count)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Enumerable.Range(1, Math.Max(0, count)).Select(i => i.ToString()).ToArray();
            var labels = raw.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length >= count) return labels;
            var padded = new string[count];
            for (int i = 0; i < count; i++)
                padded[i] = i < labels.Length ? labels[i] : (i + 1).ToString();
            return padded;
        }

        private static string SafeClass(TSModel.Part part)
        {
            try { return part.Class ?? ""; } catch { return ""; }
        }

        private static bool MatchesType(Drawing drawing, string typeFilter)
        {
            if (string.IsNullOrWhiteSpace(typeFilter)) return true;
            string want = typeFilter.Trim();
            if (IsGaFilter(want)) return drawing is GADrawing;
            string actual = Classify(drawing);
            return actual.Equals(want, StringComparison.OrdinalIgnoreCase);
        }

        internal static string FloorBand(double zMm)
        {
            if (zMm < 500) return "Grade / on-grade (~0 m)";
            if (zMm < 4000) return "Level 1 / podium";
            if (zMm < 8000) return "Level 2";
            if (zMm < 12000) return "Level 3";
            if (zMm < 16000) return "Level 4";
            if (zMm < 20000) return "Level 5";
            if (zMm < 24000) return "Level 6";
            if (zMm < 28000) return "Level 7";
            if (zMm < 32000) return "Level 8";
            return "Upper / roof";
        }

        // =====================================================================
        // Overlaps (CatC data)
        // =====================================================================
        private static List<OverlapHit> FindOverlaps(List<FloorWiseObject> objects)
        {
            var hits = new List<OverlapHit>();
            if (objects == null || objects.Count < 2) return hits;

            bool Label(FloorWiseObject o)
            {
                return o.Kind == "Text" || o.Kind == "Mark" || o.Kind == "LevelMark"
                    || o.Kind == "DetailMark" || o.Kind == "SectionMark"
                    || (o.Kind != null && o.Kind.IndexOf("Dimension", StringComparison.Ordinal) >= 0);
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (!Label(objects[i]) || objects[i].BoundingBoxMin == null || objects[i].BoundingBoxMax == null)
                    continue;
                for (int j = i + 1; j < objects.Count; j++)
                {
                    if (!Label(objects[j]) || objects[j].BoundingBoxMin == null || objects[j].BoundingBoxMax == null)
                        continue;
                    if (!Intersects(objects[i], objects[j])) continue;
                    hits.Add(new OverlapHit
                    {
                        IndexA = i,
                        IndexB = j,
                        KindA = objects[i].Kind,
                        KindB = objects[j].Kind,
                        TextA = objects[i].Text ?? "",
                        TextB = objects[j].Text ?? "",
                    });
                    if (hits.Count >= 200) return hits;
                }
            }
            return hits;
        }

        private static bool Intersects(FloorWiseObject a, FloorWiseObject b)
        {
            return a.BoundingBoxMin[0] < b.BoundingBoxMax[0]
                && a.BoundingBoxMax[0] > b.BoundingBoxMin[0]
                && a.BoundingBoxMin[1] < b.BoundingBoxMax[1]
                && a.BoundingBoxMax[1] > b.BoundingBoxMin[1];
        }

        // =====================================================================
        // output
        // =====================================================================
        private void WriteOutputs(FloorWiseMeta meta, Dictionary<string, FloorBucket> floors)
        {
            var root = BuildRoot(meta, floors);
            string combined = Path.Combine(_exportRoot, "floor_wise.json");
            File.WriteAllText(combined, root.ToString(Formatting.Indented), Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(_exportRoot, "floor_wise_meta.json"),
                JsonConvert.SerializeObject(meta, Formatting.Indented),
                Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine("Floor-wise drawing extract");
            sb.AppendLine($"Model: {meta.ModelName}");
            sb.AppendLine($"Generated: {meta.GeneratedUtc}");
            sb.AppendLine($"Drawings: {meta.DrawingCount}  PDFs: {meta.PdfCount}");
            sb.AppendLine();
            foreach (var kv in floors.OrderBy(k => k.Value.ElevationZmm).ThenBy(k => k.Key))
            {
                sb.AppendLine($"{kv.Key}  source={kv.Value.Source}  Z={kv.Value.ElevationZmm:0} mm  count={kv.Value.DrawingCount}");
                foreach (var d in kv.Value.Drawings)
                {
                    sb.AppendLine($"    {d.Identity}");
                    sb.AppendLine($"        PDF  {d.PdfPath}");
                    sb.AppendLine($"        JSON {d.JsonPath}");
                    sb.AppendLine($"        TXT  {d.ReadablePath}");
                }
            }
            File.WriteAllText(Path.Combine(_exportRoot, "floor_wise.txt"), sb.ToString(), Encoding.UTF8);

            var catalog = floors.SelectMany(kv => kv.Value.Drawings.Select(d => new
            {
                Floor = kv.Key,
                d.Type,
                d.Mark,
                d.PartMark,
                d.AssemblyMark,
                d.CastUnitMark,
                d.Profile,
                d.Identity,
                d.PdfPath,
                d.JsonPath,
                d.ReadablePath,
            })).ToList();
            File.WriteAllText(
                Path.Combine(_exportRoot, "CATALOG.json"),
                JsonConvert.SerializeObject(catalog, Formatting.Indented),
                Encoding.UTF8);

            Console.WriteLine($"[FloorWise] combined JSON → {combined}");
            Console.WriteLine($"[FloorWise] PDFs → {_exportRoot}\\<floor>\\");
            Console.WriteLine(sb.ToString());
        }

        private static JObject BuildRoot(FloorWiseMeta meta, Dictionary<string, FloorBucket> floors)
        {
            var root = new JObject();
            var settings = JsonSettings();
            root["_meta"] = JObject.FromObject(meta, JsonSerializer.Create(settings));
            foreach (var kv in floors.OrderBy(k => k.Value.ElevationZmm).ThenBy(k => k.Key))
                root[kv.Key] = JObject.FromObject(kv.Value, JsonSerializer.Create(settings));
            return root;
        }

        private static JsonSerializerSettings JsonSettings()
        {
            return new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            };
        }

        // =====================================================================
        // helpers
        // =====================================================================
        private static void FillDirectLink(Drawing drawing, FloorWiseSheet dump)
        {
            try
            {
                if (drawing is SinglePartDrawing spd && spd.PartIdentifier != null)
                {
                    dump.LinkedModelObject = "Part";
                    dump.LinkedModelId = spd.PartIdentifier.ID;
                    dump.LinkedModelGuid = spd.PartIdentifier.GUID.ToString();
                }
                else if (drawing is AssemblyDrawing ad && ad.AssemblyIdentifier != null)
                {
                    dump.LinkedModelObject = "Assembly";
                    dump.LinkedModelId = ad.AssemblyIdentifier.ID;
                    dump.LinkedModelGuid = ad.AssemblyIdentifier.GUID.ToString();
                }
                else if (drawing is CastUnitDrawing cud && cud.CastUnitIdentifier != null)
                {
                    dump.LinkedModelObject = "CastUnit";
                    dump.LinkedModelId = cud.CastUnitIdentifier.ID;
                    dump.LinkedModelGuid = cud.CastUnitIdentifier.GUID.ToString();
                }
                else if (drawing is GADrawing)
                    dump.LinkedModelObject = "GA";
                else if (drawing is MultiDrawing)
                    dump.LinkedModelObject = "Multi";
            }
            catch { /* optional */ }
        }

        private static string Classify(Drawing drawing)
        {
            if (drawing is SinglePartDrawing) return "SinglePart";
            if (drawing is AssemblyDrawing) return "Assembly";
            if (drawing is CastUnitDrawing) return "CastUnit";
            if (drawing is GADrawing) return "GA";
            if (drawing is MultiDrawing) return "Multi";
            return drawing.GetType().Name;
        }

        private static string KindOf(DrawingObject obj)
        {
            if (obj is TSDrawing.Text) return "Text";
            if (obj is DetailMark) return "DetailMark";
            if (obj is SectionMark) return "SectionMark";
            if (obj is LevelMark) return "LevelMark";
            if (obj is MarkBase) return "Mark";
            if (obj is StraightDimension || obj is StraightDimensionSet) return "StraightDimension";
            if (obj is AngleDimension) return "AngleDimension";
            if (obj is RadiusDimension) return "RadiusDimension";
            if (obj is CurvedDimensionBase) return "CurvedDimension";
            if (obj is TSDrawing.Line) return "Line";
            if (obj is TSDrawing.Arc) return "Arc";
            if (obj is Circle) return "Circle";
            if (obj is TSDrawing.Rectangle) return "Rectangle";
            if (obj is RectangularCloud) return "RectangularCloud";
            if (obj is Cloud) return "Cloud";
            if (obj is Polyline) return "Polyline";
            if (obj is Polygon) return "Polygon";
            if (obj is TSDrawing.Part) return "Part";
            if (obj is TSDrawing.Bolt) return "Bolt";
            if (obj is TSDrawing.Weld) return "Weld";
            if (obj is ReinforcementBase) return "Reinforcement";
            if (obj is Grid) return "Grid";
            if (obj is View) return "View";
            return obj.GetType().Name;
        }

        private static void FillHideable(DrawingObject obj, FloorWiseObject rec)
        {
            try
            {
                var prop = obj.GetType().GetProperty("Hideable");
                var hide = prop?.GetValue(obj, null) as Hideable;
                if (hide == null) return;
                rec.Hidden = hide.IsHidden;
                rec.HiddenFlags = HiddenFlagsField?.GetValue(hide)?.ToString();
                rec.ShouldBeHiddenFlags = ShouldHideField?.GetValue(hide)?.ToString();
            }
            catch { /* optional */ }
        }

        private static void FillAabb(DrawingObject obj, FloorWiseObject rec)
        {
            try
            {
                if (obj is IAxisAlignedBoundingBox aabb)
                {
                    var box = aabb.GetAxisAlignedBoundingBox();
                    if (box == null) return;
                    rec.BoundingBoxMin = Xyz(box.MinPoint);
                    rec.BoundingBoxMax = Xyz(box.MaxPoint);
                }
            }
            catch { /* many drawing objects do not implement AABB */ }
        }

        private static void GuessBox(FloorWiseObject rec, double[] a, double[] b)
        {
            if (rec.BoundingBoxMin != null || a == null || b == null || a.Length < 2 || b.Length < 2)
                return;
            rec.BoundingBoxMin = new[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), 0 };
            rec.BoundingBoxMax = new[] { Math.Max(a[0], b[0]), Math.Max(a[1], b[1]), 0 };
        }

        private static void FillModelId(FloorWiseObject rec, Identifier id)
        {
            if (id == null) return;
            rec.ModelObjectId = id.ID;
            rec.ModelObjectGuid = id.GUID.ToString();
        }

        private static string FlattenRelated(DrawingObject obj)
        {
            try
            {
                var en = obj.GetRelatedObjects();
                var sb = new StringBuilder();
                while (en != null && en.MoveNext())
                {
                    if (en.Current is TSDrawing.Text t)
                        sb.Append(t.TextString);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string FlattenContainer(IEnumerable container)
        {
            if (container == null) return "";
            var sb = new StringBuilder();
            foreach (var el in container)
            {
                if (el is TextElement te) sb.Append(te.Value);
                else if (el is PropertyElement pe) sb.Append(string.IsNullOrEmpty(pe.Value) ? pe.Name : pe.Value);
                else if (el is ContainerElement ce) sb.Append(FlattenContainer(ce));
                else if (el != null) sb.Append(el);
            }
            return sb.ToString();
        }

        private static List<double[]> PointsOf(IEnumerable points)
        {
            var list = new List<double[]>();
            if (points == null) return list;
            foreach (var p in points)
            {
                if (p is Point pt) list.Add(Xyz(pt));
            }
            return list;
        }

        private static Dictionary<string, string> Extra(string k, string v)
        {
            return string.IsNullOrEmpty(v) ? null : new Dictionary<string, string> { { k, v } };
        }

        private static string Report(TSModel.ModelObject obj, string name)
        {
            string v = "";
            try { obj.GetReportProperty(name, ref v); } catch { }
            return v ?? "";
        }

        private static double[] Xyz(Point p)
        {
            if (p == null) return null;
            return new[] { R(p.X), R(p.Y), R(p.Z) };
        }

        private static double[] Xyz(Vector v)
        {
            if (v == null) return null;
            return new[] { R(v.X), R(v.Y), R(v.Z) };
        }

        private static string Format(Vector v)
        {
            if (v == null) return "";
            return $"{R(v.X)},{R(v.Y)},{R(v.Z)}";
        }

        private static double R(double v, int d = 4)
        {
            return Math.Round(v, d);
        }

        private static string SafeMark(Drawing drawing)
        {
            try { return drawing.Mark ?? drawing.Name ?? drawing.GetType().Name; }
            catch { return "drawing"; }
        }

        private static string SafeFile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "drawing";
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }

        private static string IdentityStem(FloorWiseSheet s)
        {
            string owner = FirstNonEmpty(s.CastUnitMark, s.AssemblyMark, StripBrackets(s.Mark));
            string part = string.IsNullOrWhiteSpace(s.PartMark) ? "part-unknown" : "part-" + s.PartMark;
            string mark = SafeFile(string.IsNullOrWhiteSpace(s.Mark) ? s.Name : s.Mark);
            return SafeFile($"{s.Type}__{owner}__{part}__{mark}");
        }

        private static string StripBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            return s.Trim().TrimStart('[').TrimEnd(']');
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return "unknown";
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int n = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, stem + "_" + n + ext);
                n++;
            } while (File.Exists(candidate));
            return candidate;
        }

        private static string BuildReadable(FloorWiseSheet s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine("TEKLA 2D DRAWING — WHO IS THIS SHEET FOR?");
            sb.AppendLine("============================================================");
            sb.AppendLine(s.Identity);
            sb.AppendLine();
            sb.AppendLine($"Floor           : {s.Floor}   (source={s.FloorSource}, Z={s.FloorZmm:0} mm)");
            sb.AppendLine($"Drawing type    : {s.Type}  ({s.TeklaDrawingType})");
            sb.AppendLine($"Drawing mark    : {s.Mark}");
            sb.AppendLine($"Part mark       : {s.PartMark}");
            sb.AppendLine($"Assembly mark   : {s.AssemblyMark}");
            sb.AppendLine($"Cast unit mark  : {s.CastUnitMark}");
            sb.AppendLine($"Profile         : {s.Profile}");
            sb.AppendLine($"Linked 3D       : {s.LinkedModelObject} id={s.LinkedModelId} guid={s.LinkedModelGuid}");
            sb.AppendLine($"Titles          : {s.Title1} | {s.Title2} | {s.Title3}");
            sb.AppendLine($"UpToDate        : {s.UpToDateStatus}");
            sb.AppendLine($"Sheet mm        : {s.SheetWidth} x {s.SheetHeight}");
            sb.AppendLine($"PDF             : {s.PdfPath}");
            sb.AppendLine($"JSON            : {s.JsonPath}");
            sb.AppendLine();
            sb.AppendLine("LINKED 3D PARTS / ASSEMBLIES");
            foreach (var p in s.Linked3d)
            {
                sb.AppendLine(
                    $"  id={p.Id}  part={p.Mark}  asm={p.AssemblyMark}  cu={p.CastUnitMark}  " +
                    $"profile={p.Profile}  Z={p.Zmin:0}..{p.Zmax:0} mm  guid={p.Guid}");
            }
            sb.AppendLine();
            sb.AppendLine($"VIEWS ({s.Views.Count})");
            foreach (var v in s.Views)
            {
                sb.AppendLine($"  • {v.ViewType}  name='{v.Name}'  scale=1:{v.Scale}  size={v.Width}x{v.Height}  overlaps={v.OverlapCount}");
                sb.AppendLine($"      viewCS origin=({Join(v.ViewCsOrigin)})  X=({Join(v.ViewCsX)})  Y=({Join(v.ViewCsY)})");
                sb.AppendLine($"      restriction min=({Join(v.RestrictionBoxMin)}) max=({Join(v.RestrictionBoxMax)})");

                var dims = v.Objects.Where(o => o.Kind != null && o.Kind.IndexOf("Dimension", StringComparison.Ordinal) >= 0).ToList();
                var texts = v.Objects.Where(o => o.Kind == "Text" || o.Kind == "Mark" || o.Kind == "DetailMark" || o.Kind == "LevelMark").ToList();
                var parts = v.Objects.Where(o => o.Kind == "Part" || o.Kind == "Bolt" || o.Kind == "Weld").ToList();
                sb.AppendLine($"      objects={v.Objects.Count}  dims={dims.Count}  texts/marks={texts.Count}  parts/bolts/welds={parts.Count}");
                foreach (var d in dims.Take(80))
                {
                    sb.AppendLine($"        DIM {d.Kind} '{d.Text}' dist={d.Distance} start=({Join(d.StartPoint)}) end=({Join(d.EndPoint)})");
                }
                foreach (var t in texts.Take(80))
                    sb.AppendLine($"        {t.Kind} '{t.Text}' @ ({Join(t.InsertionPoint ?? t.LabelPoint)}) hidden={t.Hidden}");
                foreach (var p in parts.Take(40))
                    sb.AppendLine($"        {p.Kind} 3D id={p.ModelObjectId} guid={p.ModelObjectGuid} hidden={p.Hidden}");
            }
            if (!string.IsNullOrEmpty(s.Error))
            {
                sb.AppendLine();
                sb.AppendLine("ERROR: " + s.Error);
            }
            return sb.ToString();
        }

        private static string Join(double[] xyz)
        {
            if (xyz == null || xyz.Length == 0) return "";
            return string.Join(", ", xyz.Select(v => v.ToString("0.###")));
        }

        private static string SafeFolder(string s)
        {
            s = SafeFile(s).Replace(' ', '_').Replace('/', '_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
        }
    }
}

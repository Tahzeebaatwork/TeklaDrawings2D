// CastUnitShopDrawingExtractor.cs
// Precast Piece Ticket / Cast Unit Fabrication Drawing extractor.
//
// Bounding boxes are metadata only. Drawable geometry comes from:
//   1. Every Line / Polyline / Polygon / Arc / Circle on the sheet (already 2D)
//   2. Visible solid FACE LOOPS of each linked 3D part, projected
//      model → view CS → sheet  (exact outer + inner contours)
//   3. Boolean cuts as independent openings
//   4. Rebar geometries, bolts, welds, embeds
//
// One JSON + SVG + DXF + PDF + TXT + validation report per Cast Unit drawing.

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
using Tekla.Structures.Solid;
using TSDrawing = Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;
using TSSolid = Tekla.Structures.Model.Solid;

namespace TeklaExtractor.Services
{
    public class CastUnitShopDrawingExtractor
    {
        public const int MaxEdgesPerPart = 400;
        public const int MaxFaceLoops = 80;

        private static readonly string[] FloorUdaNames =
        {
            "FLOOR_LEVEL", "FLOOR", "FLOOR_NAME", "STOREY", "STORY", "LEVEL", "FLOORLEVEL"
        };

        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;
        private readonly DrawingGenerator _printer;
        private readonly string _outputRoot;
        private readonly bool _upToDateOnly;
        private readonly bool _failOnEmpty;

        public CastUnitShopDrawingExtractor(
            TSModel.Model model,
            string outputRoot,
            bool upToDateOnly = true,
            bool failOnEmpty = true)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
            _printer = new DrawingGenerator();
            _outputRoot = outputRoot ?? Path.Combine("Export", "CastUnitDrawings");
            _upToDateOnly = upToDateOnly;
            _failOnEmpty = failOnEmpty;
        }

        public ShopDrawingIndex Run()
        {
            Directory.CreateDirectory(_outputRoot);
            var index = new ShopDrawingIndex
            {
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
                OutputRoot = _outputRoot,
            };
            try { index.ModelName = _model.GetInfo()?.ModelName ?? ""; } catch { /* optional */ }

            if (_handler == null || !_handler.GetConnectionStatus())
            {
                Console.WriteLine("[Shop] DrawingHandler not connected.");
                return index;
            }

            var drawings = new List<CastUnitDrawing>();
            var en = _handler.GetDrawings();
            while (en != null && en.MoveNext())
            {
                if (en.Current is CastUnitDrawing cu)
                {
                    if (_upToDateOnly &&
                        cu.UpToDateStatus != DrawingUpToDateStatus.DrawingIsUpToDate)
                        continue;
                    drawings.Add(cu);
                }
            }

            Console.WriteLine($"[Shop] {drawings.Count} up-to-date Cast Unit drawings (piece tickets).");
            int n = 0;
            foreach (var drawing in drawings)
            {
                n++;
                try
                {
                    var shop = ExtractOne(drawing);
                    ShopDrawingExport.WriteAll(shop, _outputRoot);
                    bool passed = shop.Validation != null && shop.Validation.Passed;
                    index.Drawings.Add(new ShopDrawingIndexItem
                    {
                        Mark = shop.DrawingMark,
                        CastUnitMark = shop.CastUnitMark,
                        Passed = passed,
                        Status = shop.Validation?.Status ?? "",
                        JsonPath = shop.Outputs.JsonPath,
                        SvgPath = shop.Outputs.SvgPath,
                        HtmlPath = shop.Outputs.HtmlPath,
                        PdfPath = shop.Outputs.PdfPath,
                        Views = shop.Sheet.Views.Count,
                        PartContours = shop.Validation?.PartContourCount ?? 0,
                        Graphics = shop.Validation?.GraphicObjectCount ?? 0,
                    });
                    if (passed) index.Passed++; else index.Failed++;
                    Console.WriteLine(
                        $"[Shop] {n}/{drawings.Count}  {shop.DrawingMark}  " +
                        $"views={shop.Sheet.Views.Count}  contours={shop.Validation?.PartContourCount}  " +
                        $"graphics={shop.Validation?.GraphicObjectCount}  {shop.Validation?.Status}");
                }
                catch (Exception ex)
                {
                    index.Failed++;
                    Console.WriteLine($"[Shop] '{drawing.Mark}' failed: {ex.Message}");
                }
            }

            index.DrawingCount = drawings.Count;
            File.WriteAllText(
                Path.Combine(_outputRoot, "index.json"),
                JsonConvert.SerializeObject(index, Formatting.Indented),
                Encoding.UTF8);
            Console.WriteLine($"[Shop] done  passed={index.Passed}  failed={index.Failed}  → {_outputRoot}");
            return index;
        }

        private ShopDrawing ExtractOne(CastUnitDrawing drawing)
        {
            var info = _model.GetInfo();
            var shop = new ShopDrawing
            {
                ModelName = info?.ModelName ?? "",
                ModelPath = info?.ModelPath ?? "",
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
                DrawingType = "CastUnit",
                TeklaDrawingType = drawing.DrawingTypeStr ?? "C",
                DrawingMark = drawing.Mark ?? "",
                DrawingName = drawing.Name ?? "",
                Title1 = drawing.Title1 ?? "",
                Title2 = drawing.Title2 ?? "",
                Title3 = drawing.Title3 ?? "",
                UpToDateStatus = drawing.UpToDateStatus.ToString(),
                CreationDate = drawing.CreationDate,
                ModificationDate = drawing.ModificationDate,
            };

            try { shop.IsLocked = drawing.IsLocked; } catch { /* optional */ }
            try { shop.IsIssued = drawing.IsIssued; } catch { /* optional */ }
            try { shop.IsFrozen = drawing.IsFrozen; } catch { /* optional */ }
            try { shop.IssuingDate = drawing.IssuingDate; } catch { /* optional */ }

            FillProject(shop);
            shop.UDAs = ReadStringUdas(drawing);
            shop.Revision = FirstNonEmpty(
                Lookup(shop.UDAs, "REVISION", "REVISION_MARK", "REV"),
                PlotRevision(drawing));
            shop.FloorLevel = FirstNonEmpty(
                Lookup(shop.UDAs, FloorUdaNames),
                FloorWiseDrawingExtractor.InferFloorFromText(
                    shop.DrawingName + " " + shop.Title1 + " " + shop.Title2 + " " + shop.Title3 + " " + shop.DrawingMark));

            if (drawing.CastUnitIdentifier != null)
            {
                shop.LinkedCastUnitId = drawing.CastUnitIdentifier.ID;
                shop.LinkedCastUnitGuid = drawing.CastUnitIdentifier.GUID.ToString();
            }

            TSModel.Assembly cu = null;
            if (shop.LinkedCastUnitId != 0)
            {
                try { cu = _model.SelectModelObject(drawing.CastUnitIdentifier) as TSModel.Assembly; }
                catch { /* optional */ }
            }
            if (cu != null)
            {
                shop.CastUnitMark = FirstNonEmpty(
                    Report(cu, "CAST_UNIT_POS"),
                    Report(cu, "ASSEMBLY_POS"));
                if (string.IsNullOrWhiteSpace(shop.FloorLevel))
                {
                    var main = cu.GetMainPart() as TSModel.ModelObject;
                    shop.FloorLevel = ReadFloor(cu) ?? ReadFloor(main) ?? "";
                }
            }

            shop.Sheet.TitleBlock = new ShopTitleBlock
            {
                PieceMark = FirstNonEmpty(shop.CastUnitMark, shop.DrawingMark),
                Title = FirstNonEmpty(shop.Title1, shop.DrawingName),
                Title1 = shop.Title1,
                Title2 = shop.Title2,
                Title3 = shop.Title3,
                Revision = shop.Revision,
                FloorLevel = shop.FloorLevel,
                ProjectName = shop.ProjectName,
                ProjectNumber = shop.ProjectNumber,
                DrawingStatus = shop.UpToDateStatus,
            };

            shop.Sheet.Tables.Add(BuildBomTable(cu, drawing));

            bool opened = false;
            try
            {
                try { _handler.UpdateDrawing(drawing); } catch { /* optional */ }
                try { opened = _handler.SetActiveDrawing(drawing, false); }
                catch { opened = false; }

                var container = drawing.GetSheet();
                if (container != null)
                {
                    shop.Sheet.Width = R(container.Width);
                    shop.Sheet.Height = R(container.Height);
                    try
                    {
                        var size = drawing.Layout?.SheetSize;
                        if (size != null)
                        {
                            shop.Sheet.Width = R(size.Width);
                            shop.Sheet.Height = R(size.Height);
                        }
                    }
                    catch { /* optional */ }

                    CollectSheetAnnotations(container, shop);
                    shop.Sheet.Views = ReadViews(container, cu, shop);
                }
            }
            finally
            {
                if (opened)
                {
                    try { _handler.CloseActiveDrawing(false); } catch { /* best effort */ }
                }
            }

            if (shop.Sheet.Views.Count > 0)
                shop.DrawingScale = shop.Sheet.Views[0].Scale;

            string stem = SafeFileName(FirstNonEmpty(shop.CastUnitMark, shop.DrawingMark, "cu"));
            shop.Outputs.PdfPath = Path.Combine(_outputRoot, stem, stem + ".pdf");
            try { _printer.ExportToPdf(drawing, shop.Outputs.PdfPath); }
            catch (Exception ex) { Console.WriteLine("[Shop] PDF: " + ex.Message); }

            return shop;
        }

        private static string SafeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "drawing";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace(' ', '_').Replace('[', '_').Replace(']', '_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s.Trim('_');
        }

        private List<ShopView> ReadViews(ContainerView sheet, TSModel.Assembly cu, ShopDrawing shop)
        {
            var list = new List<ShopView>();
            DrawingObjectEnumerator views = null;
            try { views = sheet.GetAllViews(); }
            catch { try { views = sheet.GetViews(); } catch { return list; } }
            if (views == null) return list;

            while (views.MoveNext())
            {
                if (views.Current is View view)
                {
                    try { list.Add(ReadView(view, cu, shop)); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Shop] view '{view.Name}': {ex.Message}");
                    }
                }
            }
            return list;
        }

        private ShopView ReadView(View view, TSModel.Assembly cu, ShopDrawing shop)
        {
            var rec = new ShopView
            {
                Name = view.Name ?? "",
                ViewType = view.ViewType.ToString(),
                Width = R(view.Width),
                Height = R(view.Height),
                Origin = Xyz(view.Origin),
                FrameOrigin = Xyz(view.FrameOrigin),
                SectionIdentifier = InferSectionId(view.Name),
            };
            try { rec.Scale = R(view.Attributes.Scale, 4); } catch { rec.Scale = 1; }
            if (rec.Scale < 0.01) rec.Scale = 1;

            try
            {
                var box = view.GetAxisAlignedBoundingBox();
                rec.BoundingBoxMin = Xyz(box.MinPoint);
                rec.BoundingBoxMax = Xyz(box.MaxPoint);
            }
            catch { /* optional */ }

            try
            {
                rec.RestrictionBoxMin = Xyz(view.RestrictionBox.MinPoint);
                rec.RestrictionBoxMax = Xyz(view.RestrictionBox.MaxPoint);
            }
            catch { /* optional */ }

            var xform = ViewXform.From(view, rec.Scale);
            rec.CoordinateSystems = xform.ToRecord();

            try
            {
                if (view.ViewType == View.ViewTypes.SectionView)
                {
                    rec.SectionCutStart = rec.RestrictionBoxMin;
                    rec.SectionCutEnd = rec.RestrictionBoxMax;
                    rec.SectionDirection = rec.CoordinateSystems.LocalZ;
                    if (rec.RestrictionBoxMin != null && rec.RestrictionBoxMax != null)
                        rec.SectionDepth = Math.Abs(rec.RestrictionBoxMax[2] - rec.RestrictionBoxMin[2]);
                }
            }
            catch { /* optional */ }

            DrawingObjectEnumerator en = null;
            try { en = view.GetAllObjects(); }
            catch { try { en = view.GetObjects(); } catch { /* none */ } }

            var seenDim = new HashSet<string>();
            if (en != null)
            {
                while (en.MoveNext())
                {
                    var obj = en.Current;
                    if (obj == null) continue;
                    try { ClassifyDrawingObject(obj, rec, xform, seenDim); }
                    catch { /* skip one */ }
                }
            }

            // Exact visible 2D contours from the live 3D cast unit, projected
            // into THIS view. Drawing.Part objects rarely expose polygons.
            if (cu != null)
                ProjectCastUnitIntoView(cu, rec, xform, shop);

            rec.DrawableCount =
                rec.GraphicObjects.Count
                + rec.Parts.Count(p => HasContour(p.OuterContour))
                + rec.Openings.Count(o => HasContour(o.OuterContour))
                + rec.Reinforcement.Count(r => r.Polyline2dSheet != null && r.Polyline2dSheet.Count > 1);
            return rec;
        }

        private void ClassifyDrawingObject(DrawingObject obj, ShopView rec, ViewXform xform, HashSet<string> seenDim)
        {
            if (obj is StraightDimensionSet set)
            {
                var kids = set.GetObjects();
                while (kids != null && kids.MoveNext())
                {
                    if (kids.Current is StraightDimension dim)
                    {
                        var row = ReadStraightDim(dim, set, rec.Name, xform, seenDim);
                        if (row != null) rec.Dimensions.Add(row);
                    }
                }
            }
            else if (obj is StraightDimension sdim)
            {
                var row = ReadStraightDim(sdim, sdim.GetDimensionSet() as StraightDimensionSet, rec.Name, xform, seenDim);
                if (row != null) rec.Dimensions.Add(row);
            }
            else if (obj is TSDrawing.Text text)
            {
                rec.Texts.Add(new ShopText
                {
                    Text = text.TextString ?? "",
                    InsertionPoint = Xyz(text.InsertionPoint),
                    InsertionSheet = Xyz(text.InsertionPoint),
                    FontSize = Safe(() => text.Attributes.Font.Height),
                    Alignment = Safe(() => text.Attributes.Alignment.ToString()) ?? "",
                    Rotation = Safe(() => text.Attributes.Angle),
                });
            }
            else if (obj is MarkBase mark)
            {
                rec.Marks.Add(ReadMark(mark, xform));
            }
            else if (obj is SectionMark section)
            {
                rec.SectionSymbols.Add(new ShopSectionSymbol
                {
                    Identifier = FirstNonEmpty(FlattenRelated(section), rec.SectionIdentifier),
                    LeftPoint = Xyz(section.LeftPoint),
                    RightPoint = Xyz(section.RightPoint),
                });
            }
            else if (obj is DetailMark detail)
            {
                rec.Marks.Add(new ShopMark
                {
                    Kind = "DetailMark",
                    Text = FlattenRelated(detail),
                    InsertionPoint = Xyz(detail.LabelPoint),
                    InsertionSheet = Xyz(detail.LabelPoint),
                });
            }
            else if (obj is LevelMark level)
            {
                rec.Marks.Add(new ShopMark
                {
                    Kind = "LevelMark",
                    Text = level.SubType.ToString(),
                    InsertionPoint = Xyz(level.InsertionPoint),
                    InsertionSheet = Xyz(level.InsertionPoint),
                    AssociatedId = level.ModelObjectIdentifier != null ? level.ModelObjectIdentifier.ID : 0,
                });
            }
            else if (obj is TSDrawing.Line line)
            {
                rec.GraphicObjects.Add(Graphic("Line", false, xform, line.StartPoint, line.EndPoint));
            }
            else if (obj is Polyline poly)
            {
                rec.GraphicObjects.Add(Graphic("Polyline", false, xform, PointsOf(poly.Points)));
            }
            else if (obj is Polygon polygon)
            {
                rec.GraphicObjects.Add(Graphic("Polygon", true, xform, PointsOf(polygon.Points)));
            }
            else if (obj is TSDrawing.Arc arc)
            {
                var g = Graphic("Arc", false, xform, arc.StartPoint, arc.EndPoint);
                g.Radius = R(arc.Radius);
                rec.GraphicObjects.Add(g);
            }
            else if (obj is Circle circle)
            {
                var g = Graphic("Circle", true, xform, circle.CenterPoint);
                g.Radius = R(circle.Radius);
                rec.GraphicObjects.Add(g);
            }
            else if (obj is TSDrawing.Rectangle rect)
            {
                rec.GraphicObjects.Add(Graphic("Rectangle", true, xform, rect.StartPoint, rect.EndPoint));
            }
            else if (obj is TSDrawing.Part || obj is ReinforcementBase
                     || obj is TSDrawing.Bolt || obj is TSDrawing.Weld)
            {
                // Identity is filled from the 3D model below; keep a graphic hook.
            }
        }

        private void ProjectCastUnitIntoView(TSModel.Assembly cu, ShopView rec, ViewXform xform, ShopDrawing shop)
        {
            TSModel.Part main = null;
            try { main = cu.GetMainPart() as TSModel.Part; } catch { /* optional */ }

            var parts = new List<TSModel.Part>();
            if (main != null) parts.Add(main);
            try
            {
                var seconds = cu.GetSecondaries();
                if (seconds != null)
                {
                    foreach (var o in seconds)
                    {
                        if (o is TSModel.Part p) parts.Add(p);
                    }
                }
            }
            catch { /* optional */ }

            foreach (var part in parts)
            {
                try
                {
                    bool embed = main != null && part.Identifier.ID != main.Identifier.ID;
                    if (embed)
                        rec.Hardware.Add(ReadHardware(part, xform, shop.LinkedCastUnitGuid));
                    else
                        rec.Parts.Add(ReadConcretePart(part, xform, shop, rec));

                    ProjectBooleans(part, xform, shop.LinkedCastUnitGuid, rec);
                    ProjectRebars(part, xform, rec);
                    ProjectBolts(part, xform, rec);
                    ProjectWelds(part, xform, rec);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Shop] part {SafeId(part)}: {ex.Message}");
                }
            }
        }

        private ShopPart ReadConcretePart(TSModel.Part part, ViewXform xform, ShopDrawing shop, ShopView view)
        {
            var rec = new ShopPart
            {
                Id = part.Identifier.ID,
                Guid = part.Identifier.GUID.ToString(),
                PartMark = Report(part, "PART_POS"),
                AssemblyMark = Report(part, "ASSEMBLY_POS"),
                CastUnitMark = FirstNonEmpty(Report(part, "CAST_UNIT_POS"), shop.CastUnitMark),
                Name = FirstNonEmpty(Safe(() => part.Name), Report(part, "NAME")),
                Profile = FirstNonEmpty(Safe(() => part.Profile?.ProfileString), Report(part, "PROFILE")),
                Material = FirstNonEmpty(Safe(() => part.Material?.MaterialString), Report(part, "MATERIAL")),
                Class = Safe(() => part.Class) ?? "",
                PositionNumber = Report(part, "PART_POS"),
                Finish = Safe(() => part.Finish) ?? "",
                TeklaType = part.GetType().Name,
                Role = "Concrete",
                Length = R(ReportDouble(part, "LENGTH"), 1),
                Weight = R(ReportDouble(part, "WEIGHT"), 2),
            };

            FillContoursFromSolid(part, xform, rec);
            return rec;
        }

        /// <summary>
        /// Visible face loops of the solid, projected to view XY then sheet.
        /// Largest facing loop = outer contour; remaining closed loops on that
        /// face = holes / openings. Extra facing loops become hidden/return edges.
        /// </summary>
        private static void FillContoursFromSolid(TSModel.Part part, ViewXform xform, ShopPart rec)
        {
            TSSolid solid = null;
            try { solid = part.GetSolid(); } catch { /* optional */ }
            if (solid == null) return;

            try
            {
                rec.BoundingBoxMin = xform.ModelToSheet(solid.MinimumPoint);
                rec.BoundingBoxMax = xform.ModelToSheet(solid.MaximumPoint);
            }
            catch { /* optional */ }

            var facing = new List<ProjectedLoop>();
            try { CollectFacingLoops(solid, xform, facing); }
            catch { /* fall back to edges */ }

            if (facing.Count > 0)
            {
                facing.Sort((a, b) => b.Area.CompareTo(a.Area));
                rec.OuterContour = facing[0].ToContour("Outer");
                for (int i = 1; i < facing.Count && i < MaxFaceLoops; i++)
                {
                    var loop = facing[i];
                    if (loop.Closed && loop.Area > 100)
                        rec.InnerContours.Add(loop.ToContour(loop.Area < facing[0].Area * 0.85 ? "Opening" : "Return"));
                    else
                        rec.HiddenEdges.Add(loop.ToContour("Edge"));
                }
                return;
            }

            // Fallback: every solid edge as a drawable segment (still not a bbox).
            try
            {
                var edges = solid.GetEdgeEnumerator();
                int n = 0;
                while (edges != null && edges.MoveNext() && n < MaxEdgesPerPart)
                {
                    var edge = edges.Current as Edge;
                    if (edge == null) continue;
                    n++;
                    rec.HiddenEdges.Add(SegmentContour(xform, edge.StartPoint, edge.EndPoint, "Edge"));
                }
                if (rec.HiddenEdges.Count >= 3 && rec.OuterContour == null)
                    rec.OuterContour = ConvexOutline(rec.HiddenEdges);
            }
            catch { /* optional */ }
        }

        private static void CollectFacingLoops(TSSolid solid, ViewXform xform, List<ProjectedLoop> into)
        {
            FaceEnumerator faces = null;
            try { faces = solid.GetFaceEnumerator(); } catch { return; }
            if (faces == null) return;

            Vector viewZ = xform.ViewZ;
            while (faces.MoveNext() && into.Count < MaxFaceLoops)
            {
                var face = faces.Current as Face;
                if (face == null) continue;
                Vector n = null;
                try { n = face.Normal; } catch { continue; }
                if (n == null) continue;
                double dot = n.X * viewZ.X + n.Y * viewZ.Y + n.Z * viewZ.Z;
                if (dot < 0.15) continue; // back-face

                LoopEnumerator loops = null;
                try { loops = face.GetLoopEnumerator(); } catch { continue; }
                if (loops == null) continue;
                bool first = true;
                while (loops.MoveNext())
                {
                    var pts = ReadLoopPoints(loops.Current);
                    if (pts.Count < 3) continue;
                    var proj = new ProjectedLoop { Closed = true };
                    foreach (var p in pts)
                    {
                        proj.Model.Add(Xyz(p));
                        proj.View.Add(xform.ModelToView2(p));
                        proj.Sheet.Add(xform.ModelToSheet(p));
                    }
                    proj.ComputeArea();
                    proj.Kind = first ? "Outer" : "Inner";
                    first = false;
                    if (proj.Area > 1) into.Add(proj);
                }
            }
        }

        private static List<Point> ReadLoopPoints(object loopObj)
        {
            var pts = new List<Point>();
            if (loopObj == null) return pts;
            try
            {
                var loop = loopObj as Loop;
                if (loop == null) return pts;
                var verts = loop.GetVertexEnumerator();
                while (verts != null && verts.MoveNext())
                {
                    if (verts.Current is Point p) pts.Add(p);
                    else
                    {
                        var vt = verts.Current;
                        if (vt == null) continue;
                        var prop = vt.GetType().GetProperty("Point");
                        if (prop != null && prop.GetValue(vt, null) is Point qp)
                            pts.Add(qp);
                    }
                }
            }
            catch { /* optional */ }
            return pts;
        }

        private void ProjectBooleans(TSModel.Part part, ViewXform xform, string parentGuid, ShopView rec)
        {
            Tekla.Structures.Model.ModelObjectEnumerator cuts;
            try { cuts = part.GetBooleans(); } catch { return; }
            if (cuts == null) return;

            while (cuts.MoveNext())
            {
                try
                {
                    var opening = new ShopOpening
                    {
                        OpeningId = cuts.Current is TSModel.ModelObject mo ? mo.Identifier.ID : 0,
                        Guid = cuts.Current is TSModel.ModelObject mo2 ? mo2.Identifier.GUID.ToString() : "",
                        OpeningType = cuts.Current.GetType().Name,
                        ParentCastUnitGuid = parentGuid ?? "",
                        ThroughOpening = cuts.Current is TSModel.BooleanPart,
                    };

                    TSModel.Part operative = null;
                    if (cuts.Current is TSModel.BooleanPart bp)
                    {
                        try { operative = bp.OperativePart; } catch { /* optional */ }
                    }

                    if (operative != null)
                    {
                        opening.OpeningType = FirstNonEmpty(Safe(() => operative.Name), opening.OpeningType);
                        var solid = operative.GetSolid();
                        if (solid != null)
                        {
                            opening.CenterModel = Mid(solid.MinimumPoint, solid.MaximumPoint);
                            opening.CenterView = xform.ModelToView2(solid.MinimumPoint);
                            opening.CenterSheet = xform.ModelToSheet(
                                new Point(
                                    (solid.MinimumPoint.X + solid.MaximumPoint.X) / 2,
                                    (solid.MinimumPoint.Y + solid.MaximumPoint.Y) / 2,
                                    (solid.MinimumPoint.Z + solid.MaximumPoint.Z) / 2));
                            opening.Width = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                            opening.Height = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                            opening.Depth = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);
                            var loops = new List<ProjectedLoop>();
                            CollectFacingLoops(solid, xform, loops);
                            if (loops.Count > 0)
                            {
                                loops.Sort((a, b) => b.Area.CompareTo(a.Area));
                                opening.OuterContour = loops[0].ToContour("Opening");
                            }
                        }
                    }

                    rec.Openings.Add(opening);
                }
                catch { /* skip one cut */ }
            }
        }

        private static void ProjectRebars(TSModel.Part part, ViewXform xform, ShopView rec)
        {
            Tekla.Structures.Model.ModelObjectEnumerator rebars;
            try { rebars = part.GetReinforcements(); } catch { return; }
            if (rebars == null) return;

            while (rebars.MoveNext())
            {
                var reinf = rebars.Current as TSModel.Reinforcement;
                if (reinf == null) continue;
                try
                {
                    var row = new ShopRebar
                    {
                        Id = reinf.Identifier.ID,
                        Guid = reinf.Identifier.GUID.ToString(),
                        Name = reinf.Name ?? "",
                        Grade = reinf.Grade ?? "",
                        RebarMark = FirstNonEmpty(Report(reinf, "REBAR_POS"), reinf.Name),
                        NumberOfBars = Safe(() => reinf.GetNumberOfRebars()),
                        Length = R(ReportDouble(reinf, "LENGTH"), 1),
                        Shape = FirstNonEmpty(Report(reinf, "SHAPE"), Report(reinf, "SHAPE_CODE")),
                        Cover = R(ReportDouble(reinf, "COVER"), 1),
                    };

                    var group = reinf as TSModel.RebarGroup;
                    var single = reinf as TSModel.SingleRebar;
                    row.Size = group != null ? (group.Size ?? "") : (single != null ? (single.Size ?? "") : "");
                    double.TryParse(row.Size, NumberStyles.Any, CultureInfo.InvariantCulture, out double dia);
                    row.Diameter = dia;
                    if (group?.Spacings != null)
                        row.Spacing = string.Join("*", group.Spacings.Cast<object>().Select(s => Convert.ToString(s, CultureInfo.InvariantCulture)));

                    var startHook = group != null ? group.StartHook : single?.StartHook;
                    var endHook = group != null ? group.EndHook : single?.EndHook;
                    if (startHook != null)
                        row.StartHook = new ShopHook { Angle = R(startHook.Angle), Radius = R(startHook.Radius), Length = R(startHook.Length) };
                    if (endHook != null)
                        row.EndHook = new ShopHook { Angle = R(endHook.Angle), Radius = R(endHook.Radius), Length = R(endHook.Length) };

                    ArrayList geoms = null;
                    try { geoms = reinf.GetRebarGeometries(true); } catch { /* optional */ }
                    if (geoms != null)
                    {
                        foreach (var g in geoms)
                        {
                            var geom = g as TSModel.RebarGeometry;
                            if (geom?.Shape?.Points == null) continue;
                            var barSheet = new List<double[]>();
                            var barView = new List<double[]>();
                            var bar3 = new List<double[]>();
                            Point prev = null;
                            foreach (Point p in geom.Shape.Points)
                            {
                                bar3.Add(Xyz(p));
                                barView.Add(xform.ModelToView2(p));
                                barSheet.Add(xform.ModelToSheet(p));
                                if (prev != null && Distance(prev, p) > 1)
                                    row.BendingPoints.Add(Xyz(p));
                                prev = p;
                            }
                            if (row.Polyline3d.Count == 0)
                            {
                                row.Polyline3d = bar3;
                                row.Polyline2dView = barView;
                                row.Polyline2dSheet = barSheet;
                                if (bar3.Count > 0) row.StartPoint = bar3[0];
                                if (bar3.Count > 0) row.EndPoint = bar3[bar3.Count - 1];
                            }
                            row.IndividualBars.Add(barSheet);
                        }
                    }

                    rec.Reinforcement.Add(row);
                }
                catch { /* skip one rebar */ }
            }
        }

        private static void ProjectBolts(TSModel.Part part, ViewXform xform, ShopView rec)
        {
            Tekla.Structures.Model.ModelObjectEnumerator bolts;
            try { bolts = part.GetBolts(); } catch { return; }
            if (bolts == null) return;
            while (bolts.MoveNext())
            {
                var group = bolts.Current as TSModel.BoltGroup;
                if (group == null) continue;
                ArrayList positions = null;
                try { positions = group.BoltPositions; } catch { continue; }
                if (positions == null) continue;
                foreach (Point p in positions)
                {
                    rec.Bolts.Add(new ShopBolt
                    {
                        Id = group.Identifier.ID,
                        Guid = group.Identifier.GUID.ToString(),
                        Standard = group.BoltStandard ?? "",
                        Size = group.BoltSize,
                        CenterView = xform.ModelToView2(p),
                        CenterSheet = xform.ModelToSheet(p),
                    });
                }
            }
        }

        private static void ProjectWelds(TSModel.Part part, ViewXform xform, ShopView rec)
        {
            Tekla.Structures.Model.ModelObjectEnumerator welds;
            try { welds = part.GetWelds(); } catch { return; }
            if (welds == null) return;
            while (welds.MoveNext())
            {
                var weld = welds.Current as TSModel.BaseWeld;
                if (weld == null) continue;
                Point pos = null;
                try { pos = weld.GetCoordinateSystem().Origin; } catch { /* optional */ }
                rec.Welds.Add(new ShopWeld
                {
                    Id = weld.Identifier.ID,
                    Type = weld.GetType().Name,
                    Size = Safe(() => weld.SizeAbove),
                    IsShopWeld = Safe(() => weld.ShopWeld),
                    PositionView = pos == null ? null : xform.ModelToView2(pos),
                    PositionSheet = pos == null ? null : xform.ModelToSheet(pos),
                });
            }
        }

        private static ShopHardware ReadHardware(TSModel.Part part, ViewXform xform, string parentGuid)
        {
            var hw = new ShopHardware
            {
                Id = part.Identifier.ID,
                Guid = part.Identifier.GUID.ToString(),
                Name = FirstNonEmpty(Safe(() => part.Name), Report(part, "NAME")),
                Profile = FirstNonEmpty(Safe(() => part.Profile?.ProfileString), Report(part, "PROFILE")),
                Material = FirstNonEmpty(Safe(() => part.Material?.MaterialString), Report(part, "MATERIAL")),
                Class = Safe(() => part.Class) ?? "",
                Description = Report(part, "COMMENT"),
                HardwareCode = FirstNonEmpty(Report(part, "PART_POS"), Safe(() => part.Name)),
                Kind = InferHardwareKind(part),
            };
            try
            {
                var cs = part.GetCoordinateSystem();
                hw.CenterModel = Xyz(cs.Origin);
                hw.CenterView = xform.ModelToView2(cs.Origin);
                hw.CenterSheet = xform.ModelToSheet(cs.Origin);
                hw.AxisX = Xyz(cs.AxisX);
                hw.AxisY = Xyz(cs.AxisY);
            }
            catch { /* optional */ }

            var tmp = new ShopPart();
            FillContoursFromSolid(part, xform, tmp);
            hw.VisibleContour = tmp.OuterContour;
            return hw;
        }

        private static string InferHardwareKind(TSModel.Part part)
        {
            string blob = ((Safe(() => part.Name) ?? "") + " " + (Safe(() => part.Class) ?? "") + " " +
                           (Safe(() => part.Profile?.ProfileString) ?? "")).ToUpperInvariant();
            if (blob.Contains("LIFT")) return "LiftingInsert";
            if (blob.Contains("BRACE") || blob.Contains("SHORE")) return "BracingInsert";
            if (blob.Contains("GROUT")) return "GroutTube";
            if (blob.Contains("SLEEVE")) return "Sleeve";
            if (blob.Contains("COUPL") || blob.Contains("WILLIAMS") || blob.Contains("SPLICE")) return "SpliceBar";
            if (blob.Contains("ANCHOR")) return "Anchor";
            if (blob.Contains("PLATE") || blob.Contains("PLT")) return "Plate";
            if (blob.Contains("INSERT")) return "Insert";
            return "Embed";
        }

        private ShopTable BuildBomTable(TSModel.Assembly cu, Drawing drawing)
        {
            var table = new ShopTable { Kind = "BOM", Title = "Bill of Materials" };
            var parts = new List<TSModel.Part>();
            if (cu != null)
            {
                try { parts.Add(cu.GetMainPart() as TSModel.Part); } catch { /* optional */ }
                try
                {
                    var seconds = cu.GetSecondaries();
                    if (seconds != null)
                    {
                        foreach (var o in seconds)
                            if (o is TSModel.Part p) parts.Add(p);
                    }
                }
                catch { /* optional */ }
            }

            var groups = new Dictionary<string, ShopBomRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Where(p => p != null))
            {
                string mark = Report(part, "PART_POS");
                string profile = FirstNonEmpty(Safe(() => part.Profile?.ProfileString), Report(part, "PROFILE"));
                string material = FirstNonEmpty(Safe(() => part.Material?.MaterialString), Report(part, "MATERIAL"));
                double length = R(ReportDouble(part, "LENGTH"), 1);
                double weight = R(ReportDouble(part, "WEIGHT"), 2);
                string key = mark + "|" + profile + "|" + material + "|" + length.ToString("0.#", CultureInfo.InvariantCulture);
                if (!groups.TryGetValue(key, out var row))
                {
                    row = new ShopBomRow
                    {
                        PieceMark = FirstNonEmpty(Report(part, "CAST_UNIT_POS"), Report(part, "ASSEMBLY_POS")),
                        PartMark = mark,
                        Description = FirstNonEmpty(Safe(() => part.Name), mark),
                        Profile = profile,
                        Material = material,
                        Length = length,
                        HardwareCode = InferHardwareKind(part),
                    };
                    groups[key] = row;
                }
                row.Quantity++;
                row.Weight = R(row.Weight + weight, 2);
                try { row.Guids.Add(part.Identifier.GUID.ToString()); } catch { /* optional */ }
            }
            table.Rows = groups.Values.OrderBy(r => r.PartMark).ToList();
            return table;
        }

        private void CollectSheetAnnotations(ContainerView sheet, ShopDrawing shop)
        {
            DrawingObjectEnumerator en = null;
            try { en = sheet.GetAllObjects(); } catch { return; }
            if (en == null) return;
            while (en.MoveNext())
            {
                if (en.Current is View) continue;
                if (en.Current is TSDrawing.Text text)
                {
                    shop.Sheet.TitleBlock.Cells.Add(new ShopText
                    {
                        Text = text.TextString ?? "",
                        InsertionPoint = Xyz(text.InsertionPoint),
                        InsertionSheet = Xyz(text.InsertionPoint),
                    });
                }
            }
        }

        private void FillProject(ShopDrawing shop)
        {
            try
            {
                var info = _model.GetInfo();
                shop.ProjectName = info?.ModelName ?? "";
            }
            catch { /* optional */ }
            try
            {
                var dummy = _model.GetProjectInfo();
                if (dummy != null)
                {
                    shop.ProjectName = FirstNonEmpty(dummy.Name, shop.ProjectName);
                    shop.ProjectNumber = dummy.ProjectNumber ?? "";
                }
            }
            catch { /* optional */ }
        }

        // =====================================================================
        // dimensions / marks / graphics
        // =====================================================================
        private static ShopDimension ReadStraightDim(
            StraightDimension dim, StraightDimensionSet parent, string viewName, ViewXform xform, HashSet<string> seen)
        {
            string key = Format(dim.StartPoint) + "|" + Format(dim.EndPoint);
            if (!seen.Add(key)) return null;

            string displayed = FlattenContainer(dim.Value);
            double measured = Distance2d(dim.StartPoint, dim.EndPoint);
            var rec = new ShopDimension
            {
                DimensionType = "StraightDimension",
                DisplayedValue = displayed,
                MeasuredValue = R(ParseNumber(displayed) > 0 ? ParseNumber(displayed) : measured, 3),
                StartPoint = Xyz(dim.StartPoint),
                EndPoint = Xyz(dim.EndPoint),
                StartSheet = Xyz(dim.StartPoint),
                EndSheet = Xyz(dim.EndPoint),
                Offset = R(dim.Distance),
                Direction = Xyz(dim.UpDirection),
                ViewName = viewName,
                Prefix = FlattenContainer(Safe(() => dim.Attributes.DimensionValuePrefix)),
                Postfix = FlattenContainer(Safe(() => dim.Attributes.DimensionValuePostfix)),
            };

            var mid = new Point(
                (dim.StartPoint.X + dim.EndPoint.X) / 2.0,
                (dim.StartPoint.Y + dim.EndPoint.Y) / 2.0,
                (dim.StartPoint.Z + dim.EndPoint.Z) / 2.0);
            Vector up = dim.UpDirection ?? new Vector(0, 1, 0);
            rec.TextPosition = new[]
            {
                R(mid.X + up.X * dim.Distance),
                R(mid.Y + up.Y * dim.Distance),
                R(mid.Z + up.Z * dim.Distance)
            };
            rec.TextSheet = rec.TextPosition;
            rec.DimensionLinePosition = rec.TextPosition;
            rec.ExtensionStart = rec.StartPoint;
            rec.ExtensionEnd = rec.EndPoint;

            if (parent != null)
            {
                try
                {
                    rec.Precision = parent.Attributes.Format.Precision.ToString();
                    rec.Format = parent.Attributes.Format.Format.ToString();
                    rec.Units = parent.Attributes.Format.Unit.ToString();
                }
                catch { /* optional */ }
            }
            return rec;
        }

        private static ShopMark ReadMark(MarkBase mark, ViewXform xform)
        {
            var rec = new ShopMark
            {
                Kind = "PartMark",
                Text = FirstNonEmpty(FlattenRelated(mark), FlattenMarkContent(mark)),
                InsertionPoint = Xyz(mark.InsertionPoint),
                InsertionSheet = Xyz(mark.InsertionPoint),
                Rotation = Safe(() => mark.Attributes.Angle),
                Alignment = Safe(() => mark.Attributes.TextAlignment.ToString()) ?? "",
            };
            try
            {
                var box = mark.GetAxisAlignedBoundingBox();
                rec.BoundingBoxMin = Xyz(box.MinPoint);
                rec.BoundingBoxMax = Xyz(box.MaxPoint);
            }
            catch { /* optional */ }

            try
            {
                var related = mark.GetRelatedObjects();
                while (related != null && related.MoveNext())
                {
                    if (related.Current is ReinforcementBase rb)
                    {
                        rec.Kind = "RebarMark";
                        rec.AssociatedId = rb.ModelIdentifier != null ? rb.ModelIdentifier.ID : 0;
                        rec.AssociatedGuid = rb.ModelIdentifier != null ? rb.ModelIdentifier.GUID.ToString() : "";
                    }
                    else if (related.Current is TSDrawing.Part p)
                    {
                        rec.AssociatedId = p.ModelIdentifier != null ? p.ModelIdentifier.ID : 0;
                        rec.AssociatedGuid = p.ModelIdentifier != null ? p.ModelIdentifier.GUID.ToString() : "";
                    }
                    else if (related.Current is TSDrawing.Weld)
                        rec.Kind = "WeldMark";
                }
            }
            catch { /* optional */ }

            if (LooksLikeRebar(rec.Text)) rec.Kind = "RebarMark";
            else if (LooksLikeHardware(rec.Text)) rec.Kind = "HardwareMark";
            return rec;
        }

        private static ShopGraphic Graphic(string kind, bool closed, ViewXform xform, params Point[] pts)
        {
            return Graphic(kind, closed, xform, (IEnumerable<Point>)pts);
        }

        private static ShopGraphic Graphic(string kind, bool closed, ViewXform xform, IEnumerable<Point> pts)
        {
            var g = new ShopGraphic { Kind = kind, Closed = closed };
            if (pts == null) return g;
            foreach (var p in pts)
            {
                if (p == null) continue;
                g.PointsSheet.Add(Xyz(p));
                g.PointsView.Add(xform.SheetToView(p));
            }
            return g;
        }

        // =====================================================================
        // view transform: model mm → view mm → sheet mm
        // Drawing object points are already sheet millimetres.
        // =====================================================================
        private sealed class ViewXform
        {
            public Point Origin;
            public double Scale = 1;
            public CoordinateSystem ViewCs;
            public Matrix ModelToViewM;
            public Vector ViewZ = new Vector(0, 0, 1);

            public static ViewXform From(View view, double scale)
            {
                var x = new ViewXform { Origin = view.Origin ?? new Point(), Scale = scale < 0.01 ? 1 : scale };
                try { x.ViewCs = view.DisplayCoordinateSystem ?? view.ViewCoordinateSystem; }
                catch { try { x.ViewCs = view.ViewCoordinateSystem; } catch { x.ViewCs = new CoordinateSystem(); } }
                try { x.ModelToViewM = MatrixFactory.ToCoordinateSystem(x.ViewCs); }
                catch { x.ModelToViewM = new Matrix(); }
                try
                {
                    var vx = x.ViewCs.AxisX;
                    var vy = x.ViewCs.AxisY;
                    x.ViewZ = new Vector(
                        vx.Y * vy.Z - vx.Z * vy.Y,
                        vx.Z * vy.X - vx.X * vy.Z,
                        vx.X * vy.Y - vx.Y * vy.X);
                }
                catch { x.ViewZ = new Vector(0, 0, 1); }
                return x;
            }

            public Point ModelToView(Point model)
            {
                if (model == null) return new Point();
                try { return ModelToViewM.Transform(model); }
                catch { return model; }
            }

            public double[] ModelToView2(Point model)
            {
                var p = ModelToView(model);
                return new[] { R(p.X), R(p.Y), R(p.Z) };
            }

            public double[] ModelToSheet(Point model)
            {
                var p = ModelToView(model);
                return new[]
                {
                    R(Origin.X + p.X / Scale),
                    R(Origin.Y + p.Y / Scale),
                    0
                };
            }

            public double[] SheetToView(Point sheet)
            {
                if (sheet == null) return null;
                return new[]
                {
                    R((sheet.X - Origin.X) * Scale),
                    R((sheet.Y - Origin.Y) * Scale),
                    0
                };
            }

            public ShopCoordinateSystems ToRecord()
            {
                var cs = new ShopCoordinateSystems();
                if (ViewCs != null)
                {
                    cs.ModelOrigin = Xyz(ViewCs.Origin);
                    cs.ModelAxisX = Xyz(ViewCs.AxisX);
                    cs.DisplayOrigin = Xyz(ViewCs.Origin);
                    cs.DisplayAxisX = Xyz(ViewCs.AxisX);
                    cs.DisplayAxisY = Xyz(ViewCs.AxisY);
                    cs.LocalX = Xyz(ViewCs.AxisX);
                    cs.LocalY = Xyz(ViewCs.AxisY);
                    cs.LocalZ = new[] { R(ViewZ.X, 4), R(ViewZ.Y, 4), R(ViewZ.Z, 4) };
                }
                cs.ViewToSheet = new[]
                {
                    1 / Scale, 0, 0, Origin.X,
                    0, 1 / Scale, 0, Origin.Y,
                    0, 0, 1, 0,
                    0, 0, 0, 1
                };
                return cs;
            }
        }

        private sealed class ProjectedLoop
        {
            public string Kind = "Outer";
            public bool Closed = true;
            public double Area;
            public List<double[]> Model = new List<double[]>();
            public List<double[]> View = new List<double[]>();
            public List<double[]> Sheet = new List<double[]>();

            public void ComputeArea()
            {
                double a = 0;
                for (int i = 0, n = Sheet.Count; i < n; i++)
                {
                    var p = Sheet[i];
                    var q = Sheet[(i + 1) % n];
                    a += p[0] * q[1] - q[0] * p[1];
                }
                Area = Math.Abs(a) * 0.5;
            }

            public ShopContour ToContour(string kind)
            {
                return new ShopContour { Kind = kind, Closed = Closed, Model = Model, View = View, Sheet = Sheet };
            }
        }

        // =====================================================================
        // helpers
        // =====================================================================
        private static bool HasContour(ShopContour c) =>
            c != null && c.Sheet != null && c.Sheet.Count >= 3;

        private static ShopContour SegmentContour(ViewXform xform, Point a, Point b, string kind)
        {
            return new ShopContour
            {
                Kind = kind,
                Closed = false,
                Model = new List<double[]> { Xyz(a), Xyz(b) },
                View = new List<double[]> { xform.ModelToView2(a), xform.ModelToView2(b) },
                Sheet = new List<double[]> { xform.ModelToSheet(a), xform.ModelToSheet(b) },
            };
        }

        private static ShopContour ConvexOutline(List<ShopContour> edges)
        {
            var pts = edges.SelectMany(e => e.Sheet ?? Enumerable.Empty<double[]>())
                .Where(p => p != null && p.Length >= 2).ToList();
            if (pts.Count < 3) return null;
            double minX = pts.Min(p => p[0]), maxX = pts.Max(p => p[0]);
            double minY = pts.Min(p => p[1]), maxY = pts.Max(p => p[1]);
            return new ShopContour
            {
                Kind = "Outer",
                Closed = true,
                Sheet = new List<double[]>
                {
                    new[] { minX, minY, 0 }, new[] { maxX, minY, 0 },
                    new[] { maxX, maxY, 0 }, new[] { minX, maxY, 0 },
                }
            };
        }

        private static List<Point> PointsOf(IEnumerable raw)
        {
            var list = new List<Point>();
            if (raw == null) return list;
            foreach (var o in raw)
            {
                if (o is Point p) list.Add(p);
            }
            return list;
        }

        private static string InferSectionId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(name, @"([A-Z])\s*[-–]\s*\1");
            return m.Success ? m.Value.Replace(" ", "") : "";
        }

        private static Dictionary<string, string> ReadStringUdas(DatabaseObject obj)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (obj.GetStringUserProperties(out var values) && values != null)
                {
                    foreach (var kv in values)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Value))
                            map[kv.Key] = kv.Value.Trim();
                    }
                }
            }
            catch { /* optional */ }
            return map;
        }

        private static string ReadFloor(TSModel.ModelObject obj)
        {
            if (obj == null) return "";
            foreach (var name in FloorUdaNames)
            {
                string v = "";
                try { if (obj.GetUserProperty(name, ref v) && !string.IsNullOrWhiteSpace(v)) return v.Trim(); } catch { }
                try { if (obj.GetReportProperty(name, ref v) && !string.IsNullOrWhiteSpace(v)) return v.Trim(); } catch { }
            }
            return "";
        }

        private static string PlotRevision(Drawing drawing)
        {
            try
            {
                string name = drawing.GetPlotFileName(true) ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(name, @"[_\-][Rr](?:ev)?[\s_\-]*([A-Z0-9]+)");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
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

        private static string FlattenMarkContent(MarkBase mark)
        {
            try
            {
                if (mark is Mark m && m.Attributes?.Content != null)
                    return FlattenContainer(m.Attributes.Content);
            }
            catch { /* optional */ }
            return "";
        }

        private static string FlattenContainer(IEnumerable container)
        {
            if (container == null) return "";
            var sb = new StringBuilder();
            foreach (var el in container)
            {
                if (el is TextElement te) sb.Append(te.Value);
                else if (el is PropertyElement pe)
                    sb.Append(string.IsNullOrEmpty(pe.Value) ? pe.Name : pe.Value);
                else if (el is ContainerElement ce) sb.Append(FlattenContainer(ce));
                else if (el != null) sb.Append(el);
            }
            return sb.ToString();
        }

        private static string Report(TSModel.ModelObject obj, string name)
        {
            string v = "";
            try { obj.GetReportProperty(name, ref v); } catch { }
            return v ?? "";
        }

        private static double ReportDouble(TSModel.ModelObject obj, string name)
        {
            double v = 0;
            try { if (obj.GetReportProperty(name, ref v)) return v; } catch { }
            return v;
        }

        private static double Distance(Point a, Point b)
        {
            if (a == null || b == null) return 0;
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Distance2d(Point a, Point b)
        {
            if (a == null || b == null) return 0;
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double ParseNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"[-+]?\d+(?:[.,]\d+)?");
            if (!m.Success) return 0;
            double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double v);
            return v;
        }

        private static double[] Mid(Point a, Point b)
        {
            if (a == null || b == null) return null;
            return new[] { R((a.X + b.X) / 2), R((a.Y + b.Y) / 2), R((a.Z + b.Z) / 2) };
        }

        private static bool LooksLikeRebar(string t) =>
            !string.IsNullOrWhiteSpace(t) &&
            System.Text.RegularExpressions.Regex.IsMatch(t, @"(?i)(\bREBAR\b|\bSTIRRUP\b|[ØΦ]|DIA\b|\bT\d{1,2}\b)");

        private static bool LooksLikeHardware(string t) =>
            !string.IsNullOrWhiteSpace(t) &&
            System.Text.RegularExpressions.Regex.IsMatch(t, @"(?i)(LIFT|GROUT|BRACE|INSERT|SLEEVE|COUPL|WILLIAMS|ANCHOR)");

        private static string Lookup(Dictionary<string, string> map, params string[] keys)
        {
            if (map == null) return "";
            foreach (var k in keys)
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            }
            return "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        private static string Format(Point p) =>
            p == null ? "" : p.X.ToString("0.#", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.#", CultureInfo.InvariantCulture);

        private static double[] Xyz(Point p) => p == null ? null : new[] { R(p.X), R(p.Y), R(p.Z) };
        private static double[] Xyz(Vector v) => v == null ? null : new[] { R(v.X, 4), R(v.Y, 4), R(v.Z, 4) };
        private static double R(double v, int d = 3) => Math.Round(v, d);
        private static string SafeId(TSModel.Part p) { try { return p.Identifier.ID.ToString(); } catch { return "?"; } }
        private static T Safe<T>(Func<T> fn) { try { return fn(); } catch { return default; } }
    }
}

// ActiveDrawingQaExtractor.cs
// ─────────────────────────────────────────────────────────────────
// Currently active Cast Unit / Assembly drawing
//   → DrawingSheet { TitleBlock, BOM_Table, Views[] }
//        View { Dimensions, PartMarks, RebarMarks }
//   → indented JSON in the Tekla model folder
//
// Drawing API used:
//   DrawingHandler.GetActiveDrawing()
//   Drawing.GetSheet() → ContainerView
//   ContainerView.GetAllViews() / GetViews()
//   ViewBase.GetAllObjects()  (enumerator of every drawing object)
//   StraightDimensionSet.GetObjects() → child StraightDimension
//   StraightDimension.StartPoint / EndPoint / Distance / Value / UpDirection
//   MarkBase.InsertionPoint + GetRelatedObjects() for mark text
//   DrawingHandler.GetModelObjectIdentifiers() + Model.SelectModelObject
//     for the BOM (live part profile / material / length / weight)

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
using TSDrawing = Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    // =====================================================================
    // Hierarchy a civil engineer reads: sheet → title / BOM → views
    // =====================================================================

    public class QaDrawingSheet
    {
        public string Schema { get; set; } = "tekla-drawing-qa/1.0";
        public string ModelName { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string DrawingType { get; set; } = "";
        public string TeklaDrawingType { get; set; } = "";
        public double[] SheetSizeMm { get; set; }
        public QaTitleBlock TitleBlock { get; set; } = new QaTitleBlock();
        public List<QaBomRow> BOM_Table { get; set; } = new List<QaBomRow>();
        public int BomLineCount { get; set; }
        public List<QaView> Views { get; set; } = new List<QaView>();
        public string Error { get; set; }
    }

    public class QaTitleBlock
    {
        public string DrawingName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Title1 { get; set; } = "";
        public string Title2 { get; set; } = "";
        public string Title3 { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Revision { get; set; } = "";
        public string FloorLevel { get; set; } = "";
        public string FloorSource { get; set; } = "";
        public string UpToDateStatus { get; set; } = "";
        public bool IsLocked { get; set; }
        public bool IsIssued { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime ModificationDate { get; set; }
        public DateTime IssuingDate { get; set; }
        public string LinkedModelObject { get; set; } = "";
        public int LinkedModelId { get; set; }
        public string LinkedModelGuid { get; set; } = "";
        public Dictionary<string, string> UDAs { get; set; } = new Dictionary<string, string>();
    }

    public class QaBomRow
    {
        public string PartName { get; set; } = "";
        public string PartMark { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public int Quantity { get; set; }
        public double LengthMm { get; set; }
        public double UnitWeightKg { get; set; }
        public double TotalWeightKg { get; set; }
        public string Class { get; set; } = "";
        public string TeklaType { get; set; } = "";
        public List<string> Guids { get; set; } = new List<string>();
    }

    public class QaView
    {
        public string Name { get; set; } = "";
        public string ViewType { get; set; } = "";
        public double Scale { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public double[] Origin { get; set; }
        public double[] FrameOrigin { get; set; }
        public double[] RestrictionBoxMin { get; set; }
        public double[] RestrictionBoxMax { get; set; }
        public List<QaDimension> Dimensions { get; set; } = new List<QaDimension>();
        public List<QaMark> PartMarks { get; set; } = new List<QaMark>();
        public List<QaMark> RebarMarks { get; set; } = new List<QaMark>();
    }

    public class QaDimension
    {
        public string Kind { get; set; } = "StraightDimension";
        public double NumericValue { get; set; }
        public string DisplayedValue { get; set; } = "";
        public double MeasuredLengthMm { get; set; }
        public string Precision { get; set; } = "";
        public string Format { get; set; } = "";
        public string Unit { get; set; } = "";
        public bool UseDigitGrouping { get; set; }
        public string Prefix { get; set; } = "";
        public string Postfix { get; set; } = "";
        public string Tolerance { get; set; } = "";
        public double DimensionLineOffset { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] TextPoint { get; set; }
        public double[] UpPoint { get; set; }
        public double[] DownPoint { get; set; }
        public double[] UpDirection { get; set; }
        public string TextPlacing { get; set; } = "";
    }

    public class QaMark
    {
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public double[] InsertionPoint { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public int ModelObjectId { get; set; }
        public string ModelObjectGuid { get; set; } = "";
        public string LinkedType { get; set; } = "";
    }

    public class ActiveDrawingQaExtractor
    {
        private static readonly string[] FloorUdaNames =
        {
            "FLOOR_LEVEL", "FLOOR", "FLOOR_NAME", "STOREY", "STORY",
            "LEVEL", "FLOORLEVEL", "STOREY_NAME"
        };

        private static readonly string[] RevisionUdaNames =
        {
            "REVISION", "REVISION_MARK", "DR_REVISION", "DRAWING_REVISION", "REV"
        };

        private readonly DrawingHandler _handler;
        private readonly TSModel.Model _model;

        public ActiveDrawingQaExtractor(TSModel.Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
        }

        public QaDrawingSheet Run(string workspaceDir = null)
        {
            if (_handler == null || !_handler.GetConnectionStatus())
                throw new InvalidOperationException("DrawingHandler is not connected. Open a drawing in Tekla.");

            // GetActiveDrawing() returns the sheet the user is looking at.
            // Cast Unit / Assembly drawings must be opened first (double-click in Document manager).
            Drawing drawing = _handler.GetActiveDrawing();
            if (drawing == null)
            {
                throw new InvalidOperationException(
                    "No active drawing. In Tekla: open a Cast Unit or Assembly drawing, then run --drawing-qa.");
            }

            var info = _model.GetInfo();
            var sheet = new QaDrawingSheet
            {
                ModelName = info?.ModelName ?? "",
                ModelPath = info?.ModelPath ?? "",
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
                DrawingType = Classify(drawing),
                TeklaDrawingType = drawing.DrawingTypeStr ?? drawing.GetType().Name,
            };

            Console.WriteLine($"[DrawingQA] active {sheet.DrawingType}  mark='{drawing.Mark}'  name='{drawing.Name}'");

            try
            {
                FillTitleBlock(drawing, sheet.TitleBlock);
                sheet.BOM_Table = BuildBom(drawing);
                sheet.BomLineCount = sheet.BOM_Table.Count;

                var container = drawing.GetSheet();
                if (container != null)
                {
                    try
                    {
                        var size = drawing.Layout?.SheetSize;
                        if (size != null)
                            sheet.SheetSizeMm = new[] { R(size.Width), R(size.Height) };
                    }
                    catch { /* layout optional */ }

                    if (sheet.SheetSizeMm == null)
                        sheet.SheetSizeMm = new[] { R(container.Width), R(container.Height) };

                    sheet.Views = ReadViews(container);
                }
            }
            catch (Exception ex)
            {
                sheet.Error = ex.Message;
                Console.WriteLine("[DrawingQA] " + ex.Message);
            }

            string json = JsonConvert.SerializeObject(sheet, JsonSettings());
            string outPath = WriteOutput(info?.ModelPath, workspaceDir, drawing, json);
            sheet.OutputPath = outPath;
            json = JsonConvert.SerializeObject(sheet, JsonSettings());
            if (!string.IsNullOrEmpty(outPath))
                File.WriteAllText(outPath, json, Encoding.UTF8);

            Console.WriteLine(
                $"[DrawingQA] views={sheet.Views.Count}  dims={sheet.Views.Sum(v => v.Dimensions.Count)}  " +
                $"partMarks={sheet.Views.Sum(v => v.PartMarks.Count)}  rebarMarks={sheet.Views.Sum(v => v.RebarMarks.Count)}  " +
                $"BOM={sheet.BomLineCount}");
            Console.WriteLine($"[DrawingQA] wrote {outPath}");
            return sheet;
        }

        // =====================================================================
        // Title block
        // =====================================================================
        private void FillTitleBlock(Drawing drawing, QaTitleBlock tb)
        {
            tb.DrawingName = drawing.Name ?? "";
            tb.Title1 = drawing.Title1 ?? "";
            tb.Title2 = drawing.Title2 ?? "";
            tb.Title3 = drawing.Title3 ?? "";
            tb.Title = FirstNonEmpty(tb.Title1, tb.Title2, tb.DrawingName);
            tb.Mark = drawing.Mark ?? "";
            tb.UpToDateStatus = drawing.UpToDateStatus.ToString();
            tb.CreationDate = drawing.CreationDate;
            tb.ModificationDate = drawing.ModificationDate;
            try { tb.IsLocked = drawing.IsLocked; } catch { /* optional */ }
            try { tb.IsIssued = drawing.IsIssued; } catch { /* optional */ }
            try { tb.IssuingDate = drawing.IssuingDate; } catch { /* optional */ }

            tb.UDAs = ReadAllStringUdas(drawing);
            tb.Revision = FirstNonEmpty(
                Lookup(tb.UDAs, RevisionUdaNames),
                ReadUda(drawing, RevisionUdaNames),
                PlotRevision(drawing));

            FillLinkedModel(drawing, tb);

            string floor = Lookup(tb.UDAs, FloorUdaNames);
            string source = string.IsNullOrWhiteSpace(floor) ? "" : "DrawingUDA";
            if (string.IsNullOrWhiteSpace(floor) && tb.LinkedModelId != 0)
            {
                try
                {
                    var mo = _model.SelectModelObject(new Identifier(tb.LinkedModelId));
                    floor = ReadUda(mo, FloorUdaNames);
                    if (string.IsNullOrWhiteSpace(floor) && mo is TSModel.Assembly asm)
                    {
                        var main = asm.GetMainPart() as TSModel.ModelObject;
                        floor = ReadUda(main, FloorUdaNames);
                    }
                    if (!string.IsNullOrWhiteSpace(floor))
                        source = "ModelUDA";
                }
                catch { /* optional */ }
            }

            if (string.IsNullOrWhiteSpace(floor))
            {
                floor = FloorWiseDrawingExtractor.InferFloorFromText(
                    tb.DrawingName + " " + tb.Title1 + " " + tb.Title2 + " " + tb.Title3 + " " + tb.Mark);
                if (!string.IsNullOrWhiteSpace(floor))
                    source = "DrawingTitle";
            }

            tb.FloorLevel = floor ?? "";
            tb.FloorSource = source;
        }

        private static void FillLinkedModel(Drawing drawing, QaTitleBlock tb)
        {
            try
            {
                if (drawing is AssemblyDrawing ad && ad.AssemblyIdentifier != null)
                {
                    tb.LinkedModelObject = "Assembly";
                    tb.LinkedModelId = ad.AssemblyIdentifier.ID;
                    tb.LinkedModelGuid = ad.AssemblyIdentifier.GUID.ToString();
                }
                else if (drawing is CastUnitDrawing cud && cud.CastUnitIdentifier != null)
                {
                    tb.LinkedModelObject = "CastUnit";
                    tb.LinkedModelId = cud.CastUnitIdentifier.ID;
                    tb.LinkedModelGuid = cud.CastUnitIdentifier.GUID.ToString();
                }
                else if (drawing is SinglePartDrawing spd && spd.PartIdentifier != null)
                {
                    tb.LinkedModelObject = "Part";
                    tb.LinkedModelId = spd.PartIdentifier.ID;
                    tb.LinkedModelGuid = spd.PartIdentifier.GUID.ToString();
                }
            }
            catch { /* optional */ }
        }

        // =====================================================================
        // BOM from linked 3D assembly / cast unit (not the drawing table graphic)
        // =====================================================================
        private List<QaBomRow> BuildBom(Drawing drawing)
        {
            var parts = new List<TSModel.Part>();
            var seen = new HashSet<int>();

            void AddPart(TSModel.Part part)
            {
                if (part == null || !part.Identifier.IsValid()) return;
                if (!seen.Add(part.Identifier.ID)) return;
                parts.Add(part);
            }

            void AddFromAssembly(TSModel.Assembly assembly)
            {
                if (assembly == null) return;
                try { assembly.Select(); } catch { /* best effort */ }
                AddPart(assembly.GetMainPart() as TSModel.Part);
                try
                {
                    var seconds = assembly.GetSecondaries();
                    if (seconds == null) return;
                    foreach (var obj in seconds)
                        AddPart(obj as TSModel.Part);
                }
                catch { /* optional */ }
            }

            Identifier owner = null;
            try
            {
                if (drawing is AssemblyDrawing ad) owner = ad.AssemblyIdentifier;
                else if (drawing is CastUnitDrawing cud) owner = cud.CastUnitIdentifier;
                else if (drawing is SinglePartDrawing spd) owner = spd.PartIdentifier;
            }
            catch { /* optional */ }

            if (owner != null && owner.ID != 0)
            {
                var mo = _model.SelectModelObject(owner);
                if (mo is TSModel.Assembly asm) AddFromAssembly(asm);
                else if (mo is TSModel.Part p)
                {
                    try { AddFromAssembly(p.GetAssembly()); }
                    catch { AddPart(p); }
                }
            }

            try
            {
                var ids = _handler.GetModelObjectIdentifiers(drawing);
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        if (id == null || id.ID == 0) continue;
                        var mo = _model.SelectModelObject(id);
                        if (mo is TSModel.Part p) AddPart(p);
                        else if (mo is TSModel.Assembly a) AddFromAssembly(a);
                    }
                }
            }
            catch { /* optional */ }

            var groups = new Dictionary<string, QaBomRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                try
                {
                    string mark = FirstNonEmpty(Report(part, "PART_POS"), Report(part, "PARTMARK"));
                    string profile = FirstNonEmpty(Safe(() => part.Profile?.ProfileString), Report(part, "PROFILE"));
                    string material = FirstNonEmpty(Safe(() => part.Material?.MaterialString), Report(part, "MATERIAL"));
                    string name = FirstNonEmpty(Safe(() => part.Name), Report(part, "NAME"), mark);
                    double length = Math.Round(ReportDouble(part, "LENGTH"), 1);
                    double weight = Math.Round(ReportDouble(part, "WEIGHT"), 2);
                    string key = mark + "|" + profile + "|" + material + "|" + length.ToString("0.#", CultureInfo.InvariantCulture);

                    if (!groups.TryGetValue(key, out var row))
                    {
                        row = new QaBomRow
                        {
                            PartName = name ?? "",
                            PartMark = mark ?? "",
                            Profile = profile ?? "",
                            Material = material ?? "",
                            LengthMm = length,
                            UnitWeightKg = weight,
                            Class = Safe(() => part.Class) ?? "",
                            TeklaType = part.GetType().Name,
                        };
                        groups[key] = row;
                    }

                    row.Quantity++;
                    row.TotalWeightKg = Math.Round(row.TotalWeightKg + weight, 2);
                    try { row.Guids.Add(part.Identifier.GUID.ToString()); } catch { /* optional */ }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DrawingQA] BOM skip part: {ex.Message}");
                }
            }

            return groups.Values
                .OrderBy(r => r.PartMark, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Profile, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // =====================================================================
        // Views + objects
        // =====================================================================
        private List<QaView> ReadViews(ContainerView sheet)
        {
            var list = new List<QaView>();
            DrawingObjectEnumerator views = null;
            try { views = sheet.GetAllViews(); }
            catch
            {
                try { views = sheet.GetViews(); } catch { return list; }
            }
            if (views == null) return list;

            while (views.MoveNext())
            {
                if (views.Current is View view)
                {
                    try { list.Add(ReadView(view)); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DrawingQA] view '{view.Name}': {ex.Message}");
                    }
                }
            }
            return list;
        }

        private QaView ReadView(View view)
        {
            var rec = new QaView
            {
                Name = view.Name ?? "",
                ViewType = view.ViewType.ToString(),
                Width = R(view.Width),
                Height = R(view.Height),
                Origin = Xyz(view.Origin),
                FrameOrigin = Xyz(view.FrameOrigin),
            };

            try { rec.Scale = R(view.Attributes.Scale, 4); } catch { /* optional */ }

            // Sheet-space AABB of the view frame (where the view sits on the paper).
            try
            {
                var box = view.GetAxisAlignedBoundingBox();
                rec.BoundingBoxMin = Xyz(box.MinPoint);
                rec.BoundingBoxMax = Xyz(box.MaxPoint);
            }
            catch
            {
                rec.BoundingBoxMin = rec.Origin;
                rec.BoundingBoxMax = rec.Origin == null
                    ? null
                    : new[] { R(view.Origin.X + view.Width), R(view.Origin.Y + view.Height), R(view.Origin.Z) };
            }

            try
            {
                rec.RestrictionBoxMin = Xyz(view.RestrictionBox.MinPoint);
                rec.RestrictionBoxMax = Xyz(view.RestrictionBox.MaxPoint);
            }
            catch { /* optional */ }

            // GetAllObjects() walks every drawing object in this view
            // (dimensions, marks, parts, rebar, texts). Type-filter after the fact
            // so one pass fills Dimensions / PartMarks / RebarMarks.
            DrawingObjectEnumerator en = null;
            try { en = view.GetAllObjects(); }
            catch { try { en = view.GetObjects(); } catch { return rec; } }
            if (en == null) return rec;

            var seenDim = new HashSet<int>();
            while (en.MoveNext())
            {
                var obj = en.Current;
                if (obj == null) continue;
                try
                {
                    if (obj is StraightDimensionSet set)
                        ReadDimensionSet(set, rec, seenDim);
                    else if (obj is StraightDimension dim)
                        AddStraightDimension(dim, rec, seenDim, parentSet: null);
                    else if (obj is AngleDimension ang)
                        rec.Dimensions.Add(ReadAngle(ang));
                    else if (obj is RadiusDimension rad)
                        rec.Dimensions.Add(ReadRadius(rad));
                    else if (obj is MarkBase mark)
                        AddMark(mark, rec);
                }
                catch { /* skip one bad object */ }
            }

            return rec;
        }

        /// <summary>
        /// A StraightDimensionSet is a chain of child StraightDimension objects.
        /// Format / precision / unit live on the set; start/end/value live on each child.
        /// </summary>
        private static void ReadDimensionSet(StraightDimensionSet set, QaView view, HashSet<int> seen)
        {
            DrawingObjectEnumerator kids = null;
            try { kids = set.GetObjects(); } catch { /* fall through */ }
            bool any = false;
            if (kids != null)
            {
                while (kids.MoveNext())
                {
                    if (kids.Current is StraightDimension dim)
                    {
                        AddStraightDimension(dim, view, seen, set);
                        any = true;
                    }
                }
            }
            if (!any)
            {
                // Empty set — still record format so QA can see a missing value.
                view.Dimensions.Add(new QaDimension
                {
                    Kind = "StraightDimensionSet",
                    DisplayedValue = FlattenRelated(set),
                    DimensionLineOffset = R(set.Distance),
                    Precision = Safe(() => set.Attributes.Format.Precision.ToString()) ?? "",
                    Format = Safe(() => set.Attributes.Format.Format.ToString()) ?? "",
                    Unit = Safe(() => set.Attributes.Format.Unit.ToString()) ?? "",
                    UseDigitGrouping = Safe(() => set.Attributes.Format.UseDigitGrouping),
                    TextPlacing = Safe(() => set.Attributes.Text.TextPlacing.ToString()) ?? "",
                });
            }
        }

        private static void AddStraightDimension(
            StraightDimension dim, QaView view, HashSet<int> seen, StraightDimensionSet parentSet)
        {
            int id = 0;
            try { id = dim.QueryReturnValue.GetHashCode() ^ dim.StartPoint.GetHashCode() ^ dim.EndPoint.GetHashCode(); }
            catch { id = dim.GetHashCode(); }
            if (!seen.Add(id)) return;

            if (parentSet == null)
            {
                try { parentSet = dim.GetDimensionSet() as StraightDimensionSet; } catch { /* standalone */ }
            }

            string displayed = FlattenContainer(dim.Value);
            double measured = Distance2d(dim.StartPoint, dim.EndPoint);
            double numeric = ParseNumber(displayed);
            if (numeric <= 0) numeric = measured;

            string prefix = FlattenContainer(Safe(() => dim.Attributes.DimensionValuePrefix));
            string postfix = FlattenContainer(Safe(() => dim.Attributes.DimensionValuePostfix));

            var rec = new QaDimension
            {
                Kind = "StraightDimension",
                NumericValue = R(numeric, 3),
                DisplayedValue = displayed,
                MeasuredLengthMm = R(measured, 3),
                Prefix = prefix,
                Postfix = postfix,
                Tolerance = GuessTolerance(prefix, postfix, displayed),
                DimensionLineOffset = R(dim.Distance),
                StartPoint = Xyz(dim.StartPoint),
                EndPoint = Xyz(dim.EndPoint),
                UpDirection = Xyz(dim.UpDirection),
            };

            // Tekla does not expose TextPoint. The dimension text sits on the
            // dimension line: midpoint of Start/End, offset by Distance along UpDirection.
            // UpPoint / DownPoint are the two sides of that offset (QA layout checks).
            ComputeDimPoints(dim.StartPoint, dim.EndPoint, dim.UpDirection, dim.Distance, rec);

            if (parentSet != null)
            {
                try
                {
                    var fmt = parentSet.Attributes.Format;
                    rec.Precision = fmt.Precision.ToString();
                    rec.Format = fmt.Format.ToString();
                    rec.Unit = fmt.Unit.ToString();
                    rec.UseDigitGrouping = fmt.UseDigitGrouping;
                    rec.TextPlacing = parentSet.Attributes.Text.TextPlacing.ToString();
                }
                catch { /* optional */ }
            }

            view.Dimensions.Add(rec);
        }

        private static void ComputeDimPoints(
            Point start, Point end, Vector up, double offset, QaDimension rec)
        {
            if (start == null || end == null) return;
            var mid = new Point((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0, (start.Z + end.Z) / 2.0);
            Vector dir = up ?? new Vector(0, 1, 0);
            double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (len < 1e-9) { dir = new Vector(0, 1, 0); len = 1; }
            dir = new Vector(dir.X / len, dir.Y / len, dir.Z / len);

            rec.UpPoint = new[] { R(mid.X + dir.X * offset), R(mid.Y + dir.Y * offset), R(mid.Z + dir.Z * offset) };
            rec.DownPoint = new[] { R(mid.X - dir.X * offset), R(mid.Y - dir.Y * offset), R(mid.Z - dir.Z * offset) };
            rec.TextPoint = rec.UpPoint;
        }

        private static QaDimension ReadAngle(AngleDimension ang)
        {
            return new QaDimension
            {
                Kind = "AngleDimension",
                NumericValue = R(ang.Distance),
                DisplayedValue = FlattenRelated(ang),
                DimensionLineOffset = R(ang.Distance),
                StartPoint = Xyz(ang.Point1),
                EndPoint = Xyz(ang.Point2),
                TextPoint = Xyz(ang.Origin),
            };
        }

        private static QaDimension ReadRadius(RadiusDimension rad)
        {
            return new QaDimension
            {
                Kind = "RadiusDimension",
                NumericValue = R(rad.Distance),
                DisplayedValue = FlattenRelated(rad),
                DimensionLineOffset = R(rad.Distance),
                StartPoint = Xyz(rad.ArcPoint1),
                EndPoint = Xyz(rad.ArcPoint3),
                TextPoint = Xyz(rad.ArcPoint2),
            };
        }

        /// <summary>
        /// Tekla has no ReinforcementMark class. A rebar mark is a Mark whose
        /// related objects include ReinforcementBase (or whose text looks like a bar mark).
        /// </summary>
        private static void AddMark(MarkBase mark, QaView view)
        {
            var rec = new QaMark
            {
                Kind = mark.GetType().Name,
                Text = FirstNonEmpty(FlattenRelated(mark), FlattenMarkContent(mark)),
                InsertionPoint = Xyz(mark.InsertionPoint),
            };

            try
            {
                var box = mark.GetAxisAlignedBoundingBox();
                rec.BoundingBoxMin = Xyz(box.MinPoint);
                rec.BoundingBoxMax = Xyz(box.MaxPoint);
            }
            catch { /* some marks have no AABB */ }

            bool rebar = false;
            try
            {
                var related = mark.GetRelatedObjects();
                while (related != null && related.MoveNext())
                {
                    var cur = related.Current;
                    if (cur is ReinforcementBase rb)
                    {
                        rebar = true;
                        rec.LinkedType = rb.GetType().Name;
                        FillModelId(rec, rb.ModelIdentifier);
                    }
                    else if (cur is TSDrawing.Part part)
                    {
                        rec.LinkedType = "Part";
                        FillModelId(rec, part.ModelIdentifier);
                    }
                }
            }
            catch { /* optional */ }

            if (!rebar)
                rebar = LooksLikeRebar(rec.Text);

            rec.Kind = rebar ? "RebarMark" : "PartMark";
            if (rebar) view.RebarMarks.Add(rec);
            else view.PartMarks.Add(rec);
        }

        // =====================================================================
        // Output
        // =====================================================================
        private static string WriteOutput(string modelDir, string workspaceDir, Drawing drawing, string json)
        {
            string stem = SafeFile(FirstNonEmpty(drawing.Mark, drawing.Name, "active_drawing")) + "_qa";
            string primary = "";

            if (!string.IsNullOrWhiteSpace(modelDir) && Directory.Exists(modelDir))
            {
                string dir = Path.Combine(modelDir, "drawing_qa");
                Directory.CreateDirectory(dir);
                primary = Path.Combine(dir, stem + ".json");
                File.WriteAllText(primary, json, Encoding.UTF8);
            }

            if (!string.IsNullOrWhiteSpace(workspaceDir))
            {
                string dir = Path.Combine(workspaceDir, "Export", "drawing_qa");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, stem + ".json"), json, Encoding.UTF8);
            }

            return primary;
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
        // Helpers — enumerators, containers, report properties
        // =====================================================================
        private static string Classify(Drawing drawing)
        {
            if (drawing is CastUnitDrawing) return "CastUnit";
            if (drawing is AssemblyDrawing) return "Assembly";
            if (drawing is SinglePartDrawing) return "SinglePart";
            if (drawing is GADrawing) return "GA";
            if (drawing is MultiDrawing) return "Multi";
            return drawing.GetType().Name;
        }

        /// <summary>
        /// Marks store visible text as related Text objects and/or Attributes.Content
        /// (a ContainerElement of TextElement / PropertyElement).
        /// </summary>
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

        private static void FillModelId(QaMark rec, Identifier id)
        {
            if (id == null) return;
            rec.ModelObjectId = id.ID;
            rec.ModelObjectGuid = id.GUID != Guid.Empty ? id.GUID.ToString() : "";
        }

        private static Dictionary<string, string> ReadAllStringUdas(DatabaseObject obj)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var values = new Dictionary<string, string>();
                if (obj.GetStringUserProperties(out values) && values != null)
                {
                    foreach (var kv in values)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Value))
                            map[kv.Key] = kv.Value.Trim();
                    }
                }
            }
            catch { /* optional */ }

            foreach (var name in FloorUdaNames.Concat(RevisionUdaNames))
            {
                if (map.ContainsKey(name)) continue;
                string v = ReadUda(obj, new[] { name });
                if (!string.IsNullOrWhiteSpace(v))
                    map[name] = v;
            }
            return map;
        }

        private static string ReadUda(DatabaseObject obj, string[] names)
        {
            if (obj == null || names == null) return "";
            foreach (var name in names)
            {
                string v = "";
                try
                {
                    if (obj.GetUserProperty(name, ref v) && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                catch { /* next */ }
            }
            return "";
        }

        private static string ReadUda(TSModel.ModelObject obj, string[] names)
        {
            if (obj == null || names == null) return "";
            foreach (var name in names)
            {
                string v = "";
                try
                {
                    if (obj.GetUserProperty(name, ref v) && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                catch { /* try report */ }
                try
                {
                    if (obj.GetReportProperty(name, ref v) && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                catch { /* next */ }
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

        private static string Lookup(Dictionary<string, string> map, string[] keys)
        {
            if (map == null) return "";
            foreach (var k in keys)
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return "";
        }

        private static string Report(TSModel.ModelObject obj, string name)
        {
            string v = "";
            try { obj.GetReportProperty(name, ref v); } catch { /* optional */ }
            return v ?? "";
        }

        private static double ReportDouble(TSModel.ModelObject obj, string name)
        {
            double v = 0;
            try
            {
                if (obj.GetReportProperty(name, ref v))
                    return v;
            }
            catch { /* optional */ }
            double.TryParse(Report(obj, name), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
            return v;
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

        private static string GuessTolerance(string prefix, string postfix, string displayed)
        {
            string blob = (prefix + " " + postfix + " " + displayed) ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(blob, @"[±+\-]+\s*\d+(?:[.,]\d+)?(?:\s*/\s*[+\-]?\d+(?:[.,]\d+)?)?");
            return m.Success ? m.Value.Trim() : "";
        }

        private static bool LooksLikeRebar(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                text, @"(?i)(\bREBAR\b|\bSTIRRUP\b|\bLINK\b|[ØΦ]|DIA\b|\bT\d{1,2}\b|\bY\d{1,2}\b|\bH\d{1,2}\b)");
        }

        private static double[] Xyz(Point p)
        {
            if (p == null) return null;
            return new[] { R(p.X), R(p.Y), R(p.Z) };
        }

        private static double[] Xyz(Vector v)
        {
            if (v == null) return null;
            return new[] { R(v.X, 4), R(v.Y, 4), R(v.Z, 4) };
        }

        private static double R(double v, int d = 3) => Math.Round(v, d);

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return "";
        }

        private static T Safe<T>(Func<T> fn)
        {
            try { return fn(); } catch { return default; }
        }

        private static string SafeFile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "drawing";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace(' ', '_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s.Trim('_');
        }
    }
}

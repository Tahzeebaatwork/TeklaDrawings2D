// DrawingPostProcessor.cs
// ─────────────────────────────────────────────────────────────────
// Runs AFTER default drawings exist. Uses DrawingHandler to CORRECT
// sheets (not just detect issues):
//   CatA  missing cut / boolean dimensions
//   CatB  missing part / assembly marks
//   CatC  overlapping texts, marks, dimension labels → shift
//   CatD  wrong-view / outside-restriction objects → hide
//   CatE  precast COG + embed labels
//   CatF  fabrication bolt-hole / edge dimensions from live model
//   CatG  tolerance / critical UDA → DetailMark

using System;
using System.Collections;
using System.Collections.Generic;
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
    public class CorrectionCounts
    {
        public string Mark { get; set; } = "";
        public string Type { get; set; } = "";
        public int CatA_CutDims { get; set; }
        public int CatB_MarksAdded { get; set; }
        public int CatC_Shifted { get; set; }
        public int CatD_Hidden { get; set; }
        public int CatE_CogEmbeds { get; set; }
        public int CatF_BoltDims { get; set; }
        public int CatG_DetailMarks { get; set; }
        public string Error { get; set; } = "";
    }

    public class CorrectionReport
    {
        public string GeneratedUtc { get; set; } = "";
        public int DrawingsProcessed { get; set; }
        public int DrawingsFailed { get; set; }
        public CorrectionCounts Totals { get; set; } = new CorrectionCounts { Mark = "TOTAL" };
        public List<CorrectionCounts> Drawings { get; set; } = new List<CorrectionCounts>();
    }

    public class DrawingPostProcessor
    {
        private readonly DrawingHandler _handler;
        private readonly TSModel.Model _model;
        private readonly StraightDimensionSetHandler _dimHandler;

        public DrawingPostProcessor(TSModel.Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
            _dimHandler = new StraightDimensionSetHandler();
        }

        public bool IsConnected => _handler != null && _handler.GetConnectionStatus();

        public CorrectionReport Run(string baseDir, string markFilter = null)
        {
            var report = new CorrectionReport { GeneratedUtc = DateTime.UtcNow.ToString("o") };
            if (!IsConnected)
            {
                Console.WriteLine("[Correct] DrawingHandler is not connected.");
                return report;
            }

            var drawings = new List<Drawing>();
            var en = _handler.GetDrawings();
            while (en.MoveNext())
            {
                if (en.Current != null) drawings.Add(en.Current);
            }

            if (!string.IsNullOrWhiteSpace(markFilter))
            {
                string target = markFilter.Trim();
                drawings = drawings.Where(d =>
                    (d.Mark ?? "").IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.Name ?? "").IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0
                ).ToList();
                Console.WriteLine($"[Correct] Filtered to mark '{target}': {drawings.Count} drawing(s) matched.");
            }

            Console.WriteLine($"[Correct] Post-processing {drawings.Count} drawings (CatA–CatG)...");
            int n = 0;
            foreach (var drawing in drawings)
            {
                n++;
                var counts = CorrectDrawing(drawing);
                report.Drawings.Add(counts);
                report.DrawingsProcessed++;
                if (!string.IsNullOrEmpty(counts.Error))
                    report.DrawingsFailed++;
                AddTotals(report.Totals, counts);
                if (n % 10 == 0)
                    Console.WriteLine($"[Correct] {n}/{drawings.Count}...");
            }

            WriteReport(report, baseDir);
            return report;
        }

        public CorrectionCounts CorrectDrawing(Drawing drawing)
        {
            var counts = new CorrectionCounts
            {
                Mark = drawing.Mark ?? drawing.Name ?? "",
                Type = drawing.GetType().Name,
            };

            bool opened = false;
            try
            {
                try { _handler.UpdateDrawing(drawing); } catch { /* optional */ }
                opened = _handler.SetActiveDrawing(drawing, false);
                if (!opened)
                    opened = _handler.SetActiveDrawing(drawing, false, true);

                var sheet = drawing.GetSheet();
                if (sheet == null)
                {
                    counts.Error = "No sheet";
                    return counts;
                }

                var views = new List<View>();
                var viewEn = sheet.GetAllViews();
                while (viewEn != null && viewEn.MoveNext())
                {
                    if (viewEn.Current is View v)
                        views.Add(v);
                }

                View primary = PickPrimaryView(views);

                TSModel.Part mainPart = ResolveMainPart(drawing);
                CoordinateSystem partCs = mainPart != null ? mainPart.GetCoordinateSystem() : null;

                // CatC first so later inserts have room
                // PREVENT SHADOW (Final): Disable FixOverlaps as it aggressively shifts
                // text labels upwards out of the drawing to avoid dimension lines.
                // counts.CatC_Shifted = FixOverlaps(sheet, views);
                counts.CatC_Shifted = 0;

                if (primary != null)
                {
                    counts.CatD_Hidden = HideWrongProjection(primary);

                    if (mainPart != null && partCs != null)
                    {
                        counts.CatF_BoltDims = AddBoltAndEdgeDimensions(primary, mainPart, partCs);
                        counts.CatA_CutDims = AddCutDimensions(primary, mainPart, partCs);
                        counts.CatG_DetailMarks = AddToleranceDetails(primary, mainPart, partCs);
                        counts.CatE_CogEmbeds = AddCogAndEmbeds(primary, drawing, mainPart, partCs);
                    }

                    counts.CatB_MarksAdded = EnsureMarks(primary, drawing, mainPart);
                }

                try { drawing.CommitChanges(); } catch { /* best effort */ }
                try { drawing.Modify(); } catch { /* best effort */ }

                Console.WriteLine(
                    $"[Correct] '{counts.Mark}' A={counts.CatA_CutDims} B={counts.CatB_MarksAdded} " +
                    $"C={counts.CatC_Shifted} D={counts.CatD_Hidden} E={counts.CatE_CogEmbeds} " +
                    $"F={counts.CatF_BoltDims} G={counts.CatG_DetailMarks}");
            }
            catch (Exception ex)
            {
                counts.Error = ex.Message;
                Console.WriteLine($"[Correct] '{counts.Mark}' failed: {ex.Message}");
            }
            finally
            {
                if (opened)
                {
                    try { _handler.CloseActiveDrawing(true); }
                    catch
                    {
                        try { _handler.CloseActiveDrawing(false); } catch { /* ignore */ }
                    }
                }
            }

            return counts;
        }

        // =====================================================================
        // CatC — overlapping marks / texts / dimension labels → shift
        // =====================================================================
        private int FixOverlaps(ContainerView sheet, List<View> views)
        {
            int shifted = 0;
            var boxes = CollectLabelBoxes(sheet, views);
            const double gap = 4.0;

            for (int pass = 0; pass < 6; pass++)
            {
                bool moved = false;
                for (int i = 0; i < boxes.Count; i++)
                {
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        if (!Intersects(boxes[i], boxes[j])) continue;

                        double dy = boxes[i].MaxY - boxes[j].MinY + gap;
                        if (dy < gap) dy = gap;

                        if (ShiftLabel(boxes[j], 0, dy))
                        {
                            boxes[j].MinY += dy;
                            boxes[j].MaxY += dy;
                            shifted++;
                            moved = true;
                        }
                    }
                }
                if (!moved) break;
            }
            return shifted;
        }

        private List<LabelBox> CollectLabelBoxes(ContainerView sheet, List<View> views)
        {
            var list = new List<LabelBox>();
            HarvestLabels(sheet, list);
            foreach (var v in views)
                HarvestLabels(v, list);
            return list;
        }

        private void HarvestLabels(ViewBase container, List<LabelBox> list)
        {
            DrawingObjectEnumerator en;
            try { en = container.GetAllObjects(); }
            catch { return; }
            if (en == null) return;

            while (en.MoveNext())
            {
                var obj = en.Current;
                if (obj == null) continue;
                try
                {
                    if (obj is TSDrawing.Text text)
                    {
                        var box = BoxOf(text.GetAxisAlignedBoundingBox(), text);
                        if (box != null) list.Add(box);
                    }
                    // PREVENT SHADOW: Do not collect MarkBase here, as it includes dimension tags and part marks
                    // which Tekla already positions properly. Shifting them causes the "shadow" leader lines.
                    else if (obj is StraightDimension dim)
                    {
                        double minX = Math.Min(dim.StartPoint.X, dim.EndPoint.X) - 3;
                        double maxX = Math.Max(dim.StartPoint.X, dim.EndPoint.X) + 3;
                        double minY = Math.Min(dim.StartPoint.Y, dim.EndPoint.Y) - 3;
                        double maxY = Math.Max(dim.StartPoint.Y, dim.EndPoint.Y) + 3;
                        list.Add(new LabelBox
                        {
                            Object = dim,
                            MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
                            IsDimension = true,
                        });
                    }
                }
                catch { /* skip */ }
            }
        }

        private static LabelBox BoxOf(RectangleBoundingBox aabb, DrawingObject obj)
        {
            if (aabb == null) return null;
            return new LabelBox
            {
                Object = obj,
                MinX = aabb.MinPoint.X,
                MaxX = aabb.MaxPoint.X,
                MinY = aabb.MinPoint.Y,
                MaxY = aabb.MaxPoint.Y,
            };
        }

        private static bool Intersects(LabelBox a, LabelBox b)
        {
            return a.MinX < b.MaxX && a.MaxX > b.MinX && a.MinY < b.MaxY && a.MaxY > b.MinY;
        }

        private static bool ShiftLabel(LabelBox box, double dx, double dy)
        {
            try
            {
                if (box.Object is TSDrawing.Text text)
                {
                    var p = text.InsertionPoint;
                    text.InsertionPoint = new Point(p.X + dx, p.Y + dy, p.Z);
                    return text.Modify();
                }
                if (box.Object is MarkBase mark)
                {
                    var p = mark.InsertionPoint;
                    mark.InsertionPoint = new Point(p.X + dx, p.Y + dy, p.Z);
                    return mark.Modify();
                }
            }
            catch { /* cannot shift */ }
            return false;
        }

        // =====================================================================
        // CatD — hide objects that do not belong in this view
        // =====================================================================
        private int HideWrongProjection(View view)
        {
            int hidden = 0;
            AABB clip;
            try { clip = view.RestrictionBox; }
            catch { return 0; }
            if (clip == null) return 0;

            DrawingObjectEnumerator en;
            try { en = view.GetAllObjects(); }
            catch { return 0; }

            while (en != null && en.MoveNext())
            {
                var obj = en.Current;
                if (obj == null || obj is ViewBase) continue;
                try
                {
                    bool outside = false;
                    if (obj is TSDrawing.Line line)
                    {
                        outside = !clip.IsInside(line.StartPoint) && !clip.IsInside(line.EndPoint);
                        double dz = Math.Abs(line.StartPoint.Z - line.EndPoint.Z);
                        if (dz > 5.0 && view.ViewType != View.ViewTypes._3DView)
                            outside = true;
                    }
                    else if (obj is TSDrawing.Text text)
                    {
                        outside = !clip.IsInside(text.InsertionPoint);
                    }

                    if (!outside) continue;
                    Hideable hide = GetHideable(obj);
                    if (hide == null || hide.IsHidden) continue;
                    hide.HideFromDrawingView();
                    obj.Modify();
                    hidden++;
                }
                catch { /* skip */ }
            }
            return hidden;
        }

        private static Hideable GetHideable(DrawingObject obj)
        {
            try
            {
                var prop = obj.GetType().GetProperty("Hideable");
                return prop?.GetValue(obj, null) as Hideable;
            }
            catch { return null; }
        }

        // =====================================================================
        // CatF — bolt hole + edge dimensions from live model
        // =====================================================================
        private int AddBoltAndEdgeDimensions(View view, TSModel.Part mainPart, CoordinateSystem partCs)
        {
            var holes = new List<Point>();
            CollectHoles(mainPart, holes);
            var asm = mainPart.GetAssembly();
            if (asm != null)
            {
                foreach (var obj in asm.GetSecondaries() ?? new ArrayList())
                {
                    if (obj is TSModel.Part p)
                        CollectHoles(p, holes);
                }
            }

            var viewPts = holes
                .Select(g => ToView(g, view))
                .Where(p => p != null)
                .ToList();
            if (viewPts.Count < 1) return 0;

            int added = 0;
            var rows = viewPts
                .GroupBy(p => Math.Round(p.Y / 5.0) * 5.0)
                .Where(g => g.Count() >= 2)
                .Take(12);

            foreach (var row in rows)
            {
                var ordered = row.OrderBy(p => p.X).ToList();
                if (CreateDim(view, ordered, new Vector(0, 1, 0), 40))
                    added++;
            }

            var cols = viewPts
                .GroupBy(p => Math.Round(p.X / 5.0) * 5.0)
                .Where(g => g.Count() >= 2)
                .Take(8);

            foreach (var col in cols)
            {
                var ordered = col.OrderBy(p => p.Y).ToList();
                if (CreateDim(view, ordered, new Vector(1, 0, 0), 40))
                    added++;
            }

            // Edge distance: extreme hole to view extrema along X
            try
            {
                var minHole = viewPts.OrderBy(p => p.X).First();
                var edge = new Point(view.RestrictionBox.MinPoint.X, minHole.Y, minHole.Z);
                if (Math.Abs(minHole.X - edge.X) > 8)
                {
                    if (CreateDim(view, new List<Point> { edge, minHole }, new Vector(0, 1, 0), 25))
                        added++;
                }
            }
            catch { /* optional */ }

            return added;
        }

        private static void CollectHoles(TSModel.Part part, List<Point> holes)
        {
            Tekla.Structures.Model.ModelObjectEnumerator bolts;
            try { bolts = part.GetBolts(); }
            catch { return; }
            if (bolts == null) return;
            while (bolts.MoveNext())
            {
                var g = bolts.Current as TSModel.BoltGroup;
                if (g?.BoltPositions == null) continue;
                foreach (Point p in g.BoltPositions)
                {
                    if (p != null) holes.Add(p);
                }
            }
        }

        // =====================================================================
        // CatA — boolean cut / fitting dimensions
        // =====================================================================
        private int AddCutDimensions(View view, TSModel.Part mainPart, CoordinateSystem partCs)
        {
            int added = 0;
            Tekla.Structures.Model.ModelObjectEnumerator booleans;
            try { booleans = mainPart.GetBooleans(); }
            catch { return 0; }
            if (booleans == null) return 0;

            Point origin = ToView(partCs.Origin, view);
            int n = 0;
            while (booleans.MoveNext() && n < 20)
            {
                Point center = null;
                if (booleans.Current is TSModel.BooleanPart bp && bp.OperativePart != null)
                    center = ToView(bp.OperativePart.GetCoordinateSystem().Origin, view);
                else if (booleans.Current is TSModel.CutPlane cp && cp.Plane?.Origin != null)
                    center = ToView(cp.Plane.Origin, view);
                else if (booleans.Current is TSModel.Fitting fit && fit.Plane?.Origin != null)
                    center = ToView(fit.Plane.Origin, view);

                if (center == null || origin == null) continue;
                if (Distance(origin, center) < 8) continue;
                n++;
                if (CreateDim(view, new List<Point> { origin, center }, new Vector(0, 1, 0), 55 + n * 8))
                    added++;
            }
            return added;
        }

        // =====================================================================
        // CatG — tolerance UDA / cuts need a detail mark
        // =====================================================================
        private int AddToleranceDetails(View view, TSModel.Part mainPart, CoordinateSystem partCs)
        {
            string tol = "";
            try { mainPart.GetUserProperty("TOLERANCE_CLASS", ref tol); } catch { /* optional */ }
            if (string.IsNullOrWhiteSpace(tol))
            {
                try { mainPart.GetReportProperty("TOLERANCE_CLASS", ref tol); } catch { /* optional */ }
            }

            bool hasCut = false;
            try
            {
                var b = mainPart.GetBooleans();
                hasCut = b != null && b.GetSize() > 0;
            }
            catch { /* optional */ }

            string finish = "";
            try { finish = mainPart.Finish ?? ""; } catch { /* optional */ }

            if (string.IsNullOrWhiteSpace(tol) && !hasCut && string.IsNullOrWhiteSpace(finish))
                return 0;

            Point center = ToView(partCs.Origin, view);
            if (center == null)
            {
                try { center = view.ExtremaCenter; } catch { return 0; }
            }

            try
            {
                var boundary = new Point(center.X + 40, center.Y + 40, center.Z);
                var label = new Point(center.X + 90, center.Y + 70, center.Z);
                var mark = new DetailMark(view, center, boundary, label);
                if (mark.Insert())
                {
                    var note = new TSDrawing.Text(view, label, string.IsNullOrWhiteSpace(tol) ? "DETAIL" : "TOL " + tol);
                    try { note.Insert(); } catch { /* optional */ }
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Correct] DetailMark: {ex.Message}");
            }
            return 0;
        }

        // =====================================================================
        // CatE — COG + embed labels (precast / any assembly with secondaries)
        // =====================================================================
        private int AddCogAndEmbeds(View view, Drawing drawing, TSModel.Part mainPart, CoordinateSystem partCs)
        {
            int added = 0;
            bool precast = drawing is CastUnitDrawing;

            double cogX = 0, cogY = 0, cogZ = 0;
            try
            {
                mainPart.GetReportProperty("COG_X", ref cogX);
                mainPart.GetReportProperty("COG_Y", ref cogY);
                mainPart.GetReportProperty("COG_Z", ref cogZ);
            }
            catch { /* optional */ }

            if (precast || (Math.Abs(cogX) + Math.Abs(cogY) + Math.Abs(cogZ) > 1))
            {
                var cogView = ToView(new Point(cogX, cogY, cogZ), view);
                if (cogView != null && InsertText(view, cogView, "COG"))
                    added++;
            }

            var asm = mainPart.GetAssembly();
            if (asm == null) return added;
            ArrayList secondaries;
            try { secondaries = asm.GetSecondaries(); }
            catch { return added; }
            if (secondaries == null) return added;

            int n = 0;
            foreach (var obj in secondaries)
            {
                if (!(obj is TSModel.Part embed)) continue;
                n++;
                if (n > 15) break;
                var pt = ToView(embed.GetCoordinateSystem().Origin, view);
                string name = string.IsNullOrWhiteSpace(embed.Name) ? "EMBED" : embed.Name;
                if (pt != null && InsertText(view, pt, name))
                    added++;
            }
            return added;
        }

        // =====================================================================
        // CatB — missing identifying mark on the view
        // =====================================================================
        private int EnsureMarks(View view, Drawing drawing, TSModel.Part mainPart)
        {
            int marks = 0, parts = 0;
            DrawingObjectEnumerator en;
            try { en = view.GetAllObjects(); }
            catch { return 0; }
            while (en != null && en.MoveNext())
            {
                if (en.Current is MarkBase) marks++;
                if (en.Current is TSDrawing.Part) parts++;
            }

            if (marks > 0) return 0;
            string label = drawing.Mark;
            if (string.IsNullOrWhiteSpace(label) && mainPart != null)
            {
                try { label = mainPart.GetPartMark(); } catch { label = drawing.Name; }
            }
            if (string.IsNullOrWhiteSpace(label)) return 0;

            Point at;
            try { at = view.ExtremaCenter; }
            catch { at = view.Origin; }
            at = new Point(at.X, at.Y + view.Height * 0.4, at.Z);
            return InsertText(view, at, label) ? 1 : 0;
        }

        // =====================================================================
        // helpers
        // =====================================================================
        private bool CreateDim(View view, List<Point> pts, Vector up, double distance)
        {
            if (pts == null || pts.Count < 2) return false;
            try
            {
                var list = new PointList();
                foreach (var p in pts)
                    list.Add(new Point(p.X, p.Y, 0));
                var set = _dimHandler.CreateDimensionSet(view, list, up, distance);
                return set != null;
            }
            catch
            {
                try
                {
                    var dim = new StraightDimension(view, pts[0], pts[pts.Count - 1], up, distance);
                    return dim.Insert();
                }
                catch { return false; }
            }
        }

        private static bool InsertText(View view, Point at, string text)
        {
            if (at == null || string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                var t = new TSDrawing.Text(view, at, text);
                return t.Insert();
            }
            catch { return false; }
        }

        private static Point ToView(Point global, View view)
        {
            if (global == null || view == null) return null;
            try
            {
                var m = MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem);
                return m.Transform(global);
            }
            catch { return global; }
        }

        private static View PickPrimaryView(List<View> views)
        {
            if (views == null || views.Count == 0) return null;
            return views.FirstOrDefault(v => v.ViewType == View.ViewTypes.FrontView)
                ?? views.FirstOrDefault(v => v.ViewType == View.ViewTypes.TopView)
                ?? views.FirstOrDefault(v => v.ViewType != View.ViewTypes._3DView)
                ?? views[0];
        }

        private TSModel.Part ResolveMainPart(Drawing drawing)
        {
            try
            {
                Identifier id = null;
                if (drawing is AssemblyDrawing ad) id = ad.AssemblyIdentifier;
                else if (drawing is CastUnitDrawing cu) id = cu.CastUnitIdentifier;
                else if (drawing is SinglePartDrawing sp)
                {
                    var part = new TSModel.Beam { Identifier = sp.PartIdentifier };
                    try { if (part.Select()) return part; } catch { }
                    return null;
                }
                if (id == null || !id.IsValid()) return null;

                var asm = new TSModel.Assembly { Identifier = id };
                if (!asm.Select()) return null;
                return asm.GetMainPart() as TSModel.Part;
            }
            catch { return null; }
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static void AddTotals(CorrectionCounts t, CorrectionCounts c)
        {
            t.CatA_CutDims += c.CatA_CutDims;
            t.CatB_MarksAdded += c.CatB_MarksAdded;
            t.CatC_Shifted += c.CatC_Shifted;
            t.CatD_Hidden += c.CatD_Hidden;
            t.CatE_CogEmbeds += c.CatE_CogEmbeds;
            t.CatF_BoltDims += c.CatF_BoltDims;
            t.CatG_DetailMarks += c.CatG_DetailMarks;
        }

        private static void WriteReport(CorrectionReport report, string baseDir)
        {
            Directory.CreateDirectory(baseDir);
            string json = Path.Combine(baseDir, "correction_report.json");
            File.WriteAllText(json, JsonConvert.SerializeObject(report, Formatting.Indented), Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine("Drawing post-processor (CatA–CatG)");
            sb.AppendLine($"Processed: {report.DrawingsProcessed}  Failed: {report.DrawingsFailed}");
            var t = report.Totals;
            sb.AppendLine($"CatA cuts={t.CatA_CutDims}  CatB marks={t.CatB_MarksAdded}  CatC shifts={t.CatC_Shifted}");
            sb.AppendLine($"CatD hidden={t.CatD_Hidden}  CatE COG/embeds={t.CatE_CogEmbeds}  CatF bolts={t.CatF_BoltDims}  CatG details={t.CatG_DetailMarks}");
            File.WriteAllText(Path.Combine(baseDir, "correction_report.txt"), sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"[Correct] report → {json}");
            Console.WriteLine(sb.ToString());
        }

        private class LabelBox
        {
            public DrawingObject Object;
            public double MinX, MaxX, MinY, MaxY;
            public bool IsDimension;
        }
    }
}

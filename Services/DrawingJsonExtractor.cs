// DrawingJsonExtractor.cs
// Dumps every Tekla drawing (Single-Part / Assembly / Cast Unit / GA / Multi)
// with view CS, dimensions, texts, geometry — plus PDF text+coordinates
// in human-readable form.

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
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using TSDrawing = Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public class DrawingJsonExtractor
    {
        private readonly DrawingHandler _handler;
        private readonly string _baseDir;
        private readonly string _outDir;
        private readonly string _pdfDir;

        public DrawingJsonExtractor(string baseDir)
        {
            _handler = new DrawingHandler();
            _baseDir = baseDir;
            _outDir = Path.Combine(baseDir, "drawing_json");
            _pdfDir = Path.Combine(baseDir, "drawings");
        }

        public bool IsConnected => _handler != null && _handler.GetConnectionStatus();

        public DrawingDumpIndex Run(bool pdfOnly = false)
        {
            Directory.CreateDirectory(_outDir);
            var index = new DrawingDumpIndex
            {
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
            };

            try
            {
                var model = new TSModel.Model();
                if (model.GetConnectionStatus())
                    index.ModelName = model.GetInfo().ModelName ?? "";
            }
            catch { /* optional */ }

            var pdfByStem = IndexPdfs(_pdfDir);
            var usedPdfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (pdfOnly)
            {
                Console.WriteLine($"[DrawingJSON] PDF-only extract of {pdfByStem.Count} files in drawings\\");
                DumpOrphanPdfs(pdfByStem, usedPdfs, index);
                index.PdfCount = pdfByStem.Count;
                WriteIndex(index);
                return index;
            }

            if (!IsConnected)
            {
                Console.WriteLine("[DrawingJSON] DrawingHandler not connected — PDF-only extract.");
                DumpOrphanPdfs(pdfByStem, usedPdfs, index);
                WriteIndex(index);
                return index;
            }

            var drawings = new List<TSDrawing.Drawing>();
            var en = _handler.GetDrawings();
            while (en.MoveNext())
            {
                if (en.Current != null)
                    drawings.Add(en.Current);
            }

            Console.WriteLine($"[DrawingJSON] {drawings.Count} Tekla drawings, {pdfByStem.Count} PDFs.");

            int n = 0;
            foreach (var drawing in drawings)
            {
                n++;
                try
                {
                    var dump = ExtractDrawing(drawing, pdfByStem, usedPdfs);
                    var summary = WriteDrawingFiles(dump);
                    index.Drawings.Add(summary);
                    if (!index.CountsByType.ContainsKey(dump.Type))
                        index.CountsByType[dump.Type] = 0;
                    index.CountsByType[dump.Type]++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DrawingJSON] '{SafeMark(drawing)}' failed: {ex.Message}");
                }

                if (n % 10 == 0)
                    Console.WriteLine($"[DrawingJSON] {n}/{drawings.Count}...");
            }

            DumpOrphanPdfs(pdfByStem, usedPdfs, index);
            index.PdfCount = pdfByStem.Count;
            WriteIndex(index);
            return index;
        }

        private DrawingSheetDump ExtractDrawing(
            TSDrawing.Drawing drawing,
            Dictionary<string, string> pdfByStem,
            HashSet<string> usedPdfs)
        {
            var dump = new DrawingSheetDump
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

            FillLinkedModel(drawing, dump);

            bool opened = false;
            try
            {
                try { _handler.UpdateDrawing(drawing); } catch { /* optional */ }
                opened = _handler.SetActiveDrawing(drawing, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DrawingJSON] open '{dump.Mark}': {ex.Message}");
            }

            try
            {
                var sheet = drawing.GetSheet();
                if (sheet != null)
                {
                    dump.SheetWidth = R(sheet.Width);
                    dump.SheetHeight = R(sheet.Height);
                    dump.SheetOrigin = Xyz(sheet.Origin);
                    try
                    {
                        var size = drawing.Layout?.SheetSize;
                        if (size != null)
                            dump.SheetSize = new[] { R(size.Width), R(size.Height) };
                    }
                    catch { /* layout optional */ }

                    CollectViews(sheet, dump);
                    dump.SheetObjects = CollectObjects(sheet, skipViews: true);
                }
            }
            finally
            {
                if (opened)
                {
                    try { _handler.CloseActiveDrawing(false); } catch { /* best effort */ }
                }
            }

            string pdfPath = MatchPdf(dump.Mark, dump.Name, pdfByStem);
            if (!string.IsNullOrEmpty(pdfPath))
            {
                usedPdfs.Add(pdfPath);
                dump.Pdf = ExtractPdf(pdfPath);
            }

            dump.HumanReadable = BuildReadable(dump);
            return dump;
        }

        private void CollectViews(ContainerView sheet, DrawingSheetDump dump)
        {
            DrawingObjectEnumerator views;
            try { views = sheet.GetAllViews(); }
            catch { views = sheet.GetViews(); }
            if (views == null) return;

            while (views.MoveNext())
            {
                var view = views.Current as View;
                if (view == null) continue;
                dump.Views.Add(ReadView(view));
            }
        }

        private DrawingViewDump ReadView(View view)
        {
            var rec = new DrawingViewDump
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
            return rec;
        }

        private List<DrawingObjectDump> CollectObjects(ViewBase container, bool skipViews)
        {
            var list = new List<DrawingObjectDump>();
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
                catch { /* skip corrupt object */ }
            }
            return list;
        }

        private DrawingObjectDump ReadObject(DrawingObject obj)
        {
            var rec = new DrawingObjectDump
            {
                TypeName = obj.GetType().Name,
                Kind = KindOf(obj),
            };

            try
            {
                var hide = obj.GetType().GetProperty("Hideable");
                if (hide != null)
                {
                    var h = hide.GetValue(obj, null) as Hideable;
                    if (h != null) rec.Hidden = h.IsHidden;
                }
            }
            catch { /* optional */ }

            if (obj is TSDrawing.Text text)
            {
                rec.Text = text.TextString ?? "";
                rec.InsertionPoint = Xyz(text.InsertionPoint);
                FillBox(rec, () => text.GetAxisAlignedBoundingBox());
            }
            else if (obj is MarkBase mark)
            {
                rec.InsertionPoint = Xyz(mark.InsertionPoint);
                rec.Text = FlattenRelated(mark);
                try { FillBox(rec, () => mark.GetAxisAlignedBoundingBox()); } catch { /* some marks lack AABB */ }
            }
            else if (obj is StraightDimension dim)
            {
                rec.StartPoint = Xyz(dim.StartPoint);
                rec.EndPoint = Xyz(dim.EndPoint);
                rec.Distance = R(dim.Distance);
                rec.Text = FlattenContainer(dim.Value);
                rec.Extra = Extra("UpDirection", Format(dim.UpDirection));
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
            }
            else if (obj is RadiusDimension rad)
            {
                rec.Point1 = Xyz(rad.ArcPoint1);
                rec.Point2 = Xyz(rad.ArcPoint2);
                rec.Point3 = Xyz(rad.ArcPoint3);
                rec.Distance = R(rad.Distance);
            }
            else if (obj is LevelMark level)
            {
                rec.InsertionPoint = Xyz(level.InsertionPoint);
                rec.StartPoint = Xyz(level.BasePoint);
                rec.Text = level.SubType.ToString();
                rec.ModelObjectId = level.ModelObjectIdentifier != null ? level.ModelObjectIdentifier.ID : 0;
                rec.ModelObjectGuid = level.ModelObjectIdentifier != null ? level.ModelObjectIdentifier.GUID.ToString() : "";
            }
            else if (obj is TSDrawing.Line line)
            {
                rec.StartPoint = Xyz(line.StartPoint);
                rec.EndPoint = Xyz(line.EndPoint);
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
            else if (obj is Polyline poly)
            {
                rec.Points = PointsOf(poly.Points);
            }
            else if (obj is Polygon polygon)
            {
                rec.Points = PointsOf(polygon.Points);
            }
            else if (obj is SectionMark section)
            {
                rec.StartPoint = Xyz(section.LeftPoint);
                rec.EndPoint = Xyz(section.RightPoint);
            }
            else if (obj is DetailMark detail)
            {
                rec.CenterPoint = Xyz(detail.CenterPoint);
                rec.Point1 = Xyz(detail.BoundaryPoint);
                rec.Point2 = Xyz(detail.LabelPoint);
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

        private static string KindOf(DrawingObject obj)
        {
            if (obj is TSDrawing.Text) return "Text";
            if (obj is MarkBase) return "Mark";
            if (obj is StraightDimension || obj is StraightDimensionSet) return "StraightDimension";
            if (obj is AngleDimension) return "AngleDimension";
            if (obj is RadiusDimension) return "RadiusDimension";
            if (obj is LevelMark) return "LevelMark";
            if (obj is TSDrawing.Line) return "Line";
            if (obj is TSDrawing.Arc) return "Arc";
            if (obj is Circle) return "Circle";
            if (obj is Polyline) return "Polyline";
            if (obj is Polygon) return "Polygon";
            if (obj is SectionMark) return "SectionMark";
            if (obj is DetailMark) return "DetailMark";
            if (obj is TSDrawing.Part) return "Part";
            if (obj is TSDrawing.Bolt) return "Bolt";
            if (obj is TSDrawing.Weld) return "Weld";
            if (obj is ReinforcementBase) return "Reinforcement";
            if (obj is Grid) return "Grid";
            if (obj is View) return "View";
            return obj.GetType().Name;
        }

        private static void FillLinkedModel(TSDrawing.Drawing drawing, DrawingSheetDump dump)
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
            }
            catch { /* optional */ }
        }

        private static string Classify(TSDrawing.Drawing drawing)
        {
            if (drawing is SinglePartDrawing) return "SinglePart";
            if (drawing is AssemblyDrawing) return "Assembly";
            if (drawing is CastUnitDrawing) return "CastUnit";
            if (drawing is GADrawing) return "GA";
            if (drawing is MultiDrawing) return "Multi";
            string s = (drawing.DrawingTypeStr ?? "").ToUpperInvariant();
            if (s.Contains("W") || s.Contains("SINGLE") || s.Contains("PART")) return "SinglePart";
            if (s.Contains("A") || s.Contains("ASSEMBLY")) return "Assembly";
            if (s.Contains("CAST") || s.Contains("CU")) return "CastUnit";
            if (s.Contains("GA") || s.Contains("GENERAL")) return "GA";
            return drawing.GetType().Name;
        }

        public static PdfReadableDump ExtractPdf(string pdfPath)
        {
            var dump = new PdfReadableDump { PdfPath = pdfPath };
            if (!File.Exists(pdfPath)) return dump;

            try
            {
                using (var doc = PdfDocument.Open(pdfPath))
                {
                    dump.PageCount = doc.NumberOfPages;
                    var allText = new StringBuilder();
                    foreach (Page page in doc.GetPages())
                    {
                        var pageDump = new PdfPageDump
                        {
                            PageNumber = page.Number,
                            Width = R(page.Width),
                            Height = R(page.Height),
                            Text = page.Text ?? "",
                        };

                        foreach (Word word in page.GetWords())
                        {
                            var box = word.BoundingBox;
                            double font = 0;
                            try
                            {
                                var letters = word.Letters;
                                if (letters != null && letters.Count > 0)
                                    font = letters[0].PointSize;
                            }
                            catch { /* optional */ }

                            pageDump.Words.Add(new PdfTextItem
                            {
                                Text = word.Text ?? "",
                                X = R(box.Left, 2),
                                Y = R(box.Bottom, 2),
                                Width = R(box.Width, 2),
                                Height = R(box.Height, 2),
                                FontSize = R(font, 2),
                            });
                        }

                        pageDump.Lines = GroupLines(pageDump.Words);
                        dump.Pages.Add(pageDump);
                        if (allText.Length > 0) allText.AppendLine();
                        allText.AppendLine($"--- page {page.Number} ({pageDump.Width} x {pageDump.Height}) ---");
                        foreach (var line in pageDump.Lines)
                            allText.AppendLine(line.Text);
                    }
                    dump.FullText = allText.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                dump.FullText = "PDF extract failed: " + ex.Message;
            }

            return dump;
        }

        private static List<PdfTextLine> GroupLines(List<PdfTextItem> words)
        {
            var lines = new List<PdfTextLine>();
            if (words == null || words.Count == 0) return lines;

            var ordered = words.OrderByDescending(w => w.Y).ThenBy(w => w.X).ToList();
            var current = new List<PdfTextItem>();
            double y = ordered[0].Y;

            void Flush()
            {
                if (current.Count == 0) return;
                current.Sort((a, b) => a.X.CompareTo(b.X));
                string text = string.Join(" ", current.Select(w => w.Text));
                double minX = current.Min(w => w.X);
                double minY = current.Min(w => w.Y);
                double maxX = current.Max(w => w.X + w.Width);
                double maxY = current.Max(w => w.Y + w.Height);
                lines.Add(new PdfTextLine
                {
                    Text = text,
                    X = R(minX, 2),
                    Y = R(minY, 2),
                    Width = R(maxX - minX, 2),
                    Height = R(maxY - minY, 2),
                });
                current.Clear();
            }

            foreach (var w in ordered)
            {
                if (Math.Abs(w.Y - y) > 3.0)
                {
                    Flush();
                    y = w.Y;
                }
                current.Add(w);
            }
            Flush();
            return lines;
        }

        private static string BuildReadable(DrawingSheetDump dump)
        {
            var sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine("TEKLA 2D DRAWING — HUMAN READABLE EXTRACT");
            sb.AppendLine("============================================================");
            sb.AppendLine($"Type            : {dump.Type}  ({dump.TeklaDrawingType})");
            sb.AppendLine($"Mark            : {dump.Mark}");
            sb.AppendLine($"Name            : {dump.Name}");
            sb.AppendLine($"Titles          : {dump.Title1} | {dump.Title2} | {dump.Title3}");
            sb.AppendLine($"UpToDate        : {dump.UpToDateStatus}");
            sb.AppendLine($"Linked model    : {dump.LinkedModelObject} id={dump.LinkedModelId} guid={dump.LinkedModelGuid}");
            sb.AppendLine($"Sheet size mm   : {dump.SheetWidth} x {dump.SheetHeight}  origin=({Join(dump.SheetOrigin)})");
            if (dump.SheetSize != null && dump.SheetSize.Length == 2)
                sb.AppendLine($"Layout paper    : {dump.SheetSize[0]} x {dump.SheetSize[1]}");
            sb.AppendLine();

            sb.AppendLine($"VIEWS ({dump.Views.Count})");
            foreach (var v in dump.Views)
            {
                sb.AppendLine($"  • {v.ViewType}  name='{v.Name}'  scale=1:{v.Scale}  size={v.Width}x{v.Height}");
                sb.AppendLine($"      origin=({Join(v.Origin)})  extrema=({Join(v.ExtremaCenter)})");
                sb.AppendLine($"      viewCS origin=({Join(v.ViewCsOrigin)})  X=({Join(v.ViewCsX)})  Y=({Join(v.ViewCsY)})");
                sb.AppendLine($"      restriction min=({Join(v.RestrictionBoxMin)}) max=({Join(v.RestrictionBoxMax)})");

                var dims = v.Objects.Where(o => o.Kind.Contains("Dimension")).ToList();
                var texts = v.Objects.Where(o => o.Kind == "Text" || o.Kind == "Mark").ToList();
                sb.AppendLine($"      objects={v.Objects.Count}  dimensions={dims.Count}  texts/marks={texts.Count}");
                foreach (var d in dims)
                {
                    sb.AppendLine($"        DIM {d.Kind}  text='{d.Text}'  dist={d.Distance}  " +
                                  $"start=({Join(d.StartPoint)}) end=({Join(d.EndPoint)}) center=({Join(d.CenterPoint)})");
                }
                foreach (var t in texts.Take(80))
                    sb.AppendLine($"        {t.Kind} '{t.Text}' @ ({Join(t.InsertionPoint)})");
            }

            sb.AppendLine();
            sb.AppendLine("PDF TEXT (human readable, with page coordinates)");
            if (dump.Pdf == null || dump.Pdf.PageCount == 0)
            {
                sb.AppendLine("  (no matching PDF in drawings\\)");
            }
            else
            {
                sb.AppendLine($"  file: {dump.Pdf.PdfPath}  pages={dump.Pdf.PageCount}");
                foreach (var page in dump.Pdf.Pages)
                {
                    sb.AppendLine($"  Page {page.PageNumber}  {page.Width} x {page.Height} pt");
                    foreach (var line in page.Lines)
                        sb.AppendLine($"    [{line.X,8:0.0},{line.Y,8:0.0}]  {line.Text}");
                }
            }

            return sb.ToString();
        }

        private DrawingDumpSummary WriteDrawingFiles(DrawingSheetDump dump)
        {
            string typeDir = Path.Combine(_outDir, dump.Type);
            string readableDir = Path.Combine(_outDir, "human_readable", dump.Type);
            Directory.CreateDirectory(typeDir);
            Directory.CreateDirectory(readableDir);

            string stem = SafeFile(string.IsNullOrWhiteSpace(dump.Mark) ? dump.Name : dump.Mark);
            string jsonPath = UniquePath(Path.Combine(typeDir, stem + ".json"));
            string txtPath = UniquePath(Path.Combine(readableDir, stem + ".txt"));

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            };
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(dump, settings), Encoding.UTF8);
            File.WriteAllText(txtPath, dump.HumanReadable ?? "", Encoding.UTF8);

            int objCount = dump.SheetObjects.Count + dump.Views.Sum(v => v.Objects.Count);
            int dimCount = dump.Views.SelectMany(v => v.Objects).Count(o => o.Kind.Contains("Dimension"))
                           + dump.SheetObjects.Count(o => o.Kind.Contains("Dimension"));

            Console.WriteLine($"[DrawingJSON] {dump.Type} '{dump.Mark}' views={dump.Views.Count} objects={objCount} → {jsonPath}");

            return new DrawingDumpSummary
            {
                Type = dump.Type,
                Mark = dump.Mark,
                Name = dump.Name,
                JsonPath = jsonPath,
                ReadablePath = txtPath,
                PdfPath = dump.Pdf?.PdfPath ?? "",
                ViewCount = dump.Views.Count,
                ObjectCount = objCount,
                DimensionCount = dimCount,
                PdfTextCount = dump.Pdf?.Pages.Sum(p => p.Words.Count) ?? 0,
            };
        }

        private void DumpOrphanPdfs(
            Dictionary<string, string> pdfByStem,
            HashSet<string> usedPdfs,
            DrawingDumpIndex index)
        {
            foreach (var kv in pdfByStem)
            {
                if (usedPdfs.Contains(kv.Value)) continue;
                try
                {
                    var dump = new DrawingSheetDump
                    {
                        Type = "PdfOnly",
                        Mark = kv.Key,
                        Name = Path.GetFileName(kv.Value),
                        Pdf = ExtractPdf(kv.Value),
                    };
                    dump.HumanReadable = BuildReadable(dump);
                    var summary = WriteDrawingFiles(dump);
                    index.Drawings.Add(summary);
                    if (!index.CountsByType.ContainsKey("PdfOnly"))
                        index.CountsByType["PdfOnly"] = 0;
                    index.CountsByType["PdfOnly"]++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DrawingJSON] PDF {kv.Value}: {ex.Message}");
                }
            }
        }

        private void WriteIndex(DrawingDumpIndex index)
        {
            string path = Path.Combine(_outDir, "index.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(index, Formatting.Indented), Encoding.UTF8);

            var summary = new StringBuilder();
            summary.AppendLine("Drawing JSON extract");
            summary.AppendLine($"Model: {index.ModelName}");
            summary.AppendLine($"Generated: {index.GeneratedUtc}");
            summary.AppendLine();
            foreach (var kv in index.CountsByType.OrderBy(k => k.Key))
                summary.AppendLine($"  {kv.Key}: {kv.Value}");
            summary.AppendLine();
            foreach (var d in index.Drawings)
                summary.AppendLine($"{d.Type,-12} {d.Mark,-24} views={d.ViewCount,-3} dims={d.DimensionCount,-4} pdfWords={d.PdfTextCount}");
            File.WriteAllText(Path.Combine(_outDir, "index.txt"), summary.ToString(), Encoding.UTF8);

            Console.WriteLine($"[DrawingJSON] index → {path}");
            Console.WriteLine($"[DrawingJSON] human readable → {Path.Combine(_outDir, "human_readable")}");
        }

        private static Dictionary<string, string> IndexPdfs(string pdfDir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(pdfDir)) return map;
            foreach (var file in Directory.GetFiles(pdfDir, "*.pdf"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (!map.ContainsKey(stem))
                    map[stem] = file;
            }
            return map;
        }

        private static string MatchPdf(string mark, string name, Dictionary<string, string> pdfs)
        {
            foreach (var key in new[] { mark, name, StripBrackets(mark), StripBrackets(name) })
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (pdfs.TryGetValue(key, out var path)) return path;
                foreach (var kv in pdfs)
                {
                    if (kv.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;
                }
            }
            return "";
        }

        private static string StripBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim().TrimStart('[').TrimEnd(']');
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

        private static void FillModelId(DrawingObjectDump rec, Identifier id)
        {
            if (id == null) return;
            rec.ModelObjectId = id.ID;
            rec.ModelObjectGuid = id.GUID.ToString();
        }

        private static void FillBox(DrawingObjectDump rec, Func<RectangleBoundingBox> fn)
        {
            try
            {
                var box = fn();
                if (box == null) return;
                rec.BoundingBoxMin = Xyz(box.MinPoint);
                rec.BoundingBoxMax = Xyz(box.MaxPoint);
                rec.Width = R(box.Width);
                rec.Height = R(box.Height);
            }
            catch { /* optional */ }
        }

        private static Dictionary<string, string> Extra(string k, string v)
        {
            return new Dictionary<string, string> { { k, v ?? "" } };
        }

        private static double[] Xyz(Point p)
        {
            if (p == null) return new double[3];
            return new[] { R(p.X), R(p.Y), R(p.Z) };
        }

        private static double[] Xyz(Vector v)
        {
            if (v == null) return new double[3];
            return new[] { R(v.X, 4), R(v.Y, 4), R(v.Z, 4) };
        }

        private static string Format(Vector v)
        {
            if (v == null) return "";
            return $"{R(v.X, 4)},{R(v.Y, 4)},{R(v.Z, 4)}";
        }

        private static string Join(double[] a)
        {
            if (a == null || a.Length == 0) return "";
            return string.Join(", ", a.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        private static double R(double v, int d = 2) => Math.Round(v, d);

        private static string SafeMark(TSDrawing.Drawing d)
        {
            try { return d.Mark ?? d.Name ?? "?"; } catch { return "?"; }
        }

        private static string SafeFile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "drawing";
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int n = 2;
            string next;
            do
            {
                next = Path.Combine(dir ?? "", stem + "_" + n + ext);
                n++;
            } while (File.Exists(next));
            return next;
        }
    }
}

// ShopDrawingExport.cs
// Writes JSON, SVG, DXF, TXT and a validation report for one piece ticket.
// SVG is rebuilt from sheet-space contours / graphics / dimensions / texts
// so a civil engineer can open it without Tekla.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace TeklaExtractor.Services
{
    public static class ShopDrawingExport
    {
        public static void WriteAll(ShopDrawing shop, string outputRoot)
        {
            string stem = SafeFile(First(shop.CastUnitMark, shop.DrawingMark, "cu"));
            string dir = Path.Combine(outputRoot, stem);
            Directory.CreateDirectory(dir);

            shop.Validation = Validate(shop, shop.Outputs.PdfPath);

            string json = JsonConvert.SerializeObject(shop, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            });

            shop.Outputs.JsonPath = Path.Combine(dir, stem + ".json");
            shop.Outputs.SvgPath = Path.Combine(dir, stem + ".svg");
            shop.Outputs.DxfPath = Path.Combine(dir, stem + ".dxf");
            shop.Outputs.TxtPath = Path.Combine(dir, stem + ".txt");
            shop.Outputs.HtmlPath = Path.Combine(dir, stem + ".html");
            shop.Outputs.ValidationPath = Path.Combine(dir, stem + ".validation.json");
            if (string.IsNullOrEmpty(shop.Outputs.PdfPath))
                shop.Outputs.PdfPath = Path.Combine(dir, stem + ".pdf");

            // PDF first so validation can count PDF words.
            TryPrintPdf(shop);

            shop.Validation = Validate(shop, shop.Outputs.PdfPath);
            json = JsonConvert.SerializeObject(shop, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            });

            File.WriteAllText(shop.Outputs.JsonPath, json, Encoding.UTF8);
            File.WriteAllText(shop.Outputs.SvgPath, BuildSvg(shop), Encoding.UTF8);
            File.WriteAllText(shop.Outputs.HtmlPath, BuildHtml(shop), Encoding.UTF8);
            File.WriteAllText(shop.Outputs.DxfPath, BuildDxf(shop), Encoding.UTF8);
            File.WriteAllText(shop.Outputs.TxtPath, BuildTxt(shop), Encoding.UTF8);
            File.WriteAllText(
                shop.Outputs.ValidationPath,
                JsonConvert.SerializeObject(shop.Validation, Formatting.Indented),
                Encoding.UTF8);
        }

        public static void TryPrintPdf(ShopDrawing shop)
        {
            if (string.IsNullOrEmpty(shop.Outputs.PdfPath)) return;
            try
            {
                var printer = new DrawingGenerator();
                var handler = new Tekla.Structures.Drawing.DrawingHandler();
                if (handler == null || !handler.GetConnectionStatus()) return;
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var d = en.Current as Tekla.Structures.Drawing.CastUnitDrawing;
                    if (d == null) continue;
                    if (!string.Equals(d.Mark, shop.DrawingMark, StringComparison.OrdinalIgnoreCase))
                        continue;
                    printer.ExportToPdf(d, shop.Outputs.PdfPath);
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Shop] PDF: " + ex.Message);
            }
        }

        public static ShopValidation Validate(ShopDrawing shop, string pdfPath)
        {
            var v = new ShopValidation();
            if (shop?.Sheet?.Views == null)
            {
                v.Status = "FAIL";
                v.Missing.Add("No views");
                return v;
            }

            v.ViewCount = shop.Sheet.Views.Count;
            foreach (var view in shop.Sheet.Views)
            {
                int contours = view.Parts.Count(p => p.OuterContour != null && p.OuterContour.Sheet != null && p.OuterContour.Sheet.Count >= 3);
                int graphics = view.GraphicObjects.Count;
                int opens = view.Openings.Count(o => o.OuterContour != null && o.OuterContour.Sheet != null && o.OuterContour.Sheet.Count >= 3);
                v.PartContourCount += contours;
                v.GraphicObjectCount += graphics;
                v.OpeningCount += view.Openings.Count;
                v.RebarCount += view.Reinforcement.Count;
                v.HardwareCount += view.Hardware.Count;
                v.DimensionCount += view.Dimensions.Count;
                v.MarkCount += view.Marks.Count;
                v.ExtractedTextCount += view.Texts.Count;

                bool drawable = contours > 0 || graphics > 0 || opens > 0
                    || view.Reinforcement.Any(r => r.Polyline2dSheet != null && r.Polyline2dSheet.Count > 1);
                if (drawable) v.ViewsWithGeometry++;
                else
                {
                    v.ViewsFailedEmpty++;
                    v.Missing.Add("View '" + view.Name + "' has no part contours, openings or graphic objects.");
                }
            }

            if (v.PartContourCount == 0 && v.GraphicObjectCount == 0)
            {
                v.Missing.Add("No drawable part contours or graphic objects in any view.");
            }

            if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
            {
                try
                {
                    using (var doc = PdfDocument.Open(pdfPath))
                    {
                        v.PdfPageCount = doc.NumberOfPages;
                        int words = 0;
                        foreach (Page page in doc.GetPages())
                            words += page.GetWords().Count();
                        v.PdfWordCount = words;
                    }
                    v.Notes.Add("Tekla PDF exported for visual check (pixel raster compare needs an external viewer).");
                }
                catch (Exception ex)
                {
                    v.Notes.Add("PDF parse: " + ex.Message);
                }
            }
            else
            {
                v.Notes.Add("Tekla PDF missing — print failed or numbering not up to date.");
            }

            bool fail = v.ViewsFailedEmpty > 0
                || (v.PartContourCount == 0 && v.GraphicObjectCount == 0);
            v.Passed = !fail && v.ViewCount > 0;
            v.Status = v.Passed ? "PASS" : "FAIL";
            if (v.Passed)
                v.Notes.Add("Views contain drawable geometry (contours and/or linework). Bounding-box-only extraction rejected.");
            return v;
        }

        public static string BuildSvg(ShopDrawing shop)
        {
            double w = shop.Sheet.Width > 1 ? shop.Sheet.Width : 432;
            double h = shop.Sheet.Height > 1 ? shop.Sheet.Height : 279;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}mm\" height=\"{F(h)}mm\" viewBox=\"0 0 {F(w)} {F(h)}\">");
            sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"#fff\" stroke=\"#222\" stroke-width=\"0.3\"/>");
            sb.AppendLine($"  <g transform=\"translate(0,{F(h)}) scale(1,-1)\">");

            foreach (var view in shop.Sheet.Views)
            {
                foreach (var p in view.Parts)
                {
                    Poly(sb, p.OuterContour, "#1a1a1a", 0.35, "#f4f0e6", 0.35);
                    foreach (var inner in p.InnerContours)
                        Poly(sb, inner, "#c0392b", 0.25, "#fff", 0.9);
                    foreach (var e in p.HiddenEdges)
                        Poly(sb, e, "#888", 0.12, "none", 0);
                }
                foreach (var o in view.Openings)
                    Poly(sb, o.OuterContour, "#c0392b", 0.3, "#fff", 0.95);
                foreach (var hw in view.Hardware)
                    Poly(sb, hw.VisibleContour, "#1f6feb", 0.25, "none", 0);
                foreach (var r in view.Reinforcement)
                    LineStrip(sb, r.Polyline2dSheet, "#2e7d32", 0.18);
                foreach (var g in view.GraphicObjects)
                    LineStrip(sb, g.PointsSheet, "#444", 0.12, g.Closed);
                foreach (var d in view.Dimensions)
                {
                    if (d.StartSheet == null || d.EndSheet == null) continue;
                    sb.AppendLine($"    <line x1=\"{F(d.StartSheet[0])}\" y1=\"{F(d.StartSheet[1])}\" x2=\"{F(d.EndSheet[0])}\" y2=\"{F(d.EndSheet[1])}\" stroke=\"#0b57d0\" stroke-width=\"0.15\"/>");
                }
            }

            sb.AppendLine("  </g>");

            foreach (var view in shop.Sheet.Views)
            {
                foreach (var t in view.Texts)
                    Text(sb, t.Text, t.InsertionSheet ?? t.InsertionPoint, h, 2.2);
                foreach (var m in view.Marks)
                    Text(sb, m.Text, m.InsertionSheet ?? m.InsertionPoint, h, 2.0);
                foreach (var d in view.Dimensions)
                    Text(sb, d.DisplayedValue, d.TextSheet ?? d.TextPosition, h, 1.8);
            }
            foreach (var c in shop.Sheet.TitleBlock.Cells)
                Text(sb, c.Text, c.InsertionSheet ?? c.InsertionPoint, h, 2.0);

            sb.AppendLine($"  <text x=\"4\" y=\"{F(h - 4)}\" font-size=\"3\" fill=\"#333\">{Esc(shop.CastUnitMark + "  " + shop.DrawingMark)}</text>");
            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        /// <summary>
        /// Standalone HTML 2D shop drawing: JSON geometry as SVG + original PDF beside it.
        /// </summary>
        public static string BuildHtml(ShopDrawing shop)
        {
            string svg = BuildSvg(shop);
            int svgStart = svg.IndexOf("<svg", StringComparison.Ordinal);
            if (svgStart >= 0) svg = svg.Substring(svgStart);
            string pdf = Path.GetFileName(shop.Outputs.PdfPath ?? "");
            string mark = Esc(shop.CastUnitMark);
            string title = Esc(shop.CastUnitMark + "  " + shop.DrawingMark);
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"UTF-8\"/>");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
            sb.AppendLine($"<title>{title} — HTML 2D shop drawing</title>");
            sb.AppendLine("<link rel=\"stylesheet\" href=\"../drawing-2d.css\"/>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body data-drawing2d=\"1\">");
            sb.AppendLine("<header class=\"top\"><div class=\"brand\">" + mark +
                "<small>JSON + PDF converted to HTML 2D drawing</small></div>");
            sb.AppendLine("<nav class=\"nav\"><a href=\"../index.html\">Hub</a><a class=\"active\" href=\"../drawing-2d.html\">2D drawings</a>");
            sb.AppendLine("<a href=\"../floor-plans.html\">Floor plans</a><a href=\"../pdf-review.html\">PDF sequence</a></nav></header>");
            sb.AppendLine("<div class=\"toolbar\">");
            sb.AppendLine("<button type=\"button\" data-mode=\"2d\" class=\"on\">2D HTML</button>");
            sb.AppendLine("<button type=\"button\" data-mode=\"pdf\">Original PDF</button>");
            sb.AppendLine("<button type=\"button\" data-mode=\"split\">Split</button>");
            sb.AppendLine("<button type=\"button\" id=\"fit\">Fit</button></div>");
            sb.AppendLine("<div class=\"layout\"><aside class=\"rail\"><h3>Layers</h3>");
            sb.AppendLine("<p class=\"meta\">Geometry rebuilt from sheet-space contours in JSON.</p></aside>");
            sb.AppendLine("<div class=\"stage\" id=\"stage\"><div class=\"viewport\" id=\"viewport\">");
            sb.AppendLine("<div class=\"sheet-wrap\" id=\"sheet-wrap\">");
            sb.AppendLine(svg);
            sb.AppendLine("</div></div>");
            if (!string.IsNullOrEmpty(pdf))
                sb.AppendLine($"<iframe class=\"pdf-frame hidden\" id=\"pdf-frame\" title=\"PDF\" src=\"{Esc(pdf)}\"></iframe>");
            sb.AppendLine("</div><aside class=\"side\"><h3>Source</h3>");
            sb.AppendLine($"<p><a class=\"btn\" href=\"{mark}.json\">JSON</a> <a class=\"btn\" href=\"{mark}.svg\">SVG</a>");
            if (!string.IsNullOrEmpty(pdf))
                sb.AppendLine($"<a class=\"btn\" href=\"{Esc(pdf)}\">PDF</a>");
            sb.AppendLine("</p></aside></div>");
            sb.AppendLine("<p class=\"hint\">Wheel zoom · drag pan · F fit · 1 HTML 2D · 2 PDF · 3 split</p>");
            sb.AppendLine("<script src=\"../drawing-2d.js\"></script></body></html>");
            return sb.ToString();
        }

        public static string BuildDxf(ShopDrawing shop)
        {
            var sb = new StringBuilder();
            sb.Append("0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n");
            foreach (var view in shop.Sheet.Views)
            {
                foreach (var p in view.Parts)
                {
                    DxfPoly(sb, p.OuterContour);
                    foreach (var i in p.InnerContours) DxfPoly(sb, i);
                    foreach (var e in p.HiddenEdges) DxfPoly(sb, e);
                }
                foreach (var o in view.Openings) DxfPoly(sb, o.OuterContour);
                foreach (var hw in view.Hardware) DxfPoly(sb, hw.VisibleContour);
                foreach (var r in view.Reinforcement) DxfStrip(sb, r.Polyline2dSheet);
                foreach (var g in view.GraphicObjects) DxfStrip(sb, g.PointsSheet);
                foreach (var d in view.Dimensions)
                {
                    if (d.StartSheet != null && d.EndSheet != null)
                        DxfLine(sb, d.StartSheet, d.EndSheet);
                }
            }
            sb.Append("0\nENDSEC\n0\nEOF\n");
            return sb.ToString();
        }

        public static string BuildTxt(ShopDrawing shop)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PRECAST PIECE TICKET / CAST UNIT SHOP DRAWING");
            sb.AppendLine("============================================");
            sb.AppendLine($"Cast unit : {shop.CastUnitMark}");
            sb.AppendLine($"Drawing   : {shop.DrawingMark}  {shop.DrawingName}");
            sb.AppendLine($"Titles    : {shop.Title1} | {shop.Title2} | {shop.Title3}");
            sb.AppendLine($"Revision  : {shop.Revision}   floor={shop.FloorLevel}");
            sb.AppendLine($"Sheet     : {shop.Sheet.Width} x {shop.Sheet.Height} mm");
            sb.AppendLine($"Status    : {shop.Validation?.Status}");
            sb.AppendLine();
            foreach (var t in shop.Sheet.Tables)
            {
                sb.AppendLine($"TABLE {t.Kind}  {t.Title}");
                foreach (var r in t.Rows)
                    sb.AppendLine($"  {r.PartMark,-10} {r.Profile,-16} {r.Material,-10} qty={r.Quantity} L={r.Length} W={r.Weight}");
                sb.AppendLine();
            }
            foreach (var v in shop.Sheet.Views)
            {
                sb.AppendLine($"VIEW {v.Name}  type={v.ViewType}  scale=1:{v.Scale}  section={v.SectionIdentifier}");
                sb.AppendLine($"  parts={v.Parts.Count} openings={v.Openings.Count} rebar={v.Reinforcement.Count} hardware={v.Hardware.Count}");
                sb.AppendLine($"  dims={v.Dimensions.Count} marks={v.Marks.Count} graphics={v.GraphicObjects.Count} drawable={v.DrawableCount}");
                foreach (var p in v.Parts)
                    sb.AppendLine($"  PART {p.PartMark} {p.Profile} outerPts={p.OuterContour?.Sheet?.Count ?? 0} inner={p.InnerContours.Count}");
            }
            if (shop.Validation?.Missing != null)
            {
                foreach (var m in shop.Validation.Missing)
                    sb.AppendLine("MISSING: " + m);
            }
            return sb.ToString();
        }

        private static void Poly(StringBuilder sb, ShopContour c, string stroke, double sw, string fill, double fo)
        {
            if (c?.Sheet == null || c.Sheet.Count < 2) return;
            var pts = string.Join(" ", c.Sheet.Select(p => F(p[0]) + "," + F(p[1])));
            string tag = c.Closed ? "polygon" : "polyline";
            sb.AppendLine($"    <{tag} points=\"{pts}\" fill=\"{fill}\" fill-opacity=\"{fo.ToString(CultureInfo.InvariantCulture)}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>");
        }

        private static void LineStrip(StringBuilder sb, System.Collections.Generic.List<double[]> pts, string stroke, double sw, bool closed = false)
        {
            if (pts == null || pts.Count < 2) return;
            var s = string.Join(" ", pts.Select(p => F(p[0]) + "," + F(p[1])));
            string tag = closed ? "polygon" : "polyline";
            sb.AppendLine($"    <{tag} points=\"{s}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>");
        }

        private static void Text(StringBuilder sb, string text, double[] xy, double sheetH, double size)
        {
            if (string.IsNullOrWhiteSpace(text) || xy == null || xy.Length < 2) return;
            double y = sheetH - xy[1];
            sb.AppendLine($"  <text x=\"{F(xy[0])}\" y=\"{F(y)}\" font-size=\"{F(size)}\" fill=\"#111\" font-family=\"Arial,sans-serif\">{Esc(text)}</text>");
        }

        private static void DxfPoly(StringBuilder sb, ShopContour c)
        {
            if (c?.Sheet == null) return;
            DxfStrip(sb, c.Sheet);
        }

        private static void DxfStrip(StringBuilder sb, System.Collections.Generic.List<double[]> pts)
        {
            if (pts == null || pts.Count < 2) return;
            for (int i = 1; i < pts.Count; i++)
                DxfLine(sb, pts[i - 1], pts[i]);
        }

        private static void DxfLine(StringBuilder sb, double[] a, double[] b)
        {
            if (a == null || b == null) return;
            sb.Append("0\nLINE\n8\n0\n10\n").Append(F(a[0])).Append("\n20\n").Append(F(a[1]))
              .Append("\n11\n").Append(F(b[0])).Append("\n21\n").Append(F(b[1])).Append('\n');
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string SafeFile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "drawing";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace(' ', '_').Replace('[', '_').Replace(']', '_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s.Trim('_');
        }

        private static string First(params string[] v)
        {
            foreach (var s in v)
                if (!string.IsNullOrWhiteSpace(s)) return s;
            return "drawing";
        }
    }
}

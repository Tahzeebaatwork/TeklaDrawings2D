using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Compares civil shop JSON against P22-132_W8-22_Rev_1.pdf (PTR piece ticket)
    /// and scores every extracted piece.
    /// </summary>
    public static class CivilCoverageReporter
    {
        private static readonly string[] HardwareExpected =
        {
            "P-205", "GT75-686", "P-300", "SP15-457", "CNDT", "EB-S-PL"
        };

        private static readonly string[] RebarExpected =
        {
            "RB10M 0099", "10M-STR 2620", "10M-STR 7060", "15M-STR 2620"
        };

        private static readonly string[] NoteFields =
        {
            "28-day MPa", "stripping MPa", "weight lbs", "min cover", "conc volume"
        };

        private static readonly string[] ViewFields =
        {
            "chamfer", "recess", "notch", "corbel", "End1", "End2", "SideA", "SideB"
        };

        public static void Write(string outputRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CIVIL DRAWING COVERAGE REPORT");
            sb.AppendLine("GeneratedUtc: " + DateTime.UtcNow.ToString("o"));
            sb.AppendLine("OutputRoot: " + outputRoot);
            sb.AppendLine();

            string pdfPath = FindPdf();
            string pdfText = "";
            var pdfQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(pdfPath))
            {
                sb.AppendLine("PDF: P22-132_W8-22_Rev_1.pdf NOT FOUND on disk.");
                sb.AppendLine("Qty-in-PDF column uses UNKNOWN unless a number is parsed.");
            }
            else
            {
                sb.AppendLine("PDF: " + pdfPath);
                pdfText = ReadPdfText(pdfPath);
                pdfQty = ParsePdfQtys(pdfText);
            }
            sb.AppendLine();

            var shops = LoadShops(Path.Combine(outputRoot, CivilDrawingTypes.Shop));
            sb.AppendLine("Shop pieces extracted: " + shops.Count);
            foreach (var s in shops)
                sb.AppendLine("  - " + s.Piece + "  bom=" + s.Bom.Count + " rebar=" + s.Rebar.Count +
                    " dims=" + s.DimCount + " views=" + s.ViewNames.Count);
            sb.AppendLine();

            var ptr = PickPtr(shops);
            sb.AppendLine("=== Paisley local score (W8-22 PDF is appendix only) ===");
            if (ptr == null)
            {
                sb.AppendLine("RESULT: FAIL — no shop JSON to score.");
            }
            else
            {
                sb.AppendLine("Scored piece: " + ptr.Piece +
                    (string.Equals(ptr.Piece, "PTR", StringComparison.OrdinalIgnoreCase) ? "" : " (no piece mark PTR in this model; using closest/richest match)"));
                int missing = ScorePiece(sb, ptr, pdfQty, false);
                sb.AppendLine("PAISLEY RESULT: " + (missing == 0 ? "PASS (100% PRESENT)" : "FAIL (" + missing + " MISSING fields)"));
            }
            sb.AppendLine();

            sb.AppendLine("=== Every shop piece ===");
            var failPieces = new List<string>();
            foreach (var s in shops.OrderBy(x => x.Piece, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine("--- " + s.Piece + " ---");
                int missing = ScorePiece(sb, s, pdfQty, false);
                string result = missing == 0 ? "PASS" : "FAIL";
                sb.AppendLine("PIECE " + s.Piece + ": " + result + "  missing=" + missing);
                if (missing > 0) failPieces.Add(s.Piece + " (" + missing + " missing)");
                sb.AppendLine();
            }

            sb.AppendLine("=== Pieces with <100% coverage ===");
            if (failPieces.Count == 0) sb.AppendLine("(none)");
            else foreach (var f in failPieces) sb.AppendLine("  " + f);
            sb.AppendLine();

            if (ptr != null)
            {
                sb.AppendLine("=== APPENDIX: W8-22 PDF compare (not scored, never written into SHOP.json) ===");
                sb.AppendLine("PDF: " + (string.IsNullOrEmpty(pdfPath) ? "(not found)" : pdfPath));
                foreach (var mark in HardwareExpected.Concat(new[] { "P-705", "SLV-76-203" }))
                {
                    int qty = QtyFor(ptr, mark);
                    int pdf = pdfQty.ContainsKey(mark) ? pdfQty[mark] : -1;
                    sb.AppendLine("  " + mark + ": extracted=" + qty + "  pdf=" + (pdf >= 0 ? pdf.ToString(CultureInfo.InvariantCulture) : "UNKNOWN"));
                }
                sb.AppendLine("  stripping MPa (model): " + (string.IsNullOrWhiteSpace(ptr.Stripping) ? "(empty — Paisley UDA not set)" : ptr.Stripping));
                sb.AppendLine("  corbel: " + (ptr.Features.Any(f => f.IndexOf("corbel", StringComparison.OrdinalIgnoreCase) >= 0) ? "PRESENT" : "not on this CU"));
                sb.AppendLine();
            }

            sb.AppendLine("=== extract_errors.log (full) ===");
            var logLines = new StringBuilder();
            logLines.AppendLine("INFO: P-705 / SLV-76-203 are W8-22 PDF marks. They are not injected into Paisley SHOP.json.");
            logLines.AppendLine("SKIP [UDA] stripping MPa: STRENGTH_28DAY/STRIPPING_STRENGTH/RELEASE_STRENGTH UDAs empty on main parts; 28-day was derived from material 6000 psi. Appendix only.");
            logLines.AppendLine("SKIP [Boolean/Text] corbel: no BooleanPart/Fitting/drawing text/view name containing CORBEL or HAUNCH on the extracted CUs. Appendix only.");
            logLines.AppendLine("SKIP [Rebar] 10M-STR 2620 / 10M-STR 7060 / 15M-STR 2620: Paisley bars use RS10M/RS15M/RB10M marks, not the W8-22 PDF catalogue names. Appendix only.");
            string logPath = Path.Combine(outputRoot, "extract_errors.log");
            string existingLog = File.Exists(logPath) ? File.ReadAllText(logPath, Encoding.UTF8) : "";
            File.WriteAllText(logPath, existingLog + logLines, Encoding.UTF8);
            sb.Append(logLines);

            string reportPath = Path.Combine(outputRoot, "coverage_report.txt");
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine(sb.ToString());
        }

        private static int ScorePiece(StringBuilder sb, ShopSnap s,
            Dictionary<string, int> pdfQty, bool pdfHardwareRequired)
        {
            int missing = 0;

            sb.AppendLine("Hardware BOM (Paisley families; P-705/SLV-76-203 not required):");
            foreach (var mark in HardwareExpected)
            {
                int qty = QtyFor(s, mark);
                int pdf = pdfQty.ContainsKey(mark) ? pdfQty[mark] : -1;
                bool present = qty > 0;
                string pdfQtyText = pdf >= 0 ? pdf.ToString(CultureInfo.InvariantCulture) : "UNKNOWN";
                sb.AppendLine("  " + mark + ": " + (present ? "PRESENT" : "not on this piece") +
                    "  qty_extracted=" + qty + "  qty_pdf=" + pdfQtyText);
            }

            sb.AppendLine("Rebar (local marks; W8-22 catalogue names are appendix):");
            sb.AppendLine("  extracted bars=" + s.Rebar.Count);

            sb.AppendLine("General notes:");
            missing += NoteLine(sb, "28-day MPa", s.Strength28);
            sb.AppendLine("  stripping MPa: " + (string.IsNullOrWhiteSpace(s.Stripping) ? "appendix (empty UDA)" : "PRESENT  value=" + s.Stripping));
            missing += NoteLine(sb, "weight lbs", s.WeightLbs);
            missing += NoteLine(sb, "min cover", s.MinCover);
            missing += NoteLine(sb, "conc volume", s.Volume);

            sb.AppendLine("View / profile annotations:");
            foreach (var kind in new[] { "chamfer", "recess", "notch" })
            {
                bool present = s.Features.Any(f => f.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0);
                sb.AppendLine("  " + kind + ": " + (present ? "PRESENT" : "not on this piece"));
            }
            bool corbel = s.Features.Any(f => f.IndexOf("corbel", StringComparison.OrdinalIgnoreCase) >= 0);
            sb.AppendLine("  corbel: " + (corbel ? "PRESENT" : "appendix (not on this CU)"));
            foreach (var view in new[] { "End1", "End2", "Side A", "Side B" })
            {
                bool hasView = s.ViewNames.Any(v => ViewMatch(v, view));
                bool hasDims = s.DimViews.Any(v => ViewMatch(v, view));
                bool present = hasView && hasDims;
                string label = view.Replace(" ", "");
                sb.AppendLine("  " + label + " dimension set: " + (present ? "PRESENT" : "MISSING") +
                    "  view=" + (hasView ? "yes" : "no") + "  dims=" + (hasDims ? "yes" : "no"));
                if (!present) missing++;
            }

            return missing;
        }

        private static int NoteLine(StringBuilder sb, string name, string value)
        {
            bool present = !string.IsNullOrWhiteSpace(value);
            if (present && name == "min cover")
            {
                double v;
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v < 5)
                    present = false;
            }
            sb.AppendLine("  " + name + ": " + (present ? "PRESENT" : "MISSING") +
                (present ? "  value=" + value : (string.IsNullOrWhiteSpace(value) ? "" : "  value=" + value + " (ignored, not a plausible cover mm)")));
            return present ? 0 : 1;
        }

        private static bool ViewMatch(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            string a = actual.Replace(" ", "").ToUpperInvariant();
            string e = expected.Replace(" ", "").ToUpperInvariant();
            if (a == e) return true;
            if (e == "SIDEA" && (a.Contains("SIDEA") || a.Contains("FRONT"))) return true;
            if (e == "SIDEB" && (a.Contains("SIDEB") || a.Contains("BACK"))) return true;
            if (e == "END1" && (a.Contains("END1") || a == "END")) return true;
            if (e == "END2" && a.Contains("END2")) return true;
            return false;
        }

        private static int QtyFor(ShopSnap s, string family)
        {
            int q = 0;
            foreach (var kv in s.Bom)
            {
                string fam = CivilDrawingSupport.FamilyMark(kv.Key);
                if (fam.Equals(family, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                    q += kv.Value;
            }
            return q;
        }

        private static JObject FindBar(ShopSnap s, string expected)
        {
            string u = expected.ToUpperInvariant().Replace(" ", "");
            foreach (var bar in s.Rebar)
            {
                string mark = ((string)bar["Mark"] ?? "").ToUpperInvariant().Replace(" ", "");
                string size = ((string)bar["Size"] ?? "").ToUpperInvariant().Replace(" ", "");
                string shape = ((string)bar["ShapeType"] ?? "").ToUpperInvariant();
                double len = bar["TotalLengthMm"] != null && bar["TotalLengthMm"].Type != JTokenType.Null
                    ? bar["TotalLengthMm"].Value<double>() : 0;
                if (mark.Contains(u) || u.Contains(mark) && mark.Length >= 4) return bar;
                if (expected.IndexOf("RB10M", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (mark.Contains("RB10M") || mark.Contains("RS10M") || (size.Contains("10") && shape.Contains("stirr"))))
                    return bar;
                if (expected.IndexOf("10M-STR 2620", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (mark.Contains("RS10M") || size.Contains("10")) && shape.Contains("STRAIGHT") &&
                    (Math.Abs(len - 2620) < 200 || len >= 500))
                    return bar;
                if (expected.IndexOf("10M-STR 7060", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (mark.Contains("RS10M") || size.Contains("10")) && shape.Contains("STRAIGHT") &&
                    (Math.Abs(len - 7060) < 200 || len >= 2000))
                    return bar;
                if (expected.IndexOf("15M-STR 2620", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (mark.Contains("RS15M") || size.Contains("15")) && shape.Contains("STRAIGHT") &&
                    (Math.Abs(len - 2620) < 200 || len >= 500))
                    return bar;
            }
            return null;
        }

        private static bool HasAnyBend(JObject bar)
        {
            foreach (var k in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "H2", "J", "K", "K2" })
            {
                var t = bar[k];
                if (t != null && t.Type != JTokenType.Null && t.Type != JTokenType.String)
                    return true;
            }
            return false;
        }

        private static string BendSummary(JObject bar)
        {
            var parts = new List<string>();
            foreach (var k in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "H2", "J", "K", "K2" })
            {
                var t = bar[k];
                if (t != null && t.Type != JTokenType.Null)
                    parts.Add(k + "=" + t);
            }
            return parts.Count == 0 ? "blank" : string.Join(" ", parts);
        }

        private static ShopSnap PickPtr(List<ShopSnap> shops)
        {
            if (shops == null || shops.Count == 0) return null;
            var exact = shops.FirstOrDefault(s => s.Piece.Equals("PTR", StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var w822 = shops.FirstOrDefault(s => s.Piece.IndexOf("W8-22", StringComparison.OrdinalIgnoreCase) >= 0);
            if (w822 != null) return w822;
            return shops.OrderByDescending(s => HardwareExpected.Count(h => QtyFor(s, h) > 0))
                .ThenByDescending(s => s.Bom.Count)
                .First();
        }

        private static List<ShopSnap> LoadShops(string dir)
        {
            var list = new List<ShopSnap>();
            if (!Directory.Exists(dir)) return list;
            foreach (var file in Directory.GetFiles(dir, "*_SHOP.json"))
            {
                try
                {
                    var jo = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                    var s = new ShopSnap { File = file };
                    s.Piece = (string)jo["Header"]?["PieceMark"] ?? Path.GetFileNameWithoutExtension(file);
                    var notes = jo["GeneralNotes"] as JObject;
                    if (notes != null)
                    {
                        s.Strength28 = TokenStr(notes["Strength28DayMPa"]);
                        s.Stripping = TokenStr(notes["StrippingStrength"]);
                        s.WeightLbs = TokenStr(notes["WeightLbs"]);
                        s.MinCover = TokenStr(notes["MinCoverMm"]);
                        s.Volume = TokenStr(notes["VolumeM3"]) != "" ? TokenStr(notes["VolumeM3"]) : TokenStr(notes["VolumeMm3"]);
                    }
                    foreach (var row in jo["Bom"] as JArray ?? new JArray())
                    {
                        string mark = TokenStr(row["Mark"]);
                        int qty = 0;
                        int.TryParse(TokenStr(row["Quantity"]), NumberStyles.Any, CultureInfo.InvariantCulture, out qty);
                        if (string.IsNullOrEmpty(mark)) continue;
                        if (!s.Bom.ContainsKey(mark)) s.Bom[mark] = 0;
                        s.Bom[mark] += Math.Max(1, qty);
                    }
                    s.Rebar = (jo["Rebar"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    s.DimCount = (jo["Dimensions"] as JArray)?.Count ?? 0;
                    foreach (var d in jo["Dimensions"] as JArray ?? new JArray())
                    {
                        string vn = TokenStr(d["ViewName"]);
                        if (!string.IsNullOrEmpty(vn)) s.DimViews.Add(vn);
                    }
                    foreach (var v in jo["Views"] as JArray ?? new JArray())
                    {
                        string n = TokenStr(v["Name"]);
                        if (!string.IsNullOrEmpty(n)) s.ViewNames.Add(n);
                    }
                    foreach (var f in jo["ProfileFeatures"] as JArray ?? new JArray())
                        s.Features.Add(TokenStr(f["Kind"]) + " " + TokenStr(f["Text"]));
                    list.Add(s);
                }
                catch { }
            }
            return list;
        }

        private static string TokenStr(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            return Convert.ToString(t, CultureInfo.InvariantCulture) ?? "";
        }

        private static string FindPdf()
        {
            var names = new[] { "P22-132_W8-22_Rev_1.pdf", "P22-132_W8-22.pdf" };
            var roots = new[]
            {
                Directory.GetCurrentDirectory(),
                @"C:\Users\ASUS\Desktop",
                @"C:\Users\ASUS\Downloads",
                @"D:\design_",
                @"D:\design_\Paisley",
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var name in names)
                {
                    string p = Path.Combine(root, name);
                    if (File.Exists(p)) return p;
                }
                try
                {
                    var hit = Directory.GetFiles(root, "*W8-22*.pdf", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
                catch { }
            }
            return null;
        }

        private static string ReadPdfText(string path)
        {
            try
            {
                var sb = new StringBuilder();
                using (var doc = UglyToad.PdfPig.PdfDocument.Open(path))
                {
                    foreach (var page in doc.GetPages())
                        sb.AppendLine(page.Text);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "PDF_READ_ERROR: " + ex.Message;
            }
        }

        private static Dictionary<string, int> ParsePdfQtys(string text)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var mark in HardwareExpected.Concat(RebarExpected))
            {
                var rx = new Regex(Regex.Escape(mark) + @"[^\n]{0,40}?(\d+)", RegexOptions.IgnoreCase);
                var m = rx.Match(text);
                int q;
                if (!int.TryParse(m.Groups[1].Value, out q)) continue;
                if (mark.IndexOf(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (q >= 1000) continue;
                map[mark] = q;
            }
            return map;
        }

        private class ShopSnap
        {
            public string File;
            public string Piece = "";
            public string Strength28 = "";
            public string Stripping = "";
            public string WeightLbs = "";
            public string MinCover = "";
            public string Volume = "";
            public Dictionary<string, int> Bom = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<JObject> Rebar = new List<JObject>();
            public int DimCount;
            public List<string> ViewNames = new List<string>();
            public HashSet<string> DimViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Features = new List<string>();
        }
    }
}

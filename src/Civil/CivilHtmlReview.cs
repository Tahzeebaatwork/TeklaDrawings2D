using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Writes manifest.json + copies Tools/DrawingViewer/index.html into the export folder.
    /// </summary>
    public static class CivilHtmlReview
    {
        public static void WriteFolderIndex(string outputRoot, string title)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot)) return;
            var manifest = BuildFolderManifest(outputRoot, title);
            WriteManifestAndViewer(outputRoot, manifest);
            Console.WriteLine("[HTML] " + Path.Combine(outputRoot, "index.html"));
        }

        public static void WriteCompare(string civilRoot)
        {
            if (string.IsNullOrWhiteSpace(civilRoot)) return;
            Directory.CreateDirectory(civilRoot);
            var manifest = BuildRootManifest(civilRoot);
            WriteManifestAndViewer(civilRoot, manifest);
            File.WriteAllText(Path.Combine(civilRoot, "compare.html"),
                "<!DOCTYPE html><meta charset=\"utf-8\"/><meta http-equiv=\"refresh\" content=\"0;url=index.html\"/>" +
                "<script>location.replace('index.html'+location.hash);</script>" +
                "<p><a href=\"index.html\">Open 2D export viewer</a></p>\n",
                Encoding.UTF8);
            Console.WriteLine("[HTML] " + Path.Combine(civilRoot, "index.html"));
        }

        private static object BuildFolderManifest(string outputRoot, string title)
        {
            var files = ScanFolder(outputRoot, "");
            var pieces = new List<Dictionary<string, object>>();
            foreach (var mark in AllMarks(files))
            {
                var s = SideFor(files, mark);
                pieces.Add(new Dictionary<string, object>
                {
                    ["mark"] = mark,
                    ["family"] = FamilyOf(mark),
                    ["kind"] = KindOf(mark),
                    ["status"] = s.HasAny ? "ok" : "miss",
                    ["side"] = s.ToDict()
                });
            }
            return new Dictionary<string, object>
            {
                ["mode"] = "folder",
                ["title"] = title,
                ["macrosLabel"] = title,
                ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["leftLabel"] = title,
                ["csv"] = new Dictionary<string, object>
                {
                    ["local"] = CsvLinks(outputRoot, ""),
                    ["root"] = new List<object>(),
                    ["macros"] = new List<object>(),
                    ["api"] = new List<object>()
                },
                ["pieces"] = pieces
            };
        }

        private static object BuildRootManifest(string civilRoot)
        {
            string withDir = Path.Combine(civilRoot, CivilDrawingTypes.MacrosFolder);
            string withPrefix = UrlFolder(CivilDrawingTypes.MacrosFolder);
            string withoutDir = Path.Combine(civilRoot, CivilDrawingTypes.OpenApiFolder);
            string withoutPrefix = UrlFolder(CivilDrawingTypes.OpenApiFolder);
            var with = ScanFolder(withDir, withPrefix);
            var without = ScanFolder(withoutDir, withoutPrefix);
            var marks = new SortedSet<string>(AllMarks(with), StringComparer.OrdinalIgnoreCase);
            foreach (var m in AllMarks(without)) marks.Add(m);

            var pieces = new List<Dictionary<string, object>>();
            foreach (var mark in marks)
            {
                var left = SideFor(with, mark);
                var right = SideFor(without, mark);
                string status = left.HasAny && right.HasAny ? "both"
                    : left.HasAny ? "macros"
                    : "api";
                pieces.Add(new Dictionary<string, object>
                {
                    ["mark"] = mark,
                    ["family"] = FamilyOf(mark),
                    ["kind"] = KindOf(mark),
                    ["status"] = status,
                    ["macros"] = left.ToDict(),
                    ["api"] = right.ToDict()
                });
            }

            return new Dictionary<string, object>
            {
                ["mode"] = "root",
                ["title"] = "Tekla 2D export — " + CivilDrawingTypes.MacrosFolder + " vs Open API",
                ["macrosFolder"] = CivilDrawingTypes.MacrosFolder,
                ["macrosLabel"] = CivilDrawingTypes.MacrosFolder,
                ["apiLabel"] = CivilDrawingTypes.OpenApiFolder,
                ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["csv"] = new Dictionary<string, object>
                {
                    ["root"] = CsvLinks(civilRoot, ""),
                    ["macros"] = CsvLinks(withDir, withPrefix),
                    ["api"] = CsvLinks(withoutDir, withoutPrefix),
                    ["local"] = new List<object>()
                },
                ["pieces"] = pieces
            };
        }

        private static string UrlFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return string.Join("/", name.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString)) + "/";
        }

        private class FolderScan
        {
            public Dictionary<string, List<string>> Pdfs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Shop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Bbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Conn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Erection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Ga = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private class Side
        {
            public List<string> Pdfs = new List<string>();
            public string Shop, Bbs, Conn, Erection, Ga;
            public bool HasAny
            {
                get
                {
                    return Pdfs.Count > 0 || Shop != null || Bbs != null || Conn != null
                        || Erection != null || Ga != null;
                }
            }

            public Dictionary<string, object> ToDict()
            {
                return new Dictionary<string, object>
                {
                    ["pdfs"] = Pdfs,
                    ["shop"] = Shop,
                    ["bbs"] = Bbs,
                    ["conn"] = Conn,
                    ["erection"] = Erection,
                    ["ga"] = Ga
                };
            }
        }

        private static FolderScan ScanFolder(string folder, string urlPrefix)
        {
            var scan = new FolderScan();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return scan;
            MapFiles(scan.Pdfs, folder, "PDF", "*.pdf", urlPrefix);
            MapOne(scan.Shop, folder, CivilDrawingTypes.Shop, "*.json", urlPrefix);
            MapOne(scan.Bbs, folder, CivilDrawingTypes.RebarBbs, "*.json", urlPrefix);
            MapOne(scan.Conn, folder, CivilDrawingTypes.Connection, "*.json", urlPrefix);
            MapOne(scan.Erection, folder, CivilDrawingTypes.Erection, "*.json", urlPrefix);
            MapOne(scan.Ga, folder, CivilDrawingTypes.GA, "*.json", urlPrefix);
            return scan;
        }

        private static void MapFiles(Dictionary<string, List<string>> map, string folder, string sub, string pattern, string prefix)
        {
            foreach (var f in Files(folder, sub, pattern))
            {
                string mark = PieceFromName(f);
                string rel = prefix + Rel(folder, f);
                if (!map.TryGetValue(mark, out var list))
                {
                    list = new List<string>();
                    map[mark] = list;
                }
                list.Add(rel);
            }
        }

        private static void MapOne(Dictionary<string, string> map, string folder, string sub, string pattern, string prefix)
        {
            foreach (var f in Files(folder, sub, pattern))
            {
                string mark = PieceFromName(f);
                if (!map.ContainsKey(mark))
                    map[mark] = prefix + Rel(folder, f);
            }
        }

        private static IEnumerable<string> AllMarks(FolderScan scan)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in scan.Pdfs.Keys) set.Add(k);
            foreach (var k in scan.Shop.Keys) set.Add(k);
            foreach (var k in scan.Bbs.Keys) set.Add(k);
            foreach (var k in scan.Conn.Keys) set.Add(k);
            foreach (var k in scan.Erection.Keys) set.Add(k);
            foreach (var k in scan.Ga.Keys) set.Add(k);
            return set;
        }

        private static Side SideFor(FolderScan scan, string mark)
        {
            var s = new Side();
            if (scan.Pdfs.TryGetValue(mark, out var pdfs)) s.Pdfs = pdfs;
            scan.Shop.TryGetValue(mark, out s.Shop);
            scan.Bbs.TryGetValue(mark, out s.Bbs);
            scan.Conn.TryGetValue(mark, out s.Conn);
            scan.Erection.TryGetValue(mark, out s.Erection);
            scan.Ga.TryGetValue(mark, out s.Ga);
            return s;
        }

        private static List<object> CsvLinks(string folder, string prefix)
        {
            var list = new List<object>();
            if (!Directory.Exists(folder)) return list;
            foreach (var csv in new[] { "BOM.csv", "SHOP.csv", "REBAR_BBS.csv", "CONNECTION.csv", "ERECTION.csv", "GA.csv" })
            {
                string path = Path.Combine(folder, csv);
                if (!File.Exists(path)) continue;
                list.Add(new Dictionary<string, string> { ["name"] = csv, ["href"] = prefix + csv });
            }
            return list;
        }

        private static void WriteManifestAndViewer(string dir, object manifest)
        {
            Directory.CreateDirectory(dir);
            string json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), json, utf8);

            string template = File.ReadAllText(FindTemplate(), Encoding.UTF8);
            string injected = json.Replace("<", "\\u003c");
            const string needle = "window.MANIFEST=null;";
            if (!template.Contains(needle))
                throw new InvalidOperationException("Drawing viewer template missing window.MANIFEST=null;");
            string html = template.Replace(needle, "window.MANIFEST=" + injected + ";");
            File.WriteAllText(Path.Combine(dir, "index.html"), html, utf8);
        }

        private static string FindTemplate()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var starts = new List<string>();
            if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
                starts.Add(AppDomain.CurrentDomain.BaseDirectory);
            try { starts.Add(Path.GetDirectoryName(typeof(CivilHtmlReview).Assembly.Location)); } catch { }
            starts.Add(Directory.GetCurrentDirectory());

            foreach (var start in starts.Where(s => !string.IsNullOrEmpty(s)))
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    if (!seen.Add(dir.FullName)) continue;
                    string p = Path.Combine(dir.FullName, "Tools", "DrawingViewer", "index.html");
                    if (File.Exists(p)) return p;
                    p = Path.Combine(dir.FullName, "DrawingViewer", "index.html");
                    if (File.Exists(p)) return p;
                }
            }

            string besideExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "Tools", "DrawingViewer", "index.html");
            if (File.Exists(besideExe)) return besideExe;
            throw new FileNotFoundException("Tools/DrawingViewer/index.html not found. Rebuild so the template copies next to the exe.");
        }

        private static List<string> Files(string root, string sub, string pattern)
        {
            string dir = Path.Combine(root ?? "", sub ?? "");
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string PieceFromName(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path ?? "") ?? "";
            n = n.Replace("_SHOP", "").Replace("_CONNECTION", "").Replace("_ERECTION", "")
                .Replace("_REBAR_BBS", "").Replace("_GA", "");
            int us = n.IndexOf('_');
            if (us > 0 && us < n.Length - 1 && n.Substring(0, us).All(char.IsDigit))
                n = n.Substring(us + 1);
            int sheet = n.IndexOf("_-_", StringComparison.Ordinal);
            if (sheet > 0)
                n = n.Substring(0, sheet);
            n = n.Replace("_", "-").Trim('-');
            return string.IsNullOrWhiteSpace(n) ? Path.GetFileNameWithoutExtension(path) : n;
        }

        private static string Rel(string root, string file)
        {
            try
            {
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string f = Path.GetFullPath(file);
                if (f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    return f.Substring(r.Length).Replace('\\', '/');
            }
            catch { }
            return Path.GetFileName(file);
        }

        private static string FamilyOf(string mark)
        {
            if (string.IsNullOrWhiteSpace(mark)) return "?";
            var m = Regex.Match(mark, @"^[A-Za-z]+[0-9]*");
            return m.Success ? m.Value.ToUpperInvariant() : mark.ToUpperInvariant();
        }

        private static string KindOf(string mark)
        {
            string m = (mark ?? "").ToUpperInvariant();
            if (m.StartsWith("GAD") || m.Contains("LEVEL") || m.Contains("PODIUM") || m.Contains("ROOF"))
                return "GA";
            if (m.StartsWith("SW") || m.StartsWith("W") || m.StartsWith("P6") || m.StartsWith("ITB"))
                return "WALL";
            if (m.StartsWith("C") && !m.StartsWith("CU") && !m.StartsWith("CNDT"))
                return "COLUMN";
            if (m.StartsWith("BA") || m.StartsWith("BM") || m.StartsWith("L") || m.StartsWith("A5"))
                return "BEAM";
            return "OTHER";
        }
    }
}

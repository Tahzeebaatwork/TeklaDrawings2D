using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// After civil extract, bind that piece's 4–6 role PDFs into one fabrication booklet.
    /// Prefer Scripts/collate_production_booklet.py (PyMuPDF). Fallback: ordered copy list only.
    /// </summary>
    public static class ProductionBookletCollator
    {
        private static readonly Regex StemPiece = new Regex(
            @"^(?<piece>.+)_-_(?:1|2(?:_Sections_3D)?|3|4|5|6)\.pdf$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Collate booklets under outputRoot/PDF. If markFilter is set, only that piece;
        /// otherwise every piece that has at least a _-_1.pdf (or any role sheet).
        /// </summary>
        public static int Collate(string projectRoot, string outputRoot, string markFilter = null)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.WriteLine("[Booklet] skipped: no output root");
                return 0;
            }

            string pdfDir = Path.Combine(outputRoot, "PDF");
            if (!Directory.Exists(pdfDir))
            {
                Console.WriteLine("[Booklet] skipped: PDF folder missing → " + pdfDir);
                return 0;
            }

            var marks = new List<string>();
            if (!string.IsNullOrWhiteSpace(markFilter))
                marks.Add(markFilter.Trim());
            else
                marks.AddRange(DiscoverMarks(pdfDir));

            if (marks.Count == 0)
            {
                Console.WriteLine("[Booklet] no piece marks found under " + pdfDir);
                return 0;
            }

            string script = Path.Combine(projectRoot ?? ".", "Scripts", "collate_production_booklet.py");
            int ok = 0;
            foreach (string mark in marks.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m))
            {
                if (TryCollatePython(script, pdfDir, mark))
                    ok++;
                else if (TryListSheets(pdfDir, mark))
                    Console.WriteLine("[Booklet] WARNING: install PyMuPDF (pip install pymupdf) to write the multi-page PDF for " + mark);
            }
            return ok;
        }

        private static IEnumerable<string> DiscoverMarks(string pdfDir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(pdfDir, "*.pdf"))
            {
                string name = Path.GetFileName(file);
                if (name.IndexOf(".Rev ", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (name.StartsWith("P22-", StringComparison.OrdinalIgnoreCase) && name.Contains(".Rev"))
                    continue;
                var m = StemPiece.Match(name);
                if (m.Success)
                    set.Add(m.Groups["piece"].Value);
            }
            return set;
        }

        private static bool TryCollatePython(string script, string pdfDir, string mark)
        {
            if (!File.Exists(script))
            {
                Console.WriteLine("[Booklet] script missing: " + script);
                return false;
            }

            string python = FindPython();
            if (python == null)
            {
                Console.WriteLine("[Booklet] python not on PATH");
                return false;
            }

            string pyArgs = (python == "py" ? "-3 " : "")
                + "\"" + script + "\" --mark \"" + mark + "\" --pdf-dir \"" + pdfDir + "\"";
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = pyArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? "."
            };

            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    if (!string.IsNullOrWhiteSpace(stdout))
                        Console.Write(stdout.EndsWith("\n") ? stdout : stdout + Environment.NewLine);
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Console.WriteLine("[Booklet] " + stderr.Trim());
                    if (p.ExitCode == 0)
                    {
                        Console.WriteLine("[Booklet] OK mark=" + mark + " (multi-page fabrication PDF)");
                        return true;
                    }
                    Console.WriteLine("[Booklet] collate exit=" + p.ExitCode + " mark=" + mark);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Booklet] failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryListSheets(string pdfDir, string mark)
        {
            string[] candidates =
            {
                mark + "_-_1.pdf",
                mark + "_-_2_Sections_3D.pdf",
                mark + "_-_2.pdf",
                mark + "_-_3.pdf",
                mark + "_-_4.pdf",
                mark + "_-_5.pdf",
                mark + "_-_6.pdf",
            };
            int n = 0;
            foreach (string c in candidates)
            {
                string path = Path.Combine(pdfDir, c);
                if (File.Exists(path))
                {
                    Console.WriteLine("[Booklet] sheet ready: " + c);
                    n++;
                }
            }
            return n > 0;
        }

        private static string FindPython()
        {
            foreach (string name in new[] { "python", "py", "python3" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = name == "py" ? "-3 -c \"print(1)\"" : "-c \"print(1)\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) continue;
                        p.WaitForExit(5000);
                        if (p.ExitCode == 0) return name == "py" ? "py" : name;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}

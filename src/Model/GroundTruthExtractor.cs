// GroundTruthExtractor.cs
// ─────────────────────────────────────────────────────────────────
// Flowchart step: "Extract Data — Mark, Type, Profile, Material,
// Floor, Dimensions, Weight, GUID"  →  "Ground Truth DB (JSON + CSV)"
//
// Reads every Part in the connected Tekla model via the Tekla Open
// API and writes the fields shown in the flowchart to two files:
//   ground_truth.json
//   ground_truth.csv

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public class GroundTruthRecord
    {
        public string Guid { get; set; } = "";
        public int Id { get; set; }
        public string Mark { get; set; } = "";
        public string AssemblyMark { get; set; } = "";
        public string Type { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Floor { get; set; } = "";
        public double Length { get; set; }
        public double Weight { get; set; }
        public string PartClass { get; set; } = "";
    }

    /// <summary>
    /// Extracts Mark / Type / Profile / Material / Floor / Dimensions /
    /// Weight / GUID for every part in the model, and writes the
    /// "Ground Truth DB" (JSON + CSV) that the flowchart depicts.
    /// </summary>
    public static class GroundTruthExtractor
    {
        // Part-like object types to scan. Tekla 2026 GetAllObjectsWithType
        // takes Type[] (not ModelObjectEnum[]).
        private static readonly Type[] PartTypes =
        {
            typeof(Beam),
            typeof(ContourPlate),
            typeof(PolyBeam),
        };

        public static List<GroundTruthRecord> Extract(Model model)
        {
            var records = new List<GroundTruthRecord>();
            var enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(PartTypes);

            while (enumerator.MoveNext())
            {
                if (!(enumerator.Current is Part part)) continue;
                try
                {
                    records.Add(ReadPart(part));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GroundTruth] skipped part {SafeId(part)}: {ex.Message}");
                }
            }

            return records;
        }

        /// <summary>
        /// Live Tekla Part objects for DrawingGenerator (not the JSON records).
        /// </summary>
        public static List<Part> ExtractParts(Model model)
        {
            var parts = new List<Part>();
            var enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(PartTypes);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is Part part)
                    parts.Add(part);
            }
            return parts;
        }

        private static GroundTruthRecord ReadPart(Part part)
        {
            // Report properties work uniformly across Beam / ContourPlate /
            // PolyBeam etc. without needing type-specific casts.
            string mark = ReportProperty(part, "PART_POS");
            if (string.IsNullOrWhiteSpace(mark))
                mark = ReportProperty(part, "PARTMARK");

            string assemblyMark = ReportProperty(part, "ASSEMBLY_POS");
            string profile      = ReportProperty(part, "PROFILE");
            string material     = ReportProperty(part, "MATERIAL");

            // "FLOOR" is the standard template name; if your model uses a
            // custom UDA for storey/level, change this key to match it.
            string floor = ReportProperty(part, "FLOOR");

            double weight = ReportDouble(part, "WEIGHT");
            double length = ReportDouble(part, "LENGTH");

            string cls = "";
            try { cls = part.Class; } catch { /* not all part types expose Class */ }

            return new GroundTruthRecord
            {
                Guid         = part.Identifier.GUID.ToString(),
                Id           = part.Identifier.ID,
                Mark         = mark,
                AssemblyMark = assemblyMark,
                Type         = part.GetType().Name,   // Beam / ContourPlate / PolyBeam ...
                Profile      = profile,
                Material     = material,
                Floor        = floor,
                Length       = Math.Round(length, 1),
                Weight       = Math.Round(weight, 2),
                PartClass    = cls,
            };
        }

        private static string ReportProperty(Part part, string name)
        {
            string value = "";
            try { part.GetReportProperty(name, ref value); } catch { /* optional */ }
            return value ?? "";
        }

        private static double ReportDouble(Part part, string name)
        {
            double value = 0;
            try
            {
                if (part.GetReportProperty(name, ref value))
                    return value;
            }
            catch { /* optional */ }

            double.TryParse(ReportProperty(part, name), NumberStyles.Any,
                CultureInfo.InvariantCulture, out value);
            return value;
        }

        private static string SafeId(Part part)
        {
            try { return part.Identifier.ID.ToString(); } catch { return "?"; }
        }

        /// <summary>
        /// Writes the "Ground Truth DB" — ground_truth.json + ground_truth.csv —
        /// into baseDir, matching the flowchart.
        /// </summary>
        public static void WriteGroundTruthDb(List<GroundTruthRecord> records, string baseDir)
        {
            Directory.CreateDirectory(baseDir);

            string jsonPath = Path.Combine(baseDir, "ground_truth.json");
            string csvPath  = Path.Combine(baseDir, "ground_truth.csv");

            File.WriteAllText(jsonPath,
                JsonConvert.SerializeObject(records, Formatting.Indented),
                Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine("GUID,ID,Mark,AssemblyMark,Type,Profile,Material,Floor,Length,Weight,Class");
            foreach (var r in records)
            {
                sb.AppendLine(string.Join(",",
                    Csv(r.Guid), r.Id, Csv(r.Mark), Csv(r.AssemblyMark), Csv(r.Type),
                    Csv(r.Profile), Csv(r.Material), Csv(r.Floor),
                    r.Length.ToString(CultureInfo.InvariantCulture),
                    r.Weight.ToString(CultureInfo.InvariantCulture), Csv(r.PartClass)));
            }
            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);

            Console.WriteLine($"[GroundTruth] {records.Count} parts → {jsonPath}");
            Console.WriteLine($"[GroundTruth] {records.Count} parts → {csvPath}");
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return (s.Contains(",") || s.Contains("\""))
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        /// <summary>Convenience: extract + write in one call.</summary>
        public static List<GroundTruthRecord> Run(Model model, string baseDir)
        {
            Console.WriteLine("\n[GroundTruth] Extracting Mark/Type/Profile/Material/Floor/Dimensions/Weight/GUID...");
            var records = Extract(model);
            WriteGroundTruthDb(records, baseDir);
            return records;
        }
    }
}

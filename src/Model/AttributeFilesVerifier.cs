using System;
using System.Collections.Generic;
using System.IO;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Verifies engineer Drawing Properties / layout files exist on Tekla search paths
    /// (model attributes, firm, optional bundled). Copies missing WETCAST.lay from firm when found.
    /// </summary>
    public static class AttributeFilesVerifier
    {
        public static readonly string[] RequiredPropNames =
        {
            "SP_M.CU_HARDWARE_PROPS_11X17",
            "SP_M.CU_REINFORCING_PLACING_PROPS_11X17",
            "SP_M.CU_REINFORCING_TABLE_PROPS_11X17",
            "SP_M.CU_HARDWARE_11x17",
        };

        public static readonly string[] RequiredLayouts =
        {
            "WETCAST.lay",
            "CastUnit.lay",
            "Precast Shop.lay",
        };

        public sealed class Report
        {
            public string ModelAttributesDir = "";
            public string FirmAttributesDir = "";
            public List<string> Found = new List<string>();
            public List<string> Missing = new List<string>();
            public List<string> Copied = new List<string>();
            public bool Ok => Missing.Count == 0;
        }

        public static Report VerifyAndRepair(TSModel.Model model)
        {
            var report = new Report();
            string modelDir = "";
            try { modelDir = model?.GetInfo()?.ModelPath ?? ""; } catch { }
            report.ModelAttributesDir = string.IsNullOrWhiteSpace(modelDir)
                ? ""
                : Path.Combine(modelDir, "attributes");
            report.FirmAttributesDir = FindFirmAttributes();

            var search = new List<string>();
            if (!string.IsNullOrWhiteSpace(report.ModelAttributesDir) && Directory.Exists(report.ModelAttributesDir))
                search.Add(report.ModelAttributesDir);
            if (!string.IsNullOrWhiteSpace(report.FirmAttributesDir) && Directory.Exists(report.FirmAttributesDir))
                search.Add(report.FirmAttributesDir);

            foreach (string prop in RequiredPropNames)
            {
                string hit = FindFile(search, prop + ".cud") ?? FindFile(search, prop + ".cudl");
                if (hit != null) report.Found.Add(hit);
                else report.Missing.Add(prop + ".cud|.cudl");
            }

            foreach (string lay in RequiredLayouts)
            {
                string inModel = string.IsNullOrWhiteSpace(report.ModelAttributesDir)
                    ? null
                    : Path.Combine(report.ModelAttributesDir, lay);
                if (inModel != null && File.Exists(inModel))
                {
                    report.Found.Add(inModel);
                    continue;
                }
                string inFirm = string.IsNullOrWhiteSpace(report.FirmAttributesDir)
                    ? null
                    : Path.Combine(report.FirmAttributesDir, lay);
                if (inFirm != null && File.Exists(inFirm) && inModel != null)
                {
                    try
                    {
                        Directory.CreateDirectory(report.ModelAttributesDir);
                        File.Copy(inFirm, inModel, overwrite: false);
                        report.Copied.Add(lay + " <- firm");
                        report.Found.Add(inModel);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Attrs] copy " + lay + ": " + ex.Message);
                    }
                }
                string any = FindFile(search, lay);
                if (any != null) report.Found.Add(any);
                else report.Missing.Add(lay);
            }

            Console.WriteLine("[Attrs] model=" + report.ModelAttributesDir);
            Console.WriteLine("[Attrs] firm=" + report.FirmAttributesDir);
            Console.WriteLine("[Attrs] found=" + report.Found.Count + " missing=" + report.Missing.Count
                + " copied=" + report.Copied.Count);
            foreach (string m in report.Missing)
                Console.WriteLine("[Attrs] MISSING " + m);
            foreach (string c in report.Copied)
                Console.WriteLine("[Attrs] COPIED " + c);
            return report;
        }

        private static string FindFirmAttributes()
        {
            string[] candidates =
            {
                @"D:\design_\METRIC_FIRM2021\attributes",
                Environment.GetEnvironmentVariable("XS_FIRM") ?? "",
            };
            foreach (string c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                string dir = c;
                if (!dir.EndsWith("attributes", StringComparison.OrdinalIgnoreCase))
                    dir = Path.Combine(dir, "attributes");
                if (Directory.Exists(dir)) return dir;
            }
            return "";
        }

        private static string FindFile(List<string> dirs, string fileName)
        {
            foreach (string d in dirs)
            {
                string p = Path.Combine(d, fileName);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}

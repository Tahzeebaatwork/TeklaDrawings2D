// FloorWiseCastUnitExtractor.cs
// ─────────────────────────────────────────────────────────────────
// Model API (not Drawing API) → floor-wise Cast Unit / Assembly JSON
// for piece tickets and floor-level shop drawings.
//
// Tekla has no separate CastUnit class. A Cast Unit is an Assembly
// whose GetAssemblyType() is PRECAST_ASSEMBLY.
//
// Floor name:
//   1. UDA / report: FLOOR_LEVEL, FLOOR, FLOOR_NAME, STOREY, LEVEL, …
//   2. Else Z-band from the main-part solid AABB (same bands as GAD).
//
// Writes:
//   {Tekla model folder}/floor_wise_cast_units.json
//   {workspace}/Export/cast_units/floor_wise_cast_units.json
//   {workspace}/Export/cast_units/<floor>.json

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    // =====================================================================
    // Hierarchical JSON models: Project → Floor → CastUnits / Assemblies
    // =====================================================================

    public class FloorWiseProject
    {
        public string Schema { get; set; } = "tekla-floor-wise-cast-units/1.0";
        public string ModelName { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public string GroupingRule { get; set; } =
            "UDA FLOOR_LEVEL / FLOOR / FLOOR_NAME / STOREY / LEVEL if set; " +
            "otherwise Z-band from main-part solid AABB (or CS origin).";
        public int AssemblyCount { get; set; }
        public int CastUnitCount { get; set; }
        public int SteelAssemblyCount { get; set; }
        public int InSituCount { get; set; }
        public int UdaFloorCount { get; set; }
        public int ZBandFloorCount { get; set; }
        public string OutputPath { get; set; } = "";
        public List<FloorWiseLevel> Floors { get; set; } = new List<FloorWiseLevel>();
    }

    public class FloorWiseLevel
    {
        public string Name { get; set; } = "";
        /// <summary>UDA or ZBand — how this floor name was decided.</summary>
        public string FloorSource { get; set; } = "";
        public double ElevationZmm { get; set; }
        public int CastUnitCount { get; set; }
        public int AssemblyCount { get; set; }
        public List<FloorCastUnit> CastUnits { get; set; } = new List<FloorCastUnit>();
        public List<FloorCastUnit> Assemblies { get; set; } = new List<FloorCastUnit>();
    }

    public class FloorCastUnit
    {
        public string Guid { get; set; } = "";
        public int Id { get; set; }
        /// <summary>CAST_UNIT_POS for precast, ASSEMBLY_POS otherwise.</summary>
        public string Mark { get; set; } = "";
        public string AssemblyMark { get; set; } = "";
        public string CastUnitMark { get; set; } = "";
        public string Name { get; set; } = "";
        public string AssemblyType { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Floor { get; set; } = "";
        public string FloorSource { get; set; } = "";
        public string FloorUda { get; set; } = "";
        public double VolumeMm3 { get; set; }
        public double Volume_m3 { get; set; }
        public double WeightKg { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public double[] Cog { get; set; }
        public MainPartDetails MainPart { get; set; } = new MainPartDetails();
        public int PartCount { get; set; }
        public int SecondaryCount { get; set; }
    }

    public class MainPartDetails
    {
        public string Guid { get; set; } = "";
        public int Id { get; set; }
        public string Mark { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Class { get; set; } = "";
        public string TeklaType { get; set; } = "";
        public double Length { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
    }

    /// <summary>
    /// Live Tekla Model API extractor. Does not open drawings.
    /// </summary>
    public class FloorWiseCastUnitExtractor
    {
        private static readonly string[] FloorUdaNames =
        {
            "FLOOR_LEVEL", "FLOOR", "FLOOR_NAME", "STOREY", "STORY",
            "LEVEL", "FLOORLEVEL", "STOREY_NAME"
        };

        private static readonly Type[] AssemblyTypes = { typeof(Assembly) };
        private static readonly Type[] PartTypes =
        {
            typeof(Beam),
            typeof(ContourPlate),
            typeof(PolyBeam),
        };

        private readonly Model _model;

        public FloorWiseCastUnitExtractor(Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            if (!_model.GetConnectionStatus())
                throw new InvalidOperationException("Tekla Structures is not connected.");
        }

        public FloorWiseProject Run(string workspaceDir = null)
        {
            var info = _model.GetInfo();
            string modelDir = info?.ModelPath ?? "";
            string modelName = info?.ModelName ?? "";

            Console.WriteLine("[CastUnits] Model API → floor-wise Cast Unit / Assembly JSON...");
            var assemblies = CollectAssemblies();
            Console.WriteLine($"[CastUnits] {assemblies.Count} unique assemblies.");

            var floors = new Dictionary<string, FloorWiseLevel>(StringComparer.OrdinalIgnoreCase);
            int udaHits = 0, zHits = 0, cu = 0, steel = 0, insitu = 0;
            int n = 0;

            foreach (var assembly in assemblies)
            {
                n++;
                try
                {
                    var rec = ReadAssembly(assembly);
                    if (rec == null) continue;

                    if (rec.FloorSource == "UDA") udaHits++;
                    else zHits++;

                    if (rec.Kind == "CastUnit") cu++;
                    else if (rec.Kind == "SteelAssembly") steel++;
                    else insitu++;

                    if (!floors.TryGetValue(rec.Floor, out var level))
                    {
                        level = new FloorWiseLevel
                        {
                            Name = rec.Floor,
                            FloorSource = rec.FloorSource,
                        };
                        floors[rec.Floor] = level;
                    }

                    if (rec.Kind == "CastUnit")
                        level.CastUnits.Add(rec);
                    else
                        level.Assemblies.Add(rec);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CastUnits] skipped id={SafeId(assembly)}: {ex.Message}");
                }

                if (n % 200 == 0)
                    Console.WriteLine($"[CastUnits] {n}/{assemblies.Count}...");
            }

            foreach (var level in floors.Values)
            {
                level.CastUnits = level.CastUnits
                    .OrderBy(c => c.Mark, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                level.Assemblies = level.Assemblies
                    .OrderBy(c => c.Mark, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                level.CastUnitCount = level.CastUnits.Count;
                level.AssemblyCount = level.Assemblies.Count;
                var zs = level.CastUnits.Concat(level.Assemblies)
                    .Select(MidZ)
                    .Where(z => !double.IsNaN(z))
                    .ToList();
                if (zs.Count > 0)
                    level.ElevationZmm = Math.Round(zs.OrderBy(z => z).Skip(zs.Count / 2).First(), 1);
            }

            var project = new FloorWiseProject
            {
                ModelName = modelName,
                ModelPath = modelDir,
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
                AssemblyCount = assemblies.Count,
                CastUnitCount = cu,
                SteelAssemblyCount = steel,
                InSituCount = insitu,
                UdaFloorCount = udaHits,
                ZBandFloorCount = zHits,
                Floors = floors.Values
                    .OrderBy(f => f.ElevationZmm)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

            string json = JsonConvert.SerializeObject(project, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            });

            string modelOut = WriteModelCopy(modelDir, json);
            project.OutputPath = modelOut;
            // Re-write so OutputPath is inside the file.
            json = JsonConvert.SerializeObject(project, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            });
            if (!string.IsNullOrEmpty(modelOut))
                File.WriteAllText(modelOut, json, Encoding.UTF8);

            WriteWorkspaceCopies(workspaceDir, project, json);

            Console.WriteLine($"[CastUnits] floors={project.Floors.Count}  CU={cu}  steel={steel}  in-situ={insitu}");
            Console.WriteLine($"[CastUnits] floor source: UDA={udaHits}  Z-band={zHits}");
            Console.WriteLine($"[CastUnits] wrote {modelOut}");
            return project;
        }

        private List<Assembly> CollectAssemblies()
        {
            var map = new Dictionary<int, Assembly>();

            try
            {
                var en = _model.GetModelObjectSelector().GetAllObjectsWithType(AssemblyTypes);
                while (en != null && en.MoveNext())
                {
                    if (!(en.Current is Assembly assembly)) continue;
                    if (!assembly.Identifier.IsValid()) continue;
                    int id = assembly.Identifier.ID;
                    if (id == 0 || map.ContainsKey(id)) continue;
                    map[id] = assembly;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CastUnits] GetAllObjectsWithType(Assembly): {ex.Message}");
            }

            if (map.Count > 0)
                return new List<Assembly>(map.Values);

            // Fallback: walk parts and take their parent assembly.
            Console.WriteLine("[CastUnits] Assembly enumerator empty — walking parts...");
            var parts = _model.GetModelObjectSelector().GetAllObjectsWithType(PartTypes);
            while (parts != null && parts.MoveNext())
            {
                if (!(parts.Current is Part part)) continue;
                Assembly assembly = null;
                try { assembly = part.GetAssembly(); } catch { /* optional */ }
                if (assembly == null || !assembly.Identifier.IsValid()) continue;
                int id = assembly.Identifier.ID;
                if (id == 0 || map.ContainsKey(id)) continue;
                map[id] = assembly;
            }

            return new List<Assembly>(map.Values);
        }

        private FloorCastUnit ReadAssembly(Assembly assembly)
        {
            try { assembly.Select(); } catch { /* best effort */ }

            var main = assembly.GetMainPart() as Part;
            if (main == null) return null;

            var type = assembly.GetAssemblyType();
            string kind = KindOf(type);

            string cuMark = Report(assembly, "CAST_UNIT_POS");
            if (string.IsNullOrWhiteSpace(cuMark))
                cuMark = Report(main, "CAST_UNIT_POS");

            string asmMark = Report(assembly, "ASSEMBLY_POS");
            if (string.IsNullOrWhiteSpace(asmMark))
                asmMark = Report(main, "ASSEMBLY_POS");

            string mark = !string.IsNullOrWhiteSpace(cuMark) ? cuMark : asmMark;

            string name = Safe(() => main.Name);
            if (string.IsNullOrWhiteSpace(name))
                name = Report(main, "NAME");
            if (string.IsNullOrWhiteSpace(name))
                name = Report(assembly, "NAME");

            double volumeMm3 = ReportDouble(assembly, "VOLUME");
            if (volumeMm3 <= 0)
                volumeMm3 = ReportDouble(main, "VOLUME");

            double weight = ReportDouble(assembly, "WEIGHT");
            if (weight <= 0)
                weight = ReportDouble(main, "WEIGHT");

            GetBbox(main, out double[] min, out double[] max, out double[] cog);

            string uda = ReadFloorUda(assembly);
            if (string.IsNullOrWhiteSpace(uda))
                uda = ReadFloorUda(main);

            double z = MidZ(min, max);
            if (double.IsNaN(z))
            {
                try { z = main.GetCoordinateSystem().Origin.Z; }
                catch { z = 0; }
            }

            string floor;
            string source;
            if (!string.IsNullOrWhiteSpace(uda))
            {
                floor = NormalizeFloorName(uda);
                source = "UDA";
            }
            else
            {
                floor = FloorWiseDrawingExtractor.FloorBand(z);
                source = "ZBand";
            }

            int secondaries = CountSecondaries(assembly);

            var rec = new FloorCastUnit
            {
                Guid = assembly.Identifier.GUID.ToString(),
                Id = assembly.Identifier.ID,
                Mark = mark ?? "",
                AssemblyMark = asmMark ?? "",
                CastUnitMark = cuMark ?? "",
                Name = name ?? "",
                AssemblyType = type.ToString(),
                Kind = kind,
                Floor = floor,
                FloorSource = source,
                FloorUda = uda ?? "",
                VolumeMm3 = Math.Round(volumeMm3, 1),
                Volume_m3 = Math.Round(volumeMm3 / 1e9, 4),
                WeightKg = Math.Round(weight, 2),
                BoundingBoxMin = min,
                BoundingBoxMax = max,
                Cog = cog,
                PartCount = secondaries + 1,
                SecondaryCount = secondaries,
                MainPart = ReadMainPart(main),
            };
            return rec;
        }

        private static MainPartDetails ReadMainPart(Part part)
        {
            var d = new MainPartDetails
            {
                Guid = part.Identifier.GUID.ToString(),
                Id = part.Identifier.ID,
                Mark = Report(part, "PART_POS"),
                Name = Safe(() => part.Name) ?? "",
                Profile = Safe(() => part.Profile?.ProfileString) ?? "",
                Material = Safe(() => part.Material?.MaterialString) ?? "",
                Class = Safe(() => part.Class) ?? "",
                TeklaType = part.GetType().Name,
                Length = Math.Round(ReportDouble(part, "LENGTH"), 1),
            };

            if (part is Beam beam)
            {
                d.StartPoint = Xyz(beam.StartPoint);
                d.EndPoint = Xyz(beam.EndPoint);
            }
            else if (part is PolyBeam poly)
            {
                try
                {
                    var pts = poly.Contour?.ContourPoints;
                    if (pts != null && pts.Count >= 2)
                    {
                        d.StartPoint = Xyz(pts[0] as Point);
                        d.EndPoint = Xyz(pts[pts.Count - 1] as Point);
                    }
                }
                catch { /* optional */ }
            }

            return d;
        }

        private static void GetBbox(Part part, out double[] min, out double[] max, out double[] cog)
        {
            min = max = cog = null;
            try
            {
                var solid = part.GetSolid();
                if (solid != null)
                {
                    min = Xyz(solid.MinimumPoint);
                    max = Xyz(solid.MaximumPoint);
                    if (min != null && max != null)
                    {
                        cog = new[]
                        {
                            Math.Round((min[0] + max[0]) / 2.0, 1),
                            Math.Round((min[1] + max[1]) / 2.0, 1),
                            Math.Round((min[2] + max[2]) / 2.0, 1),
                        };
                    }
                    return;
                }
            }
            catch { /* fallback below */ }

            try
            {
                var o = part.GetCoordinateSystem().Origin;
                min = Xyz(o);
                max = Xyz(o);
                cog = Xyz(o);
            }
            catch
            {
                min = max = cog = new[] { 0.0, 0.0, 0.0 };
            }
        }

        private static int CountSecondaries(Assembly assembly)
        {
            try
            {
                ArrayList list = assembly.GetSecondaries();
                return list == null ? 0 : list.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string KindOf(Assembly.AssemblyTypeEnum type)
        {
            switch (type)
            {
                case Assembly.AssemblyTypeEnum.PRECAST_ASSEMBLY:
                    return "CastUnit";
                case Assembly.AssemblyTypeEnum.STEEL_ASSEMBLY:
                case Assembly.AssemblyTypeEnum.TIMBER_ASSEMBLY:
                    return "SteelAssembly";
                default:
                    return "InSitu";
            }
        }

        private static string ReadFloorUda(ModelObject obj)
        {
            if (obj == null) return "";
            foreach (var name in FloorUdaNames)
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
                catch { /* next name */ }
            }
            return "";
        }

        /// <summary>
        /// Keep author-typed UDAs ("Level 01", "L1") but map common aliases.
        /// </summary>
        internal static string NormalizeFloorName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string inferred = FloorWiseDrawingExtractor.InferFloorFromText(raw);
            return string.IsNullOrWhiteSpace(inferred) ? raw.Trim() : inferred;
        }

        private static string WriteModelCopy(string modelDir, string json)
        {
            if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            {
                Console.WriteLine("[CastUnits] model folder missing — skip model-folder write.");
                return "";
            }

            string path = Path.Combine(modelDir, "floor_wise_cast_units.json");
            File.WriteAllText(path, json, Encoding.UTF8);
            return path;
        }

        private static void WriteWorkspaceCopies(string workspaceDir, FloorWiseProject project, string json)
        {
            if (string.IsNullOrWhiteSpace(workspaceDir)) return;
            string dir = Path.Combine(workspaceDir, "Export", "cast_units");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "floor_wise_cast_units.json"), json, Encoding.UTF8);

            foreach (var floor in project.Floors)
            {
                string safe = SafeFile(floor.Name);
                var slice = new
                {
                    project.ModelName,
                    Floor = floor.Name,
                    floor.FloorSource,
                    floor.ElevationZmm,
                    floor.CastUnitCount,
                    floor.AssemblyCount,
                    floor.CastUnits,
                    floor.Assemblies,
                };
                File.WriteAllText(
                    Path.Combine(dir, safe + ".json"),
                    JsonConvert.SerializeObject(slice, Formatting.Indented),
                    Encoding.UTF8);
            }
        }

        private static double MidZ(FloorCastUnit rec) => MidZ(rec.BoundingBoxMin, rec.BoundingBoxMax);

        private static double MidZ(double[] min, double[] max)
        {
            if (min == null || max == null || min.Length < 3 || max.Length < 3)
                return double.NaN;
            return (min[2] + max[2]) / 2.0;
        }

        private static string Report(ModelObject obj, string name)
        {
            string v = "";
            try { obj.GetReportProperty(name, ref v); } catch { /* optional */ }
            return v ?? "";
        }

        private static double ReportDouble(ModelObject obj, string name)
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

        private static double[] Xyz(Point p)
        {
            if (p == null) return null;
            return new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1) };
        }

        private static string SafeId(Assembly a)
        {
            try { return a.Identifier.ID.ToString(); } catch { return "?"; }
        }

        private static T Safe<T>(Func<T> fn)
        {
            try { return fn(); } catch { return default; }
        }

        private static string SafeFile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace(' ', '_').Replace('/', '_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s;
        }
    }
}

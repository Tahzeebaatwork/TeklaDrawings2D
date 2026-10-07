// DeepDataExtractor.cs
// ─────────────────────────────────────────────────────────────────
// Global 3D (site) → Local 2D (drawing) via
//   MatrixFactory.ToCoordinateSystem(mainPart.GetCoordinateSystem())
//
// Writes three purpose-specific JSON trees:
//   Steel  → fabrication_assemblies.json  (bolts, welds, cuts, solid edges)
//   Precast → precast_cast_units.json     (COG, embeds, cuts)
//   RCC    → rcc_elements.json            (rebar shape / DIM_A.. / cover)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;
using TSSolid = Tekla.Structures.Model.Solid;

namespace TeklaExtractor.Services
{
    public class DeepDataExtractor
    {
        public const int MaxSolidEdgesPerPart = 150;

        private readonly Model _model;

        public DeepDataExtractor(Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            if (!_model.GetConnectionStatus())
                throw new InvalidOperationException("Tekla Structures is not connected.");
        }

        public DeepExtractionResult Run(string baseDir, bool selectedOnly)
        {
            var result = new DeepExtractionResult
            {
                ModelName = SafeModelName(),
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
            };

            Console.WriteLine(selectedOnly
                ? "\n[Deep] Extracting selected assemblies (global → local CS)..."
                : "\n[Deep] Extracting all unique assemblies (global → local CS)...");

            var assemblies = selectedOnly ? CollectSelectedAssemblies() : CollectAllAssemblies();
            Console.WriteLine($"[Deep] {assemblies.Count} unique assemblies to process.");

            int n = 0;
            foreach (var assembly in assemblies)
            {
                n++;
                try
                {
                    ExtractAssembly(assembly, result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Deep] assembly {SafeId(assembly)} skipped: {ex.Message}");
                }

                if (n % 50 == 0)
                    Console.WriteLine($"[Deep] {n}/{assemblies.Count}...");
            }

            result.SteelCount = result.Steel.Count;
            result.PrecastCount = result.Precast.Count;
            result.RccCount = result.Rcc.Count;
            WriteFiles(result, baseDir);
            return result;
        }

        public void ExtractAssembly(Assembly assembly, DeepExtractionResult result)
        {
            if (assembly == null) return;
            try { assembly.Select(); } catch { /* best effort */ }
            if (!assembly.Identifier.IsValid()) return;

            var mainPart = assembly.GetMainPart() as Part;
            if (mainPart == null) return;

            var localCs = mainPart.GetCoordinateSystem();
            Matrix globalToLocal = MatrixFactory.ToCoordinateSystem(localCs);

            var type = assembly.GetAssemblyType();
            switch (type)
            {
                case Assembly.AssemblyTypeEnum.STEEL_ASSEMBLY:
                case Assembly.AssemblyTypeEnum.TIMBER_ASSEMBLY:
                    result.Steel.Add(BuildSteel(assembly, mainPart, localCs, globalToLocal, type));
                    break;
                case Assembly.AssemblyTypeEnum.PRECAST_ASSEMBLY:
                    result.Precast.Add(BuildPrecast(assembly, mainPart, localCs, globalToLocal));
                    break;
                default:
                    result.Rcc.Add(BuildRcc(assembly, mainPart, localCs, globalToLocal));
                    break;
            }
        }

        // =====================================================================
        // STEEL FABRICATOR
        // =====================================================================
        private FabricationAssembly BuildSteel(
            Assembly assembly, Part mainPart, CoordinateSystem localCs, Matrix toLocal,
            Assembly.AssemblyTypeEnum type)
        {
            var solidInfo = ExtractSolid(mainPart, toLocal);
            var cuts = ExtractCutsAndFittings(mainPart, toLocal);
            var rec = new FabricationAssembly
            {
                AssemblyMark = AssemblyMark(assembly, mainPart),
                AssemblyType = type.ToString(),
                AssemblyId = assembly.Identifier.ID,
                MainPart = ReadMainPart(mainPart),
                Geometry = BuildGeometry(localCs, mainPart, toLocal, solidInfo),
                Features =
                {
                    BoltHoles = ExtractBoltHoles(assembly, mainPart, toLocal, solidInfo.Box),
                    Welds = ExtractWeldsOn(mainPart),
                    CutsAndFittings = cuts,
                },
                SecondaryParts = ExtractSecondaries(assembly, toLocal),
                NeedsDetailView = cuts.Count > 0,
            };
            return rec;
        }

        // =====================================================================
        // PRECAST CAST UNIT
        // =====================================================================
        private CastUnitRecord BuildPrecast(
            Assembly assembly, Part mainPart, CoordinateSystem localCs, Matrix toLocal)
        {
            var solidInfo = ExtractSolid(mainPart, toLocal);
            var cuts = ExtractCutsAndFittings(mainPart, toLocal);
            var geo = BuildGeometry(localCs, mainPart, toLocal, solidInfo);

            return new CastUnitRecord
            {
                CUMark = AssemblyMark(assembly, mainPart),
                AssemblyId = assembly.Identifier.ID,
                MainPartProfile = ProfileOf(mainPart),
                ConcreteGrade = MaterialOf(mainPart),
                Volume_m3 = Round(ReportDouble(mainPart, "VOLUME") / 1e9, 4),
                WeightKg = Round(ReportDouble(mainPart, "WEIGHT"), 2),
                Geometry = new CastUnitGeometry
                {
                    LocalDatumPoint_0_0_0 = geo.LocalDatumPoint_0_0_0,
                    LocalVector_X = geo.LocalVector_X,
                    LocalVector_Y = geo.LocalVector_Y,
                    COG_Local = geo.COG_Local,
                    BoundingBox = geo.BoundingBox,
                    HasFormlinerFinish = !string.IsNullOrWhiteSpace(Safe(() => mainPart.Finish)),
                    SolidEdgeCount = geo.SolidEdgeCount,
                },
                Embeds = ExtractEmbeds(assembly, toLocal),
                CutsAndFittings = cuts,
                SolidEdges = solidInfo.Edges,
                UDAs = ReadCommonUdas(mainPart),
                NeedsDetailView = cuts.Count > 0,
            };
        }

        // =====================================================================
        // RCC / CAST-IN-PLACE
        // =====================================================================
        private RccElement BuildRcc(
            Assembly assembly, Part mainPart, CoordinateSystem localCs, Matrix toLocal)
        {
            var solidInfo = ExtractSolid(mainPart, toLocal);
            var cuts = ExtractCutsAndFittings(mainPart, toLocal);
            return new RccElement
            {
                ElementMark = AssemblyMark(assembly, mainPart),
                ElementType = InferRccType(mainPart),
                PourPhase = Safe(() => mainPart.PourPhase),
                AssemblyId = assembly.Identifier.ID,
                Profile = ProfileOf(mainPart),
                ConcreteGrade = MaterialOf(mainPart),
                ConcreteCover = ReadCover(mainPart),
                Geometry = BuildGeometry(localCs, mainPart, toLocal, solidInfo),
                Reinforcement = ExtractRebars(assembly, mainPart),
                NeedsDetailView = cuts.Count > 0,
            };
        }

        // =====================================================================
        // BOLT HOLES (local X,Y for 2D dimensions)
        // =====================================================================
        private List<BoltHoleRecord> ExtractBoltHoles(
            Assembly assembly, Part mainPart, Matrix toLocal, BoundingBoxRecord box)
        {
            var holes = new List<BoltHoleRecord>();
            foreach (var part in AllParts(assembly, mainPart))
            {
                ModelObjectEnumerator bolts;
                try { bolts = part.GetBolts(); }
                catch { continue; }
                if (bolts == null) continue;

                while (bolts.MoveNext())
                {
                    var group = bolts.Current as BoltGroup;
                    if (group == null) continue;
                    ArrayList positions;
                    try { positions = group.BoltPositions; }
                    catch { continue; }
                    if (positions == null) continue;

                    string holeType = HoleTypeName(group);
                    double diameter = group.BoltSize + group.Tolerance;
                    if (diameter <= 0) diameter = group.BoltSize;

                    foreach (Point global in positions)
                    {
                        if (global == null) continue;
                        Point local = toLocal.Transform(global);
                        holes.Add(new BoltHoleRecord
                        {
                            Type = holeType,
                            Diameter = Round(diameter, 2),
                            BoltSize = Round(group.BoltSize, 2),
                            Tolerance = Round(group.Tolerance, 2),
                            LocalCenterX = Round(local.X, 2),
                            LocalCenterY = Round(local.Y, 2),
                            LocalCenterZ = Round(local.Z, 2),
                            EdgeDistanceX = Round(EdgeDistance(local.X, box.Min[0], box.Max[0]), 2),
                            EdgeDistanceY = Round(EdgeDistance(local.Y, box.Min[1], box.Max[1]), 2),
                            SlottedHoleX = Round(group.SlottedHoleX, 2),
                            SlottedHoleY = Round(group.SlottedHoleY, 2),
                        });
                    }
                }
            }
            return holes;
        }

        // =====================================================================
        // WELDS + SECONDARIES / EMBEDS
        // =====================================================================
        private List<SecondaryPartRecord> ExtractSecondaries(Assembly assembly, Matrix toLocal)
        {
            var list = new List<SecondaryPartRecord>();
            ArrayList secondaries;
            try { secondaries = assembly.GetSecondaries(); }
            catch { return list; }
            if (secondaries == null) return list;

            foreach (var obj in secondaries)
            {
                var part = obj as Part;
                if (part == null) continue;
                Point origin = part.GetCoordinateSystem().Origin;
                Point local = toLocal.Transform(origin);
                list.Add(new SecondaryPartRecord
                {
                    Mark = PartMark(part),
                    GUID = part.Identifier.GUID.ToString(),
                    Profile = ProfileOf(part),
                    MaterialGrade = MaterialOf(part),
                    LocalInsertPoint = Xyz(local),
                    Welds = ExtractWeldsOn(part),
                });
            }
            return list;
        }

        private List<EmbedRecord> ExtractEmbeds(Assembly assembly, Matrix toLocal)
        {
            var list = new List<EmbedRecord>();
            ArrayList secondaries;
            try { secondaries = assembly.GetSecondaries(); }
            catch { return list; }
            if (secondaries == null) return list;

            foreach (var obj in secondaries)
            {
                var part = obj as Part;
                if (part == null) continue;
                var cs = part.GetCoordinateSystem();
                Point local = toLocal.Transform(cs.Origin);
                bool skewed = Math.Abs(cs.AxisX.Z) > 0.15 || Math.Abs(cs.AxisY.Z) > 0.15;
                list.Add(new EmbedRecord
                {
                    GUID = part.Identifier.GUID.ToString(),
                    Name = string.IsNullOrWhiteSpace(part.Name) ? ProfileOf(part) : part.Name,
                    Profile = ProfileOf(part),
                    LocalInsertPoint = Xyz(local),
                    IsSkewed = skewed,
                });
            }
            return list;
        }

        private static List<WeldRecord> ExtractWeldsOn(Part part)
        {
            var list = new List<WeldRecord>();
            ModelObjectEnumerator welds;
            try { welds = part.GetWelds(); }
            catch { return list; }
            if (welds == null) return list;

            while (welds.MoveNext())
            {
                var weld = welds.Current as BaseWeld;
                if (weld == null) continue;
                double size = weld.SizeAbove > 0 ? weld.SizeAbove : weld.SizeBelow;
                list.Add(new WeldRecord
                {
                    Type = weld.TypeAbove.ToString().Replace("WELD_TYPE_", ""),
                    Size = Round(size, 2),
                    SizeAbove = Round(weld.SizeAbove, 2),
                    SizeBelow = Round(weld.SizeBelow, 2),
                    ShopWeld = weld.ShopWeld,
                    IsSiteWeld = !weld.ShopWeld,
                });
            }
            return list;
        }

        // =====================================================================
        // BOOLEAN CUTS, FITTINGS, CUT PLANES + SOLID EDGES
        // =====================================================================
        private List<CutFittingRecord> ExtractCutsAndFittings(Part mainPart, Matrix toLocal)
        {
            var cuts = new List<CutFittingRecord>();
            ModelObjectEnumerator booleans;
            try { booleans = mainPart.GetBooleans(); }
            catch { booleans = null; }

            if (booleans != null)
            {
                while (booleans.MoveNext())
                {
                    if (booleans.Current is BooleanPart booleanPart)
                    {
                        if (booleanPart.Type != BooleanPart.BooleanTypeEnum.BOOLEAN_CUT &&
                            booleanPart.Type != BooleanPart.BooleanTypeEnum.BOOLEAN_WELDPREP)
                            continue;

                        Part tool = booleanPart.OperativePart;
                        Point global = tool != null
                            ? tool.GetCoordinateSystem().Origin
                            : booleanPart.GetCoordinateSystem().Origin;
                        Point local = toLocal.Transform(global);
                        var size = CutToolSize(tool, toLocal);
                        cuts.Add(new CutFittingRecord
                        {
                            Type = booleanPart.Type == BooleanPart.BooleanTypeEnum.BOOLEAN_WELDPREP ? "WeldPrep" : "BooleanCut",
                            Profile = tool != null ? ProfileOf(tool) : "",
                            Length = size[0],
                            Depth = size[1],
                            Width = size[2],
                            LocalCenterX = Round(local.X, 2),
                            LocalCenterY = Round(local.Y, 2),
                            LocalCenterZ = Round(local.Z, 2),
                        });
                    }
                    else if (booleans.Current is CutPlane cutPlane)
                    {
                        Point local = PlaneOriginLocal(cutPlane.Plane, toLocal);
                        cuts.Add(new CutFittingRecord
                        {
                            Type = "CutPlane",
                            LocalCenterX = local.X,
                            LocalCenterY = local.Y,
                            LocalCenterZ = local.Z,
                        });
                    }
                    else if (booleans.Current is Fitting fitting)
                    {
                        Point local = PlaneOriginLocal(fitting.Plane, toLocal);
                        cuts.Add(new CutFittingRecord
                        {
                            Type = "Fitting",
                            LocalCenterX = local.X,
                            LocalCenterY = local.Y,
                            LocalCenterZ = local.Z,
                        });
                    }
                }
            }

            return cuts;
        }

        private SolidInfo ExtractSolid(Part part, Matrix toLocal)
        {
            var info = new SolidInfo();
            try
            {
                TSSolid solid = part.GetSolid();
                if (solid == null || !solid.IsValid())
                    return info;

                Point minL = toLocal.Transform(solid.MinimumPoint);
                Point maxL = toLocal.Transform(solid.MaximumPoint);
                info.Box = new BoundingBoxRecord
                {
                    Min = new[] { Round(Math.Min(minL.X, maxL.X), 1), Round(Math.Min(minL.Y, maxL.Y), 1), Round(Math.Min(minL.Z, maxL.Z), 1) },
                    Max = new[] { Round(Math.Max(minL.X, maxL.X), 1), Round(Math.Max(minL.Y, maxL.Y), 1), Round(Math.Max(minL.Z, maxL.Z), 1) },
                };

                EdgeEnumerator edges = solid.GetEdgeEnumerator();
                int count = 0;
                while (edges != null && edges.MoveNext())
                {
                    count++;
                    if (info.Edges.Count >= MaxSolidEdgesPerPart)
                    {
                        info.Truncated = true;
                        continue;
                    }

                    var edge = edges.Current as Edge;
                    if (edge == null) continue;
                    Point s = toLocal.Transform(edge.StartPoint);
                    Point e = toLocal.Transform(edge.EndPoint);
                    double len = Distance(s, e);
                    info.Edges.Add(new SolidEdgeRecord
                    {
                        StartLocal = Xyz(s),
                        EndLocal = Xyz(e),
                        Length = Round(len, 1),
                    });
                }
                info.EdgeCount = count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Deep] GetSolid failed: {ex.Message}");
            }
            return info;
        }

        // =====================================================================
        // REBAR (BBS legs DIM_A.. + hooks + spacing)
        // =====================================================================
        private List<RebarRecord> ExtractRebars(Assembly assembly, Part mainPart)
        {
            var list = new List<RebarRecord>();
            foreach (var part in AllParts(assembly, mainPart))
            {
                ModelObjectEnumerator rebars;
                try { rebars = part.GetReinforcements(); }
                catch { continue; }
                if (rebars == null) continue;

                while (rebars.MoveNext())
                {
                    var reinf = rebars.Current as Reinforcement;
                    if (reinf == null) continue;
                    list.Add(ReadRebar(reinf));
                }
            }
            return list;
        }

        private static RebarRecord ReadRebar(Reinforcement reinf)
        {
            var rec = new RebarRecord
            {
                RebarGroupGUID = reinf.Identifier.GUID.ToString(),
                Name = reinf.Name ?? "",
                Grade = reinf.Grade ?? "",
                BarCount = Safe(() => reinf.GetNumberOfRebars()),
            };

            var group = reinf as RebarGroup;
            var single = reinf as SingleRebar;
            rec.Size = group != null ? (group.Size ?? "") : (single != null ? (single.Size ?? "") : "");

            rec.ShapeCode = ReportString(reinf, "SHAPE");
            if (string.IsNullOrWhiteSpace(rec.ShapeCode))
                rec.ShapeCode = ReportString(reinf, "SHAPE_CODE");

            rec.TotalLength = Round(ReportDouble(reinf, "LENGTH"), 1);
            rec.LegDimensions = ReadLegDimensions(reinf);
            if (rec.LegDimensions.Count == 0)
                rec.LegDimensions = LegsFromGeometry(reinf);

            rec.SpacingList = BuildSpacing(group);
            rec.SpacingZone = rec.SpacingList;

            var startHook = group != null ? group.StartHook : single?.StartHook;
            var endHook = group != null ? group.EndHook : single?.EndHook;
            rec.Hooks = new RebarHooks
            {
                StartHook = ToHook(startHook),
                EndHook = ToHook(endHook),
            };
            return rec;
        }

        private static List<double> ReadLegDimensions(Reinforcement reinf)
        {
            var dims = new List<double>();
            foreach (var key in new[] { "DIM_A", "DIM_B", "DIM_C", "DIM_D", "DIM_E", "DIM_F", "DIM_G", "DIM_H", "DIM_I", "DIM_J" })
            {
                double v = ReportDouble(reinf, key);
                if (v > 0.5) dims.Add(Round(v, 1));
            }
            return dims;
        }

        private static List<double> LegsFromGeometry(Reinforcement reinf)
        {
            var dims = new List<double>();
            try
            {
                ArrayList geoms = reinf.GetRebarGeometries(true);
                if (geoms == null || geoms.Count == 0) return dims;
                var geom = geoms[0] as RebarGeometry;
                if (geom?.Shape?.Points == null) return dims;
                Point prev = null;
                foreach (Point p in geom.Shape.Points)
                {
                    if (prev != null)
                    {
                        double len = Distance(prev, p);
                        if (len > 1.0) dims.Add(Round(len, 1));
                    }
                    prev = p;
                }
            }
            catch { /* optional */ }
            return dims;
        }

        private static string BuildSpacing(RebarGroup group)
        {
            if (group?.Spacings == null || group.Spacings.Count == 0) return "";
            var parts = new List<string>();
            foreach (var s in group.Spacings)
            {
                if (s == null) continue;
                double v;
                if (s is double d) v = d;
                else if (!double.TryParse(Convert.ToString(s, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                    continue;
                parts.Add(Round(v, 1).ToString(CultureInfo.InvariantCulture));
            }
            return string.Join("*", parts);
        }

        private static HookRecord ToHook(RebarHookData hook)
        {
            if (hook == null) return new HookRecord();
            return new HookRecord
            {
                Angle = Round(hook.Angle, 1),
                Radius = Round(hook.Radius, 1),
                Length = Round(hook.Length, 1),
            };
        }

        // =====================================================================
        // ASSEMBLY COLLECTION
        // =====================================================================
        private List<Assembly> CollectAllAssemblies()
        {
            var map = new Dictionary<int, Assembly>();
            var parts = GroundTruthExtractor.ExtractParts(_model);
            foreach (var part in parts)
            {
                Assembly assembly = ResolveParentAssembly(part);
                if (assembly == null) continue;
                int id = assembly.Identifier.ID;
                if (id == 0 || map.ContainsKey(id)) continue;
                map[id] = assembly;
            }
            return new List<Assembly>(map.Values);
        }

        private List<Assembly> CollectSelectedAssemblies()
        {
            var map = new Dictionary<int, Assembly>();
            var selector = new Tekla.Structures.Model.UI.ModelObjectSelector();
            var selected = selector.GetSelectedObjects();
            while (selected.MoveNext())
            {
                Part part = selected.Current as Part;
                if (part == null && selected.Current is Assembly already)
                {
                    if (already.Identifier.IsValid() && !map.ContainsKey(already.Identifier.ID))
                        map[already.Identifier.ID] = already;
                    continue;
                }
                if (part == null) continue;
                Assembly assembly = ResolveParentAssembly(part);
                if (assembly == null) continue;
                int id = assembly.Identifier.ID;
                if (id == 0 || map.ContainsKey(id)) continue;
                map[id] = assembly;
            }
            return new List<Assembly>(map.Values);
        }

        /// <summary>
        /// Part → parent Assembly. Never treat a Part identifier as an Assembly id.
        /// </summary>
        public static Assembly ResolveParentAssembly(Part part)
        {
            if (part == null) return null;
            Assembly assembly;
            try { assembly = part.GetAssembly(); }
            catch { return null; }
            if (assembly == null) return null;
            try { assembly.Select(); } catch { /* identifier may still be valid */ }
            if (!assembly.Identifier.IsValid() || assembly.Identifier.ID == 0)
                return null;
            return assembly;
        }

        // =====================================================================
        // HELPERS
        // =====================================================================
        private LocalGeometry BuildGeometry(CoordinateSystem localCs, Part mainPart, Matrix toLocal, SolidInfo solid)
        {
            return new LocalGeometry
            {
                LocalDatumPoint_0_0_0 = Xyz(localCs.Origin),
                LocalVector_X = Xyz(localCs.AxisX),
                LocalVector_Y = Xyz(localCs.AxisY),
                COG_Local = CogLocal(mainPart, toLocal),
                BoundingBox = solid.Box,
                SolidEdgeCount = solid.EdgeCount,
                SolidEdgesTruncated = solid.Truncated,
                SolidEdges = solid.Edges,
            };
        }

        private static double[] CogLocal(Part part, Matrix toLocal)
        {
            double x = ReportDouble(part, "COG_X");
            double y = ReportDouble(part, "COG_Y");
            double z = ReportDouble(part, "COG_Z");
            var global = new Point(x, y, z);
            return Xyz(toLocal.Transform(global));
        }

        private MainPartRecord ReadMainPart(Part part)
        {
            return new MainPartRecord
            {
                GUID = part.Identifier.GUID.ToString(),
                Id = part.Identifier.ID,
                Mark = PartMark(part),
                Profile = ProfileOf(part),
                MaterialGrade = MaterialOf(part),
                Length = Round(ReportDouble(part, "LENGTH"), 1),
                WeightKg = Round(ReportDouble(part, "WEIGHT"), 2),
            };
        }

        private static CoverRecord ReadCover(Part part)
        {
            double cover = ReportDouble(part, "COVER");
            if (cover <= 0) cover = ReportDouble(part, "CONCRETE_COVER");
            double top = ReportDouble(part, "COVER_TOP");
            double bottom = ReportDouble(part, "COVER_BOTTOM");
            double sides = ReportDouble(part, "COVER_SIDE");
            if (top <= 0) top = cover;
            if (bottom <= 0) bottom = cover;
            if (sides <= 0) sides = cover;
            return new CoverRecord { Top = Round(top, 1), Bottom = Round(bottom, 1), Sides = Round(sides, 1) };
        }

        private static Dictionary<string, string> ReadCommonUdas(Part part)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "TOLERANCE_CLASS", "POUR_PHASE", "FLOOR", "COMMENT", "PRECAST_TYPE" })
            {
                string v = "";
                try { part.GetUserProperty(key, ref v); } catch { v = ""; }
                if (string.IsNullOrWhiteSpace(v))
                    v = ReportString(part, key);
                if (!string.IsNullOrWhiteSpace(v))
                    map[key] = v;
            }
            return map;
        }

        private static IEnumerable<Part> AllParts(Assembly assembly, Part mainPart)
        {
            yield return mainPart;
            ArrayList secondaries;
            try { secondaries = assembly.GetSecondaries(); }
            catch { yield break; }
            if (secondaries == null) yield break;
            foreach (var obj in secondaries)
            {
                if (obj is Part p) yield return p;
            }
        }

        private static string AssemblyMark(Assembly assembly, Part mainPart)
        {
            string mark = ReportString(assembly, "ASSEMBLY_POS");
            if (!string.IsNullOrWhiteSpace(mark)) return mark;
            try
            {
                var ns = assembly.AssemblyNumber;
                if (ns != null && (!string.IsNullOrWhiteSpace(ns.Prefix) || ns.StartNumber > 0))
                    return (ns.Prefix ?? "") + ns.StartNumber;
            }
            catch { /* fall through */ }
            return PartMark(mainPart);
        }

        private static string PartMark(Part part)
        {
            try
            {
                string m = part.GetPartMark();
                if (!string.IsNullOrWhiteSpace(m)) return m;
            }
            catch { /* optional */ }
            string pos = ReportString(part, "PART_POS");
            return string.IsNullOrWhiteSpace(pos) ? "" : pos;
        }

        private static string InferRccType(Part part)
        {
            var n = (part.Name ?? "").ToUpperInvariant();
            if (n.Contains("COL")) return "Column";
            if (n.Contains("BEAM")) return "Beam";
            if (n.Contains("SLAB") || n.Contains("DECK")) return "Slab";
            if (n.Contains("WALL")) return "Wall";
            if (n.Contains("FOOT") || n.Contains("FOUND")) return "Foundation";
            return part.GetType().Name;
        }

        private static string HoleTypeName(BoltGroup group)
        {
            try
            {
                if (group.HoleType == BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED) return "Slotted";
                if (group.HoleType == BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_OVERSIZED) return "Oversized";
                if (group.HoleType == BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_NO_HOLE) return "NoHole";
                if (group.HoleType == BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_TAPPED) return "Tapped";
            }
            catch { /* default */ }
            return "Round";
        }

        private static double[] CutToolSize(Part tool, Matrix toLocal)
        {
            if (tool == null) return new[] { 0.0, 0.0, 0.0 };
            try
            {
                var solid = tool.GetSolid();
                Point minL = toLocal.Transform(solid.MinimumPoint);
                Point maxL = toLocal.Transform(solid.MaximumPoint);
                return new[]
                {
                    Round(Math.Abs(maxL.X - minL.X), 1),
                    Round(Math.Abs(maxL.Y - minL.Y), 1),
                    Round(Math.Abs(maxL.Z - minL.Z), 1),
                };
            }
            catch
            {
                return new[] { 0.0, 0.0, 0.0 };
            }
        }

        private static Point PlaneOriginLocal(Plane plane, Matrix toLocal)
        {
            if (plane?.Origin == null) return new Point();
            Point local = toLocal.Transform(plane.Origin);
            return new Point(Round(local.X, 2), Round(local.Y, 2), Round(local.Z, 2));
        }

        private static double EdgeDistance(double value, double min, double max)
        {
            return Math.Min(Math.Abs(value - min), Math.Abs(max - value));
        }

        private static string ReportString(ModelObject obj, string name)
        {
            string value = "";
            try { obj.GetReportProperty(name, ref value); } catch { /* optional */ }
            return value ?? "";
        }

        private static double ReportDouble(ModelObject obj, string name)
        {
            double value = 0;
            try
            {
                if (obj.GetReportProperty(name, ref value))
                    return value;
            }
            catch { /* optional */ }
            double.TryParse(ReportString(obj, name), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            return value;
        }

        private static string ProfileOf(Part part)
        {
            try { return part.Profile?.ProfileString ?? ""; } catch { return ""; }
        }

        private static string MaterialOf(Part part)
        {
            try { return part.Material?.MaterialString ?? ""; } catch { return ""; }
        }

        private static double[] Xyz(Point p)
        {
            if (p == null) return new double[3];
            return new[] { Round(p.X, 2), Round(p.Y, 2), Round(p.Z, 2) };
        }

        private static double[] Xyz(Vector v)
        {
            if (v == null) return new double[3];
            return new[] { Round(v.X, 4), Round(v.Y, 4), Round(v.Z, 4) };
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Round(double v, int d) => Math.Round(v, d);

        private static T Safe<T>(Func<T> fn)
        {
            try { return fn(); } catch { return default; }
        }

        private string SafeModelName()
        {
            try { return _model.GetInfo().ModelName ?? ""; } catch { return ""; }
        }

        private static string SafeId(Assembly assembly)
        {
            try { return assembly.Identifier.ID.ToString(); } catch { return "?"; }
        }

        private static void WriteFiles(DeepExtractionResult result, string baseDir)
        {
            Directory.CreateDirectory(baseDir);
            string deepDir = Path.Combine(baseDir, "deep");
            Directory.CreateDirectory(deepDir);

            string combined = Path.Combine(baseDir, "deep_extraction.json");
            string steel = Path.Combine(deepDir, "fabrication_assemblies.json");
            string precast = Path.Combine(deepDir, "precast_cast_units.json");
            string rcc = Path.Combine(deepDir, "rcc_elements.json");

            var settings = new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore };

            File.WriteAllText(combined, JsonConvert.SerializeObject(result, settings), Encoding.UTF8);
            File.WriteAllText(steel, JsonConvert.SerializeObject(result.Steel, settings), Encoding.UTF8);
            File.WriteAllText(precast, JsonConvert.SerializeObject(result.Precast, settings), Encoding.UTF8);
            File.WriteAllText(rcc, JsonConvert.SerializeObject(result.Rcc, settings), Encoding.UTF8);

            Console.WriteLine($"[Deep] Steel={result.SteelCount}  Precast={result.PrecastCount}  RCC={result.RccCount}");
            Console.WriteLine($"[Deep] {combined}");
            Console.WriteLine($"[Deep] {steel}");
            Console.WriteLine($"[Deep] {precast}");
            Console.WriteLine($"[Deep] {rcc}");
        }

        private class SolidInfo
        {
            public BoundingBoxRecord Box { get; set; } = new BoundingBoxRecord();
            public List<SolidEdgeRecord> Edges { get; set; } = new List<SolidEdgeRecord>();
            public int EdgeCount { get; set; }
            public bool Truncated { get; set; }
        }
    }
}

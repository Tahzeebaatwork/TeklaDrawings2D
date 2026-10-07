// GaDrawingBuilder.cs
// Creates one General Arrangement (GADrawing) per floor via
// Tekla.Structures.Drawing Open API, then FloorWise extracts JSON+PDF.

using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public class GaDrawingBuilder
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public GaDrawingBuilder(TSModel.Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _handler = new DrawingHandler();
        }

        public int CreateMissingFloorPlans(IEnumerable<TSModel.Part> parts)
        {
            if (_handler == null || !_handler.GetConnectionStatus())
            {
                Console.WriteLine("[GAD] DrawingHandler not connected.");
                return 0;
            }

            var groups = new Dictionary<string, FloorGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts ?? Enumerable.Empty<TSModel.Part>())
            {
                if (part == null) continue;
                Point origin;
                try { origin = part.GetCoordinateSystem().Origin; }
                catch { continue; }
                double z = origin.Z;
                string floor = FloorWiseDrawingExtractor.FloorBand(z);
                if (!groups.TryGetValue(floor, out var g))
                {
                    g = new FloorGroup { Floor = floor };
                    groups[floor] = g;
                }
                g.Parts.Add(part);
                g.Zs.Add(z);
                g.MinX = Math.Min(g.MinX, origin.X);
                g.MinY = Math.Min(g.MinY, origin.Y);
                g.MaxX = Math.Max(g.MaxX, origin.X);
                g.MaxY = Math.Max(g.MaxY, origin.Y);
                g.Zmin = Math.Min(g.Zmin, z);
                g.Zmax = Math.Max(g.Zmax, z);
                if (g.Parts.Count <= 60)
                    Expand(g, part);
            }

            int created = 0;
            foreach (var g in groups.Values.OrderBy(x => x.Zs.Count == 0 ? 0 : x.Zs.Average()))
            {
                if (g.Parts.Count < 1) continue;
                try
                {
                    if (EnsureGa(g))
                        created++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GAD] create '{g.Floor}': {ex.Message}");
                }
            }

            Console.WriteLine($"[GAD] floor-plan GA sheets ready: {created} / {groups.Count} floors.");
            return created;
        }

        private bool EnsureGa(FloorGroup g)
        {
            string name = "GAD " + g.Floor;
            var existing = FindGa(name);
            if (existing != null)
            {
                Console.WriteLine($"[GAD] exists '{name}' — adding plan view if missing");
                AddPlanView(existing, g);
                return true;
            }

            var gad = TryInsertGa(name, "EXP_GA_STD") ?? TryInsertGa(name, "standard");
            if (gad == null)
            {
                Console.WriteLine($"[GAD] Insert failed '{name}'");
                return false;
            }
            gad.Title1 = g.Floor;
            gad.Title2 = "General Arrangement";
            gad.Title3 = $"Z {g.Zmin:0}–{g.Zmax:0} mm";
            try { gad.Modify(); } catch { /* titles optional */ }

            AddPlanView(gad, g);
            return true;
        }

        private static GADrawing TryInsertGa(string name, string attributes)
        {
            try
            {
                var gad = new GADrawing(name, attributes);
                if (gad.Insert())
                {
                    Console.WriteLine($"[GAD] created '{name}' attributes={attributes}");
                    return gad;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GAD] Insert '{name}' attributes={attributes}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// UpdateDrawing / SetActiveDrawing throw "does not work for GA drawings"
        /// in this Tekla 2026 build. Create3dView / CreateTopView take the
        /// Drawing object directly.
        /// </summary>
        private static void AddPlanView(GADrawing gad, FloorGroup g)
        {
            try
            {
                var sheet = gad.GetSheet();
                if (sheet != null)
                {
                    var views = sheet.GetAllViews();
                    int n = 0;
                    while (views != null && views.MoveNext())
                    {
                        if (views.Current is View) n++;
                    }
                    if (n > 0)
                    {
                        Console.WriteLine($"[GAD] '{gad.Name}' already has {n} view(s)");
                        return;
                    }
                }
            }
            catch { /* continue and try insert */ }

            var viewCs = new CoordinateSystem
            {
                Origin = new Point(
                    Finite(g.MinX, 0), Finite(g.MinY, 0),
                    Finite((g.Zmin + g.Zmax) / 2.0, 0)),
                AxisX = new Vector(1, 0, 0),
                AxisY = new Vector(0, 1, 0),
            };
            var box = new AABB(
                new Point(Finite(g.MinX, 0) - 500, Finite(g.MinY, 0) - 500, Finite(g.Zmin, 0) - 200),
                new Point(Finite(g.MaxX, 10000) + 500, Finite(g.MaxY, 10000) + 500, Finite(g.Zmax, 3000) + 200));

            var attrs = new View.ViewAttributes();
            try { attrs.Scale = SuggestScale(g); } catch { /* optional */ }

            View view = null;
            bool ok = false;
            try
            {
                var sheet = gad.GetSheet();
                if (sheet != null)
                {
                    view = new View(sheet, viewCs, viewCs, box, "standard");
                    try { view.Attributes.Scale = SuggestScale(g); } catch { /* optional */ }
                    view.Name = g.Floor + " plan";
                    view.Origin = new Point(180, 140);
                    ok = view.Insert();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GAD] View.Insert: {ex.Message}");
            }

            if (!ok)
            {
                try { ok = View.Create3dView(gad, viewCs, viewCs, box, new Point(180, 140), attrs, out view); }
                catch (Exception ex) { Console.WriteLine($"[GAD] Create3dView: {ex.Message}"); }
            }
            if (!ok)
            {
                try { ok = View.CreateTopView(gad, new Point(180, 140), attrs, out view); }
                catch (Exception ex) { Console.WriteLine($"[GAD] CreateTopView: {ex.Message}"); }
            }

            try { gad.Modify(); } catch { /* optional */ }
            Console.WriteLine(ok
                ? $"[GAD] plan view on '{gad.Name}' parts={g.Parts.Count}"
                : $"[GAD] no view added on '{gad.Name}' (sheet still exported)");
        }

        private static double Finite(double v, double fallback)
        {
            return double.IsInfinity(v) || double.IsNaN(v) ? fallback : v;
        }

        private GADrawing FindGa(string name)
        {
            var en = _handler.GetDrawings();
            while (en != null && en.MoveNext())
            {
                if (en.Current is GADrawing ga)
                {
                    if (string.Equals(ga.Name, name, StringComparison.OrdinalIgnoreCase))
                        return ga;
                    if (string.Equals(ga.Title1, name.Replace("GAD ", ""), StringComparison.OrdinalIgnoreCase)
                        && (ga.Name ?? "").StartsWith("GAD", StringComparison.OrdinalIgnoreCase))
                        return ga;
                }
            }
            return null;
        }

        private static double SuggestScale(FloorGroup g)
        {
            double span = Math.Max(g.MaxX - g.MinX, g.MaxY - g.MinY);
            if (span > 80000) return 200;
            if (span > 40000) return 100;
            if (span > 20000) return 75;
            return 50;
        }

        private static double PartZ(TSModel.Part part)
        {
            try
            {
                var solid = part.GetSolid();
                if (solid != null)
                    return (solid.MinimumPoint.Z + solid.MaximumPoint.Z) / 2.0;
            }
            catch { /* optional */ }
            try { return part.GetCoordinateSystem().Origin.Z; }
            catch { return 0; }
        }

        private static void Expand(FloorGroup g, TSModel.Part part)
        {
            try
            {
                var solid = part.GetSolid();
                if (solid == null) return;
                var mn = solid.MinimumPoint;
                var mx = solid.MaximumPoint;
                g.MinX = Math.Min(g.MinX, mn.X);
                g.MinY = Math.Min(g.MinY, mn.Y);
                g.MaxX = Math.Max(g.MaxX, mx.X);
                g.MaxY = Math.Max(g.MaxY, mx.Y);
                g.Zmin = Math.Min(g.Zmin, mn.Z);
                g.Zmax = Math.Max(g.Zmax, mx.Z);
            }
            catch { /* optional */ }
        }

        private class FloorGroup
        {
            public string Floor;
            public List<TSModel.Part> Parts = new List<TSModel.Part>();
            public List<double> Zs = new List<double>();
            public double MinX = double.MaxValue, MinY = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue;
            public double Zmin = double.MaxValue, Zmax = double.MinValue;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace TeklaExtractor
{
	/// <summary>
	/// Opt-in mesh for a wall that has no bars yet. Cover, spacing, and trimmers follow the outline.
	/// Skips a part that already has reinforcement unless replace is set.
	/// </summary>
    public class RebarMeshBuilder
    {
		private readonly Model _model;

		public RebarMeshBuilder(Model model)
        {
            _model = model;
        }

		public void Generate(Part part, double cover = 25.0, double spacing = 200.0, string size = "10M", double diagonalLength = 600.0, bool replace = false)
        {
            if (part == null) return;
			string id = "";
			try
			{
				string mark = "";
				if (part.GetReportProperty("PART_POS", ref mark) && !string.IsNullOrEmpty(mark))
					id = mark;
			}
			catch { }
			if (string.IsNullOrEmpty(id))
			{
				try { id = part.Identifier.ID.ToString(CultureInfo.InvariantCulture); } catch { id = "?"; }
			}

			if (HasReinforcement(part))
            {
                if (!replace)
                {
					Console.WriteLine("[RebarMesh] " + id + " already has reinforcement. Skipping.");
					return;
				}
				DeleteReinforcement(part);
			}

			double partCover = ReadCover(part);
			if (partCover > 1.0) cover = partCover;
			if (spacing < 20.0) spacing = 200.0;
			if (diagonalLength < 50.0) diagonalLength = 600.0;

			Frame frame = Frame.From(part);
			if (frame == null)
			{
				Console.WriteLine("[RebarMesh] " + id + " no solid frame");
				return;
			}

			double length = frame.MaxA - frame.MinA;
			double height = frame.MaxU - frame.MinU;
			double thick = frame.MaxZ - frame.MinZ;
			if (length < cover * 2 + 10 || height < cover * 2 + 10)
			{
				Console.WriteLine("[RebarMesh] " + id + " too small for cover " + cover.ToString("0", CultureInfo.InvariantCulture));
                    return;
                }

			var openings = ReadOpenings(part, frame, cover);
			var zLayers = Layers(frame, cover, thick);
			int seq = 1;
			int inserted = 0;

			inserted += PlaceGrid(part, frame, openings, zLayers, cover, spacing, size, "V", ref seq);
			inserted += PlaceGrid(part, frame, openings, zLayers, cover, spacing, size, "H", ref seq);
			inserted += PlaceTrimmers(part, frame, openings, zLayers, cover, diagonalLength, ref seq);

			try { _model.CommitChanges(); } catch { }
			Console.WriteLine("[RebarMesh] " + id
				+ " groups=" + inserted.ToString(CultureInfo.InvariantCulture)
				+ " cover=" + cover.ToString("0.#", CultureInfo.InvariantCulture)
				+ " spacing=" + spacing.ToString("0.#", CultureInfo.InvariantCulture)
				+ " openings=" + openings.Count.ToString(CultureInfo.InvariantCulture));
		}

		private static int PlaceGrid(Part part, Frame frame, List<Opening> openings, List<double> zLayers,
			double cover, double spacing, string size, string orientation, ref int seq)
		{
			bool vertical = orientation == "V";
			double min = vertical ? frame.MinA : frame.MinU;
			double max = vertical ? frame.MaxA : frame.MaxU;
			double inner = (max - min) - 2.0 * cover;
			if (inner < spacing) return 0;
			int spaces = (int)Math.Floor(inner / spacing);
			if (spaces < 1) spaces = 1;
			double used = spaces * spacing;
			double edge = (inner - used) / 2.0;
			double first = min + cover + edge;

			var stations = new List<double>();
			for (double s = first; s <= max - cover + 0.5; s += spacing)
				stations.Add(s);

			// Segments that share length and line-up become one run.
			var buckets = new Dictionary<string, List<Seg>>();
			foreach (double station in stations)
			{
				double a0, a1;
				if (vertical)
				{
					a0 = frame.MinU + cover;
					a1 = LocalFar(frame, station, vertical: true) - cover;
					if (a1 < a0) { double t = a0; a0 = a1; a1 = t; }
				}
				else
				{
					a0 = frame.MinA + cover;
					a1 = LocalFar(frame, station, vertical: false) - cover;
					if (a1 < a0) { double t = a0; a0 = a1; a1 = t; }
				}
				foreach (var span in Subtract(a0, a1, openings, vertical, station, cover))
				{
					if (span.Item2 - span.Item1 < 12.0) continue;
					string key = Math.Round(span.Item1, 0).ToString(CultureInfo.InvariantCulture)
						+ "|" + Math.Round(span.Item2, 0).ToString(CultureInfo.InvariantCulture);
					if (!buckets.TryGetValue(key, out var list))
					{
						list = new List<Seg>();
						buckets[key] = list;
					}
					list.Add(new Seg { Station = station, A = span.Item1, B = span.Item2 });
				}
			}

			int n = 0;
			foreach (var kv in buckets)
			{
				kv.Value.Sort((x, y) => x.Station.CompareTo(y.Station));
				var run = new List<Seg>();
				foreach (Seg seg in kv.Value)
				{
					if (run.Count == 0)
					{
						run.Add(seg);
						continue;
					}
					double gap = seg.Station - run[run.Count - 1].Station;
					if (Math.Abs(gap - spacing) <= 1.0)
					{
						run.Add(seg);
						continue;
					}
					if (InsertRun(part, frame, run, vertical, zLayers, size, spacing, ref seq)) n++;
					run = new List<Seg> { seg };
				}
				if (run.Count > 0 && InsertRun(part, frame, run, vertical, zLayers, size, spacing, ref seq))
					n++;
			}
			return n;
		}

		private static bool InsertRun(Part part, Frame frame, List<Seg> run, bool vertical, List<double> zLayers,
			string size, double spacing, ref int seq)
		{
			if (run == null || run.Count == 0) return false;
			Seg first = run[0];
			string mark = "RB" + size + " " + seq.ToString("0000", CultureInfo.InvariantCulture);
			seq++;
			bool any = false;
			foreach (double z in zLayers)
			{
				Point barA, barB, spaceA, spaceB;
				if (vertical)
				{
					barA = frame.GlobalPoint(first.Station, first.A, z);
					barB = frame.GlobalPoint(first.Station, first.B, z);
					spaceA = frame.GlobalPoint(first.Station, (first.A + first.B) / 2.0, z);
					spaceB = frame.GlobalPoint(run[run.Count - 1].Station, (first.A + first.B) / 2.0, z);
				}
				else
				{
					barA = frame.GlobalPoint(first.A, first.Station, z);
					barB = frame.GlobalPoint(first.B, first.Station, z);
					spaceA = frame.GlobalPoint((first.A + first.B) / 2.0, first.Station, z);
					spaceB = frame.GlobalPoint((first.A + first.B) / 2.0, run[run.Count - 1].Station, z);
				}
				if (run.Count == 1)
				{
					any |= InsertSingle(part, barA, barB, size, mark);
				}
				else
				{
					double gap = Math.Abs(run[1].Station - run[0].Station);
					if (gap < 1.0) gap = spacing;
					any |= InsertGroup(part, barA, barB, spaceA, spaceB, run.Count, gap, size, mark);
				}
			}
			return any;
		}

		private static int PlaceTrimmers(Part part, Frame frame, List<Opening> openings, List<double> zLayers,
			double cover, double diagonalLength, ref int seq)
		{
			int n = 0;
			foreach (Opening op in openings)
			{
				double left = op.MinA - cover;
				double right = op.MaxA + cover;
				double bot = op.MinU - cover;
				double top = op.MaxU + cover;
				double y0 = frame.MinU + cover;
				double y1 = frame.MaxU - cover;
				double x0 = frame.MinA + cover;
				double x1 = frame.MaxA - cover;
				// Two bars each side, just outside the expanded opening, still inside the panel.
				double pair = Math.Max(cover, 40.0);
				n += TrimmerPair(part, frame, zLayers, true, left - pair, left, y0, y1, ref seq);
				n += TrimmerPair(part, frame, zLayers, true, right, right + pair, y0, y1, ref seq);
				n += TrimmerPair(part, frame, zLayers, false, bot - pair, bot, x0, x1, ref seq);
				n += TrimmerPair(part, frame, zLayers, false, top, top + pair, x0, x1, ref seq);
				n += Diagonals(part, frame, zLayers, op, diagonalLength, ref seq);
			}
			return n;
		}

		private static int TrimmerPair(Part part, Frame frame, List<double> zLayers, bool vertical,
			double s0, double s1, double a, double b, ref int seq)
		{
			if (s1 < s0) { double t = s0; s0 = s1; s1 = t; }
			if (b - a < 12.0) return 0;
			var run = new List<Seg>
			{
				new Seg { Station = s0, A = a, B = b },
				new Seg { Station = s1, A = a, B = b }
			};
			double gap = Math.Abs(s1 - s0);
			if (gap < 1.0) gap = 40.0;
			string size = "15M";
			// InsertRun assigns the RB name. Force 15M by passing size.
			return InsertRun(part, frame, run, vertical, zLayers, size, gap, ref seq) ? 1 : 0;
		}

		private static int Diagonals(Part part, Frame frame, List<double> zLayers, Opening op, double length, ref int seq)
		{
			// 45 degrees into the concrete, away from the opening center.
			double cx = (op.MinA + op.MaxA) / 2.0;
			double cy = (op.MinU + op.MaxU) / 2.0;
			var corners = new[]
			{
				new Point(op.MinA, op.MinU, 0),
				new Point(op.MaxA, op.MinU, 0),
				new Point(op.MinA, op.MaxU, 0),
				new Point(op.MaxA, op.MaxU, 0)
			};
			int n = 0;
			string mark = "RB15M " + seq.ToString("0000", CultureInfo.InvariantCulture);
			seq++;
			double leg = length / Math.Sqrt(2.0);
			foreach (Point c in corners)
			{
				double dx = c.X >= cx ? 1.0 : -1.0;
				double dy = c.Y >= cy ? 1.0 : -1.0;
				foreach (double z in zLayers)
				{
					Point a = frame.GlobalPoint(c.X, c.Y, z);
					Point b = frame.GlobalPoint(c.X + dx * leg, c.Y + dy * leg, z);
					if (InsertSingle(part, a, b, "15M", mark)) n++;
				}
			}
			return n > 0 ? 1 : 0;
		}

		private static bool InsertGroup(Part part, Point barA, Point barB, Point spaceA, Point spaceB,
			int count, double spacing, string size, string mark)
		{
			try
			{
				var group = new RebarGroup();
				group.Father = part;
				group.Size = size;
				group.Grade = "400W";
				group.Name = mark;
				group.Class = 3;
				group.SpacingType = RebarGroup.RebarGroupSpacingTypeEnum.SPACING_TYPE_EXACT_SPACINGS;
				var polygon = new Polygon();
				polygon.Points.Add(barA);
				polygon.Points.Add(barB);
				group.Polygons.Add(polygon);
				group.StartPoint = spaceA;
				group.EndPoint = spaceB;
				for (int i = 1; i < count; i++)
					group.Spacings.Add(spacing);
				ZeroCoverOffset(group);
				if (!group.Insert())
				{
					Console.WriteLine("[RebarMesh] group insert failed " + mark);
					return false;
				}
				try { group.SetUserProperty("Mark_ID", mark); } catch { }
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine("[RebarMesh] group " + mark + ": " + ex.Message);
				return false;
			}
		}

		private static bool InsertSingle(Part part, Point a, Point b, string size, string mark)
		{
			try
			{
				var bar = new SingleRebar();
				bar.Father = part;
				bar.Size = size;
				bar.Grade = "400W";
				bar.Name = mark;
				bar.Class = 3;
				if (bar.Polygon == null) bar.Polygon = new Polygon();
				bar.Polygon.Points.Add(a);
				bar.Polygon.Points.Add(b);
				ZeroCoverOffset(bar);
				if (!bar.Insert())
				{
					Console.WriteLine("[RebarMesh] single insert failed " + mark);
					return false;
				}
				try { bar.SetUserProperty("Mark_ID", mark); } catch { }
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine("[RebarMesh] single " + mark + ": " + ex.Message);
				return false;
			}
		}

		private static void ZeroCoverOffset(Reinforcement bar)
		{
			try
			{
				bar.StartPointOffsetType = Reinforcement.RebarOffsetTypeEnum.OFFSET_TYPE_COVER_THICKNESS;
				bar.StartPointOffsetValue = 0;
				bar.EndPointOffsetType = Reinforcement.RebarOffsetTypeEnum.OFFSET_TYPE_COVER_THICKNESS;
				bar.EndPointOffsetValue = 0;
			}
			catch { }
		}

		private static List<Tuple<double, double>> Subtract(double a, double b, List<Opening> openings, bool verticalBar, double station, double cover)
		{
			var spans = new List<Tuple<double, double>> { Tuple.Create(Math.Min(a, b), Math.Max(a, b)) };
			foreach (Opening op in openings)
			{
				bool hits = verticalBar
					? station >= op.MinA - cover && station <= op.MaxA + cover
					: station >= op.MinU - cover && station <= op.MaxU + cover;
				if (!hits) continue;
				double h0 = verticalBar ? op.MinU - cover : op.MinA - cover;
				double h1 = verticalBar ? op.MaxU + cover : op.MaxA + cover;
				var next = new List<Tuple<double, double>>();
				foreach (var s in spans)
				{
					if (h1 <= s.Item1 || h0 >= s.Item2)
					{
						next.Add(s);
						continue;
					}
					if (h0 > s.Item1 + 1.0) next.Add(Tuple.Create(s.Item1, h0));
					if (h1 < s.Item2 - 1.0) next.Add(Tuple.Create(h1, s.Item2));
				}
				spans = next;
			}
			return spans;
		}

		private static double LocalFar(Frame frame, double station, bool vertical)
		{
			// Far end of the concrete at this station. A stepped edge returns a shorter height or length.
			double bestMin = vertical ? frame.MinU : frame.MinA;
			double bestMax = vertical ? frame.MaxU : frame.MaxA;
			bool hit = false;
			double lo = double.MaxValue;
			double hi = double.MinValue;
			foreach (Point p in frame.LocalPoints)
			{
				double along = vertical ? p.X : p.Y;
				double across = vertical ? p.Y : p.X;
				if (Math.Abs(along - station) > 25.0) continue;
				hit = true;
				if (across < lo) lo = across;
				if (across > hi) hi = across;
			}
			if (!hit) return bestMax;
			return hi;
		}

		private static List<double> Layers(Frame frame, double cover, double thick)
		{
			var z = new List<double>();
			if (thick < cover * 2.0 + 8.0)
			{
				z.Add((frame.MinZ + frame.MaxZ) / 2.0);
				return z;
			}
			z.Add(frame.MinZ + cover);
			z.Add(frame.MaxZ - cover);
			return z;
		}

		private static List<Opening> ReadOpenings(Part part, Frame frame, double cover)
		{
			var list = new List<Opening>();
			ModelObjectEnumerator cuts = null;
			try { cuts = part.GetBooleans(); } catch { return list; }
			while (cuts != null && cuts.MoveNext())
			{
				var bp = cuts.Current as BooleanPart;
				if (bp == null || bp.OperativePart == null) continue;
				if (bp.Type != BooleanPart.BooleanTypeEnum.BOOLEAN_CUT) continue;
				Solid solid = null;
				try { solid = bp.OperativePart.GetSolid(); } catch { }
				if (solid == null) continue;
				double minA = double.MaxValue, maxA = double.MinValue, minU = double.MaxValue, maxU = double.MinValue;
				int n = 0;
				try
				{
					EdgeEnumerator edges = solid.GetEdgeEnumerator();
					while (edges != null && edges.MoveNext() && n < 2000)
					{
						var edge = edges.Current as Edge;
						if (edge == null) continue;
						n++;
						Consider(frame, edge.StartPoint, ref minA, ref maxA, ref minU, ref maxU);
						Consider(frame, edge.EndPoint, ref minA, ref maxA, ref minU, ref maxU);
					}
				}
				catch { }
				if (minA >= maxA || minU >= maxU) continue;
				double w = maxA - minA;
				double h = maxU - minU;
				if (w < 50.0 || h < 50.0) continue;
				if (w > frame.MaxA - frame.MinA - 20.0 && h > frame.MaxU - frame.MinU - 20.0) continue;
				list.Add(new Opening { MinA = minA, MaxA = maxA, MinU = minU, MaxU = maxU });
			}
			return list;
		}

		private static void Consider(Frame frame, Point global, ref double minA, ref double maxA, ref double minU, ref double maxU)
		{
			if (global == null) return;
			Point local = frame.ToLocal.Transform(global);
			double a = frame.AlongOf(local);
			double u = frame.UpOf(local);
			if (a < minA) minA = a;
			if (a > maxA) maxA = a;
			if (u < minU) minU = u;
			if (u > maxU) maxU = u;
		}

		private static bool HasReinforcement(Part part)
		{
			try
			{
				ModelObjectEnumerator en = part.GetReinforcements();
				return en != null && en.GetSize() > 0;
			}
			catch { return false; }
		}

		private static void DeleteReinforcement(Part part)
		{
			var doomed = new List<ModelObject>();
			try
			{
				ModelObjectEnumerator en = part.GetReinforcements();
				while (en != null && en.MoveNext())
				{
					if (en.Current is ModelObject mo) doomed.Add(mo);
				}
			}
			catch { return; }
			foreach (ModelObject mo in doomed)
			{
				try { mo.Delete(); } catch { }
			}
		}

		private static double ReadCover(Part part)
		{
			foreach (string key in new[] { "COVER", "COVER_THICKNESS", "CONCRETE_COVER" })
			{
				double v = 0;
				try
				{
					if (part.GetReportProperty(key, ref v) && v > 5.0 && v < 80.0)
						return v;
				}
				catch { }
			}
			return 0;
		}

		private sealed class Seg
		{
			public double Station;
			public double A;
			public double B;
		}

		private sealed class Opening
		{
			public double MinA, MaxA, MinU, MaxU;
		}

		private sealed class Frame
		{
			public Matrix ToLocal;
			public Matrix ToGlobal;
			public int Along;
			public int Up;
			public int Thick;
			public double MinA, MaxA, MinU, MaxU, MinZ, MaxZ;
			public List<Point> LocalPoints = new List<Point>();

			public static Frame From(Part part)
			{
				CoordinateSystem cs;
				try { cs = part.GetCoordinateSystem(); }
				catch { return null; }
				if (cs == null) return null;
				Matrix toLocal, toGlobal;
				try
				{
					toLocal = MatrixFactory.ToCoordinateSystem(cs);
					toGlobal = MatrixFactory.FromCoordinateSystem(cs);
				}
				catch { return null; }

				var locals = new List<Point>();
				try
				{
					EdgeEnumerator edges = part.GetSolid()?.GetEdgeEnumerator();
					int n = 0;
					while (edges != null && edges.MoveNext() && n < 4000)
					{
						if (edges.Current is Edge edge)
						{
							n++;
							if (edge.StartPoint != null) locals.Add(toLocal.Transform(edge.StartPoint));
							if (edge.EndPoint != null) locals.Add(toLocal.Transform(edge.EndPoint));
						}
					}
				}
				catch { }
				if (locals.Count < 2)
				{
					try
					{
						Solid solid = part.GetSolid();
						if (solid != null)
						{
							locals.Add(toLocal.Transform(solid.MinimumPoint));
							locals.Add(toLocal.Transform(solid.MaximumPoint));
						}
					}
					catch { return null; }
				}
				if (locals.Count < 2) return null;

				double[] span = new double[3];
				double[] min = new double[3];
				double[] max = new double[3];
				for (int i = 0; i < 3; i++)
				{
					min[i] = double.MaxValue;
					max[i] = double.MinValue;
					foreach (Point p in locals)
					{
						double c = Coord(p, i);
						if (c < min[i]) min[i] = c;
						if (c > max[i]) max[i] = c;
					}
					span[i] = max[i] - min[i];
				}
				int along = 0;
				if (span[1] > span[along]) along = 1;
				if (span[2] > span[along]) along = 2;
				int up = along == 0 ? 1 : 0;
				int thick = 3 - along - up;
				if (span[thick] > span[up])
				{
					int t = up;
					up = thick;
					thick = t;
				}

				var frame = new Frame
				{
					ToLocal = toLocal,
					ToGlobal = toGlobal,
					Along = along,
					Up = up,
					Thick = thick,
					MinA = min[along],
					MaxA = max[along],
					MinU = min[up],
					MaxU = max[up],
					MinZ = min[thick],
					MaxZ = max[thick]
				};
				foreach (Point p in locals)
				{
					frame.LocalPoints.Add(new Point(frame.AlongOf(p), frame.UpOf(p), frame.ThickOf(p)));
				}
				return frame;
			}

			public double AlongOf(Point p) { return Coord(p, Along); }
			public double UpOf(Point p) { return Coord(p, Up); }
			public double ThickOf(Point p) { return Coord(p, Thick); }

			public Point GlobalPoint(double a, double u, double z)
			{
				double[] c = new double[3];
				c[Along] = a;
				c[Up] = u;
				c[Thick] = z;
				return ToGlobal.Transform(new Point(c[0], c[1], c[2]));
			}

			private static double Coord(Point p, int axis)
			{
				if (axis == 0) return p.X;
				if (axis == 1) return p.Y;
				return p.Z;
			}
        }
    }
}

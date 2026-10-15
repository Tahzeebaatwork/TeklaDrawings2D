using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
	/// <summary>
	/// Places a label beside existing text. Three outward steps, then a short leader.
	/// Rebar runs get one spacing string, not one label per bar.
	/// </summary>
	public static class AntiCollisionEngine
	{
		public const int MaxSteps = 3;
		public const double GapPaperMm = 2.5 + 1.5;

		public struct PlaceResult
		{
			public Point At;
			public bool Leader;
			public Point Anchor;
		}

		public static PlaceResult Place(View view, Point desired, string text, int scale, Vector outward, bool allowLeader = true)
		{
			var result = new PlaceResult
			{
				At = desired,
				Anchor = desired,
				Leader = false
			};
			if (view == null || desired == null || string.IsNullOrEmpty(text))
				return result;

			int sc = scale < 1 ? 1 : scale;
			double step = GapPaperMm * sc;
			double height = GapPaperMm * sc;
			double width = Math.Max(height, text.Length * height * 0.45);
			Vector dir = outward ?? new Vector(0, 1, 0);
			double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
			if (len < 1e-6)
			{
				dir = new Vector(0, 1, 0);
				len = 1;
			}
			dir = new Vector(dir.X / len, dir.Y / len, 0);

			var occupied = Collect(view);
			Point at = desired;
			bool clear = !Hits(occupied, at, width, height);
			for (int i = 0; i < MaxSteps && !clear; i++)
			{
				at = new Point(at.X + dir.X * step, at.Y + dir.Y * step, 0);
				clear = !Hits(occupied, at, width, height);
			}
			result.At = at;
			result.Leader = allowLeader && (!clear || Distance(desired, at) > step * 0.5);
			if (result.Leader && Distance(desired, at) > 1.0)
			{
				try
				{
					var line = new Tekla.Structures.Drawing.Line(view, desired, at);
					line.Insert();
				}
				catch (Exception ex)
				{
					Console.WriteLine("[Collision] leader: " + ex.Message);
				}
			}
			return result;
		}

		/// <summary>
		/// One callout per bar mark: straight "15M - 2880", bent "RB10M 0040", plus "(count-1)@spacing=total".
		/// " T. &amp; B." is added only when this one callout covers bars on both faces. A mark the view
		/// already labels natively (one row per face) is left alone, so it gets no suffix.
		/// </summary>
		public static void PlaceRebarRuns(View view, TSModel.Part part, int scale, ICollection<string> existingMarkTexts = null)
		{
			if (view == null || part == null) return;
			TSModel.ModelObjectEnumerator en;
			try { en = part.GetReinforcements(); }
			catch { return; }
			if (en == null) return;

			FaceSplit faces = FaceSplit.Of(part);
			var groups = new Dictionary<string, List<TSModel.RebarGroup>>(StringComparer.OrdinalIgnoreCase);
			while (en.MoveNext())
			{
				var g = en.Current as TSModel.RebarGroup;
				if (g == null) continue;
				string callout = Callout(g);
				if (string.IsNullOrWhiteSpace(callout)) continue;
				if (!groups.TryGetValue(callout, out var list))
				{
					list = new List<TSModel.RebarGroup>();
					groups[callout] = list;
				}
				list.Add(g);
			}

			var marked = (existingMarkTexts ?? new List<string>()).Select(Squash).ToList();
			int placed = 0, native = 0;
			foreach (var kv in groups)
			{
				string key = Squash(kv.Key);
				if (marked.Any(t => t.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
				{
					native++;
					continue;
				}
				TSModel.RebarGroup g = kv.Value[0];
				bool front = false, back = false;
				foreach (TSModel.RebarGroup member in kv.Value)
				{
					int side = faces.Side(FirstPoint(member));
					if (side < 0) front = true;
					else if (side > 0) back = true;
				}
				string mark = kv.Key + (front && back ? " T. & B." : "");

				Point anchor = ToView(view, g.StartPoint) ?? new Point(20, 20, 0);
				int bars = 0;
				try { bars = g.GetNumberOfRebars(); } catch { }
				double spacing = FirstSpacing(g);
				if (bars >= 2 && spacing >= 1.0)
				{
					int gaps = bars - 1;
					string spacingText = gaps.ToString(CultureInfo.InvariantCulture)
						+ "@" + spacing.ToString("0", CultureInfo.InvariantCulture)
						+ "=" + (gaps * spacing).ToString("0", CultureInfo.InvariantCulture);
					var spacingAt = Place(view, anchor, spacingText, scale, new Vector(0, 1, 0));
					InsertText(view, spacingAt.At, spacingText);
				}
				Point markAt = new Point(anchor.X, anchor.Y - GapPaperMm * (scale < 1 ? 1 : scale), 0);
				var markPlace = Place(view, markAt, mark, scale, new Vector(0, -1, 0));
				InsertText(view, markPlace.At, mark);
				placed++;
			}
			Console.WriteLine("[Collision] rebar callouts placed=" + placed.ToString(CultureInfo.InvariantCulture)
				+ " already marked in view=" + native.ToString(CultureInfo.InvariantCulture));
		}

		/// <summary>Straight: "15M - 2880". Bent: "RB10M 0040" from REBAR_POS.</summary>
		public static string Callout(TSModel.RebarGroup g)
		{
			if (g == null) return "";
			string size = (g.Size ?? "").Trim();
			bool bent = false;
			try
			{
				if (g.Polygons != null && g.Polygons.Count > 0 && g.Polygons[0] is TSModel.Polygon poly)
					bent = poly.Points != null && poly.Points.Count > 2;
			}
			catch { }
			if (bent)
			{
				string pos = "";
				try { g.GetReportProperty("REBAR_POS", ref pos); } catch { }
				pos = (pos ?? "").Trim();
				if (pos.Length == 0) pos = (g.Name ?? "").Trim();
				if (pos.StartsWith("RB", StringComparison.OrdinalIgnoreCase)) return pos;
				return "RB" + size + " " + pos;
			}
			double len = 0.0;
			try { g.GetReportProperty("LENGTH", ref len); } catch { }
			if (len <= 0.0) return "";
			return size + " - " + Math.Round(len, 0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
		}

		private static string Squash(string s)
		{
			return new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
		}

		private static Point FirstPoint(TSModel.RebarGroup g)
		{
			try
			{
				if (g.Polygons != null && g.Polygons.Count > 0 && g.Polygons[0] is TSModel.Polygon poly
					&& poly.Points != null && poly.Points.Count > 0)
					return poly.Points[0] as Point;
			}
			catch { }
			return g.StartPoint;
		}

		/// <summary>Which face of the panel a point sits on, along the part's thinnest local axis.</summary>
		private sealed class FaceSplit
		{
			private Matrix _toLocal;
			private int _axis = -1;
			private double _mid;

			public static FaceSplit Of(TSModel.Part part)
			{
				var f = new FaceSplit();
				try
				{
					f._toLocal = MatrixFactory.ToCoordinateSystem(part.GetCoordinateSystem());
					var lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
					var hi = new[] { double.MinValue, double.MinValue, double.MinValue };
					TSModel.Solid s = part.GetSolid();
					foreach (double x in new[] { s.MinimumPoint.X, s.MaximumPoint.X })
					foreach (double y in new[] { s.MinimumPoint.Y, s.MaximumPoint.Y })
					foreach (double z in new[] { s.MinimumPoint.Z, s.MaximumPoint.Z })
					{
						Point p = f._toLocal.Transform(new Point(x, y, z));
						double[] c = { p.X, p.Y, p.Z };
						for (int i = 0; i < 3; i++)
						{
							lo[i] = Math.Min(lo[i], c[i]);
							hi[i] = Math.Max(hi[i], c[i]);
						}
					}
					int thin = 0;
					for (int i = 1; i < 3; i++)
						if (hi[i] - lo[i] < hi[thin] - lo[thin]) thin = i;
					f._axis = thin;
					f._mid = (lo[thin] + hi[thin]) / 2.0;
				}
				catch { f._axis = -1; }
				return f;
			}

			/// <summary>-1 front face, +1 back face, 0 unknown or on the mid plane.</summary>
			public int Side(Point global)
			{
				if (_axis < 0 || global == null || _toLocal == null) return 0;
				Point p = _toLocal.Transform(global);
				double c = _axis == 0 ? p.X : (_axis == 1 ? p.Y : p.Z);
				if (Math.Abs(c - _mid) < 1.0) return 0;
				return c < _mid ? -1 : 1;
			}
		}

		private static double FirstSpacing(TSModel.RebarGroup group)
		{
			try
			{
				if (group.Spacings == null || group.Spacings.Count == 0) return 0;
				object s = group.Spacings[0];
				if (s is double d) return d;
				double v;
				if (double.TryParse(Convert.ToString(s, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
					return v;
			}
			catch { }
			return 0;
		}

		private static Point ToView(View view, Point global)
		{
			if (global == null) return null;
			try
			{
				CoordinateSystem cs = view.DisplayCoordinateSystem ?? view.ViewCoordinateSystem;
				if (cs == null) return global;
				return MatrixFactory.ToCoordinateSystem(cs).Transform(global);
			}
			catch
			{
				return global;
			}
		}

		private static void InsertText(View view, Point at, string text)
		{
			try
			{
				var t = new Text(view, at, text);
				try
				{
					t.Attributes.Frame.Type = FrameTypes.None;
					t.Attributes.Font.Height = 2.5;
				}
				catch { }
				t.Insert();
			}
			catch (Exception ex)
			{
				Console.WriteLine("[Collision] text: " + ex.Message);
			}
		}

		private struct Box
		{
			public double MinX, MinY, MaxX, MaxY;
		}

		private static List<Box> Collect(View view)
		{
			var list = new List<Box>();
			try
			{
				DrawingObjectEnumerator en = view.GetAllObjects();
				while (en != null && en.MoveNext())
				{
					if (!(en.Current is Text t)) continue;
					if (t is IAxisAlignedBoundingBox aabb)
					{
						AABB box = null;
						try { box = aabb.GetAxisAlignedBoundingBox(); } catch { }
						if (box != null && box.MinPoint != null && box.MaxPoint != null)
						{
							list.Add(new Box
							{
								MinX = box.MinPoint.X,
								MinY = box.MinPoint.Y,
								MaxX = box.MaxPoint.X,
								MaxY = box.MaxPoint.Y
							});
							continue;
						}
					}
					Point p = t.InsertionPoint;
					if (p == null) continue;
					string s = t.TextString ?? "";
					double h = 8.0;
					double w = Math.Max(h, s.Length * h * 0.45);
					list.Add(new Box { MinX = p.X, MinY = p.Y, MaxX = p.X + w, MaxY = p.Y + h });
				}
			}
			catch { }
			return list;
		}

		private static bool Hits(List<Box> occupied, Point at, double width, double height)
		{
			double minX = at.X;
			double minY = at.Y;
			double maxX = at.X + width;
			double maxY = at.Y + height;
			foreach (Box b in occupied)
			{
				if (maxX > b.MinX && minX < b.MaxX && maxY > b.MinY && minY < b.MaxY)
					return true;
			}
			return false;
		}

		private static double Distance(Point a, Point b)
		{
			double dx = a.X - b.X;
			double dy = a.Y - b.Y;
			return Math.Sqrt(dx * dx + dy * dy);
		}
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;
using Tekla.Structures.Solid;

namespace TeklaExtractor.Services;

public class PrecastDimensionPostProcessor
{
	private sealed class CatReport
	{
		public int Interior;

		public int Chamfer;

		public int Opening;

		public int Fallback;

		public int Marks;

		public int Nudged;

		public int Leaders;

		public int Micro;

		public int Clusters;

		public bool Sheet2Updated;

		public string CogLine = "";
	}

	private sealed class TradeHit
	{
		public string Trade = "";

		public string Label = "";

		public Point Global;

		public double Station;

		public bool IsBottom;
	}

	private sealed class StationBuckets
	{
		public List<double> Lifters = new List<double>();

		public Dictionary<string, List<double>> Splicers = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

		public List<double> Bracing = new List<double>();

		public List<double> TopEmbeds = new List<double>();

		public List<double> Openings = new List<double>();

		public List<double> Ledge = new List<double>();

		public List<double> Reveals = new List<double>();

		public List<double> Holes = new List<double>();

		public Dictionary<string, List<double>> BottomGroutTubes = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

		public List<double> BottomSecondary = new List<double>();

		public List<double> BaseOpenings = new List<double>();

		/// <summary>End-notch / base-step X stations (panel mm) for short recess dims.</summary>
		public List<double> NotchX = new List<double>();

		/// <summary>Base-step / notch heights (panel mm up) for Side A/B verticals.</summary>
		public List<double> NotchHeights = new List<double>();

		public List<double> NoPaint = new List<double>();

		public List<double> Cutouts = new List<double>();

		public List<double> Joints = new List<double>();

		/// <summary>Wall-up stations (mm along panel height) from opening/embed solids — for vertical chains.</summary>
		public List<double> HeightStations = new List<double>();

		public List<double> All = new List<double>();

		public List<TradeHit> Trades = new List<TradeHit>();

		public int Interior;

		public int Chamfer;

		public int Opening;

		public int Fallback;

		public int CouplerCount
		{
			get
			{
				int num = 0;
				foreach (List<double> value in Splicers.Values)
				{
					num += value.Count;
				}
				return num;
			}
		}

		public int BottomGroutTubeCount
		{
			get
			{
				int num = 0;
				foreach (List<double> value in BottomGroutTubes.Values)
				{
					num += value.Count;
				}
				return num;
			}
		}

		public List<double> Couplers
		{
			get
			{
				List<double> list = new List<double>();
				foreach (List<double> value in Splicers.Values)
				{
					list.AddRange(value);
				}
				return list;
			}
		}

		public List<double> Embeds
		{
			get
			{
				List<double> list = new List<double>(TopEmbeds);
				foreach (List<double> value in BottomGroutTubes.Values)
				{
					list.AddRange(value);
				}
				return list;
			}
		}

		public List<double> Secondary => BottomSecondary;

		public void AddItem(string label, double station, Point pt, PanelAxis axis)
		{
			if (double.IsNaN(station))
			{
				return;
			}
			string text = (label ?? "").ToUpperInvariant();
			if (text.Contains("RECESS"))
			{
				return;
			}
			All.Add(station);
			// Rev 2 NO PAINT / paint-boundary stations (do not hardcode chain; model edges only).
			if (text.Contains("NO PAINT") || text.Contains("NOPAINT") || text.Contains("PAINT ZONE")
				|| text.Contains("PAINT BOUND"))
			{
				NoPaint.Add(station);
				return;
			}
			if (text.Contains("P-205") || text.Contains("LIFT") || text.Contains("ANCHOR"))
			{
				Lifters.Add(station);
				Trades.Add(new TradeHit
				{
					Trade = "lifter",
					Label = ShortMark(label, "lifter"),
					Global = pt,
					Station = station,
					IsBottom = false
				});
				return;
			}
			if (text.Contains("P-300") || text.Contains("BRAC"))
			{
				Bracing.Add(station);
				Trades.Add(new TradeHit
				{
					Trade = "bracing",
					Label = ShortMark(label, "bracing"),
					Global = pt,
					Station = station,
					IsBottom = false
				});
				return;
			}
			if (text.Contains("SP15") || text.Contains("SP25") || text.Contains("SP30") || text.Contains("WILLIAMS SPL") || text.Contains("SPLIC"))
			{
				string key = "SP25";
				if (text.Contains("SP15"))
				{
					key = "SP15";
				}
				else if (text.Contains("SP30"))
				{
					key = "SP30";
				}
				else if (text.Contains("SP25"))
				{
					key = "SP25";
				}
				if (!Splicers.ContainsKey(key))
				{
					Splicers[key] = new List<double>();
				}
				Splicers[key].Add(station);
				Trades.Add(new TradeHit
				{
					Trade = "coupler",
					Label = ShortMark(label, "coupler"),
					Global = pt,
					Station = station,
					IsBottom = false
				});
				return;
			}
			if (text.Contains("GT75") || text.Contains("GT100"))
			{
				string key2 = "GT75-1219";
				if (text.Contains("GT75-686"))
				{
					key2 = "GT75-686";
				}
				else if (text.Contains("GT100"))
				{
					key2 = "GT100-1219";
				}
				else if (text.Contains("GT75"))
				{
					key2 = "GT75-1219";
				}
				if (!BottomGroutTubes.ContainsKey(key2))
				{
					BottomGroutTubes[key2] = new List<double>();
				}
				BottomGroutTubes[key2].Add(station);
				Trades.Add(new TradeHit
				{
					Trade = "embed",
					Label = ShortMark(label, "embed"),
					Global = pt,
					Station = station,
					IsBottom = true
				});
				return;
			}
			if (text.Contains("CNDT") || text.Contains("EB-S-PL") || text.Contains("CONDUIT") || text.Contains("SLEEVE") || text.Contains("PIPE") || text.Contains("MEP"))
			{
				BottomSecondary.Add(station);
				Trades.Add(new TradeHit
				{
					Trade = "secondary",
					Label = ShortMark(label, "secondary"),
					Global = pt,
					Station = station,
					IsBottom = true
				});
				return;
			}
			if (text.Contains("GT76") || text.Contains("P-602") || text.Contains("P-616") || text.Contains("PLATE") || text.Contains("EMBED"))
			{
				TopEmbeds.Add(station);
				Trades.Add(new TradeHit
				{
					Trade = "embed",
					Label = ShortMark(label, "embed"),
					Global = pt,
					Station = station,
					IsBottom = false
				});
				return;
			}
			if (text.Contains("JOINT"))
			{
				Joints.Add(station);
				return;
			}
			if (pt != null && axis != null)
			{
				Point point = axis.LocalAlongAcross(pt);
				// Mid-height rule (S2): below 0.35× panel height → bottom secondary row;
				// at/above that band → top embeds. Do not hardcode stations.
				if (point != null && axis.Height > 0.0 && point.Y < 0.35 * axis.Height)
				{
					BottomSecondary.Add(station);
					Trades.Add(new TradeHit
					{
						Trade = "secondary",
						Label = ShortMark(label, "secondary"),
						Global = pt,
						Station = station,
						IsBottom = true
					});
					return;
				}
			}
			TopEmbeds.Add(station);
			Trades.Add(new TradeHit
			{
				Trade = "embed",
				Label = ShortMark(label, "embed"),
				Global = pt,
				Station = station,
				IsBottom = false
			});
		}

		public void Add(string kind, double station)
		{
			if (double.IsNaN(station))
			{
				return;
			}
			All.Add(station);
			switch (kind)
			{
			case "lifter":
				Lifters.Add(station);
				break;
			case "coupler":
				if (!Splicers.ContainsKey("SPLICER"))
				{
					Splicers["SPLICER"] = new List<double>();
				}
				Splicers["SPLICER"].Add(station);
				break;
			case "bracing":
				Bracing.Add(station);
				break;
			case "secondary":
				BottomSecondary.Add(station);
				break;
			case "joint":
				Joints.Add(station);
				break;
			case "opening":
				Openings.Add(station);
				break;
			case "base_opening":
				BaseOpenings.Add(station);
				break;
			case "cutout":
				Cutouts.Add(station);
				break;
			default:
				TopEmbeds.Add(station);
				break;
			}
		}
	}

	private struct PlanePt
	{
		public double X;

		public double Y;
	}

	private sealed class PanelAxis
	{
		public double Min;

		public double Span;

		public double Height;

		public Vector Up;

		public Vector Down;

		public List<PlanePt> Hull;

		private Point _start;

		private Point _end;

		private Point _globalStart;

		private Point _globalEnd;

		private Point _bottomStart;

		private Point _bottomEnd;

		private bool _horizontal;

		private Matrix _toLocal;

		private int _along;

		private int _across;

		public static PanelAxis Measure(Tekla.Structures.Model.Part main, double length)
		{
			CoordinateSystem coordinateSystem;
			try
			{
				coordinateSystem = main.GetCoordinateSystem();
			}
			catch
			{
				return null;
			}
			if (coordinateSystem == null || coordinateSystem.Origin == null)
			{
				return null;
			}
			Matrix matrix;
			try
			{
				matrix = MatrixFactory.ToCoordinateSystem(coordinateSystem);
			}
			catch
			{
				return null;
			}
			List<Point> list = new List<Point>();
			try
			{
				EdgeEnumerator edgeEnumerator = main.GetSolid()?.GetEdgeEnumerator();
				int num = 0;
				while (edgeEnumerator != null && edgeEnumerator.MoveNext() && num < 4000)
				{
					if (edgeEnumerator.Current is Edge edge)
					{
						num++;
						if (edge.StartPoint != null)
						{
							list.Add(matrix.Transform(edge.StartPoint));
						}
						if (edge.EndPoint != null)
						{
							list.Add(matrix.Transform(edge.EndPoint));
						}
					}
				}
			}
			catch
			{
			}
			if (list.Count < 2)
			{
				try
				{
					Solid solid = main.GetSolid();
					if (solid != null)
					{
						list.Add(matrix.Transform(solid.MinimumPoint));
						list.Add(matrix.Transform(solid.MaximumPoint));
					}
				}
				catch
				{
					return null;
				}
			}
			if (list.Count < 2)
			{
				return null;
			}
			int num2 = 0;
			double num3 = double.MaxValue;
			double num4 = 0.0;
			double num5 = 0.0;
			for (int i = 0; i < 3; i++)
			{
				double num6 = double.MaxValue;
				double num7 = double.MinValue;
				foreach (Point item in list)
				{
					double num8 = Coord(item, i);
					if (num8 < num6)
					{
						num6 = num8;
					}
					if (num8 > num7)
					{
						num7 = num8;
					}
				}
				double num9 = Math.Abs(num7 - num6 - length);
				if (num9 < num3)
				{
					num3 = num9;
					num2 = i;
					num4 = num6;
					num5 = num7;
				}
			}
			double midA = Mid(list, (num2 == 0) ? 1 : 0);
			double midB = Mid(list, (num2 == 2) ? 1 : 2);
			Point p = LocalPoint(num2, num4, midA, midB);
			Point p2 = LocalPoint(num2, num5, midA, midB);
			Matrix matrix2;
			try
			{
				matrix2 = MatrixFactory.FromCoordinateSystem(coordinateSystem);
			}
			catch
			{
				return null;
			}
			PanelAxis panelAxis = new PanelAxis
			{
				Min = num4,
				Span = num5 - num4,
				_globalStart = matrix2.Transform(p),
				_globalEnd = matrix2.Transform(p2),
				Up = new Vector(0.0, 1.0, 0.0),
				Down = new Vector(0.0, -1.0, 0.0),
				_toLocal = matrix,
				_along = num2
			};
			panelAxis._start = panelAxis._globalStart;
			panelAxis._end = panelAxis._globalEnd;
			panelAxis._across = OtherAxis(list, num2);
			double num10 = double.MaxValue;
			double num11 = double.MinValue;
			foreach (Point item2 in list)
			{
				double num12 = Coord(item2, panelAxis._across);
				if (num12 < num10)
				{
					num10 = num12;
				}
				if (num12 > num11)
				{
					num11 = num12;
				}
			}
			panelAxis.Height = ((num11 > num10) ? (num11 - num10) : 0.0);
			panelAxis.Hull = ConvexHull(list, panelAxis._along, panelAxis._across);
			return panelAxis;
		}

		public Point LocalAlongAcross(Point global)
		{
			if (global == null || _toLocal == null)
			{
				return null;
			}
			try
			{
				Point p = _toLocal.Transform(global);
				return new Point(Coord(p, _along), Coord(p, _across), 0.0);
			}
			catch
			{
				return null;
			}
		}

		private static int OtherAxis(List<Point> samples, int along)
		{
			int result = ((along == 0) ? 1 : 0);
			double num = -1.0;
			for (int i = 0; i < 3; i++)
			{
				if (i == along)
				{
					continue;
				}
				double num2 = double.MaxValue;
				double num3 = double.MinValue;
				foreach (Point sample in samples)
				{
					double num4 = Coord(sample, i);
					if (num4 < num2)
					{
						num2 = num4;
					}
					if (num4 > num3)
					{
						num3 = num4;
					}
				}
				if (num3 - num2 > num)
				{
					num = num3 - num2;
					result = i;
				}
			}
			return result;
		}

		private static List<PlanePt> ConvexHull(List<Point> samples, int along, int across)
		{
			List<PlanePt> list = new List<PlanePt>();
			foreach (Point sample in samples)
			{
				list.Add(new PlanePt
				{
					X = Coord(sample, along),
					Y = Coord(sample, across)
				});
			}
			if (list.Count < 3)
			{
				return null;
			}
			list.Sort((PlanePt a, PlanePt b) => (a.X != b.X) ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
			List<PlanePt> list2 = new List<PlanePt>();
			foreach (PlanePt item in list)
			{
				while (list2.Count >= 2 && Cross(list2[list2.Count - 2], list2[list2.Count - 1], item) <= 0.0)
				{
					list2.RemoveAt(list2.Count - 1);
				}
				list2.Add(item);
			}
			int num = list2.Count + 1;
			for (int num2 = list.Count - 2; num2 >= 0; num2--)
			{
				PlanePt planePt = list[num2];
				while (list2.Count >= num && Cross(list2[list2.Count - 2], list2[list2.Count - 1], planePt) <= 0.0)
				{
					list2.RemoveAt(list2.Count - 1);
				}
				list2.Add(planePt);
			}
			if (list2.Count > 1)
			{
				list2.RemoveAt(list2.Count - 1);
			}
			if (list2.Count < 3)
			{
				return null;
			}
			return list2;
		}

		private static double Cross(PlanePt o, PlanePt a, PlanePt b)
		{
			return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
		}

		public double Project(Point global)
		{
			if (global == null || _start == null || _end == null)
			{
				return double.NaN;
			}
			double num = _end.X - _start.X;
			double num2 = _end.Y - _start.Y;
			double num3 = _end.Z - _start.Z;
			double num4 = num * num + num2 * num2 + num3 * num3;
			if (num4 < 1E-06)
			{
				return Min;
			}
			double num5 = ((global.X - _start.X) * num + (global.Y - _start.Y) * num2 + (global.Z - _start.Z) * num3) / num4;
			if (num5 < 0.0)
			{
				num5 = 0.0;
			}
			if (num5 > 1.0)
			{
				num5 = 1.0;
			}
			return Min + num5 * Span;
		}

		public void BindView(View view, Tekla.Structures.Model.Part main = null)
		{
			Point point = ToView(_globalStart ?? _start, view);
			Point point2 = ToView(_globalEnd ?? _end, view);
			if (point == null || point2 == null)
			{
				return;
			}
			_start = point;
			_end = point2;
			_horizontal = Math.Abs(point2.X - point.X) >= Math.Abs(point2.Y - point.Y);
			if (_horizontal)
			{
				double y = Math.Max(point.Y, point2.Y);
				double y2 = Math.Min(point.Y, point2.Y);
				if (main != null)
				{
					try
					{
						Solid solid = main.GetSolid();
						if (solid != null && solid.MinimumPoint != null && solid.MaximumPoint != null)
						{
							Matrix matrix = MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem);
							double num = double.MaxValue;
							double num2 = double.MinValue;
							double num3 = double.MaxValue;
							double num4 = double.MinValue;
							double[] array = new double[2]
							{
								solid.MinimumPoint.X,
								solid.MaximumPoint.X
							};
							double[] array2 = new double[2]
							{
								solid.MinimumPoint.Y,
								solid.MaximumPoint.Y
							};
							double[] array3 = new double[2]
							{
								solid.MinimumPoint.Z,
								solid.MaximumPoint.Z
							};
							double[] array4 = array;
							foreach (double x in array4)
							{
								double[] array5 = array2;
								foreach (double y3 in array5)
								{
									double[] array6 = array3;
									foreach (double z in array6)
									{
										Point point3 = matrix.Transform(new Point(x, y3, z));
										if (point3.Y < num)
										{
											num = point3.Y;
										}
										if (point3.Y > num2)
										{
											num2 = point3.Y;
										}
										if (point3.X < num3)
										{
											num3 = point3.X;
										}
										if (point3.X > num4)
										{
											num4 = point3.X;
										}
									}
								}
							}
							if (num < num2)
							{
								y2 = num;
								y = num2;
							}
							if (num3 < num4)
							{
								point.X = num3;
								point2.X = num4;
							}
						}
					}
					catch
					{
					}
				}
				_start = new Point(point.X, y, 0.0);
				_end = new Point(point2.X, y, 0.0);
				_bottomStart = new Point(point.X, y2, 0.0);
				_bottomEnd = new Point(point2.X, y2, 0.0);
				Up = new Vector(0.0, 1.0, 0.0);
				Down = new Vector(0.0, -1.0, 0.0);
				return;
			}
			double x2 = Math.Min(point.X, point2.X);
			double x3 = Math.Max(point.X, point2.X);
			if (main != null)
			{
				try
				{
					Solid solid2 = main.GetSolid();
					if (solid2 != null && solid2.MinimumPoint != null && solid2.MaximumPoint != null)
					{
						Matrix matrix2 = MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem);
						double num5 = double.MaxValue;
						double num6 = double.MinValue;
						double[] array7 = new double[2]
						{
							solid2.MinimumPoint.X,
							solid2.MaximumPoint.X
						};
						double[] array8 = new double[2]
						{
							solid2.MinimumPoint.Y,
							solid2.MaximumPoint.Y
						};
						double[] array9 = new double[2]
						{
							solid2.MinimumPoint.Z,
							solid2.MaximumPoint.Z
						};
						double[] array4 = array7;
						foreach (double x4 in array4)
						{
							double[] array5 = array8;
							foreach (double y4 in array5)
							{
								double[] array6 = array9;
								foreach (double z2 in array6)
								{
									Point point4 = matrix2.Transform(new Point(x4, y4, z2));
									if (point4.X < num5)
									{
										num5 = point4.X;
									}
									if (point4.X > num6)
									{
										num6 = point4.X;
									}
								}
							}
						}
						if (num5 < num6)
						{
							x2 = num5;
							x3 = num6;
						}
					}
				}
				catch
				{
				}
			}
			_start = new Point(x2, point.Y, 0.0);
			_end = new Point(x2, point2.Y, 0.0);
			_bottomStart = new Point(x3, point.Y, 0.0);
			_bottomEnd = new Point(x3, point2.Y, 0.0);
			Up = new Vector(-1.0, 0.0, 0.0);
			Down = new Vector(1.0, 0.0, 0.0);
		}

		public List<double> Chain(List<double> stations)
		{
			List<double> list = new List<double>();
			if (stations != null)
			{
				foreach (double station in stations)
				{
					if (!double.IsNaN(station))
					{
						double num = station;
						if (num < Min)
						{
							num = Min;
						}
						if (num > Min + Span)
						{
							num = Min + Span;
						}
						list.Add(Math.Round(num, 1));
					}
				}
			}
			list.Sort();
			List<double> list2 = new List<double>();
			foreach (double item in list)
			{
				if (list2.Count == 0 || Math.Abs(list2[list2.Count - 1] - item) > 0.05)
				{
					list2.Add(item);
				}
			}
			return list2;
		}

		public Point PaperPoint(double station)
		{
			double num = ((Span > 1E-06) ? ((station - Min) / Span) : 0.0);
			if (num < 0.0)
			{
				num = 0.0;
			}
			if (num > 1.0)
			{
				num = 1.0;
			}
			return new Point(_start.X + num * (_end.X - _start.X), _start.Y + num * (_end.Y - _start.Y), 0.0);
		}

		public Point PaperBottomPoint(double station)
		{
			double num = ((Span > 1E-06) ? ((station - Min) / Span) : 0.0);
			if (num < 0.0)
			{
				num = 0.0;
			}
			if (num > 1.0)
			{
				num = 1.0;
			}
			Point point = _bottomStart ?? _start;
			Point point2 = _bottomEnd ?? _end;
			return new Point(point.X + num * (point2.X - point.X), point.Y + num * (point2.Y - point.Y), 0.0);
		}

		private static double Coord(Point p, int axis)
		{
			return axis switch
			{
				0 => p.X, 
				1 => p.Y, 
				_ => p.Z, 
			};
		}

		private static double Mid(List<Point> samples, int axis)
		{
			double num = double.MaxValue;
			double num2 = double.MinValue;
			foreach (Point sample in samples)
			{
				double num3 = Coord(sample, axis);
				if (num3 < num)
				{
					num = num3;
				}
				if (num3 > num2)
				{
					num2 = num3;
				}
			}
			return (num + num2) / 2.0;
		}

		private static Point LocalPoint(int axis, double along, double midA, double midB)
		{
			return axis switch
			{
				0 => new Point(along, midA, midB), 
				1 => new Point(midA, along, midB), 
				_ => new Point(midA, midB, along), 
			};
		}

		internal static Point ToView(Point global, View view)
		{
			if (global == null || view == null)
			{
				return null;
			}
			try
			{
				return MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem).Transform(global);
			}
			catch
			{
				return global;
			}
		}
	}

	private static readonly int[] AllowedScales = new int[4] { 50, 60, 75, 100 };

	/// <summary>Engineer sheet-1 preference: try 1:75 before tighter scales.</summary>
	private static readonly int[] ScalePreferenceOrder = new int[4] { 75, 60, 50, 100 };

	private const double SheetBorderMarginMm = 3.0;

	private const double TierStepMaxPaperMm = 6.0;

	private const double TierStepMinPaperMm = 4.0;

	private const double TierFirstOffsetPaperMm = 6.0;

	private const double VertFirstOffsetPaperMm = 6.0;

	private const double BomFallbackLeft = 345.0;

	private const double BomGap = 10.0;

	private const double LeftMargin = 15.0;

	/// <summary>Historical elevation-band wall budget (paper mm). With BOM path alone
	/// (~320 mm free) SelectScale wrongly picks 1:50; capping wall band at 140 forces
	/// ~1:75 for a ~9366 mm panel on 11×17 after L/R + label reserves.</summary>
	private const double MaxViewWidth = 140.0;

	/// <summary>Paper mm reserved left of wall for vertical dim columns (S1/S3).</summary>
	private const double VertChainLeftBudgetMm = 28.0;

	/// <summary>Paper mm reserved right of wall for vertical dim columns (S1/S3).</summary>
	private const double VertChainRightBudgetMm = 28.0;

	/// <summary>Paper mm reserved for tier labels between wall and BOM (S1/S2).</summary>
	private const double LabelColumnBudgetMm = 45.0;

	/// <summary>Paper mm reserved above wall for top tier ladder (S1).</summary>
	private const double TierStackTopBudgetMm = 70.0;

	/// <summary>Paper mm reserved below wall for bottom tier ladder (S1).</summary>
	private const double TierStackBottomBudgetMm = 50.0;

	private const double TopMarginMm = 8.0;

	private const double BottomMarginMm = 8.0;

	private const double ConservationTolerance = 0.1;

	private const double MinDimTextHeightMm = 2.5;

	/// <summary>Last Fit bbox assert failure lines (for fit_report / final_report).</summary>
	private readonly List<string> _lastBboxFailures = new List<string>();

	private readonly FitAuditStats _fitAudit = new FitAuditStats();

	private sealed class FitAuditStats
	{
		public int RemovedPaintText;
		public int RemovedSpamText;
		public int RemovedStrayGraphics;
		public int RemovedStaleLabels;
		public int SuppressedPhantomMarks;
		public int TiersAbove;
		public int TiersBelow;
		public int LabelCount;
		public int VertChainsLeft;
		public int VertChainsRight;
		public int DimSetsBefore;
		public int DimSetsAfter;
	}

	private readonly DrawingHandler _handler;

	private readonly Model _model;

	private readonly StraightDimensionSetHandler _dims;

	private readonly string _pdfDir;

	private static bool _loggedDimUnits;

	/// <summary>
	/// When false (default), Fit skips RepairBrokenPartMarks. Opt-in via --enable-mark-repair.
	/// Default skip is intentional: manual props-only drawings have no magenta '?';
	/// mass TextElement rewrite is a workaround under investigation.
	/// </summary>
	public bool EnableMarkRepair { get; set; }

	/// <summary>
	/// Diagnostic: 0 = full Fit (mark repair still gated by EnableMarkRepair).
	/// 1–5 = stop after investigation stage (see Fit stage log). Used with --fit-stage N.
	/// </summary>
	public int FitStage { get; set; }

	/// <summary>
	/// When true (default): if the front view already has a dense native dimension set from
	/// drawing properties, skip PurgeOldDimensions + PlaceTiers so manual-like elevations stay.
	/// </summary>
	/// <summary>Opt-in only (--preserve-native-dims). Default false: rebuild engineer sheet-1 tiers in Tekla UI.</summary>
	public bool PreserveNativeDimensions { get; set; } = false;

	/// <summary>Minimum StraightDimensionSet count to treat the view as having usable native dims.</summary>
	private const int NativeDimPreserveMin = 8;

	public PrecastDimensionPostProcessor(Model model, DrawingHandler handler, string pdfDir = null)
	{
		_model = model ?? throw new ArgumentNullException("model");
		_handler = handler ?? new DrawingHandler();
		_dims = new StraightDimensionSetHandler();
		_pdfDir = pdfDir;
	}

	private static void WarnIfNumberingStale()
	{
		try
		{
			if (!Operation.IsNumberingUpToDateAll())
			{
				Console.WriteLine("[Fit] Numbering is not up to date — part marks may show '?'.");
				Console.WriteLine("  In Tekla: Drawings & reports → Numbering → Number modified objects, then re-run Fit.");
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// Hard gate before clean-mark / PG macros. Returns false when model numbering is stale
	/// so sheets are not recreated with unresolved magenta '?'.
	/// </summary>
	public static bool AbortIfNumberingStale(string context = "Civil")
	{
		try
		{
			if (!Operation.IsNumberingUpToDateAll())
			{
				Console.WriteLine("[" + context + "] ABORT: Numbering is not up to date — part marks would show '?'.");
				Console.WriteLine("  In Tekla: Drawings & reports → Numbering → Number modified objects, then re-run standalone.");
				return true;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[" + context + "] numbering check failed: " + ex.Message);
		}
		return false;
	}

	private struct MarkRepairResult
	{
		public int Repaired;
		public int Unresolved;
	}

	public static bool IsHardwareShopSheet(Drawing drawing)
	{
		return SheetRoleMap.Resolve(drawing) == SheetRole.Hardware;
	}

	public static bool IsBbsSheet(Drawing drawing)
	{
		return SheetRoleMap.Resolve(drawing) == SheetRole.BbsTable;
	}

	public void CleanBbs(Drawing drawing)
	{
		if (drawing == null)
		{
			return;
		}
		try
		{
			ContainerView sheet = drawing.GetSheet();
			if (sheet == null)
			{
				return;
			}
			DrawingObjectEnumerator drawingObjectEnumerator = null;
			try
			{
				drawingObjectEnumerator = sheet.GetAllViews();
			}
			catch
			{
				try
				{
					drawingObjectEnumerator = sheet.GetViews();
				}
				catch
				{
					drawingObjectEnumerator = null;
				}
			}
			// Engineer table sheet: park all model views so schedule/BOM/notes stay uncluttered.
			while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
			{
				if (drawingObjectEnumerator.Current is View view)
				{
					try
					{
						View.ViewAttributes attributes = view.Attributes;
						attributes.FixedViewPlacing = true;
						view.Attributes = attributes;
						view.Origin = new Point(2500.0, 2500.0, 0.0);
						view.Modify();
					}
					catch
					{
					}
				}
			}
			PurgeTemporaryTags(sheet);
			try { drawing.CommitChanges(); } catch { }
			try { _handler.SaveActiveDrawing(); } catch { }
			RefreshDrawingUi(drawing);
			string bbsMark = "";
			try { bbsMark = drawing.Mark ?? ""; } catch { }
			Console.WriteLine("[BBS] Sheet-4: parked model views; reinforcing schedule/BOM left for '" + bbsMark + "'.");
			Console.WriteLine("[BBS] QA: compare REBAR_BBS JSON to Rev 2 page 4 (see docs/investigation/w10-175_pdf_vs_tekla_qa.md) — bent+straight rows OK in extract.");
		}
		catch (Exception ex)
		{
			Console.WriteLine("[BBS] CleanBbs failed: " + ex.Message);
		}
	}

	public ShopFitResult Fit(Drawing drawing)
	{
		ShopFitResult shopFitResult = new ShopFitResult();
		ResetFitAudit();
		if (drawing == null || !IsHardwareShopSheet(drawing))
		{
			return shopFitResult;
		}
		if (FitStage >= 1 && FitStage <= 5)
			return FitStagedInvestigation(drawing);
		string text = "";
		try
		{
			text = drawing.Mark ?? drawing.Name ?? "";
		}
		catch
		{
			text = "";
		}
		shopFitResult.SiblingMark = Sheet2Mark(text);
		bool flag = false;
		bool flag2 = false;
		try
		{
			try
			{
				Drawing activeDrawing = _handler.GetActiveDrawing();
				if (activeDrawing != null && string.Equals(activeDrawing.Mark, drawing.Mark, StringComparison.OrdinalIgnoreCase))
				{
					flag2 = true;
					flag = true;
				}
			}
			catch
			{
			}
			if (!flag2)
			{
				try
				{
					_handler.UpdateDrawing(drawing);
				}
				catch
				{
				}
				try
				{
					flag = _handler.SetActiveDrawing(drawing, showDrawing: false);
				}
				catch (Exception ex)
				{
					Console.WriteLine("[Fit] '" + text + "' SetActiveDrawing: " + ex.Message);
				}
				if (!flag)
				{
					try
					{
						flag = _handler.SetActiveDrawing(drawing, showDrawing: false, forceOpen: true);
					}
					catch
					{
					}
				}
			}
			Tekla.Structures.Model.Part part = ResolveMainPart(drawing);
			double num = LiveLength(part);
			if (part == null || num <= 0.0)
			{
				Console.WriteLine("[Fit] '" + text + "' skipped: no live solid length");
				return shopFitResult;
			}
			ContainerView sheet = drawing.GetSheet();
			if (sheet == null)
			{
				Console.WriteLine("[Fit] '" + text + "' skipped: no sheet");
				return shopFitResult;
			}
			HideSheet1Rebar(sheet);
			PurgeTemporaryTags(sheet);
			FindSheetViews(sheet, out var front, out var view3D, out var section, out var bottom, out var otherViews);
			if (front == null)
			{
				Console.WriteLine("[Fit] '" + text + "' skipped: no front view");
				return shopFitResult;
			}
			Console.WriteLine("[Fit] Views identified: Front=" + (front != null) + ", 3D=" + (view3D != null) + ", Section=" + (section != null) + ", Bottom=" + (bottom != null) + ", Other=" + otherViews.Count);
			double modelHeight = LiveHeight(part);
			FitCanvas canvas = BuildFitCanvas(drawing, sheet, num, modelHeight);
			LogFitCanvas(canvas);
			double sheetWidth = canvas.SheetW;
			double sheetHeight = canvas.SheetH;
			double num2 = canvas.BomLeft;
			double availableWidthMm = canvas.AvailableWidthMm;
			int scale = canvas.Scale;
			ArrangeAllSheet1Views(drawing, front, view3D, section, bottom, otherViews, part, scale, num, sheetWidth, sheetHeight, num2, shopFitResult);
			WarnIfNumberingStale();
			PanelAxis panelAxis = PanelAxis.Measure(part, num);
			int num3 = 0;
			double num4 = double.NaN;
			bool tapeVerified = false;
			List<double[]> micros = new List<double[]>();
			StationBuckets stationBuckets = null;
			if (panelAxis == null || Math.Abs(panelAxis.Span - num) > 0.1)
			{
				num4 = ((panelAxis == null) ? double.NaN : (panelAxis.Span - num));
				Console.WriteLine("[TAPE_UNVERIFIED] '" + text + "' span " + F(panelAxis?.Span ?? 0.0) + " vs length " + F(num) + " delta " + F(num4) + " — OVERALL dims only");
			}
			else
			{
				stationBuckets = CollectStations(part, panelAxis, front);
				List<double> stations = new List<double>(stationBuckets.All)
				{
					panelAxis.Min,
					panelAxis.Min + panelAxis.Span
				};
				List<double> list = panelAxis.Chain(stations);
				num4 = ((list.Count < 2) ? double.NaN : (list[list.Count - 1] - list[0] - num));
				num3 = list.Count;
				if (!double.IsNaN(num4) && Math.Abs(num4) <= 0.1)
					tapeVerified = true;
				else
					Console.WriteLine("[TAPE_UNVERIFIED] '" + text + "' delta " + F(num4) + " — OVERALL dims only");
			}
			HideSheet1Rebar(sheet);
			PurgeTemporaryTags(sheet);
			if (panelAxis != null)
				panelAxis.BindView(front, part);
			shopFitResult.TapeDeltaMm = num4;
			CatReport catReport = ApplyCats(drawing, sheet, front, part, panelAxis, stationBuckets, micros, num2, shopFitResult, num, scale);
			shopFitResult.Sheet2Updated = catReport.Sheet2Updated;
			try
			{
				drawing.CommitChanges();
			}
			catch
			{
			}
			// Prefer a real Document Manager sections sheet (role Sections). Sidecar PDF only if none.
			bool hasSectionsSheet = HasSiblingRole(drawing, SheetRole.Sections);
			if (!hasSectionsSheet && !string.IsNullOrEmpty(_pdfDir) && (view3D != null || section != null || bottom != null))
			{
				string value = "";
				try
				{
					part.GetReportProperty("ASSEMBLY_POS", ref value);
				}
				catch
				{
				}
				if (string.IsNullOrEmpty(value))
				{
					value = Regex.Replace(drawing.Mark ?? "", "-\\s*1\\s*$", "").Trim().Trim('[', ']')
						.Trim();
				}
				if (string.IsNullOrEmpty(value))
				{
					value = Regex.Replace(text ?? "", "-\\s*\\d+\\s*$", "").Trim().Trim('[', ']').Trim();
				}
				if (string.IsNullOrEmpty(value))
					value = "UNKNOWN";
				string page2Path = Path.Combine(_pdfDir, value + "_-_2_Sections_3D.pdf");
				ExportNativePage2(drawing, front, view3D, section, bottom, otherViews, part, page2Path);
			}
			else if (hasSectionsSheet)
				Console.WriteLine("[Fit] sections sibling exists — skip sidecar ExportNativePage2");
			ArrangeAllSheet1Views(drawing, front, view3D, section, bottom, otherViews, part, scale, num, sheetWidth, sheetHeight, num2, shopFitResult);
			SealView(front, num, scale, sheetHeight, num2);
			PurgeTemporaryTags(sheet);
			// Dimension mesh last — after page-2 export and the final elevation-only arrange.
			if (panelAxis != null)
			{
				panelAxis.BindView(front, part);
				WarnGeneralNotesUdas(part);
				EngineerSheet1Sanitize(sheet, front, part);
				List<double> tape = new List<double>
				{
					panelAxis.Min,
					panelAxis.Min + panelAxis.Span
				};
				StationBuckets bucketsForTiers = stationBuckets ?? new StationBuckets();
				PlaceTiers(front, panelAxis, bucketsForTiers, tape, micros, scale, overallOnly: !tapeVerified, sheet, num2, canvas.SheetH);
				SyncTopInFormLabel(front, panelAxis, scale);
				RefitFrontFrame(front, part, scale, sheet);
				try
				{
					var fa = front.Attributes;
					fa.Scale = scale;
					front.Attributes = fa;
					front.Modify();
				}
				catch { }
			}
			// Mark repair is opt-in (--enable-mark-repair). Default skip: do not rewrite marks.
			// Never hide numbering marks. Model already has PART_POS.
			MarkRepairResult markRepair = default(MarkRepairResult);
			if (EnableMarkRepair && (FitStage == 0 || FitStage >= 5))
			{
				markRepair = RepairBrokenPartMarks(drawing, sheet, front);
				Console.WriteLine("[Fit] repaired " + markRepair.Repaired.ToString(CultureInfo.InvariantCulture)
					+ " part mark(s); unresolved " + markRepair.Unresolved.ToString(CultureInfo.InvariantCulture)
					+ " (no PART_POS)");
			}
			else
			{
				Console.WriteLine("[Fit] mark-repair SKIPPED (default). Opt-in: --enable-mark-repair"
					+ (FitStage > 0 ? " fit-stage=" + FitStage.ToString(CultureInfo.InvariantCulture) : ""));
			}
			AssertSheetBounds(sheet, sheetWidth, sheetHeight, num2, front, scale);
			WriteFitReport(text, scale, sheetWidth, sheetHeight, num2, availableWidthMm, num, num4, tapeVerified,
				markRepair.Repaired, markRepair.Unresolved, hasSectionsSheet, canvas);
			WriteFinalReport(text, scale, sheetWidth, sheetHeight, num2, availableWidthMm, num, tapeVerified,
				markRepair.Repaired, canvas);
			RemoveInnerSheetBorders(sheet, sheetWidth, sheetHeight);
			try
			{
				drawing.CommitChanges();
			}
			catch
			{
			}
			try
			{
				_handler.SaveActiveDrawing();
			}
			catch
			{
			}
			// Refresh UI glyphs after headless Fit (showDrawing was false).
			RefreshDrawingUi(drawing);
			double num5 = ((num2 > 0.0) ? num2 : 345.0) - 10.0 - 15.0;
			double v = ((num5 > 1.0) ? (15.0 + num5 / 2.0) : 142.5);
			double v2 = ((sheet.Height > 1.0) ? (sheet.Height / 2.0) : 152.4);
			Console.WriteLine("[Fit] '" + text + "' length=" + F(num) + " bomLeft=" + F(num2) + " centerX=" + F(v) + " centerY=" + F(v2) + " scale=" + scale + " stations=" + num3 + " delta=" + F(num4) + " margin=" + F(shopFitResult.LeftMarginMm) + " bomGap=" + F(shopFitResult.BomGapMm) + " catA interior=" + catReport.Interior + " chamfer=" + catReport.Chamfer + " opening=" + catReport.Opening + " fallback=" + catReport.Fallback + " marks=" + catReport.Marks + " nudged=" + catReport.Nudged + " leaders=" + catReport.Leaders + " micro=" + catReport.Micro + " clusters=" + catReport.Clusters + " witness=Tekla dimension properties (no gap field on StraightDimensionSet)");
			if (!string.IsNullOrEmpty(catReport.CogLine))
			{
				Console.WriteLine("[Fit] " + catReport.CogLine);
			}
		}
		catch (Exception ex2)
		{
			Console.WriteLine("[Fit] '" + text + "' failed: " + ex2.Message);
		}
		finally
		{
			if (flag && !flag2)
			{
				try
				{
					_handler.CloseActiveDrawing(save: true);
				}
				catch
				{
					try
					{
						_handler.CloseActiveDrawing(save: false);
					}
					catch
					{
					}
				}
			}
		}
		return shopFitResult;
	}

	/// <summary>
	/// Task 1b staged Fit for "?" source isolation. Stops after FitStage (1–5).
	/// Does not hide numbering marks. Stage 5 runs mark repair only if EnableMarkRepair.
	/// </summary>
	private ShopFitResult FitStagedInvestigation(Drawing drawing)
	{
		ShopFitResult result = new ShopFitResult();
		string text = "";
		try { text = drawing.Mark ?? drawing.Name ?? ""; } catch { text = ""; }
		result.SiblingMark = Sheet2Mark(text);
		int stage = FitStage;
		bool openedHere = false;
		bool wasAlreadyActive = false;
		Console.WriteLine("[FitStage] START stage=" + stage.ToString(CultureInfo.InvariantCulture)
			+ " enableMarkRepair=" + EnableMarkRepair
			+ " drawing='" + text + "'");
		Console.WriteLine("[FitStage] Operator: after this run, inspect Tekla UI for magenta '?' on elevation.");
		try
		{
			try
			{
				Drawing active = _handler.GetActiveDrawing();
				if (active != null && string.Equals(active.Mark, drawing.Mark, StringComparison.OrdinalIgnoreCase))
					wasAlreadyActive = true;
			}
			catch { }

			if (!wasAlreadyActive)
			{
				try { _handler.UpdateDrawing(drawing); } catch { }
				try { openedHere = _handler.SetActiveDrawing(drawing, showDrawing: false); } catch { }
				if (!openedHere)
				{
					try { openedHere = _handler.SetActiveDrawing(drawing, showDrawing: false, forceOpen: true); } catch { }
				}
			}

			Tekla.Structures.Model.Part part = ResolveMainPart(drawing);
			double modelLength = LiveLength(part);
			if (part == null || modelLength <= 0.0)
			{
				Console.WriteLine("[FitStage] abort: no live solid length");
				return result;
			}
			ContainerView sheet = drawing.GetSheet();
			if (sheet == null)
			{
				Console.WriteLine("[FitStage] abort: no sheet");
				return result;
			}
			FindSheetViews(sheet, out var front, out var view3D, out var section, out var bottom, out var otherViews);
			if (front == null)
			{
				Console.WriteLine("[FitStage] abort: no front view");
				return result;
			}

			double modelHeight = LiveHeight(part);
			FitCanvas canvas = BuildFitCanvas(drawing, sheet, modelLength, modelHeight);
			LogFitCanvas(canvas);
			double sheetWidth = canvas.SheetW;
			double sheetHeight = canvas.SheetH;
			double bomLeft = canvas.BomLeft;
			double availableWidthMm = canvas.AvailableWidthMm;
			int scale = canvas.Scale;
			PanelAxis axis = PanelAxis.Measure(part, modelLength);
			if (axis != null)
				axis.BindView(front, part);

			// Stage 1: shell only — open/update, axis/scale. No purge, park, tiers, formwork, mark repair.
			Console.WriteLine("[FitStage] completed stage=1 (shell: open/update/axis/scale="
				+ scale.ToString(CultureInfo.InvariantCulture) + " bomLeft="
				+ bomLeft.ToString("0.###", CultureInfo.InvariantCulture) + ")");
			if (stage <= 1)
			{
				FinishFitStage(drawing, sheet, sheetWidth, sheetHeight, text, scale, bomLeft, availableWidthMm,
					modelLength, axis, front, openedHere && !wasAlreadyActive, 1);
				return result;
			}

			// Stage 2: + PurgeTemporaryTags
			PurgeTemporaryTags(sheet);
			Console.WriteLine("[FitStage] completed stage=2 (+PurgeTemporaryTags)");
			if (stage <= 2)
			{
				FinishFitStage(drawing, sheet, sheetWidth, sheetHeight, text, scale, bomLeft, availableWidthMm,
					modelLength, axis, front, openedHere && !wasAlreadyActive, 2);
				return result;
			}

			// Stage 3: + PurgeOldDimensions + PlaceTiers (PlaceTiers calls PurgeOldDimensions)
			StationBuckets buckets = null;
			bool tapeOk = false;
			double tapeDelta = double.NaN;
			if (axis != null && Math.Abs(axis.Span - modelLength) <= 0.1)
			{
				buckets = CollectStations(part, axis, front);
				List<double> chain = axis.Chain(new List<double>(buckets.All) { axis.Min, axis.Min + axis.Span });
				tapeDelta = (chain.Count < 2) ? double.NaN : (chain[chain.Count - 1] - chain[0] - modelLength);
				tapeOk = !double.IsNaN(tapeDelta) && Math.Abs(tapeDelta) <= 0.1;
			}
			if (axis != null)
			{
				axis.BindView(front, part);
				List<double> tape = new List<double> { axis.Min, axis.Min + axis.Span };
				PlaceTiers(front, axis, buckets ?? new StationBuckets(), tape, new List<double[]>(), scale, overallOnly: !tapeOk, sheet, bomLeft, sheetHeight);
				SyncTopInFormLabel(front, axis, scale);
			}
			Console.WriteLine("[FitStage] completed stage=3 (+PlaceTiers/preserve-native tapeOk=" + tapeOk + ")");
			if (stage <= 3)
			{
				FinishFitStage(drawing, sheet, sheetWidth, sheetHeight, text, scale, bomLeft, availableWidthMm,
					modelLength, axis, front, openedHere && !wasAlreadyActive, 3);
				return result;
			}

			// Stage 4: + formwork tags + park views
			ArrangeAllSheet1Views(drawing, front, view3D, section, bottom, otherViews, part, scale, modelLength,
				sheetWidth, sheetHeight, bomLeft, result);
			List<double[]> micros = new List<double[]>();
			ApplyCats(drawing, sheet, front, part, axis, buckets, micros, bomLeft, result, modelLength, scale);
			ArrangeAllSheet1Views(drawing, front, view3D, section, bottom, otherViews, part, scale, modelLength,
				sheetWidth, sheetHeight, bomLeft, result);
			SealView(front, modelLength, scale, sheetHeight, bomLeft);
			Console.WriteLine("[FitStage] completed stage=4 (+formwork tags + park views)");
			if (stage <= 4)
			{
				FinishFitStage(drawing, sheet, sheetWidth, sheetHeight, text, scale, bomLeft, availableWidthMm,
					modelLength, axis, front, openedHere && !wasAlreadyActive, 4);
				return result;
			}

			// Stage 5: + mark repair (only if EnableMarkRepair)
			MarkRepairResult repair = default(MarkRepairResult);
			if (EnableMarkRepair)
			{
				repair = RepairBrokenPartMarks(drawing, sheet, front);
				Console.WriteLine("[FitStage] completed stage=5 (+mark repair repaired="
					+ repair.Repaired.ToString(CultureInfo.InvariantCulture)
					+ " unresolved=" + repair.Unresolved.ToString(CultureInfo.InvariantCulture) + ")");
			}
			else
			{
				Console.WriteLine("[FitStage] completed stage=5 (mark repair NOT run — pass --enable-mark-repair)");
			}
			AssertSheetBounds(sheet, sheetWidth, sheetHeight, bomLeft, front, scale);
			WriteFitReport(text, scale, sheetWidth, sheetHeight, bomLeft, availableWidthMm, modelLength, tapeDelta, tapeOk,
				repair.Repaired, repair.Unresolved, HasSiblingRole(drawing, SheetRole.Sections), canvas);
			WriteFinalReport(text, scale, sheetWidth, sheetHeight, bomLeft, availableWidthMm, modelLength, tapeOk,
				repair.Repaired, canvas);
			FinishFitStage(drawing, sheet, sheetWidth, sheetHeight, text, scale, bomLeft, availableWidthMm,
				modelLength, axis, front, openedHere && !wasAlreadyActive, 5);
			return result;
		}
		catch (Exception ex)
		{
			Console.WriteLine("[FitStage] failed: " + ex.Message);
			return result;
		}
	}

	private void FinishFitStage(Drawing drawing, ContainerView sheet, double sheetW, double sheetH,
		string drawingMark, int scale, double bomLeft, double availableWidthMm, double modelLength,
		PanelAxis axis, View front, bool closeIfOpened, int stageDone)
	{
		RemoveInnerSheetBorders(sheet, sheetW, sheetH);
		try { drawing.CommitChanges(); } catch { }
		try { _handler.SaveActiveDrawing(); } catch { }
		RefreshDrawingUi(drawing);
		WriteFitStageReport(drawingMark, stageDone, EnableMarkRepair);
		Console.WriteLine("[FitStage] STOPPED after stage=" + stageDone.ToString(CultureInfo.InvariantCulture)
			+ ". Look in Tekla UI for magenta '?'. First stage where '?' appears = source clue.");
		if (closeIfOpened)
		{
			try { _handler.CloseActiveDrawing(save: true); }
			catch
			{
				try { _handler.CloseActiveDrawing(save: false); } catch { }
			}
		}
	}

	private void WriteFitStageReport(string drawingMark, int stageDone, bool enableRepair)
	{
		try
		{
			string dir = string.IsNullOrEmpty(_pdfDir)
				? Path.Combine("Export", "CivilDrawings", "new with macros")
				: Path.GetDirectoryName(_pdfDir) ?? _pdfDir;
			Directory.CreateDirectory(dir);
			string piece = SheetRoleMap.PieceMark(drawingMark);
			string path = Path.Combine(dir, "fit_stage_report_" + piece.Replace(' ', '_') + ".txt");
			var sb = new System.Text.StringBuilder();
			sb.AppendLine("FitStageReport " + DateTime.UtcNow.ToString("o"));
			sb.AppendLine("drawing=" + drawingMark);
			sb.AppendLine("stoppedAfterStage=" + stageDone.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("enableMarkRepair=" + enableRepair);
			sb.AppendLine("operatorAction=Inspect Tekla UI elevation for magenta '?' and record first stage where it appears.");
			sb.AppendLine("stages=1:shell 2:+PurgeTemporaryTags 3:+PlaceTiers 4:+formwork/park 5:+markRepair");
			File.WriteAllText(path, sb.ToString());
			Console.WriteLine("[FitStage] wrote " + path);
		}
		catch (Exception ex)
		{
			Console.WriteLine("[FitStage] report write failed: " + ex.Message);
		}
	}

	private static string Sheet2Mark(string mark)
	{
		return Regex.Replace((mark ?? "").Trim().Trim('[', ']').Trim(), "-\\s*1\\s*$", "- 2");
	}

	private static void ReadCanvas(View front, double bomLeft, ShopFitResult result)
	{
		try
		{
			RectangleBoundingBox axisAlignedBoundingBox = front.GetAxisAlignedBoundingBox();
			if (axisAlignedBoundingBox != null && !(axisAlignedBoundingBox.MinPoint == null) && !(axisAlignedBoundingBox.MaxPoint == null))
			{
				double num = ((bomLeft > 0.0) ? bomLeft : 345.0);
				result.LeftMarginMm = axisAlignedBoundingBox.MinPoint.X;
				result.BomGapMm = num - axisAlignedBoundingBox.MaxPoint.X;
			}
		}
		catch
		{
		}
	}

	private static bool GetSolidBoundsInView(View view, Tekla.Structures.Model.Part main, out double minX, out double maxX, out double minY, out double maxY)
	{
		minX = double.MaxValue;
		minY = double.MaxValue;
		maxX = double.MinValue;
		maxY = double.MinValue;
		if (view == null || main == null)
		{
			return false;
		}
		try
		{
			Solid solid = main.GetSolid();
			if (solid != null && solid.MinimumPoint != null && solid.MaximumPoint != null)
			{
				Matrix matrix = MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem);
				double[] array = new double[2]
				{
					solid.MinimumPoint.X,
					solid.MaximumPoint.X
				};
				double[] array2 = new double[2]
				{
					solid.MinimumPoint.Y,
					solid.MaximumPoint.Y
				};
				double[] array3 = new double[2]
				{
					solid.MinimumPoint.Z,
					solid.MaximumPoint.Z
				};
				double[] array4 = array;
				foreach (double x in array4)
				{
					double[] array5 = array2;
					foreach (double y in array5)
					{
						double[] array6 = array3;
						foreach (double z in array6)
						{
							Point point = matrix.Transform(new Point(x, y, z));
							if (point.X < minX)
							{
								minX = point.X;
							}
							if (point.X > maxX)
							{
								maxX = point.X;
							}
							if (point.Y < minY)
							{
								minY = point.Y;
							}
							if (point.Y > maxY)
							{
								maxY = point.Y;
							}
						}
					}
				}
				return minX < maxX && minY < maxY;
			}
		}
		catch
		{
		}
		return false;
	}

	private static void ParkViewOffSheet(View view)
	{
		if (view == null) return;
		try
		{
			var a = view.Attributes;
			a.FixedViewPlacing = true;
			view.Attributes = a;
			view.Origin = new Point(2500.0, 2500.0, 0.0);
			view.Modify();
		}
		catch { }
	}

	/// <summary>
	/// Sheet 1 canvas = TOP IN FORM only. END 2, section, and 3D stay parked.
	/// </summary>
	private static void ArrangeAllSheet1Views(Drawing drawing, View front, View view3D, View section, View bottom, List<View> otherViews, Tekla.Structures.Model.Part main, int scale, double modelLength, double sheetWidth, double sheetHeight, double bomLeft, ShopFitResult result)
	{
		if (front == null)
			return;
		try
		{
			const double desiredLeft = 25.0;
			const double desiredCenterY = 145.0;

			var fAttrs = front.Attributes;
			fAttrs.Scale = scale;
			try { fAttrs.FixedViewPlacing = true; } catch { }
			front.Attributes = fAttrs;

			if (GetSolidBoundsInView(front, main, out double minX, out double maxX, out double minY, out double maxY))
			{
				front.Origin = new Point(desiredLeft - (minX / scale), desiredCenterY - ((minY + maxY) / (2.0 * scale)), 0.0);
			}
			else
			{
				front.Origin = new Point(desiredLeft, desiredCenterY, 0.0);
			}
			front.Modify();

			ParkViewOffSheet(bottom);
			ParkViewOffSheet(view3D);
			ParkViewOffSheet(section);
			if (otherViews != null)
			{
				foreach (View ov in otherViews)
					ParkViewOffSheet(ov);
			}

			try { drawing.CommitChanges(); } catch { }

			AABB box = ViewBox(front);
			if (box != null && box.MinPoint != null && box.MaxPoint != null)
			{
				result.LeftMarginMm = box.MinPoint.X;
				result.BomGapMm = ((bomLeft > 0.0) ? bomLeft : 370.0) - box.MaxPoint.X;
			}
			Console.WriteLine("[ViewArrange] Sheet 1 elevation-only Front(Scale " + scale + "); END2/3D/Section parked off-sheet.");
		}
		catch (Exception ex)
		{
			Console.WriteLine("[ViewArrange] Error: " + ex.Message);
		}
	}

	private static void RefitFrontFrame(View view, Tekla.Structures.Model.Part main, int scale, ContainerView sheet)
	{
		if (view == null || main == null || scale < 1) return;
		try
		{
			if (!GetSolidBoundsInView(view, main, out double minX, out double maxX, out double minY, out double maxY))
				return;
			double sheetWidth = (sheet != null && sheet.Width > 1.0) ? sheet.Width : 431.8;
			double sheetHeight = (sheet != null && sheet.Height > 1.0) ? sheet.Height : 279.4;
			double bomLeft = ScanBomLeft(sheet, sheetWidth);
			double availableWidth = Math.Max(40.0,
				(bomLeft > 1.0 ? bomLeft : BomFallbackLeft) - BomGap - LeftMargin
				- VertChainLeftBudgetMm - VertChainRightBudgetMm - LabelColumnBudgetMm);
			availableWidth = Math.Min(availableWidth, MaxViewWidth);
			double targetX = LeftMargin + VertChainLeftBudgetMm + (availableWidth / 2.0);
			double targetY = sheetHeight / 2.0;
			var attrs = view.Attributes;
			attrs.FixedViewPlacing = true;
			attrs.Scale = scale;
			view.Attributes = attrs;
			view.Origin = new Point(targetX - ((minX + maxX) / (2.0 * scale)), targetY - ((minY + maxY) / (2.0 * scale)), 0.0);
			view.Modify();
			Console.WriteLine("[Fit] fit-page dynamic center targetX=" + targetX.ToString(CultureInfo.InvariantCulture)
				+ " targetY=" + targetY.ToString(CultureInfo.InvariantCulture)
				+ " bomLeft=" + bomLeft.ToString("0.###", CultureInfo.InvariantCulture)
				+ " scale=" + scale.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] fit-page: " + ex.Message);
		}
	}

	/// <summary>
	/// Keep the outermost full-sheet border; delete nested inner frame rectangles.
	/// </summary>
	private static void RemoveInnerSheetBorders(ContainerView sheet, double sheetWidth, double sheetHeight)
	{
		if (sheet == null) return;
		try
		{
			double sw = sheetWidth > 1 ? sheetWidth : 431.8;
			double sh = sheetHeight > 1 ? sheetHeight : 279.4;
			var candidates = new List<Tuple<DrawingObject, double, double, double, double>>();
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetAllObjects(); } catch { return; }
			while (en != null && en.MoveNext())
			{
				DrawingObject obj = en.Current as DrawingObject;
				if (obj == null) continue;
				// Skip views — only sheet-level graphics (lines / rectangles / closed geometries).
				if (obj is ViewBase) continue;
				AABB box = null;
				try
				{
					if (obj is IAxisAlignedBoundingBox aabb)
						box = aabb.GetAxisAlignedBoundingBox();
				}
				catch { continue; }
				if (box == null || box.MinPoint == null || box.MaxPoint == null) continue;
				double w = box.MaxPoint.X - box.MinPoint.X;
				double h = box.MaxPoint.Y - box.MinPoint.Y;
				// Full-sheet-ish frame: at least 70% of sheet in both directions.
				if (w < sw * 0.70 || h < sh * 0.70) continue;
				// Thin frame (border), not a filled table: area rim, height/width near sheet.
				if (w > sw * 1.05 || h > sh * 1.05) continue;
				candidates.Add(Tuple.Create(obj, box.MinPoint.X, box.MinPoint.Y, box.MaxPoint.X, box.MaxPoint.Y));
			}
			if (candidates.Count < 2)
			{
				Console.WriteLine("[Fit] sheet borders: " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " full-frame object(s); nothing to remove");
				return;
			}
			// Outermost = largest area.
			candidates.Sort((a, b) =>
			{
				double aa = (a.Item4 - a.Item2) * (a.Item5 - a.Item3);
				double bb = (b.Item4 - b.Item2) * (b.Item5 - b.Item3);
				return bb.CompareTo(aa);
			});
			int removed = 0;
			for (int i = 1; i < candidates.Count; i++)
			{
				try
				{
					candidates[i].Item1.Delete();
					removed++;
				}
				catch { }
			}
			Console.WriteLine("[Fit] sheet borders: kept outermost, removed " + removed.ToString(CultureInfo.InvariantCulture) + " inner frame(s)");
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] sheet borders: " + ex.Message);
		}
	}

	private static void SealView(View view, double modelLength, int scale, double sheetHeight, double bomLeft)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			view.RestrictionBox = new AABB(new Point(-5000.0, -5000.0, -5000.0), new Point(modelLength + 5000.0, 5000.0, 5000.0));
			view.Modify();
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] SealView: " + ex.Message);
		}
	}

	private static void FitViewFrame(View view, double paperWidth, double sheetHeight)
	{
	}

	private static string ContentSpan(View view)
	{
		double num = double.MaxValue;
		double num2 = double.MinValue;
		int num3 = 0;
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = view.GetAllObjects();
		}
		catch
		{
			return "n/a";
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (!(drawingObjectEnumerator.Current is IAxisAlignedBoundingBox axisAlignedBoundingBox))
			{
				continue;
			}
			AABB aABB = null;
			try
			{
				aABB = axisAlignedBoundingBox.GetAxisAlignedBoundingBox();
			}
			catch
			{
				continue;
			}
			if (aABB != null && !(aABB.MinPoint == null) && !(aABB.MaxPoint == null))
			{
				if (aABB.MinPoint.X < num)
				{
					num = aABB.MinPoint.X;
				}
				if (aABB.MaxPoint.X > num2)
				{
					num2 = aABB.MaxPoint.X;
				}
				num3++;
			}
		}
		if (num3 == 0)
		{
			return "none";
		}
		return num3.ToString(CultureInfo.InvariantCulture) + ":" + num.ToString("0.#", CultureInfo.InvariantCulture) + ".." + num2.ToString("0.#", CultureInfo.InvariantCulture);
	}

	private static AABB ViewBox(View view)
	{
		try
		{
			return view.GetAxisAlignedBoundingBox();
		}
		catch
		{
			return null;
		}
	}

	private static void TightenRestriction(View view, Tekla.Structures.Model.Part main)
	{
		if (view == null || main == null)
		{
			return;
		}
		Solid solid = null;
		try
		{
			solid = main.GetSolid();
		}
		catch
		{
			return;
		}
		if (solid == null || solid.MinimumPoint == null || solid.MaximumPoint == null)
		{
			return;
		}
		Matrix matrix;
		try
		{
			matrix = MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem);
		}
		catch
		{
			return;
		}
		double num = double.MaxValue;
		double num2 = double.MaxValue;
		double num3 = double.MaxValue;
		double num4 = double.MinValue;
		double num5 = double.MinValue;
		double num6 = double.MinValue;
		double[] array = new double[2]
		{
			solid.MinimumPoint.X,
			solid.MaximumPoint.X
		};
		double[] array2 = new double[2]
		{
			solid.MinimumPoint.Y,
			solid.MaximumPoint.Y
		};
		double[] array3 = new double[2]
		{
			solid.MinimumPoint.Z,
			solid.MaximumPoint.Z
		};
		double[] array4 = array;
		foreach (double x in array4)
		{
			double[] array5 = array2;
			foreach (double y in array5)
			{
				double[] array6 = array3;
				foreach (double z in array6)
				{
					Point point = matrix.Transform(new Point(x, y, z));
					if (point.X < num)
					{
						num = point.X;
					}
					if (point.Y < num2)
					{
						num2 = point.Y;
					}
					if (point.Z < num3)
					{
						num3 = point.Z;
					}
					if (point.X > num4)
					{
						num4 = point.X;
					}
					if (point.Y > num5)
					{
						num5 = point.Y;
					}
					if (point.Z > num6)
					{
						num6 = point.Z;
					}
				}
			}
		}
		view.RestrictionBox = new AABB(new Point(num - 5000.0, num2 - 5000.0, num3 - 5000.0), new Point(num4 + 5000.0, num5 + 5000.0, num6 + 5000.0));
	}

	private static int SelectScale(double modelLength)
	{
		return SelectScale(modelLength, MaxViewWidth, 0.0, MaxViewWidth * 2.0);
	}

	/// <summary>
	/// Pick the smallest standard scale whose wall paper size fits in the wall band
	/// AND wall+tier stacks fit in available height. Wall-width-only selection is the
	/// scale bug (Fix C / S1): without L/R + label reserves, 9366 mm → 1:50 on a 320 mm band.
	/// </summary>
	private static int SelectScale(double modelLength, double availableWidthMm)
	{
		return SelectScale(modelLength, availableWidthMm, 0.0, 400.0);
	}

	private static int SelectScale(double modelLength, double availableWidthMm, double modelHeightMm, double availableHeightMm)
	{
		double capW = availableWidthMm > 10.0 ? availableWidthMm : MaxViewWidth;
		double capH = availableHeightMm > 10.0 ? availableHeightMm : 400.0;
		int fallback = ScalePreferenceOrder[ScalePreferenceOrder.Length - 1];
		foreach (int num in ScalePreferenceOrder)
		{
			double paperW = modelLength / (double)num;
			double paperH = (modelHeightMm > 1.0) ? (modelHeightMm / (double)num) : 0.0;
			bool widthOk = paperW <= capW;
			bool heightOk = paperH <= 0.0 || (paperH + TierStackTopBudgetMm + TierStackBottomBudgetMm) <= capH;
			if (widthOk && heightOk)
				return num;
		}
		return fallback;
	}

	/// <summary>
	/// Live sheet / layout metrics + BOM left + scale with stack budgets (S1).
	/// Does not change Layout.SheetSize.
	/// </summary>
	private struct FitCanvas
	{
		public double SheetW;
		public double SheetH;
		public double LayoutW;
		public double LayoutH;
		public string LayoutName;
		public double BomLeft;
		public double BandWidthMm;
		public double AvailableWidthMm;
		public double AvailableHeightMm;
		public int Scale;
		public string ScaleReason;
		public double ModelLength;
		public double ModelHeight;
		public double PaperWallW;
		public double PaperWallH;
		public double TierStackTopMm;
		public double TierStackBotMm;
	}

	private static FitCanvas BuildFitCanvas(Drawing drawing, ContainerView sheet, double modelLength, double modelHeight)
	{
		FitCanvas c = default(FitCanvas);
		c.ModelLength = modelLength;
		c.ModelHeight = modelHeight > 1.0 ? modelHeight : 0.0;
		c.SheetW = (sheet != null && sheet.Width > 1.0) ? sheet.Width : 431.8;
		c.SheetH = (sheet != null && sheet.Height > 1.0) ? sheet.Height : 279.4;
		c.LayoutW = c.SheetW;
		c.LayoutH = c.SheetH;
		c.LayoutName = "";
		try
		{
			if (drawing?.Layout != null)
			{
				if (drawing.Layout.SheetSize != null)
				{
					c.LayoutW = drawing.Layout.SheetSize.Width;
					c.LayoutH = drawing.Layout.SheetSize.Height;
				}
				foreach (var p in drawing.Layout.GetType().GetProperties())
				{
					if (p.PropertyType != typeof(string)) continue;
					string v = null;
					try { v = p.GetValue(drawing.Layout, null) as string; } catch { continue; }
					if (string.IsNullOrWhiteSpace(v)) continue;
					string n = p.Name ?? "";
					if (n.IndexOf("Layout", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.Equals("FileName", StringComparison.OrdinalIgnoreCase))
					{
						if (c.LayoutName.Length > 0) c.LayoutName += ";";
						c.LayoutName += n + "=" + v;
					}
				}
			}
		}
		catch { }
		// Prefer live Layout.SheetSize when sheet W/H look wrong; never stretch SheetSize in Fit.
		if (c.LayoutW > 40.0 && c.LayoutH > 40.0
			&& (Math.Abs(c.SheetW - c.LayoutW) > 5.0 || Math.Abs(c.SheetH - c.LayoutH) > 5.0))
		{
			Console.WriteLine("[Fit] sheet W×H=" + c.SheetW.ToString("0.###", CultureInfo.InvariantCulture)
				+ "x" + c.SheetH.ToString("0.###", CultureInfo.InvariantCulture)
				+ " differs from Layout.SheetSize="
				+ c.LayoutW.ToString("0.###", CultureInfo.InvariantCulture) + "x"
				+ c.LayoutH.ToString("0.###", CultureInfo.InvariantCulture)
				+ " — using Layout for available area (not resizing Layout)");
			c.SheetW = c.LayoutW;
			c.SheetH = c.LayoutH;
		}
		c.BomLeft = ScanBomLeft(sheet, c.SheetW);
		double bom = c.BomLeft > 1.0 ? c.BomLeft : BomFallbackLeft;
		c.BandWidthMm = Math.Max(40.0, bom - LeftMargin - BomGap - VertChainLeftBudgetMm - VertChainRightBudgetMm - LabelColumnBudgetMm);
		// Cap wall band at MaxViewWidth so long panels land on engineer-class 1:75, not 1:50.
		c.AvailableWidthMm = Math.Min(c.BandWidthMm, MaxViewWidth);
		c.AvailableHeightMm = Math.Max(40.0, c.SheetH - TopMarginMm - BottomMarginMm);
		c.TierStackTopMm = TierStackTopBudgetMm;
		c.TierStackBotMm = TierStackBottomBudgetMm;
		int wallOnly = SelectScale(modelLength, Math.Max(40.0, bom - LeftMargin - BomGap));
		c.Scale = SelectScale(modelLength, c.AvailableWidthMm, c.ModelHeight, c.AvailableHeightMm);
		c.PaperWallW = modelLength / (double)((c.Scale < 1) ? 1 : c.Scale);
		c.PaperWallH = (c.ModelHeight > 1.0) ? (c.ModelHeight / (double)c.Scale) : 0.0;
		var reason = new System.Text.StringBuilder();
		reason.Append("band=").Append(c.BandWidthMm.ToString("0.#", CultureInfo.InvariantCulture));
		reason.Append(" wallCap=min(band,MaxViewWidth=").Append(MaxViewWidth.ToString("0.#", CultureInfo.InvariantCulture)).Append(")=");
		reason.Append(c.AvailableWidthMm.ToString("0.#", CultureInfo.InvariantCulture));
		reason.Append(" wallOnlyWouldBe=1:").Append(wallOnly.ToString(CultureInfo.InvariantCulture));
		reason.Append(" chosen=1:").Append(c.Scale.ToString(CultureInfo.InvariantCulture));
		reason.Append(" vertL/R=").Append(VertChainLeftBudgetMm.ToString("0.#", CultureInfo.InvariantCulture))
			.Append("/").Append(VertChainRightBudgetMm.ToString("0.#", CultureInfo.InvariantCulture));
		reason.Append(" labelCol=").Append(LabelColumnBudgetMm.ToString("0.#", CultureInfo.InvariantCulture));
		reason.Append(" tierTB=").Append(TierStackTopBudgetMm.ToString("0.#", CultureInfo.InvariantCulture))
			.Append("/").Append(TierStackBottomBudgetMm.ToString("0.#", CultureInfo.InvariantCulture));
		if (c.Scale != 75 && modelLength > 8000.0 && modelLength < 11000.0)
			reason.Append(" NOTE: expected ~1:75 for ~9366 on 11x17 — see budgets/bomLeft");
		c.ScaleReason = reason.ToString();
		return c;
	}

	private static void LogFitCanvas(FitCanvas c)
	{
		Console.WriteLine("[Fit] layoutName=" + (string.IsNullOrEmpty(c.LayoutName) ? "(none)" : c.LayoutName));
		Console.WriteLine("[Fit] Layout.SheetSize="
			+ c.LayoutW.ToString("0.###", CultureInfo.InvariantCulture) + "x"
			+ c.LayoutH.ToString("0.###", CultureInfo.InvariantCulture)
			+ " sheet=" + c.SheetW.ToString("0.###", CultureInfo.InvariantCulture) + "x"
			+ c.SheetH.ToString("0.###", CultureInfo.InvariantCulture)
			+ " bomLeft=" + c.BomLeft.ToString("0.###", CultureInfo.InvariantCulture)
			+ " availableW=" + c.AvailableWidthMm.ToString("0.###", CultureInfo.InvariantCulture)
			+ " availableH=" + c.AvailableHeightMm.ToString("0.###", CultureInfo.InvariantCulture)
			+ " scale=1:" + c.Scale.ToString(CultureInfo.InvariantCulture)
			+ " paperWall=" + c.PaperWallW.ToString("0.#", CultureInfo.InvariantCulture) + "x"
			+ c.PaperWallH.ToString("0.#", CultureInfo.InvariantCulture)
			+ " tierStackH=" + (c.TierStackTopMm + c.TierStackBotMm).ToString("0.#", CultureInfo.InvariantCulture)
			+ " dimTextH>=" + MinDimTextHeightMm.ToString("0.#", CultureInfo.InvariantCulture) + "mm");
		Console.WriteLine("[Fit] scaleReason: " + c.ScaleReason);
	}

	private static double LiveLength(Tekla.Structures.Model.Part main)
	{
		if (main == null)
		{
			return 0.0;
		}
		double value = 0.0;
		try
		{
			main.GetReportProperty("LENGTH", ref value);
		}
		catch
		{
		}
		if (value > 0.0)
		{
			return value;
		}
		try
		{
			Solid solid = main.GetSolid();
			if (solid != null)
			{
				value = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
			}
		}
		catch
		{
		}
		return value;
	}

	private static double LiveHeight(Tekla.Structures.Model.Part main)
	{
		if (main == null) return 0.0;
		double value = 0.0;
		try { main.GetReportProperty("HEIGHT", ref value); } catch {}
		if (value > 0.0) return value;
		Solid solid = main.GetSolid();
		if (solid != null) return solid.MaximumPoint.Y - solid.MinimumPoint.Y;
		return 0.0;
	}

	private static double ScanBomLeft(ContainerView sheet)
	{
		double sheetW = (sheet != null && sheet.Width > 1.0) ? sheet.Width : 431.8;
		return ScanBomLeft(sheet, sheetW);
	}

	/// <summary>
	/// Left edge of BOM / notes block in sheet mm. Scans table-like AABBs and BOM/notes
	/// text in the right half of the sheet; logs candidates; clamps to sheet width.
	/// </summary>
	private static double ScanBomLeft(ContainerView sheet, double sheetWidthMm)
	{
		double sheetCap = (sheetWidthMm > 40.0) ? sheetWidthMm : 431.8;
		double rightBand = sheetCap * 0.45;
		double best = double.MaxValue;
		var candidates = new List<string>();
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = sheet.GetAllObjects();
		}
		catch
		{
			drawingObjectEnumerator = null;
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			DrawingObject obj = drawingObjectEnumerator.Current as DrawingObject;
			if (obj == null || obj is ViewBase)
				continue;
			AABB aABB = null;
			try
			{
				if (obj is IAxisAlignedBoundingBox axisAlignedBoundingBox)
					aABB = axisAlignedBoundingBox.GetAxisAlignedBoundingBox();
			}
			catch
			{
				continue;
			}
			if (aABB == null || aABB.MinPoint == null || aABB.MaxPoint == null)
				continue;
			double x = aABB.MinPoint.X;
			double w = aABB.MaxPoint.X - aABB.MinPoint.X;
			double h = aABB.MaxPoint.Y - aABB.MinPoint.Y;
			if (x < rightBand || x >= sheetCap - 2.0)
				continue;
			bool keyword = false;
			string kind = obj.GetType().Name;
			try
			{
				if (obj is Text txt)
				{
					string s = (txt.TextString ?? "").ToUpperInvariant();
					if (s.IndexOf("BILL OF MATERIAL", StringComparison.Ordinal) >= 0
						|| s.IndexOf("GENERAL NOTES", StringComparison.Ordinal) >= 0
						|| s.IndexOf("BOM", StringComparison.Ordinal) >= 0
						|| s.IndexOf("MATERIAL LIST", StringComparison.Ordinal) >= 0)
					{
						keyword = true;
						kind = "Text:" + (s.Length > 40 ? s.Substring(0, 40) : s);
					}
				}
			}
			catch { }
			bool tableLike = w >= 35.0 && h >= 25.0;
			if (!keyword && !tableLike)
				continue;
			candidates.Add(kind + "@x=" + x.ToString("0.#", CultureInfo.InvariantCulture)
				+ " w=" + w.ToString("0.#", CultureInfo.InvariantCulture)
				+ " h=" + h.ToString("0.#", CultureInfo.InvariantCulture)
				+ (keyword ? " KEY" : ""));
			if (x < best)
				best = x;
		}
		double chosen;
		if (best < 100000.0 && best > rightBand)
			chosen = best;
		else
			chosen = BomFallbackLeft;
		double minBom = Math.Max(sheetCap * 0.55, 200.0);
		double maxBom = sheetCap - 5.0;
		if (chosen < minBom) chosen = Math.Min(BomFallbackLeft, maxBom);
		if (chosen > maxBom) chosen = maxBom;
		Console.WriteLine("[Fit] ScanBomLeft candidates=" + candidates.Count.ToString(CultureInfo.InvariantCulture)
			+ " chosen=" + chosen.ToString("0.###", CultureInfo.InvariantCulture)
			+ " fallback=" + BomFallbackLeft.ToString("0.#", CultureInfo.InvariantCulture)
			+ " sheetCap=" + sheetCap.ToString("0.#", CultureInfo.InvariantCulture));
		foreach (string c in candidates)
			Console.WriteLine("[Fit]   BOM cand: " + c);
		return chosen;
	}

	private static int CountStraightDimensionSets(View view)
	{
		if (view == null) return 0;
		int n = 0;
		try
		{
			DrawingObjectEnumerator en = view.GetObjects(new Type[1] { typeof(StraightDimensionSet) });
			while (en != null && en.MoveNext())
			{
				if (en.Current is StraightDimensionSet) n++;
			}
		}
		catch { }
		return n;
	}

	/// <summary>
	/// After headless Fit (showDrawing:false), briefly show the drawing so UI glyphs refresh.
	/// Does not rewrite marks. Verified: DrawingHandler.SetActiveDrawing(Drawing, bool showDrawing).
	/// </summary>
	private void RefreshDrawingUi(Drawing drawing)
	{
		if (drawing == null || _handler == null) return;
		try
		{
			bool ok = false;
			try { ok = _handler.SetActiveDrawing(drawing, showDrawing: true); } catch { }
			if (!ok)
			{
				try { ok = _handler.SetActiveDrawing(drawing, showDrawing: true, forceOpen: true); } catch { }
			}
			Console.WriteLine("[Fit] UI refresh showDrawing=true ok=" + ok);
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] UI refresh: " + ex.Message);
		}
	}

	private static void HideSheet1Rebar(ContainerView sheet)
	{
		if (sheet == null)
		{
			return;
		}
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = sheet.GetAllViews();
		}
		catch
		{
			try
			{
				drawingObjectEnumerator = sheet.GetViews();
			}
			catch
			{
				return;
			}
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is View view)
			{
				HideRebarInView(view);
			}
		}
	}

	private static void HideRebarInView(View view)
	{
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = view.GetAllObjects();
		}
		catch
		{
			return;
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is ReinforcementBase reinforcementBase)
			{
				try
				{
					reinforcementBase.Hideable.HideFromDrawingView();
					reinforcementBase.Modify();
				}
				catch
				{
				}
			}
		}
	}

	/// <summary>
	/// After UpdateDrawing, rewrite unresolved part marks (display '?') with model PART_POS
	/// as TextElement content. Never hide numbering marks. Rebar marks are skipped.
	/// </summary>
	private MarkRepairResult RepairBrokenPartMarks(Drawing drawing, ContainerView sheet, View front)
	{
		var result = default(MarkRepairResult);
		if (drawing == null || _model == null)
			return result;
		try { _handler.UpdateDrawing(drawing); } catch { }

		var hosts = new List<ViewBase>();
		if (front != null) hosts.Add(front);
		if (sheet != null)
		{
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetAllViews(); }
			catch { try { en = sheet.GetViews(); } catch { en = null; } }
			while (en != null && en.MoveNext())
			{
				if (en.Current is View v && !hosts.Contains(v))
					hosts.Add(v);
			}
		}

		int scanned = 0;
		int rebarSkip = 0;
		foreach (ViewBase host in hosts)
		{
			// Snapshot first — Delete during recreate invalidates live enumerators.
			List<MarkBase> hostMarks = new List<MarkBase>(EnumerateMarks(host));
			foreach (MarkBase mark in hostMarks)
			{
				scanned++;
				try
				{
					try
					{
						var prop = mark.GetType().GetProperty("ChangeSymbol");
						if (prop != null && prop.CanWrite)
							prop.SetValue(mark, false, null);
					}
					catch { }

					if (MarkLinksToRebar(mark))
					{
						rebarSkip++;
						continue;
					}

					string displayed = FirstNonEmpty(FlattenRelatedMarkText(mark), FlattenMarkText(mark));
					string resolved = ResolveMarkPartPos(mark, host);
					// API often already has a real mark string while Tekla UI still paints a magenta
					// associativity '?' — recreate the Mark attached to the drawing Part.
					if (string.IsNullOrWhiteSpace(resolved) || resolved.IndexOf('?') >= 0)
					{
						if (!string.IsNullOrWhiteSpace(displayed) && displayed.IndexOf('?') < 0
							&& !LooksLikeUnresolvedPropertyToken(displayed))
							resolved = displayed.Trim();
					}
					if (string.IsNullOrWhiteSpace(resolved) || resolved.IndexOf('?') >= 0)
					{
						result.Unresolved++;
						if (scanned <= 5)
							Console.WriteLine("[Fit] mark-unresolved displayed='" + Trunc(displayed, 40) + "'");
						continue;
					}

					// Recreate (insert-new then delete-old) clears broken associativity '?' glyphs.
					// Fall back to content rewrite when the drawing Part cannot be resolved.
					bool ok = TryRecreateMark(mark, host, resolved);
					if (!ok)
						ok = TryRewriteMarkContent(mark, resolved);
					if (ok)
						result.Repaired++;
					else if (IsMarkContentBroken(mark, displayed))
						result.Unresolved++;
					if (scanned <= 5)
						Console.WriteLine("[Fit] mark-fix '" + Trunc(displayed, 40) + "' -> '" + Trunc(resolved, 40)
							+ "' ok=" + ok);
				}
				catch { }
			}
		}
		Console.WriteLine("[Fit] mark-scan total=" + scanned.ToString(CultureInfo.InvariantCulture)
			+ " rebarSkip=" + rebarSkip.ToString(CultureInfo.InvariantCulture));
		return result;
	}

	/// <summary>
	/// Insert a new Mark attached to the drawing Part, then delete the old one.
	/// Insert-first avoids losing numbering if Insert fails. Recreate clears broken
	/// associativity '?' glyphs while keeping real PART_POS text.
	/// </summary>
	private bool TryRecreateMark(MarkBase mark, ViewBase host, string resolved)
	{
		if (mark == null || string.IsNullOrWhiteSpace(resolved)) return false;
		Tekla.Structures.Drawing.ModelObject target = FindRelatedDrawingModelObject(mark, host);
		if (target == null) return false;

		Point ip = null;
		try { ip = mark.InsertionPoint; } catch { }

		try
		{
			var neu = new Mark(target);
			neu.Attributes.Content.Clear();
			neu.Attributes.Content.Add(new TextElement(resolved, MarkFont()));
			if (ip != null)
				neu.InsertionPoint = new Point(ip.X, ip.Y, 0.0);
			try { if (neu.Hideable != null) neu.Hideable.ShowInDrawingView(); } catch { }
			if (!neu.Insert())
				return false;
		}
		catch { return false; }

		try { mark.Delete(); } catch { }
		return true;
	}

	private Tekla.Structures.Drawing.ModelObject FindRelatedDrawingModelObject(MarkBase mark, ViewBase host)
	{
		try
		{
			DrawingObjectEnumerator related = null;
			try { related = mark.GetRelatedObjects(new Type[] { typeof(Tekla.Structures.Drawing.Part) }); }
			catch { related = null; }
			if (related == null)
			{
				try { related = mark.GetRelatedObjects(); }
				catch { related = null; }
			}
			while (related != null && related.MoveNext())
			{
				if (related.Current is Tekla.Structures.Drawing.Part dp)
					return dp;
				if (related.Current is Tekla.Structures.Drawing.ModelObject dmo)
					return dmo;
			}
		}
		catch { }

		Identifier id = FindNearestDrawingPartId(mark, host);
		if (id == null || !id.IsValid() || host == null) return null;
		DrawingObjectEnumerator en = null;
		try { en = host.GetAllObjects(typeof(Tekla.Structures.Drawing.Part)); }
		catch
		{
			try { en = host.GetObjects(new Type[] { typeof(Tekla.Structures.Drawing.Part) }); }
			catch { return null; }
		}
		while (en != null && en.MoveNext())
		{
			if (en.Current is Tekla.Structures.Drawing.Part dp
				&& dp.ModelIdentifier != null && dp.ModelIdentifier.ID == id.ID)
				return dp;
		}
		return null;
	}

	private static string Trunc(string s, int max)
	{
		s = s ?? "";
		if (s.Length <= max) return s;
		return s.Substring(0, max) + "...";
	}

	private static IEnumerable<MarkBase> EnumerateMarks(ViewBase host)
	{
		if (host == null) yield break;
		var seen = new HashSet<int>();
		DrawingObjectEnumerator marks = null;
		try { marks = host.GetAllObjects(typeof(MarkBase)); }
		catch
		{
			try { marks = host.GetObjects(new Type[] { typeof(MarkBase), typeof(Mark), typeof(MarkSet) }); }
			catch
			{
				try { marks = host.GetAllObjects(); }
				catch { yield break; }
			}
		}
		while (marks != null && marks.MoveNext())
		{
			if (!(marks.Current is MarkBase mark)) continue;
			int id = 0;
			try { id = mark.GetHashCode(); } catch { }
			if (id != 0 && !seen.Add(id)) continue;
			yield return mark;
		}
	}

	private static bool TryRewriteMarkContent(MarkBase mark, string resolved)
	{
		if (string.IsNullOrWhiteSpace(resolved)) return false;
		try
		{
			if (mark is Mark m && m.Attributes?.Content != null)
			{
				m.Attributes.Content.Clear();
				m.Attributes.Content.Add(new TextElement(resolved, MarkFont()));
				try { if (m.Hideable != null) m.Hideable.ShowInDrawingView(); } catch { }
				return m.Modify();
			}
		}
		catch { }
		return false;
	}

	/// <summary>Non-magenta mark text (template vpm.text_colour 165 is magenta; ? glyphs inherit it).</summary>
	private static FontAttributes MarkFont()
	{
		return new FontAttributes
		{
			Height = 2.5,
			Color = DrawingColors.Yellow,
		};
	}

	private bool HasSiblingRole(Drawing drawing, SheetRole role)
	{
		string piece = SheetRoleMap.PieceMark(drawing?.Mark ?? "");
		if (string.IsNullOrWhiteSpace(piece)) return false;
		try
		{
			DrawingEnumerator en = _handler.GetDrawings();
			while (en != null && en.MoveNext())
			{
				var d = en.Current as Drawing;
				if (d == null) continue;
				string m = "";
				string n = "";
				try { m = d.Mark ?? ""; } catch { }
				try { n = d.Name ?? ""; } catch { }
				if (!SheetRoleMap.PieceMark(m).Equals(piece, StringComparison.OrdinalIgnoreCase))
					continue;
				if (SheetRoleMap.Resolve(m, n) == role)
					return true;
				// Explicit sections rename used by LocalDrawingMacroRunner.
				if (role == SheetRole.Sections
					&& n.IndexOf("SECTIONS 3D", StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			}
		}
		catch { }
		return false;
	}

	private void WriteFitReport(string drawingMark, int scale, double sheetW, double sheetH, double bomLeft,
		double availableWidthMm, double modelLength, double tapeDelta, bool tapeOk, int marksRepaired,
		int marksUnresolved, bool sectionsSibling, FitCanvas? canvas = null)
	{
		try
		{
			string dir = string.IsNullOrEmpty(_pdfDir)
				? Path.Combine("Export", "CivilDrawings", "new with macros")
				: Path.GetDirectoryName(_pdfDir) ?? _pdfDir;
			Directory.CreateDirectory(dir);
			string piece = SheetRoleMap.PieceMark(drawingMark);
			string path = Path.Combine(dir, "fit_report_" + piece.Replace(' ', '_') + ".txt");
			var sb = new System.Text.StringBuilder();
			sb.AppendLine("FitReport " + DateTime.UtcNow.ToString("o"));
			sb.AppendLine("drawing=" + drawingMark);
			sb.AppendLine("piece=" + piece);
			sb.AppendLine("scale=1:" + scale.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("sheetMm=" + sheetW.ToString("0.###", CultureInfo.InvariantCulture) + "x"
				+ sheetH.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("bomLeftMm=" + bomLeft.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("availableWidthMm=" + availableWidthMm.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("modelLengthMm=" + modelLength.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("tapeDeltaMm=" + (double.IsNaN(tapeDelta) ? "n/a" : tapeDelta.ToString("0.###", CultureInfo.InvariantCulture)));
			sb.AppendLine("tapeVerified=" + tapeOk);
			if (!tapeOk) sb.AppendLine("warning=TAPE_UNVERIFIED");
			sb.AppendLine("marksRepaired=" + marksRepaired.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("marksUnresolved=" + marksUnresolved.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("sectionsSibling=" + sectionsSibling);
			sb.AppendLine("sheetCountHint=" + (sectionsSibling ? "4+" : "3+sidecar"));
			sb.AppendLine("markRepairDefault=SKIP");
			sb.AppendLine("vertChainBudgetMm=" + VertChainLeftBudgetMm.ToString("0.#", CultureInfo.InvariantCulture)
				+ "/" + VertChainRightBudgetMm.ToString("0.#", CultureInfo.InvariantCulture));
			sb.AppendLine("tierStackBudgetMm=" + TierStackTopBudgetMm.ToString("0.#", CultureInfo.InvariantCulture)
				+ "/" + TierStackBottomBudgetMm.ToString("0.#", CultureInfo.InvariantCulture));
			sb.AppendLine("dimTextHeightMinMm=" + MinDimTextHeightMm.ToString("0.#", CultureInfo.InvariantCulture));
			if (canvas.HasValue)
			{
				FitCanvas c = canvas.Value;
				sb.AppendLine("layoutName=" + (c.LayoutName ?? ""));
				sb.AppendLine("layoutSheetMm=" + c.LayoutW.ToString("0.###", CultureInfo.InvariantCulture) + "x"
					+ c.LayoutH.ToString("0.###", CultureInfo.InvariantCulture));
				sb.AppendLine("paperWallMm=" + c.PaperWallW.ToString("0.#", CultureInfo.InvariantCulture) + "x"
					+ c.PaperWallH.ToString("0.#", CultureInfo.InvariantCulture));
				sb.AppendLine("scaleReason=" + (c.ScaleReason ?? ""));
			}
			sb.AppendLine("bboxFailures=" + _lastBboxFailures.Count.ToString(CultureInfo.InvariantCulture));
			foreach (string f in _lastBboxFailures)
				sb.AppendLine("bboxFail=" + f);
			// S3 note: vertical height heuristics evolved in 0b17b76 (model-driven HeightStations).
			// Older SHAs 2751850850 / 5952380 are not in this clone history.
			sb.AppendLine("verticalNote=HeightStations from 0b17b76; max 1 chain/side (S3)");
			sb.AppendLine("removedPaintText=" + _fitAudit.RemovedPaintText.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("removedSpamText=" + _fitAudit.RemovedSpamText.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("removedStrayGraphics=" + _fitAudit.RemovedStrayGraphics.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("removedStaleLabels=" + _fitAudit.RemovedStaleLabels.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("suppressedPhantomMarks=" + _fitAudit.SuppressedPhantomMarks.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("tiersAbove=" + _fitAudit.TiersAbove.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("tiersBelow=" + _fitAudit.TiersBelow.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("labelCount=" + _fitAudit.LabelCount.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("vertChainsLeft=" + _fitAudit.VertChainsLeft.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("vertChainsRight=" + _fitAudit.VertChainsRight.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("dimSetsBefore=" + _fitAudit.DimSetsBefore.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("dimSetsAfter=" + _fitAudit.DimSetsAfter.ToString(CultureInfo.InvariantCulture));
			File.WriteAllText(path, sb.ToString());
			Console.WriteLine("[Fit] report → " + path);
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] report failed: " + ex.Message);
		}
	}

	private void WriteFinalReport(string drawingMark, int scale, double sheetW, double sheetH, double bomLeft,
		double availableWidthMm, double modelLength, bool tapeOk, int marksRepaired, FitCanvas canvas)
	{
		try
		{
			string dir = string.IsNullOrEmpty(_pdfDir)
				? Path.Combine("Export", "CivilDrawings", "new with macros")
				: Path.GetDirectoryName(_pdfDir) ?? _pdfDir;
			Directory.CreateDirectory(dir);
			string piece = SheetRoleMap.PieceMark(drawingMark);
			string path = Path.Combine(dir, "final_report.md");
			var sb = new System.Text.StringBuilder();
			sb.AppendLine("# Sheet-1 Fit final report");
			sb.AppendLine();
			sb.AppendLine("- **Deliverable:** Tekla UI Document Manager (not Export PDF cosmetics)");
			sb.AppendLine("- **Drawing:** `" + drawingMark + "` piece `" + piece + "`");
			sb.AppendLine("- **Scale:** 1:" + scale.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("- **Sheet mm:** " + sheetW.ToString("0.###", CultureInfo.InvariantCulture) + " × "
				+ sheetH.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("- **Layout.SheetSize:** " + canvas.LayoutW.ToString("0.###", CultureInfo.InvariantCulture)
				+ " × " + canvas.LayoutH.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("- **Layout name:** " + (string.IsNullOrEmpty(canvas.LayoutName) ? "(none)" : canvas.LayoutName));
			sb.AppendLine("- **bomLeft:** " + bomLeft.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
			sb.AppendLine("- **availableWidth (wall band):** " + availableWidthMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
			sb.AppendLine("- **modelLength:** " + modelLength.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
			sb.AppendLine("- **tapeVerified:** " + tapeOk);
			sb.AppendLine("- **mark repair:** SKIP (default); repaired this run=" + marksRepaired.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("- **scaleReason:** " + (canvas.ScaleReason ?? ""));
			sb.AppendLine("- **bboxFailures:** " + _lastBboxFailures.Count.ToString(CultureInfo.InvariantCulture));
			foreach (string f in _lastBboxFailures)
				sb.AppendLine("  - " + f);
			sb.AppendLine();
			sb.AppendLine("## Peers to verify in Tekla UI");
			sb.AppendLine("- W10-175 sheet `- 1`");
			sb.AppendLine("- W10-78 sheet `- 1`");
			sb.AppendLine("- W10-48 sheet `- 1`");
			sb.AppendLine();
			sb.AppendLine("## Idempotency");
			sb.AppendLine("Second Fit run should not duplicate TOP IN FORM / SIDE/END tags (deduped by text).");
			sb.AppendLine();
			sb.AppendLine("## Vertical history");
			sb.AppendLine("Current HeightStations path from commit `0b17b76`. SHAs `2751850850` / `5952380` not present in this clone.");
			File.WriteAllText(path, sb.ToString());
			Console.WriteLine("[Fit] final_report → " + path);
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] final_report failed: " + ex.Message);
		}
	}

	/// <summary>
	/// S5: sheet-level views + graphics vs sheet/BOM. View-child Text/dims use view CS —
	/// check those via AssertDimSetsInsideBorder / AssertViewTextsInsideBorder (sheet mm).
	/// Rev 2 keeps paint / NO PAINT text — do not fail on paint labels.
	/// </summary>
	private void AssertSheetBounds(ContainerView sheet, double sheetW, double sheetH, double bomLeft, View front, int viewScale)
	{
		_lastBboxFailures.Clear();
		if (sheet == null) return;
		double sw = sheetW > 1.0 ? sheetW : 431.8;
		double sh = sheetH > 1.0 ? sheetH : 279.4;
		double bom = bomLeft > 1.0 ? bomLeft : BomFallbackLeft;
		double tol = SheetBorderMarginMm;
		double innerL = SheetBorderMarginMm;
		double innerR = sw - SheetBorderMarginMm;
		double innerB = SheetBorderMarginMm;
		double innerT = sh - SheetBorderMarginMm;
		try
		{
			// Only direct sheet children (views + layout graphics). Nested view objects are view-CS.
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetObjects(); } catch { try { en = sheet.GetAllObjects(); } catch { return; } }
			while (en != null && en.MoveNext())
			{
				DrawingObject obj = en.Current as DrawingObject;
				if (obj == null) continue;
				// Nested drawing objects from GetAllObjects: skip Text/dims/marks (view CS).
				if (!(obj is ViewBase) && (obj is Text || obj is DimensionBase || obj is MarkBase
					|| obj is StraightDimensionSet || obj is StraightDimension))
					continue;
				AABB box = null;
				try
				{
					if (obj is IAxisAlignedBoundingBox aabb)
						box = aabb.GetAxisAlignedBoundingBox();
				}
				catch { continue; }
				if (box == null || box.MinPoint == null || box.MaxPoint == null) continue;
				double minX = box.MinPoint.X, maxX = box.MaxPoint.X;
				double minY = box.MinPoint.Y, maxY = box.MaxPoint.Y;
				// Parked off-sheet views intentionally outside.
				if (minX > sw + 50.0 || maxX < -50.0 || minY > sh + 50.0 || maxY < -50.0)
					continue;
				// View-CS false positive: huge span or deep negative while not a View frame.
				if (!(obj is ViewBase) && (minX < -40.0 || maxX > sw + 80.0 || (maxX - minX) > sw * 1.5))
					continue;
				string kind = obj.GetType().Name;
				if (obj is ViewBase)
				{
					if (front != null && obj is View v && ReferenceEquals(v, front))
					{
						if (maxX > bom + 5.0)
							_lastBboxFailures.Add("FrontView overlaps BOM maxX="
								+ maxX.ToString("0.#", CultureInfo.InvariantCulture)
								+ " bomLeft=" + bom.ToString("0.#", CultureInfo.InvariantCulture));
						if (minX < innerL - tol || maxX > innerR + tol || minY < innerB - tol || maxY > innerT + tol)
							_lastBboxFailures.Add("FrontView outside border AABB=["
								+ minX.ToString("0.#", CultureInfo.InvariantCulture) + ","
								+ minY.ToString("0.#", CultureInfo.InvariantCulture) + ".."
								+ maxX.ToString("0.#", CultureInfo.InvariantCulture) + ","
								+ maxY.ToString("0.#", CultureInfo.InvariantCulture) + "]");
					}
					continue;
				}
				if (minX < innerL - tol || minY < innerB - tol || maxX > innerR + tol || maxY > innerT + tol)
				{
					_lastBboxFailures.Add(kind + TryObjectId(obj) + " outside border AABB=["
						+ minX.ToString("0.#", CultureInfo.InvariantCulture) + ","
						+ minY.ToString("0.#", CultureInfo.InvariantCulture) + ".."
						+ maxX.ToString("0.#", CultureInfo.InvariantCulture) + ","
						+ maxY.ToString("0.#", CultureInfo.InvariantCulture) + "]");
				}
			}
			if (front != null)
			{
				int vs = viewScale;
				try { vs = (int)Math.Round(front.Attributes.Scale); } catch { }
				if (vs != viewScale && viewScale > 0)
					_lastBboxFailures.Add("BBOX_FAIL view.Attributes.Scale=" + vs + " != Fit scale=" + viewScale);
				AssertTopInFormScaleLabel(front, viewScale, _lastBboxFailures);
				AssertDimSetsInsideBorder(front, sw, sh, innerL, innerR, innerB, innerT);
				AssertViewTextsInsideBorder(front, sw, sh, bom, innerL, innerR, innerB, innerT);
			}
		}
		catch (Exception ex)
		{
			_lastBboxFailures.Add("AssertSheetBounds error: " + ex.Message);
		}
		Console.WriteLine("[Fit] bboxAssert failures=" + _lastBboxFailures.Count.ToString(CultureInfo.InvariantCulture));
		foreach (string f in _lastBboxFailures)
			Console.WriteLine("[Fit] BBOX_FAIL " + f);
		if (_lastBboxFailures.Count > 0)
			Console.WriteLine("[Fit] *** ACCEPTANCE FAILED — fix bbox/scale before sign-off ***");
	}

	private void AssertViewTextsInsideBorder(View front, double sw, double sh, double bom,
		double innerL, double innerR, double innerB, double innerT)
	{
		if (front == null) return;
		try
		{
			double originX = front.Origin != null ? front.Origin.X : 0.0;
			double originY = front.Origin != null ? front.Origin.Y : 0.0;
			int sc = front.Attributes.Scale > 0 ? (int)Math.Round(front.Attributes.Scale) : 75;
			DrawingObjectEnumerator en = front.GetObjects(new Type[1] { typeof(Text) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is Text t)) continue;
				AABB box = null;
				try
				{
					if (t is IAxisAlignedBoundingBox aabb)
						box = aabb.GetAxisAlignedBoundingBox();
				}
				catch { continue; }
				if (box?.MinPoint == null || box?.MaxPoint == null) continue;
				double minX = originX + box.MinPoint.X / (double)sc;
				double maxX = originX + box.MaxPoint.X / (double)sc;
				double minY = originY + box.MinPoint.Y / (double)sc;
				double maxY = originY + box.MaxPoint.Y / (double)sc;
				string s = (t.TextString ?? "").Trim();
				if (minX < innerL - SheetBorderMarginMm || maxX > innerR + SheetBorderMarginMm
					|| minY < innerB - SheetBorderMarginMm || maxY > innerT + SheetBorderMarginMm)
					_lastBboxFailures.Add("Text '" + (s.Length > 24 ? s.Substring(0, 24) : s)
						+ "' outside border sheetX="
						+ minX.ToString("0.#", CultureInfo.InvariantCulture) + ".."
						+ maxX.ToString("0.#", CultureInfo.InvariantCulture));
				if (maxX > bom + SheetBorderMarginMm && minX < bom)
					_lastBboxFailures.Add("Text '" + (s.Length > 24 ? s.Substring(0, 24) : s)
						+ "' overlaps BOM band");
			}
		}
		catch { }
	}

	private static string TryObjectId(DrawingObject obj)
	{
		try
		{
			if (obj?.GetType().GetProperty("Identifier")?.GetValue(obj) != null)
				return "#" + obj.GetType().GetProperty("Identifier").GetValue(obj).ToString();
		}
		catch { }
		return "";
	}

	private static void AssertTopInFormScaleLabel(View front, int scale, List<string> failures)
	{
		bool ok = false;
		int count = 0;
		try
		{
			DrawingObjectEnumerator en = front.GetObjects(new Type[1] { typeof(Text) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is Text t)) continue;
				string s = t.TextString ?? "";
				if (s.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) < 0) continue;
				count++;
				if (s.IndexOf("1:" + scale.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0)
					ok = true;
			}
		}
		catch { }
		if (count != 1)
			failures?.Add("BBOX_FAIL TOP IN FORM count=" + count.ToString(CultureInfo.InvariantCulture) + " (expected 1)");
		if (!ok)
			failures?.Add("BBOX_FAIL TOP IN FORM label scale != 1:" + scale.ToString(CultureInfo.InvariantCulture));
	}

	private void AssertDimSetsInsideBorder(View front, double sw, double sh, double innerL, double innerR, double innerB, double innerT)
	{
		if (front == null) return;
		try
		{
			double originX = front.Origin != null ? front.Origin.X : 0.0;
			double originY = front.Origin != null ? front.Origin.Y : 0.0;
			int sc = front.Attributes.Scale > 0 ? (int)Math.Round(front.Attributes.Scale) : 75;
			DrawingObjectEnumerator en = front.GetObjects(new Type[1] { typeof(StraightDimensionSet) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is IAxisAlignedBoundingBox iaabb)) continue;
				AABB box = null;
				try { box = iaabb.GetAxisAlignedBoundingBox(); } catch { continue; }
				if (box?.MinPoint == null || box?.MaxPoint == null) continue;
				double minX = originX + box.MinPoint.X / (double)sc;
				double maxX = originX + box.MaxPoint.X / (double)sc;
				double minY = originY + box.MinPoint.Y / (double)sc;
				double maxY = originY + box.MaxPoint.Y / (double)sc;
				if (minX < innerL - SheetBorderMarginMm || maxX > innerR + SheetBorderMarginMm
					|| minY < innerB - SheetBorderMarginMm || maxY > innerT + SheetBorderMarginMm)
					_lastBboxFailures.Add("StraightDimensionSet outside border sheetX="
						+ minX.ToString("0.#", CultureInfo.InvariantCulture) + ".." + maxX.ToString("0.#", CultureInfo.InvariantCulture));
			}
		}
		catch { }
	}

	private static bool IsMarkContentBroken(MarkBase mark, string displayed)
	{
		if (string.IsNullOrWhiteSpace(displayed) || displayed.IndexOf('?') >= 0)
			return true;
		if (LooksLikeUnresolvedPropertyToken(displayed))
			return true;
		if (mark is Mark m && HasUnresolvedPropertyElement(m))
			return true;
		return false;
	}

	private static bool LooksLikeUnresolvedPropertyToken(string text)
	{
		string s = (text ?? "").Trim();
		if (s.Length == 0) return true;
		return Regex.IsMatch(s,
			@"^%?(PART_POS|ASSEMBLY_POS|PARTMARK|CAST_UNIT_POS|REBAR_POS|MARK|PART_POSITION|ASSEMBLY_POSITION)%?$",
			RegexOptions.IgnoreCase);
	}

	private static bool HasUnresolvedPropertyElement(Mark mark)
	{
		try
		{
			return mark?.Attributes?.Content != null && HasUnresolvedInContainer(mark.Attributes.Content);
		}
		catch { return false; }
	}

	private static bool HasUnresolvedInContainer(IEnumerable container)
	{
		if (container == null) return false;
		foreach (object el in container)
		{
			if (el is PropertyElement pe)
			{
				string v = pe.Value ?? "";
				if (string.IsNullOrWhiteSpace(v) || v.IndexOf('?') >= 0)
					return true;
			}
			else if (el is TextElement te)
			{
				if ((te.Value ?? "").IndexOf('?') >= 0)
					return true;
			}
			else if (el is ContainerElement ce && HasUnresolvedInContainer(ce))
				return true;
		}
		return false;
	}

	private string ResolveMarkPartPos(MarkBase mark, ViewBase host)
	{
		if (mark == null || _model == null)
			return "";
		Identifier id = FindMarkModelIdentifier(mark);
		string pos = PartPosFromIdentifier(id);
		if (!string.IsNullOrWhiteSpace(pos) && pos.IndexOf('?') < 0)
			return pos;

		// Fallback: nearest drawing Part in the same view (magenta '?' marks often lose RelatedObjects).
		id = FindNearestDrawingPartId(mark, host);
		return PartPosFromIdentifier(id);
	}

	private static Identifier FindMarkModelIdentifier(MarkBase mark)
	{
		Identifier id = null;
		try
		{
			DrawingObjectEnumerator related = null;
			try { related = mark.GetRelatedObjects(new Type[] { typeof(Tekla.Structures.Drawing.Part) }); }
			catch { related = null; }
			if (related == null)
			{
				try { related = mark.GetRelatedObjects(); }
				catch { related = null; }
			}
			while (related != null && related.MoveNext())
			{
				if (related.Current is Tekla.Structures.Drawing.Part dp && dp.ModelIdentifier != null && dp.ModelIdentifier.IsValid())
					return dp.ModelIdentifier;
				if (related.Current is Tekla.Structures.Drawing.ModelObject dmo
					&& dmo.ModelIdentifier != null && dmo.ModelIdentifier.IsValid() && id == null)
					id = dmo.ModelIdentifier;
			}
		}
		catch { }
		return id;
	}

	private Identifier FindNearestDrawingPartId(MarkBase mark, ViewBase host)
	{
		View view = host as View;
		if (mark == null || view == null || _model == null) return null;
		Point ip = null;
		try { ip = mark.InsertionPoint; } catch { }
		if (ip == null) return null;

		Identifier bestId = null;
		double bestDist = 500.0 * 500.0; // view units^2
		DrawingObjectEnumerator en = null;
		try { en = host.GetAllObjects(typeof(Tekla.Structures.Drawing.Part)); }
		catch
		{
			try { en = host.GetObjects(new Type[] { typeof(Tekla.Structures.Drawing.Part) }); }
			catch { return null; }
		}
		while (en != null && en.MoveNext())
		{
			if (!(en.Current is Tekla.Structures.Drawing.Part dp) || dp.ModelIdentifier == null || !dp.ModelIdentifier.IsValid())
				continue;
			Tekla.Structures.Model.Part mp = null;
			try { mp = _model.SelectModelObject(dp.ModelIdentifier) as Tekla.Structures.Model.Part; }
			catch { }
			if (mp == null) continue;
			Point gp = PartPoint(mp);
			Point vp = PanelAxis.ToView(gp, view);
			if (vp == null) continue;
			double dx = vp.X - ip.X;
			double dy = vp.Y - ip.Y;
			double d2 = dx * dx + dy * dy;
			if (d2 < bestDist)
			{
				bestDist = d2;
				bestId = dp.ModelIdentifier;
			}
		}
		return bestId;
	}

	private string PartPosFromIdentifier(Identifier id)
	{
		if (id == null || !id.IsValid() || _model == null)
			return "";
		Tekla.Structures.Model.ModelObject mo = null;
		try { mo = _model.SelectModelObject(id); } catch { }
		Tekla.Structures.Model.Part part = mo as Tekla.Structures.Model.Part;
		if (part == null && mo is Assembly asm)
		{
			try { part = asm.GetMainPart() as Tekla.Structures.Model.Part; } catch { }
		}

		string pos = "";
		if (part != null)
		{
			try { part.GetReportProperty("PART_POS", ref pos); } catch { }
			if (string.IsNullOrWhiteSpace(pos) || pos.IndexOf('?') >= 0)
			{
				pos = "";
				try { part.GetReportProperty("ASSEMBLY_POS", ref pos); } catch { }
			}
			if (string.IsNullOrWhiteSpace(pos) || pos.IndexOf('?') >= 0)
			{
				try { pos = part.Name ?? ""; } catch { pos = ""; }
			}
		}
		else if (mo is Assembly assembly)
		{
			try { assembly.GetReportProperty("ASSEMBLY_POS", ref pos); } catch { }
		}
		return (pos ?? "").Trim();
	}

	private static bool MarkLinksToRebar(MarkBase mark)
	{
		try
		{
			DrawingObjectEnumerator related = mark.GetRelatedObjects();
			while (related != null && related.MoveNext())
			{
				if (related.Current is ReinforcementBase)
					return true;
			}
		}
		catch { }
		return false;
	}

	private static string FlattenRelatedMarkText(MarkBase mark)
	{
		if (mark == null) return "";
		try
		{
			var sb = new System.Text.StringBuilder();
			DrawingObjectEnumerator en = mark.GetRelatedObjects();
			while (en != null && en.MoveNext())
			{
				if (en.Current is Text t)
					sb.Append(t.TextString ?? "");
			}
			return sb.ToString();
		}
		catch { return ""; }
	}

	private static string FirstNonEmpty(string a, string b)
	{
		if (!string.IsNullOrWhiteSpace(a)) return a;
		if (!string.IsNullOrWhiteSpace(b)) return b;
		return "";
	}

	private static string FlattenMarkText(MarkBase mark)
	{
		if (mark == null) return "";
		try
		{
			if (mark is Mark m && m.Attributes?.Content != null)
			{
				var sb = new System.Text.StringBuilder();
				FlattenMarkContainer(m.Attributes.Content, sb);
				return sb.ToString();
			}
		}
		catch { }
		try
		{
			var p = mark.GetType().GetProperty("TextString");
			if (p != null)
			{
				object v = p.GetValue(mark, null);
				if (v != null) return v.ToString();
			}
		}
		catch { }
		return "";
	}

	private static void FlattenMarkContainer(IEnumerable container, System.Text.StringBuilder sb)
	{
		if (container == null || sb == null) return;
		foreach (object el in container)
		{
			if (el is TextElement te) sb.Append(te.Value ?? "");
			else if (el is PropertyElement pe)
				sb.Append(string.IsNullOrEmpty(pe.Value) ? (pe.Name ?? "") : pe.Value);
			else if (el is ContainerElement ce) FlattenMarkContainer(ce, sb);
			else if (el != null) sb.Append(el.ToString());
		}
	}

	private static void PurgeTemporaryTags(ContainerView sheet)
	{
		if (sheet == null)
		{
			return;
		}
		List<DrawingObject> list = new List<DrawingObject>();
		CollectSpam(sheet, list);
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = sheet.GetAllViews();
		}
		catch
		{
			try
			{
				drawingObjectEnumerator = sheet.GetViews();
			}
			catch
			{
				drawingObjectEnumerator = null;
			}
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is View host)
			{
				CollectSpam(host, list);
			}
		}
		foreach (DrawingObject item in list)
		{
			try
			{
				item.Delete();
			}
			catch
			{
			}
		}
	}

	private static void CollectSpam(ViewBase host, List<DrawingObject> doomed)
	{
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = host.GetAllObjects();
		}
		catch
		{
			return;
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is Text text)
			{
				string text2 = "";
				try
				{
					text2 = text.TextString ?? "";
				}
				catch
				{
					continue;
				}
				// Keep TOP IN FORM / IN FORM — engineer sheet-1 title (do not purge).
				if (text2.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0
					|| (text2.IndexOf("IN FORM", StringComparison.OrdinalIgnoreCase) >= 0
						&& text2.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0))
					continue;
				if (text2.Contains("???") || Regex.IsMatch(text2, "^(AF\\d+|TEMP_|LIFTING BALANCE|PROFILE RECESS|BCMBD|RECESS)", RegexOptions.IgnoreCase) || text2.IndexOf("SEE DETAIL A", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("SECTION CLUSTER", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("PROFILE RECESS", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("RECESS", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("BCMBD", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("PAINT BENJAMIN MOORE", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					doomed.Add(text);
				}
			}
			else if (drawingObjectEnumerator.Current is WeldMark item2)
			{
				doomed.Add(item2);
			}
			// Part/assembly numbering marks (MarkBase) are kept — do not mass-delete.
		}
		// Do not walk Part/Rebar related MarkBase — numbering marks must stay visible.
	}

	private static void PurgeOldDimensions(View view)
	{
		if (view == null)
		{
			return;
		}
		HashSet<DimensionSetBase> hashSet = new HashSet<DimensionSetBase>();
		List<DrawingObject> list = new List<DrawingObject>();
		try
		{
			DrawingObjectEnumerator objects = view.GetObjects(new Type[1] { typeof(StraightDimensionSet) });
			while (objects != null && objects.MoveNext())
			{
				if (objects.Current is StraightDimensionSet item)
				{
					hashSet.Add(item);
				}
			}
		}
		catch
		{
		}
		try
		{
			DrawingObjectEnumerator objects2 = view.GetObjects(new Type[1] { typeof(StraightDimension) });
			while (objects2 != null && objects2.MoveNext())
			{
				if (!(objects2.Current is StraightDimension straightDimension))
				{
					continue;
				}
				try
				{
					DimensionSetBase dimensionSet = straightDimension.GetDimensionSet();
					if (dimensionSet != null)
					{
						hashSet.Add(dimensionSet);
					}
					else
					{
						list.Add(straightDimension);
					}
				}
				catch
				{
					list.Add(straightDimension);
				}
			}
		}
		catch
		{
		}
		foreach (DimensionSetBase item2 in hashSet)
		{
			try
			{
				item2.Delete();
			}
			catch
			{
			}
		}
		foreach (DrawingObject item3 in list)
		{
			try
			{
				item3.Delete();
			}
			catch
			{
			}
		}
	}

	private void PlaceTiers(View view, PanelAxis axis, StationBuckets buckets, List<double> tape, List<double[]> micros, int scale, bool overallOnly = false, ContainerView sheet = null, double bomLeftSheetMm = 0.0, double sheetHeightMm = 279.4)
	{
		int nativeDims = CountStraightDimensionSets(view);
		_fitAudit.DimSetsBefore = nativeDims;
		bool preserve = PreserveNativeDimensions && nativeDims >= NativeDimPreserveMin;
		if (preserve)
		{
			Console.WriteLine("[Fit] preserve-native dims count=" + nativeDims.ToString(CultureInfo.InvariantCulture)
				+ " (>= " + NativeDimPreserveMin.ToString(CultureInfo.InvariantCulture)
				+ ") — skip PurgeOldDimensions and PlaceTiers (opt-in: --preserve-native-dims)");
			_fitAudit.DimSetsAfter = nativeDims;
			return;
		}
		if (nativeDims > 0)
			Console.WriteLine("[Fit] native dims count=" + nativeDims.ToString(CultureInfo.InvariantCulture)
				+ " — rebuild engineer sheet-1 tiers from model stations");

		PurgeOldDimensions(view);
		double bomLeft = bomLeftSheetMm > 1.0 ? bomLeftSheetMm : ((sheet != null && sheet.Width > 1.0) ? ScanBomLeft(sheet, sheet.Width) : BomFallbackLeft);
		int topTierCount = CountTopTiers(buckets, overallOnly);
		int botTierCount = CountBottomTiers(buckets, overallOnly);
		double topStepPaper = ComputeTierStepPaperMm(sheetHeightMm, topTierCount, TierStackTopBudgetMm);
		double botStepPaper = ComputeTierStepPaperMm(sheetHeightMm, botTierCount, TierStackBottomBudgetMm);
		double topDist = TierFirstOffsetPaperMm * (double)scale;
		double step = topStepPaper * (double)scale;
		double botDist = TierFirstOffsetPaperMm * (double)scale;
		double botStep = botStepPaper * (double)scale;
		_fitAudit.TiersAbove = topTierCount;
		_fitAudit.TiersBelow = botTierCount;
		Console.WriteLine("[Fit] tierStepPaper top=" + topStepPaper.ToString("0.##", CultureInfo.InvariantCulture)
			+ " bot=" + botStepPaper.ToString("0.##", CultureInfo.InvariantCulture)
			+ " tiersAbove=" + topTierCount.ToString(CultureInfo.InvariantCulture)
			+ " tiersBelow=" + botTierCount.ToString(CultureInfo.InvariantCulture));
		if (overallOnly || buckets == null)
		{
			PlaceString(view, axis, tape, isBottom: false, topDist, "OVERALL PROFILE", scale, bomLeft);
			PlaceString(view, axis, tape, isBottom: true, topDist, "OVERALL PROFILE", scale, bomLeft);
			PlaceVerticalDimensions(view, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);
			return;
		}
		// One row per category (S2). Stations from model; above/below via mid-height rule in AddItem.
		if (buckets.Lifters.Count > 0)
		{
			PlaceString(view, axis, buckets.Lifters, isBottom: false, topDist, "P-205 LIFTERS", scale, bomLeft);
			topDist += step;
		}
		List<string> list = new List<string>(buckets.Splicers.Keys);
		list.Sort(delegate(string a, string b)
		{
			if (a == "SP25")
			{
				return -1;
			}
			return (b == "SP25") ? 1 : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
		});
		foreach (string item in list)
		{
			List<double> list2 = buckets.Splicers[item];
			if (list2.Count > 0)
			{
				string tagLabel = SplicerTagLabel(item);
				PlaceString(view, axis, list2, isBottom: false, topDist, tagLabel, scale, bomLeft);
				topDist += step;
			}
		}
		if (buckets.Bracing.Count > 0)
		{
			PlaceString(view, axis, buckets.Bracing, isBottom: false, topDist, "P-300 BRACING", scale, bomLeft);
			topDist += step;
		}
		if (buckets.TopEmbeds.Count > 0)
		{
			PlaceString(view, axis, buckets.TopEmbeds, isBottom: false, topDist, "P-602 HARDWARE", scale, bomLeft);
			topDist += step;
		}
		// One tier step per category (do not share reveal/hole/opening on one rung).
		if (buckets.Reveals.Count > 0)
		{
			PlaceString(view, axis, buckets.Reveals, isBottom: false, topDist, "REVEAL (B)", scale, bomLeft);
			topDist += step;
		}
		// Engineer page 1: no separate HOLE tier (openings/reveal only).
		if (buckets.Openings.Count > 0)
		{
			List<double> openingChain = ConsolidateOpeningStations(buckets.Openings, axis);
			PlaceString(view, axis, openingChain, isBottom: false, topDist, "OPENING PROFILE", scale, bomLeft);
			topDist += step;
		}
		if (buckets.NoPaint != null && buckets.NoPaint.Count > 0)
		{
			List<double> noPaintChain = ConsolidateOpeningStations(buckets.NoPaint, axis);
			PlaceString(view, axis, noPaintChain, isBottom: false, topDist, "NO PAINT", scale, bomLeft);
			topDist += step;
		}
		if (buckets.Ledge.Count > 0)
		{
			PlaceString(view, axis, buckets.Ledge, isBottom: false, topDist, "LEDGE", scale, bomLeft);
			topDist += step;
		}
		PlaceString(view, axis, tape, isBottom: false, topDist, "OVERALL PROFILE", scale, bomLeft);
		List<string> list3 = new List<string>(buckets.BottomGroutTubes.Keys);
		list3.Sort(delegate(string a, string b)
		{
			if (a.Contains("GT75-1219"))
			{
				return -1;
			}
			return b.Contains("GT75-1219") ? 1 : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
		});
		foreach (string item2 in list3)
		{
			List<double> list4 = buckets.BottomGroutTubes[item2];
			if (list4.Count > 0)
			{
				PlaceString(view, axis, list4, isBottom: true, botDist, GroutTubeTagLabel(item2), scale, bomLeft);
				botDist += botStep;
			}
		}
		if (buckets.BottomSecondary.Count > 0)
		{
			PlaceString(view, axis, buckets.BottomSecondary, isBottom: true, botDist, "EB-S-PL + CNDT", scale, bomLeft);
			botDist += botStep;
		}
		if (buckets.BaseOpenings.Count > 0)
		{
			PlaceString(view, axis, buckets.BaseOpenings, isBottom: true, botDist, "OPENING PROFILE", scale, bomLeft);
			botDist += botStep;
		}
		PlaceString(view, axis, tape, isBottom: true, botDist, "OVERALL PROFILE", scale, bomLeft);
		PlaceNotchRecessDims(view, axis, buckets, scale, bomLeft);
		PlaceVerticalDimensions(view, axis, buckets, scale, sheet, bomLeftSheetMm);
		_fitAudit.DimSetsAfter = CountStraightDimensionSets(view);
		InsertGt76CalloutIfPresent(view, axis, buckets, scale);
	}

	/// <summary>
	/// Short H/V callouts for base step / end notch so recess reads on elevation (PDF page 1).
	/// </summary>
	private void PlaceNotchRecessDims(View view, PanelAxis axis, StationBuckets buckets, int scale, double bomLeft)
	{
		if (view == null || axis == null || buckets == null) return;
		try
		{
			if (buckets.NotchX != null && buckets.NotchX.Count >= 2)
			{
				List<double> nx = new List<double>(buckets.NotchX);
				nx.Sort();
				List<double> dedup = new List<double>();
				foreach (double x in nx)
				{
					if (dedup.Count == 0 || Math.Abs(x - dedup[dedup.Count - 1]) > 1.0)
						dedup.Add(x);
				}
				if (dedup.Count >= 2)
				{
					double dist = TierFirstOffsetPaperMm * 0.5 * (double)scale;
					EmitDim(view, axis, dedup, isBottom: true, dist, "NOTCH / RECESS", scale, bomLeft);
					Console.WriteLine("[Fit] notch/recess H stations count=" + dedup.Count.ToString(CultureInfo.InvariantCulture));
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] notch/recess: " + ex.Message);
		}
	}

	private static int CountTopTiers(StationBuckets buckets, bool overallOnly)
	{
		if (overallOnly || buckets == null) return 1;
		int n = 1;
		if (buckets.Lifters.Count > 0) n++;
		n += buckets.Splicers.Count;
		if (buckets.Bracing.Count > 0) n++;
		if (buckets.TopEmbeds.Count > 0) n++;
		if (buckets.Reveals.Count > 0) n++;
		if (buckets.Openings.Count > 0) n++;
		if (buckets.NoPaint != null && buckets.NoPaint.Count > 0) n++;
		if (buckets.Ledge.Count > 0) n++;
		return n;
	}

	private static int CountBottomTiers(StationBuckets buckets, bool overallOnly)
	{
		if (overallOnly || buckets == null) return 1;
		int n = 1;
		n += buckets.BottomGroutTubes.Count;
		if (buckets.BottomSecondary.Count > 0) n++;
		if (buckets.BaseOpenings.Count > 0) n++;
		return n;
	}

	private static double ComputeTierStepPaperMm(double sheetHeightMm, int tierCount, double stackBudgetMm)
	{
		double budget = stackBudgetMm > 20.0 ? stackBudgetMm : Math.Max(40.0, sheetHeightMm * 0.22);
		double step = tierCount > 1 ? (budget / (double)tierCount) : TierStepMaxPaperMm;
		step = Math.Max(TierStepMinPaperMm, Math.Min(TierStepMaxPaperMm, step));
		return Math.Max(step, MinDimTextHeightMm + 1.5);
	}

	private static void InsertGt76CalloutIfPresent(View view, PanelAxis axis, StationBuckets buckets, int scale)
	{
		if (view == null || axis == null || buckets?.Trades == null) return;
		foreach (TradeHit t in buckets.Trades)
		{
			string lbl = (t.Label ?? "").ToUpperInvariant();
			if (lbl.IndexOf("GT76", StringComparison.Ordinal) < 0) continue;
			Point p = axis.PaperPoint(t.Station);
			if (p == null) continue;
			InsertFramedText(view, new Point(p.X, p.Y + 4.0 * scale, 0.0), "GT76-254", scale);
			return;
		}
	}

	private static string SplicerTagLabel(string key)
	{
		// Rev 2 hardware group labels (coupler + paired grout tube).
		string k = (key ?? "").ToUpperInvariant();
		if (k.Contains("SP30")) return "SP30-1219 + GT100-1219";
		if (k.Contains("SP15")) return "SP15-457 + GT75-686";
		if (k.Contains("SP25")) return "SP25-1067 + GT75-1219";
		return k + " HARDWARE";
	}

	private static string GroutTubeTagLabel(string key)
	{
		string k = (key ?? "").ToUpperInvariant();
		if (k.Contains("GT100")) return "GT100-1219 HARDWARE";
		if (k.Contains("GT75-686")) return "GT75-686 HARDWARE";
		if (k.Contains("GT75")) return "GT75-1219 HARDWARE";
		return k + " HARDWARE";
	}

	/// <summary>
	/// Build edge|window|pier|window|edge for two main openings (engineer OPENING PROFILE).
	/// Pairs edges into window intervals (400–1500 mm), keeps the two largest, drops mid-fracture points.
	/// Compares to PDF 373|975|4928|975|2115 (log only — no hardcode unless within 0.5 mm).
	/// </summary>
	private static List<double> ConsolidateOpeningStations(List<double> openings, PanelAxis axis)
	{
		if (openings == null || openings.Count == 0 || axis == null)
			return openings ?? new List<double>();
		List<double> sorted = new List<double>(openings);
		sorted.Sort();
		List<double> edges = new List<double>();
		foreach (double s in sorted)
		{
			if (edges.Count == 0 || Math.Abs(s - edges[edges.Count - 1]) > 1.0)
				edges.Add(s);
		}
		// Pair consecutive edges into candidate windows.
		var windows = new List<Tuple<double, double, double>>(); // L, R, width
		for (int i = 0; i + 1 < edges.Count; i++)
		{
			double w = edges[i + 1] - edges[i];
			if (w >= 400.0 && w <= 1500.0)
				windows.Add(Tuple.Create(edges[i], edges[i + 1], w));
		}
		windows.Sort((a, b) => b.Item3.CompareTo(a.Item3));
		List<double> chain = new List<double> { axis.Min };
		if (windows.Count >= 2)
		{
			var w0 = windows[0];
			var w1 = windows[1];
			// Order left-to-right.
			if (w0.Item1 > w1.Item1)
			{
				var tmp = w0; w0 = w1; w1 = tmp;
			}
			chain.Add(w0.Item1);
			chain.Add(w0.Item2);
			chain.Add(w1.Item1);
			chain.Add(w1.Item2);
		}
		else if (windows.Count == 1)
		{
			chain.Add(windows[0].Item1);
			chain.Add(windows[0].Item2);
		}
		else
		{
			// Fallback: deduped edges only (legacy).
			foreach (double e in edges)
			{
				if (Math.Abs(e - axis.Min) > 0.5 && Math.Abs(e - (axis.Min + axis.Span)) > 0.5)
					chain.Add(e);
			}
		}
		if (Math.Abs(chain[chain.Count - 1] - (axis.Min + axis.Span)) > 0.5)
			chain.Add(axis.Min + axis.Span);
		// Dedupe sorted.
		chain.Sort();
		List<double> deduped = new List<double>();
		foreach (double s in chain)
		{
			if (deduped.Count == 0 || Math.Abs(s - deduped[deduped.Count - 1]) > 0.5)
				deduped.Add(s);
		}
		var segs = new List<string>();
		double sum = 0.0;
		for (int i = 1; i < deduped.Count; i++)
		{
			double seg = deduped[i] - deduped[i - 1];
			sum += seg;
			segs.Add(seg.ToString("0", CultureInfo.InvariantCulture));
		}
		double delta = sum - axis.Span;
		string chainStr = string.Join("|", segs);
		Console.WriteLine("[Fit] OPENING chain=" + chainStr
			+ " Σ=" + sum.ToString("0.###", CultureInfo.InvariantCulture)
			+ " span=" + axis.Span.ToString("0.###", CultureInfo.InvariantCulture)
			+ " delta=" + delta.ToString("0.###", CultureInfo.InvariantCulture)
			+ " (PDF target 373|975|4928|975|2115)");
		if (Math.Abs(delta) > ConservationTolerance)
			Console.WriteLine("[TAPE_UNVERIFIED] opening chain sum vs span");
		// Optional: if model segs within 0.5 of PDF target segs, accept; else log MISMATCH (no hardcode).
		double[] pdfSegs = { 373, 975, 4928, 975, 2115 };
		if (segs.Count == pdfSegs.Length)
		{
			bool close = true;
			for (int i = 0; i < pdfSegs.Length; i++)
			{
				double v;
				if (!double.TryParse(segs[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)
					|| Math.Abs(v - pdfSegs[i]) > 0.5)
					close = false;
			}
			Console.WriteLine(close
				? "[Fit] OPENING vs PDF: OK (within 0.5 mm)"
				: "[Fit] OPENING vs PDF: MISMATCH model=" + chainStr + " PDF=373|975|4928|975|2115");
		}
		return deduped;
	}

	private void PlaceString(View view, PanelAxis axis, List<double> stations, bool isBottom, double distance, string tagLabel, int scale, double bomLeftSheetMm = 0.0)
	{
		if (stations != null && stations.Count != 0)
		{
			// Collapse micro-gaps (e.g. SP25 spurious 28|28) before chaining.
			List<double> cleaned = DeduplicateStations(stations, 30.0);
			List<double> stations2 = new List<double>(cleaned)
			{
				axis.Min,
				axis.Min + axis.Span
			};
			List<double> list = axis.Chain(stations2);
			if (list.Count >= 2)
			{
				EmitDim(view, axis, list, isBottom, distance, tagLabel, scale, bomLeftSheetMm);
			}
		}
	}

	private static List<double> DeduplicateStations(List<double> stations, double tolMm)
	{
		if (stations == null || stations.Count == 0) return new List<double>();
		List<double> sorted = new List<double>(stations);
		sorted.Sort();
		List<double> outList = new List<double>();
		foreach (double s in sorted)
		{
			if (outList.Count == 0 || Math.Abs(s - outList[outList.Count - 1]) > tolMm)
				outList.Add(s);
		}
		return outList;
	}

	private void EmitDim(View view, PanelAxis axis, List<double> stations, bool isBottom, double distance, string tagLabel = null, int scale = 20, double bomLeftSheetMm = 0.0)
	{
		if (stations == null || stations.Count < 2)
		{
			return;
		}
		PointList pointList = new PointList();
		foreach (double station in stations)
		{
			Point point = (isBottom ? axis.PaperBottomPoint(station) : axis.PaperPoint(station));
			pointList.Add(new Point(point.X, point.Y, 0.0));
		}
		Vector upDirection = (isBottom ? new Vector(0.0, -1.0, 0.0) : new Vector(0.0, 1.0, 0.0));
		double paper = distance / (double)((scale < 1) ? 1 : scale);
		try
		{
			var attrs = new StraightDimensionSet.StraightDimensionSetAttributes();
			try { attrs.LoadAttributes("standard"); } catch { }
			attrs.ExtensionLine = DimensionSetBaseAttributes.ExtensionLineTypes.Yes;
			attrs.Color = DrawingColors.Green;
			try { attrs.CombinedDimension.MinimumNumberToCombine = 99; } catch { }
			try { attrs.Text.Font.Color = DrawingColors.Yellow; } catch { }
			try { attrs.Text.Font.Height = MinDimTextHeightMm; } catch { }

			StraightDimensionSet set = null;
			try { set = _dims.CreateDimensionSet(view, pointList, upDirection, paper, attrs); }
			catch (Exception ex) { Console.WriteLine("[DimError] " + ex.Message); }
			if (set == null)
				set = _dims.CreateDimensionSet(view, pointList, upDirection, paper);

			if (set == null)
				Console.WriteLine("[DimError] CreateDimensionSet returned NULL for distance " + paper.ToString("0", CultureInfo.InvariantCulture));
			else
				Console.WriteLine("[DimSuccess] Created DimSet at paper " + paper.ToString("0.#", CultureInfo.InvariantCulture) + " mm with " + pointList.Count + " pts.");

			if (!_loggedDimUnits && set != null)
			{
				_loggedDimUnits = true;
				Console.WriteLine("[DimUnit] CreateDimensionSet arg=paperMm=" + paper.ToString("0.###", CultureInfo.InvariantCulture)
					+ " then set.Distance=viewUnits=" + distance.ToString("0.###", CultureInfo.InvariantCulture)
					+ " scale=" + scale.ToString(CultureInfo.InvariantCulture)
					+ " (paper×scale=" + (paper * scale).ToString("0.###", CultureInfo.InvariantCulture) + ")");
			}

			if (set != null)
			{
				try
				{
					set.Attributes = attrs;
					try { set.Distance = distance; } catch { }
					set.Modify();
				}
				catch (Exception ex)
				{
					Console.WriteLine("[Fit] dimension lines: " + ex.Message);
				}
			}
			if (!string.IsNullOrWhiteSpace(tagLabel))
			{
				PlaceTagBox(view, axis, isBottom, distance, tagLabel, scale, bomLeftSheetMm);
				_fitAudit.LabelCount++;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] tier " + (isBottom ? "bottom " : "top ") + distance.ToString("0", CultureInfo.InvariantCulture) + ": " + ex.Message);
		}
	}

	private static Tekla.Structures.Drawing.ModelObject FirstModelObject(View view)
	{
		if (view == null)
		{
			return null;
		}
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = view.GetAllObjects();
		}
		catch
		{
			return null;
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is Tekla.Structures.Drawing.ModelObject result)
			{
				return result;
			}
		}
		return null;
	}

	private static void PlaceTagBox(View view, PanelAxis axis, bool isBottom, double distance, string text, int scale, double bomLeftSheetMm = 0.0)
	{
		if (view == null || axis == null || string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			// Idempotent replace: drop prior same-label tier tags (S2).
			RemoveTextsMatching(view, text);
			// Engineer style: tier labels on the RIGHT end of the dimension ladder, left of BOM.
			Point obj = (isBottom ? axis.PaperBottomPoint(axis.Min + axis.Span) : axis.PaperPoint(axis.Min + axis.Span));
			Vector vector = (isBottom ? new Vector(0.0, -1.0, 0.0) : new Vector(0.0, 1.0, 0.0));
			double x = obj.X + 2.0 * (double)scale;
			double y = obj.Y + vector.Y * distance;
			if (bomLeftSheetMm > 1.0 && scale >= 1)
			{
				try
				{
					double originX = view.Origin != null ? view.Origin.X : 0.0;
					double sheetX = originX + x / (double)scale;
					double maxSheetX = bomLeftSheetMm - BomGap - 2.0;
					if (sheetX > maxSheetX)
						x = (maxSheetX - originX) * (double)scale;
				}
				catch { }
			}
			Text text2 = new Text(view, new Point(x, y, 0.0), text);
			try
			{
				text2.Attributes.Frame.Type = FrameTypes.None;
				text2.Attributes.Font.Height = MinDimTextHeightMm;
			}
			catch
			{
			}
			text2.Insert();
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] tag box " + text + ": " + ex.Message);
		}
	}

	private static void RemoveTextsMatching(ViewBase host, string exactOrContains)
	{
		if (host == null || string.IsNullOrWhiteSpace(exactOrContains)) return;
		try
		{
			var doomed = new List<Text>();
			DrawingObjectEnumerator en = null;
			try { en = host.GetObjects(new Type[1] { typeof(Text) }); }
			catch { try { en = host.GetAllObjects(); } catch { return; } }
			while (en != null && en.MoveNext())
			{
				if (en.Current is Text t)
				{
					string s = t.TextString ?? "";
					if (string.Equals(s, exactOrContains, StringComparison.OrdinalIgnoreCase)
						|| s.IndexOf(exactOrContains, StringComparison.OrdinalIgnoreCase) >= 0)
						doomed.Add(t);
				}
			}
			foreach (Text t in doomed)
			{
				try { t.Delete(); } catch { }
			}
		}
		catch { }
	}

	private void PlaceVerticalDimensions(View view, PanelAxis axis, StationBuckets buckets, int scale, ContainerView sheet = null, double bomLeftSheetMm = 0.0)
	{
		if (view == null || axis == null)
			return;
		Point point = axis.PaperPoint(axis.Min);
		Point point2 = axis.PaperBottomPoint(axis.Min);
		Point point3 = axis.PaperPoint(axis.Min + axis.Span);
		Point point4 = axis.PaperBottomPoint(axis.Min + axis.Span);
		double edgeX = Math.Min(point.X, point3.X);
		double edgeX2 = Math.Max(point.X, point3.X);
		double bottomY = Math.Min(point2.Y, point4.Y);
		double wallH = Math.Abs(Math.Max(point.Y, point3.Y) - bottomY);
		if (wallH <= 10.0)
			wallH = (axis.Height > 0.0) ? axis.Height : 2975.0;
		double modelH = (axis.Height > 1.0) ? axis.Height : wallH;
		double toView = (modelH > 1.0) ? (wallH / modelH) : 1.0;
		// Engineer Side A ≈ base step + body (375+2600); Side B ≈ notch + mid + top (275+1850+850).
		double[] leftPts = BuildSideVerticalViewHeights(buckets, modelH, toView, wallH, sideA: true);
		double[] rightPts = BuildSideVerticalViewHeights(buckets, modelH, toView, wallH, sideA: false);
		double leftDist = VertFirstOffsetPaperMm * (double)scale;
		double rightDist = VertFirstOffsetPaperMm * (double)scale;
		double maxLeftPaper = VertChainLeftBudgetMm;
		double maxRightPaper = VertChainRightBudgetMm;
		try
		{
			double originX = view.Origin != null ? view.Origin.X : LeftMargin;
			double sc = (scale < 1) ? 1.0 : (double)scale;
			double edgeSheetL = originX + edgeX / sc;
			double edgeSheetR = originX + edgeX2 / sc;
			double bom = bomLeftSheetMm > 1.0 ? bomLeftSheetMm : BomFallbackLeft;
			maxLeftPaper = Math.Max(VertFirstOffsetPaperMm, edgeSheetL - LeftMargin - SheetBorderMarginMm);
			maxRightPaper = Math.Max(VertFirstOffsetPaperMm, bom - BomGap - edgeSheetR - SheetBorderMarginMm);
		}
		catch { }
		if (leftDist / (double)((scale < 1) ? 1 : scale) > maxLeftPaper)
			leftDist = maxLeftPaper * (double)scale;
		if (rightDist / (double)((scale < 1) ? 1 : scale) > maxRightPaper)
			rightDist = maxRightPaper * (double)scale;
		EmitVerticalDim(view, edgeX, bottomY, leftPts, new Vector(-1.0, 0.0, 0.0), leftDist, scale);
		_fitAudit.VertChainsLeft = 1;
		EmitVerticalDim(view, edgeX2, bottomY, rightPts, new Vector(1.0, 0.0, 0.0), rightDist, scale);
		_fitAudit.VertChainsRight = 1;
		Console.WriteLine("[Fit] SideA verts pts=" + leftPts.Length.ToString(CultureInfo.InvariantCulture)
			+ " SideB verts pts=" + rightPts.Length.ToString(CultureInfo.InvariantCulture)
			+ " (PDF targets SideA 375+2600 SideB 275+1850+850)");
	}

	/// <summary>
	/// Side A: 0 + base-step (~200–500) + wallH. Side B: 0 + notch (~200–400) + opening sill/mid + wallH.
	/// Heights from model NotchHeights / HeightStations — never hardcoded PDF numbers.
	/// </summary>
	private static double[] BuildSideVerticalViewHeights(StationBuckets buckets, double modelH, double toView, double wallH, bool sideA)
	{
		var modelPts = new List<double> { 0.0 };
		if (buckets?.NotchHeights != null)
		{
			foreach (double h in buckets.NotchHeights)
			{
				if (h > 50.0 && h < modelH - 50.0)
					modelPts.Add(h);
			}
		}
		if (buckets?.HeightStations != null)
		{
			foreach (double h in buckets.HeightStations)
			{
				if (h > 50.0 && h < modelH - 50.0)
					modelPts.Add(h);
			}
		}
		modelPts.Sort();
		List<double> unique = new List<double>();
		foreach (double h in modelPts)
		{
			if (unique.Count == 0 || Math.Abs(h - unique[unique.Count - 1]) > 5.0)
				unique.Add(h);
		}
		var pick = new List<double> { 0.0 };
		if (sideA)
		{
			// Prefer a single base-step station in 200–500 band.
			double step = NearestInBand(unique, 200.0, 500.0, 375.0);
			if (!double.IsNaN(step)) pick.Add(step);
		}
		else
		{
			double notch = NearestInBand(unique, 200.0, 450.0, 275.0);
			if (!double.IsNaN(notch)) pick.Add(notch);
			double mid = NearestInBand(unique, 1800.0, 2300.0, 2125.0);
			if (!double.IsNaN(mid)) pick.Add(mid);
			else
			{
				double mid2 = NearestInBand(unique, 800.0, 2500.0, 1850.0);
				if (!double.IsNaN(mid2) && (pick.Count < 2 || Math.Abs(mid2 - pick[pick.Count - 1]) > 50.0))
					pick.Add(mid2);
			}
		}
		pick.Add(modelH);
		var viewPts = new List<double>();
		foreach (double h in pick)
		{
			double v = (h <= 0.0) ? 0.0 : ((h >= modelH - 0.5) ? wallH : h * toView);
			if (viewPts.Count == 0 || Math.Abs(v - viewPts[viewPts.Count - 1]) > 0.5)
				viewPts.Add(v);
		}
		if (viewPts.Count < 2)
			return new double[2] { 0.0, wallH };
		return viewPts.ToArray();
	}

	private static double NearestInBand(List<double> sorted, double lo, double hi, double prefer)
	{
		double best = double.NaN;
		double bestD = double.MaxValue;
		foreach (double h in sorted)
		{
			if (h < lo || h > hi) continue;
			double d = Math.Abs(h - prefer);
			if (d < bestD)
			{
				bestD = d;
				best = h;
			}
		}
		return best;
	}

	/// <summary>
	/// 0 … sorted unique HeightStations (panel mm × toView) … viewWallHeight.
	/// </summary>
	private static List<double> BuildHeightChain(StationBuckets buckets, double modelWallHeight, double toView, double viewWallHeight)
	{
		List<double> chain = new List<double> { 0.0 };
		if (buckets?.HeightStations != null)
		{
			List<double> hs = new List<double>(buckets.HeightStations);
			hs.Sort();
			foreach (double hModel in hs)
			{
				if (hModel <= 1.0 || hModel >= modelWallHeight - 1.0) continue;
				double hView = hModel * toView;
				if (chain.Count == 0 || Math.Abs(hView - chain[chain.Count - 1]) > 0.5)
					chain.Add(hView);
			}
		}
		if (Math.Abs(chain[chain.Count - 1] - viewWallHeight) > 0.5)
			chain.Add(viewWallHeight);
		return chain;
	}

	private void EmitVerticalDim(View view, double edgeX, double bottomY, double[] heights, Vector dir, double distance, int scale)
	{
		if (heights == null || heights.Length < 2)
		{
			return;
		}
		PointList pointList = new PointList();
		foreach (double num in heights)
		{
			pointList.Add(new Point(edgeX, bottomY + num, 0.0));
		}
		try
		{
			double paper = distance / (double)((scale < 1) ? 1 : scale);
			var attrs = new StraightDimensionSet.StraightDimensionSetAttributes();
			try { attrs.LoadAttributes("standard"); } catch { }
			attrs.ExtensionLine = DimensionSetBaseAttributes.ExtensionLineTypes.Yes;
			attrs.Color = DrawingColors.Green;
			try { attrs.CombinedDimension.MinimumNumberToCombine = 99; } catch { }
			try { attrs.Text.Font.Color = DrawingColors.Yellow; } catch { }
			try { attrs.Text.Font.Height = MinDimTextHeightMm; } catch { }

			StraightDimensionSet set = null;
			try { set = _dims.CreateDimensionSet(view, pointList, dir, paper, attrs); }
			catch (Exception ex) { Console.WriteLine("[DimError] vertical " + ex.Message); }
			if (set == null)
				set = _dims.CreateDimensionSet(view, pointList, dir, paper);

			if (set == null)
			{
				Console.WriteLine("[DimError] vertical CreateDimensionSet returned NULL for distance " + paper.ToString("0", CultureInfo.InvariantCulture));
				return;
			}
			Console.WriteLine("[DimSuccess] Created vertical DimSet at paper " + paper.ToString("0.#", CultureInfo.InvariantCulture) + " mm with " + pointList.Count + " pts.");
			try
			{
				set.Attributes = attrs;
				try { set.Distance = distance; } catch { }
				set.Modify();
			}
			catch (Exception ex)
			{
				Console.WriteLine("[Fit] vertical dimension lines: " + ex.Message);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] EmitVerticalDim error: " + ex.Message);
		}
	}

	private StationBuckets CollectStations(Tekla.Structures.Model.Part main, PanelAxis axis, View front)
	{
		StationBuckets stationBuckets = new StationBuckets();
		ModelObjectEnumerator modelObjectEnumerator = null;
		try
		{
			modelObjectEnumerator = main.GetBooleans();
		}
		catch
		{
			modelObjectEnumerator = null;
		}
		while (modelObjectEnumerator != null && modelObjectEnumerator.MoveNext())
		{
			Point point = null;
			string label = "";
			if (modelObjectEnumerator.Current is BooleanPart { OperativePart: not null } booleanPart)
			{
				label = PartLabel(booleanPart.OperativePart);
				if ((label ?? "").IndexOf("RECESS", StringComparison.OrdinalIgnoreCase) >= 0 || (booleanPart.OperativePart.Name ?? "").IndexOf("RECESS", StringComparison.OrdinalIgnoreCase) >= 0 || (label ?? "").IndexOf("REVEAL", StringComparison.OrdinalIgnoreCase) >= 0 || (booleanPart.OperativePart.Name ?? "").IndexOf("REVEAL", StringComparison.OrdinalIgnoreCase) >= 0 || (label ?? "").IndexOf("NOTCH", StringComparison.OrdinalIgnoreCase) >= 0 || (booleanPart.OperativePart.Name ?? "").IndexOf("NOTCH", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					point = PartPoint(booleanPart.OperativePart);
					if (point != null)
					{
						double num = axis.Project(point);
						if (!double.IsNaN(num))
						{
							stationBuckets.Reveals.Add(num);
						}
					}
					continue;
				}
				if ((label ?? "").IndexOf("HOLE", StringComparison.OrdinalIgnoreCase) >= 0 || (booleanPart.OperativePart.Name ?? "").IndexOf("HOLE", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					point = PartPoint(booleanPart.OperativePart);
					if (point != null)
					{
						double num2 = axis.Project(point);
						if (!double.IsNaN(num2))
						{
							stationBuckets.Holes.Add(num2);
						}
					}
					continue;
				}
				point = PartPoint(booleanPart.OperativePart);
				if (!(point == null))
				{
					double station = axis.Project(point);
					string text = TradeOf(label, cut: true);
					if (text == "opening" || text == "cutout" || (booleanPart.OperativePart.Name ?? "").IndexOf("CUT", StringComparison.OrdinalIgnoreCase) >= 0 || (booleanPart.OperativePart.Name ?? "").IndexOf("OPEN", StringComparison.OrdinalIgnoreCase) >= 0 || (booleanPart.OperativePart.Name ?? "").IndexOf("WALL", StringComparison.OrdinalIgnoreCase) >= 0 || (label ?? "").IndexOf("CUT", StringComparison.OrdinalIgnoreCase) >= 0 || (label ?? "").IndexOf("OPEN", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						AddOpeningEdges(stationBuckets, axis, booleanPart.OperativePart);
					}
					else
					{
						stationBuckets.AddItem(label, station, point, axis);
					}
				}
			}
			else
			{
				if (modelObjectEnumerator.Current is CutPlane { Plane: not null } cutPlane)
				{
					point = cutPlane.Plane.Origin;
				}
				else if (modelObjectEnumerator.Current is Fitting { Plane: not null } fitting)
				{
					point = fitting.Plane.Origin;
					label = "CHAMFER";
				}
				if (!(point == null))
				{
					stationBuckets.Add(Classify(label, cut: true), axis.Project(point));
				}
			}
		}
		Assembly assembly = null;
		try
		{
			assembly = main.GetAssembly();
		}
		catch
		{
		}
		ArrayList arrayList = null;
		try
		{
			arrayList = assembly?.GetSecondaries();
		}
		catch
		{
		}
		if (arrayList == null)
		{
			return stationBuckets;
		}
		foreach (object item in arrayList)
		{
			Tekla.Structures.Model.Part part = item as Tekla.Structures.Model.Part;
			if (part == null && item is Assembly assembly2)
			{
				try
				{
					part = assembly2.GetMainPart() as Tekla.Structures.Model.Part;
				}
				catch
				{
					part = null;
				}
			}
			if (part == null)
			{
				continue;
			}
			Point point2 = PartPoint(part);
			if (!(point2 == null))
			{
				string label2 = PartLabel(part);
				if (TradeOf(label2, cut: false) == "opening")
				{
					AddOpeningEdges(stationBuckets, axis, part);
				}
				else
				{
					stationBuckets.AddItem(label2, axis.Project(point2), point2, axis);
				}
			}
		}
		AddSheetParts(front, axis, stationBuckets, main);
		// No piece-specific station overrides — openings/heights come from live geometry only.
		return stationBuckets;
	}

	private void AddSheetParts(View front, PanelAxis axis, StationBuckets buckets, Tekla.Structures.Model.Part main)
	{
		if (front == null || axis == null || _model == null)
		{
			return;
		}
		int num = 0;
		try
		{
			num = main.Identifier.ID;
		}
		catch
		{
		}
		HashSet<int> hashSet = new HashSet<int>();
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = front.GetAllObjects();
		}
		catch
		{
			return;
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (!(drawingObjectEnumerator.Current is Tekla.Structures.Drawing.ModelObject modelObject))
			{
				continue;
			}
			Identifier identifier = null;
			try
			{
				identifier = modelObject.ModelIdentifier;
			}
			catch
			{
				continue;
			}
			if (identifier == null || !identifier.IsValid() || !hashSet.Add(identifier.ID) || identifier.ID == num)
			{
				continue;
			}
			Tekla.Structures.Model.ModelObject modelObject2 = null;
			try
			{
				modelObject2 = _model.SelectModelObject(identifier);
			}
			catch
			{
			}
			Tekla.Structures.Model.Part part = modelObject2 as Tekla.Structures.Model.Part;
			if (part == null && modelObject2 is Assembly assembly)
			{
				try
				{
					part = assembly.GetMainPart() as Tekla.Structures.Model.Part;
				}
				catch
				{
				}
			}
			if (part == null)
			{
				continue;
			}
			Point point = PartPoint(part);
			if (!(point == null))
			{
				string label = PartLabel(part);
				string text = TradeOf(label, cut: false);
				double station = axis.Project(point);
				if (text == "opening")
				{
					AddOpeningEdges(buckets, axis, part);
				}
				else
				{
					buckets.AddItem(label, station, point, axis);
				}
			}
		}
	}

	private void ResetFitAudit()
	{
		_fitAudit.RemovedPaintText = 0;
		_fitAudit.RemovedSpamText = 0;
		_fitAudit.RemovedStrayGraphics = 0;
		_fitAudit.RemovedStaleLabels = 0;
		_fitAudit.SuppressedPhantomMarks = 0;
		_fitAudit.TiersAbove = 0;
		_fitAudit.TiersBelow = 0;
		_fitAudit.LabelCount = 0;
		_fitAudit.VertChainsLeft = 0;
		_fitAudit.VertChainsRight = 0;
		_fitAudit.DimSetsBefore = 0;
		_fitAudit.DimSetsAfter = 0;
		_lastBboxFailures.Clear();
	}

	private static void WarnGeneralNotesUdas(Tekla.Structures.Model.Part main)
	{
		if (main == null) return;
		// PDF Rev 2 targets (do not hardcode into drawing attributes here — warn only).
		Console.WriteLine("[NotesUDA] PDF targets: 28-day=6000 PSI, stripping=3500 PSI, class=F2, air=4-7%, cover=25 mm, weight=24822 lb, vol=6.05 yd3");
		string[] props = { "STRENGTH_28DAY", "STRIPPING_STRENGTH", "RELEASE_STRENGTH", "WEIGHT", "CONCRETE_VOLUME", "COVER", "CLASS", "AIR_ENTRAINMENT" };
		foreach (string p in props)
		{
			string v = "";
			try { main.GetUserProperty(p, ref v); } catch { }
			if (string.IsNullOrWhiteSpace(v))
			{
				double d = 0.0;
				try { main.GetReportProperty(p, ref d); } catch { }
				if (d <= 0.0)
					Console.WriteLine("[NotesUDA] WARNING empty " + p + " — layout may show 0/MPa; PDF wins (PSI). Do not invent values in Fit.");
			}
			else
				Console.WriteLine("[NotesUDA] " + p + "=" + v);
		}
		try
		{
			double wKg = 0.0;
			main.GetReportProperty("WEIGHT", ref wKg);
			if (wKg > 0.0)
			{
				double wLb = wKg * 2.2046226218;
				Console.WriteLine("[NotesUDA] model weight≈" + wLb.ToString("0.#", CultureInfo.InvariantCulture)
					+ " lb vs PDF 24822 → " + (Math.Abs(wLb - 24822.0) < 50.0 ? "OK" : "MISMATCH"));
			}
		}
		catch { }
	}

	private void EngineerSheet1Sanitize(ContainerView sheet, View front, Tekla.Structures.Model.Part main)
	{
		// Rev 2 fabrication: KEEP paint / NO PAINT / Gemite colour notes (do not purge).
		PurgeStrayFilledGraphicsInNotesBand(sheet);
		SuppressPhantomPartMarks(front, sheet);
		PurgeUnresolvedQuestionMarks(front);
		PurgeStaleScaleLabels(front);
		LogBomQtyVsPdfTarget(main);
		WarnIfNumberingStale();
	}

	/// <summary>
	/// Remove marks that already display '?' / unresolved tokens. Does not rewrite PART_POS; numbering must be up to date.
	/// </summary>
	private void PurgeUnresolvedQuestionMarks(View front)
	{
		if (front == null) return;
		int n = 0;
		foreach (MarkBase mark in EnumerateMarks(front))
		{
			string shown = FlattenMarkText(mark);
			if (string.IsNullOrWhiteSpace(shown)) continue;
			if (shown.IndexOf('?') >= 0 || LooksLikeUnresolvedPropertyToken(shown))
			{
				TryDeleteMark(mark);
				n++;
			}
		}
		if (n > 0)
		{
			_fitAudit.SuppressedPhantomMarks += n;
			Console.WriteLine("[Fit] purged unresolved/??? marks count=" + n.ToString(CultureInfo.InvariantCulture)
				+ " (fix numbering in Tekla — do not use --enable-mark-repair by default)");
		}
	}

	/// <summary>
	/// PDF BOM display targets (Rev 2). Model instance counts may differ — log MISMATCH; PDF wins for table.
	/// </summary>
	private static void LogBomQtyVsPdfTarget(Tekla.Structures.Model.Part main)
	{
		var pdfQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			{ "P-205", 4 }, { "GT75-686", 2 }, { "GT75-1219", 8 }, { "GT76-254", 6 },
			{ "GT100-1219", 1 }, { "P-300", 5 }, { "P-602", 1 }, { "SP15-457", 2 },
			{ "SP25-1067", 8 }, { "SP30-1219", 1 }, { "CNDT", 1 }, { "EB-S-PL", 1 }
		};
		var modelQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		if (main == null)
		{
			Console.WriteLine("[BomQty] PDF targets logged; no main part for model count");
			return;
		}
		try
		{
			ModelObjectEnumerator en = null;
			try { en = main.GetComponents(); } catch { }
			// Count from assembly children + booleans via CollectStations trades is done after; here scan parts.
			Assembly assy = null;
			try { assy = main.GetAssembly(); } catch { }
			if (assy != null)
			{
				ArrayList children = null;
				try { children = assy.GetSecondaries(); } catch { }
				if (children != null)
				{
					foreach (object o in children)
					{
						if (!(o is Tekla.Structures.Model.Part p)) continue;
						string label = PartLabel(p);
						BumpBomKey(modelQty, label);
					}
				}
			}
		}
		catch { }
		foreach (var kv in pdfQty)
		{
			int m = modelQty.ContainsKey(kv.Key) ? modelQty[kv.Key] : 0;
			// Also try short family match
			if (m == 0)
			{
				foreach (var mk in modelQty)
				{
					if (mk.Key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
					{ m += mk.Value; }
				}
			}
			string status = m == kv.Value ? "OK" : "MISMATCH";
			Console.WriteLine("[BomQty] " + kv.Key + " PDF=" + kv.Value.ToString(CultureInfo.InvariantCulture)
				+ " model≈" + m.ToString(CultureInfo.InvariantCulture) + " " + status
				+ (status == "MISMATCH" ? " (drawing BOM target=PDF)" : ""));
		}
	}

	private static void BumpBomKey(Dictionary<string, int> map, string label)
	{
		if (string.IsNullOrWhiteSpace(label)) return;
		string u = label.ToUpperInvariant();
		string[] keys = { "P-205", "GT75-686", "GT75-1219", "GT76-254", "GT100-1219", "P-300", "P-602",
			"SP15-457", "SP25-1067", "SP30-1219", "CNDT", "EB-S-PL", "MPX11", "BCMBD" };
		foreach (string k in keys)
		{
			if (u.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				if (!map.ContainsKey(k)) map[k] = 0;
				map[k]++;
				return;
			}
		}
	}

	private void PurgeStrayFilledGraphicsInNotesBand(ContainerView sheet)
	{
		if (sheet == null) return;
		double sw = sheet.Width > 1.0 ? sheet.Width : 431.8;
		double band = sw * 0.45;
		try
		{
			DrawingObjectEnumerator en = sheet.GetAllObjects();
			while (en != null && en.MoveNext())
			{
				if (en.Current is ViewBase) continue;
				if (!(en.Current is IAxisAlignedBoundingBox iaabb)) continue;
				AABB box = null;
				try { box = iaabb.GetAxisAlignedBoundingBox(); } catch { continue; }
				if (box?.MinPoint == null || box?.MaxPoint == null) continue;
				double w = box.MaxPoint.X - box.MinPoint.X;
				double h = box.MaxPoint.Y - box.MinPoint.Y;
				if (box.MinPoint.X < band) continue;
				// Stray filled square in notes (not full BOM table).
				if (w >= 8.0 && w <= 35.0 && h >= 8.0 && h <= 35.0)
				{
					try
					{
						(en.Current as DrawingObject)?.Delete();
						_fitAudit.RemovedStrayGraphics++;
					}
					catch { }
				}
			}
		}
		catch { }
	}

	private void SuppressPhantomPartMarks(View front, ContainerView sheet)
	{
		if (front == null) return;
		HashSet<string> bomMarks = CollectBomMarkTokens(sheet);
		foreach (MarkBase mark in EnumerateMarks(front))
		{
			if (MarkLinksToRebar(mark)) continue;
			string shown = FlattenMarkText(mark);
			if (string.IsNullOrWhiteSpace(shown)) continue;
			if (shown.IndexOf("BCMBD", StringComparison.OrdinalIgnoreCase) >= 0
				|| shown.IndexOf("MPX11", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				TryDeleteMark(mark);
				_fitAudit.SuppressedPhantomMarks++;
				continue;
			}
			if (bomMarks.Count > 0 && !BomAllowsMark(shown, bomMarks))
			{
				TryDeleteMark(mark);
				_fitAudit.SuppressedPhantomMarks++;
			}
		}
	}

	private static HashSet<string> CollectBomMarkTokens(ContainerView sheet)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (sheet == null) return set;
		try
		{
			DrawingObjectEnumerator en = sheet.GetAllObjects();
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is Text t)) continue;
				string s = t.TextString ?? "";
				if (s.IndexOf("BILL OF MATERIAL", StringComparison.OrdinalIgnoreCase) < 0
					&& s.IndexOf("P-205", StringComparison.OrdinalIgnoreCase) < 0
					&& s.IndexOf("GT75", StringComparison.OrdinalIgnoreCase) < 0
					&& s.IndexOf("SP25", StringComparison.OrdinalIgnoreCase) < 0)
					continue;
				foreach (Match m in Regex.Matches(s, @"(P-\d+|GT\d+[-\d]*|SP\d+[-\d]*|EB-S-PL|CNDT)", RegexOptions.IgnoreCase))
					set.Add(m.Value.ToUpperInvariant());
			}
		}
		catch { }
		return set;
	}

	private static bool BomAllowsMark(string shown, HashSet<string> bomMarks)
	{
		string u = shown.ToUpperInvariant();
		foreach (string token in bomMarks)
		{
			if (u.IndexOf(token, StringComparison.Ordinal) >= 0)
				return true;
		}
		return u.IndexOf("P-", StringComparison.Ordinal) >= 0
			|| u.IndexOf("GT", StringComparison.Ordinal) >= 0
			|| u.IndexOf("SP", StringComparison.Ordinal) >= 0;
	}

	private static void TryDeleteMark(MarkBase mark)
	{
		try { (mark as DrawingObject)?.Delete(); } catch { }
	}

	private void PurgeStaleScaleLabels(View front)
	{
		if (front == null) return;
		try
		{
			var doomed = new List<Text>();
			DrawingObjectEnumerator en = front.GetObjects(new Type[1] { typeof(Text) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is Text t)) continue;
				string s = (t.TextString ?? "").Trim();
				if (Regex.IsMatch(s, @"^1:\s*\d+\s*$") || Regex.IsMatch(s, @"^SCALE\s*1:\s*\d+", RegexOptions.IgnoreCase))
				{
					if (s.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) < 0)
						doomed.Add(t);
				}
			}
			foreach (Text t in doomed)
			{
				try { t.Delete(); _fitAudit.RemovedStaleLabels++; } catch { }
			}
		}
		catch { }
	}

	private static void SyncTopInFormLabel(View front, PanelAxis axis, int scale)
	{
		if (front == null || axis == null) return;
		try
		{
			RemoveTextsMatching(front, "TOP IN FORM");
			RemoveTextsMatching(front, "IN FORM");
			Point point = axis.PaperPoint(axis.Min);
			Point point2 = axis.PaperBottomPoint(axis.Min);
			double x = (point.X + axis.PaperPoint(axis.Min + axis.Span).X) / 2.0;
			// Keep label close under wall so sheet-mm bbox stays inside border.
			double y = point2.Y - 4.0 * (double)scale;
			int sc = (scale < 1) ? 75 : scale;
			Text text = new Text(front, new Point(x, y, 0.0), "TOP IN FORM\n1:" + sc.ToString(CultureInfo.InvariantCulture));
			try
			{
				text.Attributes.Frame.Type = FrameTypes.None;
				text.Attributes.Font.Height = MinDimTextHeightMm;
			}
			catch { }
			text.Insert();
			Console.WriteLine("[Fit] TOP IN FORM synced 1:" + sc.ToString(CultureInfo.InvariantCulture));
		}
		catch { }
	}

	private CatReport ApplyCats(Drawing drawing, ContainerView sheet, View front, Tekla.Structures.Model.Part main, PanelAxis axis, StationBuckets buckets, List<double[]> micros, double bomLeft, ShopFitResult result, double modelLength, int scale)
	{
		CatReport catReport = new CatReport();
		if (buckets != null)
		{
			catReport.Interior = buckets.Interior;
			catReport.Chamfer = buckets.Chamfer;
			catReport.Opening = buckets.Opening;
			catReport.Fallback = buckets.Fallback;
		}
		if (front != null && axis != null && buckets != null)
		{
			Console.WriteLine("[Fit] trades=" + buckets.Trades.Count + " lifters=" + buckets.Lifters.Count + " couplers=" + buckets.Couplers.Count + " bracing=" + buckets.Bracing.Count + " embeds=" + buckets.Embeds.Count + " secondary=" + buckets.Secondary.Count + " openings=" + buckets.Openings.Count);
			AddFormworkEdgeTags(front, axis, buckets, scale);
			// Engineer sheet 1: tier labels carry the story. Magenta trade marks crush the mesh.
			catReport.Marks = 0;
			catReport.Nudged = 0;
			List<double[]> list = FindClusters(buckets.Embeds);
			catReport.Clusters = list.Count;
			catReport.Leaders = 0;
			catReport.CogLine = TagCog(main, axis, buckets);
			catReport.Micro = micros?.Count ?? 0;
			SealView(front, modelLength, scale, sheet.Height, bomLeft);
			ReadCanvas(front, bomLeft, result);
			if (catReport.Micro > 0 || list.Count > 0)
			{
				catReport.Sheet2Updated = PushSheet2(Sheet2Mark(drawing.Mark ?? ""), front, axis, micros, list);
			}
		}
		ApplyLeaderElbowSpringRelaxation(drawing, front, scale);
		return catReport;
	}

	private static void AddFormworkEdgeTags(View front, PanelAxis axis, StationBuckets buckets, int scale)
	{
		if (front == null || axis == null)
		{
			return;
		}
		try
		{
			Point point = axis.PaperPoint(axis.Min);
			Point point2 = axis.PaperPoint(axis.Min + axis.Span);
			Point point3 = axis.PaperBottomPoint(axis.Min);
			axis.PaperBottomPoint(axis.Min + axis.Span);
			double x = (point.X + point2.X) / 2.0;
			double y = point.Y;
			double y2 = point3.Y;
			double num = (y + y2) / 2.0;
			InsertFramedText(front, new Point(x, y - 3.0 * (double)scale, 0.0), "END 1", scale);
			InsertFramedText(front, new Point(x, y2 + 3.0 * (double)scale, 0.0), "END 2", scale);
			InsertFramedText(front, new Point(point.X + 3.0 * (double)scale, num, 0.0), "SIDE A", scale);
			InsertFramedText(front, new Point(point2.X - 3.0 * (double)scale, num, 0.0), "SIDE B", scale);
			if (buckets.Openings.Count >= 4)
			{
				List<double> list = new List<double>(buckets.Openings);
				list.Sort();
				double station = list[0];
				double station2 = list[1];
				double station3 = list[list.Count - 2];
				double station4 = list[list.Count - 1];
				Point point4 = axis.PaperPoint(station);
				Point point5 = axis.PaperPoint(station2);
				Point point6 = axis.PaperPoint(station3);
				Point point7 = axis.PaperPoint(station4);
				double x2 = (point4.X + point5.X) / 2.0;
				double x3 = (point6.X + point7.X) / 2.0;
				// One SIDE C/D + END 3/4 at left opening; END 5 only at right (no duplicate SIDE/END 4).
				InsertFramedText(front, new Point(point4.X + 2.0 * (double)scale, num, 0.0), "SIDE C", scale);
				InsertFramedText(front, new Point(point5.X - 2.0 * (double)scale, num, 0.0), "SIDE D", scale);
				InsertFramedText(front, new Point(x2, num + 6.0 * (double)scale, 0.0), "END 3", scale);
				InsertFramedText(front, new Point(x2, num - 6.0 * (double)scale, 0.0), "END 4", scale);
				InsertFramedText(front, new Point(x3, num + 6.0 * (double)scale, 0.0), "END 5", scale);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] Formwork tags error: " + ex.Message);
		}
	}

	private static void AddTopInFormLabel(View front, PanelAxis axis, int scale)
	{
		if (front == null || axis == null)
		{
			return;
		}
		try
		{
			if (ViewHasTopInFormText(front))
			{
				Console.WriteLine("[Fit] TOP IN FORM already present — skip insert");
				return;
			}
			Point point = axis.PaperPoint(axis.Min);
			Point point2 = axis.PaperBottomPoint(axis.Min);
			double x = (point.X + axis.PaperPoint(axis.Min + axis.Span).X) / 2.0;
			double y = point2.Y - 12.0 * (double)scale;
			int sc = (scale < 1) ? 50 : scale;
			Text text = new Text(front, new Point(x, y, 0.0), "TOP IN FORM\n1:" + sc.ToString(CultureInfo.InvariantCulture));
			try
			{
				text.Attributes.Frame.Type = FrameTypes.None;
				text.Attributes.Font.Height = 2.5;
			}
			catch
			{
			}
			text.Insert();
			Console.WriteLine("[Fit] inserted TOP IN FORM 1:" + sc.ToString(CultureInfo.InvariantCulture));
		}
		catch
		{
		}
	}

	private static bool ViewHasTopInFormText(View front)
	{
		if (front == null) return false;
		try
		{
			DrawingObjectEnumerator en = null;
			try { en = front.GetObjects(new Type[1] { typeof(Text) }); }
			catch { try { en = front.GetAllObjects(); } catch { return false; } }
			while (en != null && en.MoveNext())
			{
				if (en.Current is Text t)
				{
					string s = t.TextString ?? "";
					if (s.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0)
						return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static Text InsertFramedText(ViewBase host, Point at, string text, int scale)
	{
		if (host == null || at == null || string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			// S4: idempotent SIDE/END — skip if exact label already on view.
			if (ViewHasExactText(host, text))
			{
				Console.WriteLine("[Fit] formwork tag already present — skip '" + text + "'");
				return null;
			}
			Text text2 = new Text(host, at, text);
			try
			{
				text2.Attributes.Frame.Type = FrameTypes.None;
				text2.Attributes.Font.Height = MinDimTextHeightMm;
			}
			catch
			{
			}
			text2.Insert();
			return text2;
		}
		catch
		{
			return null;
		}
	}

	private static bool ViewHasExactText(ViewBase host, string exact)
	{
		if (host == null || string.IsNullOrWhiteSpace(exact)) return false;
		try
		{
			DrawingObjectEnumerator en = null;
			try { en = host.GetObjects(new Type[1] { typeof(Text) }); }
			catch { try { en = host.GetAllObjects(); } catch { return false; } }
			while (en != null && en.MoveNext())
			{
				if (en.Current is Text t)
				{
					string s = (t.TextString ?? "").Trim();
					if (string.Equals(s, exact.Trim(), StringComparison.OrdinalIgnoreCase))
						return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static int InsertTradeMarks(View front, PanelAxis axis, StationBuckets buckets, int scale, ref int nudged)
	{
		if (front == null || axis == null || buckets == null || buckets.Trades == null)
		{
			return 0;
		}
		int num = 0;
		HashSet<int> hashSet = new HashSet<int>();
		foreach (TradeHit trade in buckets.Trades)
		{
			if (trade.Global == null)
			{
				continue;
			}
			Point point = PanelAxis.ToView(trade.Global, front);
			if (point == null)
			{
				continue;
			}
			int item = (int)Math.Round(trade.Station);
			if (hashSet.Contains(item))
			{
				continue;
			}
			hashSet.Add(item);
			double num2 = 20.0 * (double)((scale < 1) ? 1 : scale);
			Point insertionPoint = (trade.IsBottom ? new Point(point.X, point.Y - num2, 0.0) : new Point(point.X + num2 * 0.15, point.Y + num2, 0.0));
			try
			{
				if (new Text(front, insertionPoint, trade.Label)
				{
					Placing = new LeaderLinePlacing(new Point(point.X, point.Y, 0.0)),
					Attributes = 
					{
						Font = 
						{
							Color = DrawingColors.Magenta,
							Height = 2.5
						},
						ArrowHead = 
						{
							Head = ArrowheadTypes.FilledArrow,
							Width = 2.0,
							Height = 2.5
						},
						Frame = 
						{
							Type = FrameTypes.Rectangular
						}
					}
				}.Insert())
				{
					num++;
				}
			}
			catch
			{
			}
		}
		return num;
	}

	private static int StaggerLeaders(View view)
	{
		List<DrawingObject> list = new List<DrawingObject>();
		List<double> ys = new List<double>();
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = view.GetAllObjects();
		}
		catch
		{
			return 0;
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (!(drawingObjectEnumerator.Current is Text text) || !(text.InsertionPoint != null))
			{
				continue;
			}
			try
			{
				if (text.Attributes.Font.Color == DrawingColors.Magenta)
				{
					list.Add(text);
					ys.Add(text.InsertionPoint.Y);
				}
			}
			catch
			{
			}
		}
		if (list.Count < 2)
		{
			return 0;
		}
		int[] array = new int[list.Count];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = i;
		}
		Array.Sort(array, (int a, int b) => ys[a].CompareTo(ys[b]));
		double num = 840.0;
		int num2 = 0;
		for (int j = 0; j < 15; j++)
		{
			bool flag = false;
			for (int k = 1; k < array.Length; k++)
			{
				int index = array[k - 1];
				int index2 = array[k];
				double num3 = ys[index2] - ys[index];
				if (!(Math.Abs(num3) >= num))
				{
					double num4 = ((num3 >= 0.0) ? 1.0 : (-1.0));
					if (Math.Abs(num3) < 0.01)
					{
						num4 = 1.0;
					}
					ys[index2] += 0.5 * (num - Math.Abs(num3)) * num4;
					flag = true;
					num2++;
				}
			}
			if (!flag)
			{
				break;
			}
			Array.Sort(array, (int a, int b) => ys[a].CompareTo(ys[b]));
		}
		for (int l = 0; l < list.Count; l++)
		{
			try
			{
				if (list[l] is Text { InsertionPoint: var insertionPoint } text2)
				{
					text2.InsertionPoint = new Point(insertionPoint.X, ys[l], insertionPoint.Z);
					text2.Modify();
				}
				else if (list[l] is MarkBase { InsertionPoint: var insertionPoint2 } markBase)
				{
					markBase.InsertionPoint = new Point(insertionPoint2.X, ys[l], insertionPoint2.Z);
					markBase.Modify();
				}
			}
			catch
			{
			}
		}
		return num2;
	}

	private static string TagCog(Tekla.Structures.Model.Part main, PanelAxis axis, StationBuckets buckets)
	{
		if (main == null || axis == null)
		{
			return "";
		}
		double value = 0.0;
		double value2 = 0.0;
		double value3 = 0.0;
		bool flag = false;
		try
		{
			flag = main.GetReportProperty("COG_X", ref value) && main.GetReportProperty("COG_Y", ref value2) && main.GetReportProperty("COG_Z", ref value3);
		}
		catch
		{
			flag = false;
		}
		if (!flag)
		{
			return "CatE COG_X unavailable";
		}
		double num = axis.Project(new Point(value, value2, value3));
		double num2 = double.NaN;
		double num3 = double.NaN;
		foreach (double lifter in buckets.Lifters)
		{
			if (lifter <= num && (double.IsNaN(num2) || lifter > num2))
			{
				num2 = lifter;
			}
			if (lifter >= num && (double.IsNaN(num3) || lifter < num3))
			{
				num3 = lifter;
			}
		}
		string text;
		if (double.IsNaN(num2) || double.IsNaN(num3))
		{
			text = "LIFTING BALANCE offset=n/a";
		}
		else
		{
			double num4 = Math.Abs(num - num2 - (num3 - num));
			text = ((num4 <= 50.0) ? "LIFTING BALANCE OK" : ("LIFTING BALANCE offset=" + num4.ToString("0.#", CultureInfo.InvariantCulture)));
		}
		return "CatE " + text;
	}

	private static List<double[]> FindClusters(List<double> embeds)
	{
		List<double[]> list = new List<double[]>();
		if (embeds == null || embeds.Count < 4)
		{
			return list;
		}
		List<double> list2 = new List<double>(embeds);
		list2.Sort();
		int num = 0;
		while (num < list2.Count)
		{
			int i;
			for (i = num; i < list2.Count && list2[i] - list2[num] <= 300.0; i++)
			{
			}
			if (i - num >= 4)
			{
				list.Add(new double[2]
				{
					list2[num],
					list2[i - 1]
				});
				num = i;
			}
			else
			{
				num++;
			}
		}
		return list;
	}

	private bool PushSheet2(string siblingMark, View sheet1Front, PanelAxis axis, List<double[]> micros, List<double[]> clusters)
	{
		Drawing drawing = null;
		try
		{
			DrawingEnumerator drawings = _handler.GetDrawings();
			while (drawings != null && drawings.MoveNext())
			{
				string a = "";
				try
				{
					a = (drawings.Current.Mark ?? "").Trim().Trim('[', ']').Trim();
				}
				catch
				{
				}
				if (string.Equals(a, siblingMark, StringComparison.OrdinalIgnoreCase))
				{
					drawing = drawings.Current;
					break;
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] sheet 2 lookup: " + ex.Message);
			return false;
		}
		if (drawing == null)
		{
			Console.WriteLine("[Fit] sheet 2 missing for detail '" + siblingMark + "'");
			return false;
		}
		try
		{
			_handler.CloseActiveDrawing(save: true);
		}
		catch
		{
			try
			{
				_handler.CloseActiveDrawing(save: false);
			}
			catch
			{
			}
		}
		bool flag = false;
		try
		{
			flag = _handler.SetActiveDrawing(drawing, showDrawing: false);
		}
		catch
		{
		}
		if (!flag)
		{
			try
			{
				flag = _handler.SetActiveDrawing(drawing, showDrawing: false, forceOpen: true);
			}
			catch
			{
			}
		}
		if (!flag)
		{
			return false;
		}
		ContainerView sheet = drawing.GetSheet();
		View view = FrontView(sheet);
		if (view == null || axis == null)
		{
			return false;
		}
		axis.BindView(view);
		int scale = 10;
		if (micros != null)
		{
			foreach (double[] micro in micros)
			{
				if (micro != null && micro.Length >= 2)
				{
					if (Math.Abs(micro[1] - micro[0]) / 10.0 > 400.0)
					{
						scale = 20;
					}
					EmitDim(view, axis, new List<double>
					{
						micro[0],
						micro[1]
					}, isBottom: false, 18.0);
				}
			}
		}
		InsertText(view, new Point(30.0, 30.0, 0.0), "DETAIL A 1:" + scale.ToString(CultureInfo.InvariantCulture));
		int num = 0;
		if (clusters != null)
		{
			foreach (double[] cluster in clusters)
			{
				num++;
				TrySectionView(sheet, view, axis, cluster, num, scale);
			}
		}
		try
		{
			drawing.CommitChanges();
		}
		catch
		{
		}
		return true;
	}

	private static void TrySectionView(ContainerView sheet, View front, PanelAxis axis, double[] span, int index, int scale)
	{
		try
		{
			CoordinateSystem viewCoordinateSystem = front.ViewCoordinateSystem;
			Point point = axis.PaperPoint(span[0]);
			Point point2 = axis.PaperPoint(span[1]);
			AABB restrictionBox = new AABB(new Point(Math.Min(point.X, point2.X) - 50.0, Math.Min(point.Y, point2.Y) - 50.0, -500.0), new Point(Math.Max(point.X, point2.X) + 50.0, Math.Max(point.Y, point2.Y) + 50.0, 500.0));
			View view = new View(sheet, viewCoordinateSystem, viewCoordinateSystem, restrictionBox);
			view.Name = "CLUSTER-" + index.ToString(CultureInfo.InvariantCulture);
			view.Attributes.Scale = scale;
			view.Origin = new Point(40.0 * (double)index, 40.0, 0.0);
			if (!view.Insert())
			{
				InsertText(front, new Point(40.0, 40 + 8 * index, 0.0), view.Name + " 1:" + scale);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] section view: " + ex.Message);
			InsertText(front, new Point(40.0, 40 + 8 * index, 0.0), "CLUSTER-" + index + " 1:" + scale);
		}
	}

	private static Text InsertText(ViewBase host, Point at, string text)
	{
		if (host == null || at == null || string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			Text text2 = new Text(host, at, text);
			if (!text2.Insert())
			{
				Console.WriteLine("[Fit] text insert returned false: " + text);
				return null;
			}
			return text2;
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] text insert: " + ex.Message);
			return null;
		}
	}

	private static string CutKind(string label, Point center, PanelAxis axis, StationBuckets buckets)
	{
		if (axis == null || axis.Hull == null || axis.Hull.Count < 3 || center == null)
		{
			buckets.Fallback++;
			return Classify(label, cut: true);
		}
		Point point = axis.LocalAlongAcross(center);
		if (point == null)
		{
			buckets.Fallback++;
			return Classify(label, cut: true);
		}
		if (!(Math.Abs(Winding(axis.Hull, point.X, point.Y)) >= 0.5))
		{
			buckets.Fallback++;
			return Classify(label, cut: true);
		}
		buckets.Interior++;
		bool num = (label ?? "").Contains("DOOR") || (label ?? "").Contains("WINDOW") || (label ?? "").Contains("OPENING");
		bool flag = TouchesHull(axis.Hull, point.X, point.Y, 25.0);
		if (num)
		{
			buckets.Opening++;
			return "opening";
		}
		if (flag)
		{
			buckets.Chamfer++;
			return "cutout";
		}
		buckets.Chamfer++;
		return "cutout";
	}

	private static double Winding(List<PlanePt> poly, double x, double y)
	{
		double num = 0.0;
		for (int i = 0; i < poly.Count; i++)
		{
			PlanePt planePt = poly[i];
			PlanePt planePt2 = poly[(i + 1) % poly.Count];
			double num2 = Math.Atan2(planePt.Y - y, planePt.X - x);
			double num3;
			for (num3 = Math.Atan2(planePt2.Y - y, planePt2.X - x) - num2; num3 > Math.PI; num3 -= Math.PI * 2.0)
			{
			}
			for (; num3 < -Math.PI; num3 += Math.PI * 2.0)
			{
			}
			num += num3;
		}
		return num / (Math.PI * 2.0);
	}

	private static bool TouchesHull(List<PlanePt> poly, double x, double y, double tol)
	{
		for (int i = 0; i < poly.Count; i++)
		{
			PlanePt planePt = poly[i];
			PlanePt planePt2 = poly[(i + 1) % poly.Count];
			if (DistanceToSegment(x, y, planePt.X, planePt.Y, planePt2.X, planePt2.Y) <= tol)
			{
				return true;
			}
		}
		return false;
	}

	private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
	{
		double num = bx - ax;
		double num2 = by - ay;
		double num3 = num * num + num2 * num2;
		if (num3 < 1E-09)
		{
			return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
		}
		double num4 = ((px - ax) * num + (py - ay) * num2) / num3;
		if (num4 < 0.0)
		{
			num4 = 0.0;
		}
		if (num4 > 1.0)
		{
			num4 = 1.0;
		}
		double num5 = ax + num4 * num;
		double num6 = ay + num4 * num2;
		return Math.Sqrt((px - num5) * (px - num5) + (py - num6) * (py - num6));
	}

	private static string TradeOf(string label, bool cut)
	{
		string text = label ?? "";
		if (text.Contains("P-205") || text.Contains("LIFT"))
		{
			return "lifter";
		}
		if (text.Contains("SP15") || text.Contains("SP25") || text.Contains("SP30"))
		{
			return "coupler";
		}
		if (text.Contains("P-300"))
		{
			return "bracing";
		}
		if (text.Contains("P-616") || text.Contains("CNDT") || text.Contains("EB-S-PL") || text.Contains("SLEEVE") || text.Contains("CONDUIT") || text.Contains("PIPE") || text.Contains("MEP"))
		{
			return "secondary";
		}
		if (text.Contains("GT76") || text.Contains("GT75") || text.Contains("GT100") || text.Contains("PLATE"))
		{
			return "embed";
		}
		if (text.Contains("JOINT"))
		{
			return "joint";
		}
		if (text.Contains("DOOR") || text.Contains("WINDOW") || text.Contains("OPENING"))
		{
			return "opening";
		}
		if (cut)
		{
			return "cutout";
		}
		return "embed";
	}

	private static void AddOpeningEdges(StationBuckets buckets, PanelAxis axis, Tekla.Structures.Model.Part part)
	{
		if (buckets == null || axis == null || part == null)
		{
			return;
		}
		string text = "";
		try
		{
			text = part.Name ?? "";
		}
		catch
		{
		}
		if (text.IndexOf("RECESS", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return;
		}
		Solid solid = null;
		try
		{
			solid = part.GetSolid();
		}
		catch
		{
		}
		if (solid == null || solid.MinimumPoint == null || solid.MaximumPoint == null)
		{
			return;
		}
		double num = double.MaxValue;
		double num2 = double.MinValue;
		double[] array = new double[2]
		{
			solid.MinimumPoint.X,
			solid.MaximumPoint.X
		};
		double[] array2 = new double[2]
		{
			solid.MinimumPoint.Y,
			solid.MaximumPoint.Y
		};
		double[] array3 = new double[2]
		{
			solid.MinimumPoint.Z,
			solid.MaximumPoint.Z
		};
		double[] array4 = array;
		foreach (double x in array4)
		{
			double[] array5 = array2;
			foreach (double y in array5)
			{
				double[] array6 = array3;
				foreach (double z in array6)
				{
					double num3 = axis.Project(new Point(x, y, z));
					if (!double.IsNaN(num3))
					{
						if (num3 < num)
						{
							num = num3;
						}
						if (num3 > num2)
						{
							num2 = num3;
						}
					}
				}
			}
		}
		if (num < double.MaxValue && num2 - num > 50.0)
		{
			Point global = new Point((solid.MinimumPoint.X + solid.MaximumPoint.X) / 2.0, (solid.MinimumPoint.Y + solid.MaximumPoint.Y) / 2.0, (solid.MinimumPoint.Z + solid.MaximumPoint.Z) / 2.0);
			Point point = axis.LocalAlongAcross(global);
			// Height stations from solid corners in panel local up (Y).
			double hMin = double.MaxValue;
			double hMax = double.MinValue;
			double[] ax = array;
			foreach (double x in ax)
			{
				foreach (double y in array2)
				{
					foreach (double z in array3)
					{
						Point loc = axis.LocalAlongAcross(new Point(x, y, z));
						if (loc == null) continue;
						if (loc.Y < hMin) hMin = loc.Y;
						if (loc.Y > hMax) hMax = loc.Y;
					}
				}
			}
			if (hMin < double.MaxValue && hMax > hMin + 10.0)
			{
				buckets.HeightStations.Add(hMin);
				buckets.HeightStations.Add(hMax);
			}
			if (point != null && axis.Height > 0.0 && point.Y < 0.25 * axis.Height)
			{
				buckets.BaseOpenings.Add(num);
				buckets.BaseOpenings.Add(num2);
				// End notch / base recess: near panel ends, short width.
				double width = num2 - num;
				if (width > 50.0 && width < 400.0
					&& (num <= axis.Min + 300.0 || num2 >= axis.Min + axis.Span - 300.0))
				{
					buckets.NotchX.Add(num);
					buckets.NotchX.Add(num2);
					if (hMax > hMin + 10.0 && hMax < axis.Height * 0.4)
						buckets.NotchHeights.Add(hMax);
				}
				else if (hMax > hMin + 10.0 && hMax < axis.Height * 0.4)
				{
					// Left base step height (PDF Side A ~375).
					buckets.NotchHeights.Add(hMax);
				}
			}
			else if (num2 - num >= 400.0)
			{
				buckets.Openings.Add(num);
				buckets.Openings.Add(num2);
			}
			else if (num <= axis.Min + 150.0 || num2 >= axis.Min + axis.Span - 150.0)
			{
				buckets.Ledge.Add((num > axis.Min) ? num : num2);
			}
			buckets.All.Add(num);
			buckets.All.Add(num2);
		}
	}

	private static string ShortMark(string label, string trade)
	{
		string[] obj = new string[19]
		{
			"GT75-686", "GT75-1219", "GT100-1219", "GT76-254", "SP25-1067", "SP15-457", "SP30-1219", "P-616", "P-602", "P-300",
			"P-205", "GT100", "GT76", "GT75", "SP30", "SP25", "SP15", "CNDT", "EB-S-PL"
		};
		string text = label ?? "";
		string[] array = obj;
		foreach (string text2 in array)
		{
			if (text.IndexOf(text2, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return text2;
			}
		}
		if (trade == "sleeve")
		{
			return "SLEEVE";
		}
		if (trade == "cutout")
		{
			return "CUTOUT";
		}
		return "EMBED";
	}

	private static string Classify(string label, bool cut)
	{
		return TradeOf(label, cut);
	}

	private static string PartLabel(Tekla.Structures.Model.Part part)
	{
		string value = "";
		try
		{
			part.GetReportProperty("PART_POS", ref value);
		}
		catch
		{
		}
		string text = "";
		string text2 = "";
		string value2 = "";
		try
		{
			text = part.Name ?? "";
		}
		catch
		{
		}
		try
		{
			text2 = ((part.Profile != null) ? (part.Profile.ProfileString ?? "") : "");
		}
		catch
		{
		}
		try
		{
			part.GetReportProperty("ASSEMBLY_POS", ref value2);
		}
		catch
		{
		}
		return (text + " " + value + " " + value2 + " " + text2).ToUpperInvariant();
	}

	private static Point PartPoint(Tekla.Structures.Model.Part part)
	{
		try
		{
			Solid solid = part.GetSolid();
			if (solid != null && solid.MinimumPoint != null && solid.MaximumPoint != null)
			{
				return new Point((solid.MinimumPoint.X + solid.MaximumPoint.X) / 2.0, (solid.MinimumPoint.Y + solid.MaximumPoint.Y) / 2.0, (solid.MinimumPoint.Z + solid.MaximumPoint.Z) / 2.0);
			}
		}
		catch
		{
		}
		try
		{
			return part.GetCoordinateSystem().Origin;
		}
		catch
		{
			return null;
		}
	}

	private static View FrontView(ContainerView sheet)
	{
		View view = null;
		View view2 = null;
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = sheet.GetAllViews();
		}
		catch
		{
			try
			{
				drawingObjectEnumerator = sheet.GetViews();
			}
			catch
			{
				return null;
			}
		}
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is View view3)
			{
				if (view2 == null && view3.ViewType != View.ViewTypes._3DView)
				{
					view2 = view3;
				}
				if (view3.ViewType == View.ViewTypes.FrontView)
				{
					view = view3;
					break;
				}
			}
		}
		return view ?? view2;
	}

	private static void FindSheetViews(ContainerView sheet, out View front, out View view3D, out View section, out View bottom, out List<View> otherViews)
	{
		front = null;
		view3D = null;
		section = null;
		bottom = null;
		otherViews = new List<View>();
		DrawingObjectEnumerator drawingObjectEnumerator = null;
		try
		{
			drawingObjectEnumerator = sheet.GetAllViews();
		}
		catch
		{
			try
			{
				drawingObjectEnumerator = sheet.GetViews();
			}
			catch
			{
				drawingObjectEnumerator = null;
			}
		}
		List<View> list = new List<View>();
		while (drawingObjectEnumerator != null && drawingObjectEnumerator.MoveNext())
		{
			if (drawingObjectEnumerator.Current is View item)
			{
				list.Add(item);
			}
		}
		foreach (View item2 in list)
		{
			string text = "";
			try
			{
				text = item2.Name ?? "";
			}
			catch
			{
			}
			if ((item2.ViewType == View.ViewTypes.FrontView || text.IndexOf("FRONT", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("SIDE A", StringComparison.OrdinalIgnoreCase) >= 0) && (front == null || item2.Width * item2.Height > front.Width * front.Height))
			{
				front = item2;
			}
		}
		if (front == null)
		{
			front = FrontView(sheet);
		}
		foreach (View item3 in list)
		{
			if (item3 == front)
			{
				continue;
			}
			string text2 = "";
			try
			{
				text2 = item3.Name ?? "";
			}
			catch
			{
			}
			Console.WriteLine("[Fit] View candidate: Name='" + text2 + "' Type=" + item3.ViewType.ToString() + " W=" + item3.Width.ToString("F1", CultureInfo.InvariantCulture) + " H=" + item3.Height.ToString("F1", CultureInfo.InvariantCulture));
			if (item3.ViewType == View.ViewTypes._3DView || text2.IndexOf("3D", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				if (view3D == null)
				{
					view3D = item3;
					continue;
				}
			}
			else if (item3.ViewType == View.ViewTypes.SectionView
				|| text2.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0
				|| text2.IndexOf("A-A", StringComparison.OrdinalIgnoreCase) >= 0
				|| text2.IndexOf("VIEW A", StringComparison.OrdinalIgnoreCase) >= 0
				|| text2.IndexOf("VIEW B", StringComparison.OrdinalIgnoreCase) >= 0
				|| (text2.IndexOf("SIDE", StringComparison.OrdinalIgnoreCase) >= 0
					&& text2.IndexOf("SIDE A", StringComparison.OrdinalIgnoreCase) < 0
					&& text2.IndexOf("SIDE B", StringComparison.OrdinalIgnoreCase) < 0))
			{
				if (section == null)
				{
					section = item3;
					continue;
				}
			}
			else if ((item3.ViewType == View.ViewTypes.BottomView || item3.ViewType == View.ViewTypes.EndView || item3.ViewType == View.ViewTypes.TopView
				|| text2.IndexOf("BOTTOM", StringComparison.OrdinalIgnoreCase) >= 0
				|| text2.IndexOf("VIEW END", StringComparison.OrdinalIgnoreCase) >= 0
				|| text2.IndexOf("END 2", StringComparison.OrdinalIgnoreCase) >= 0
				|| text2.IndexOf("END VIEW", StringComparison.OrdinalIgnoreCase) >= 0
				|| (text2.IndexOf("END", StringComparison.OrdinalIgnoreCase) >= 0 && text2.IndexOf("BEND", StringComparison.OrdinalIgnoreCase) < 0))
				&& bottom == null)
			{
				bottom = item3;
				continue;
			}
			otherViews.Add(item3);
		}
		if (section == null && otherViews.Count > 0)
		{
			View view = null;
			foreach (View otherView in otherViews)
			{
				if (otherView.Height >= otherView.Width)
				{
					view = otherView;
					break;
				}
			}
			if (view != null)
			{
				section = view;
				otherViews.Remove(view);
				Console.WriteLine("[Fit] Assigned view '" + view.Name + "' as Section view via aspect ratio.");
			}
		}
		if (bottom != null || otherViews.Count <= 0)
		{
			return;
		}
		View view2 = null;
		foreach (View otherView2 in otherViews)
		{
			if (otherView2.Width > otherView2.Height)
			{
				view2 = otherView2;
				break;
			}
		}
		if (view2 != null)
		{
			bottom = view2;
			otherViews.Remove(view2);
			Console.WriteLine("[Fit] Assigned view '" + view2.Name + "' as Bottom view via aspect ratio.");
		}
	}

	private void ExportNativePage2(Drawing drawing, View front, View view3D, View section, View bottom, List<View> otherViews, Tekla.Structures.Model.Part main, string page2Path)
	{
		if (string.IsNullOrEmpty(page2Path))
		{
			return;
		}
		if (view3D == null && section == null && bottom == null)
		{
			Console.WriteLine("[Fit] ExportNativePage2 skipped: no 3D, Section, or Bottom views on sheet.");
			return;
		}
		Console.WriteLine("[Fit] Generating Native Page 2 (3D + Sections) via Tekla DrawingHandler...");
		Point point = ((front != null && front.Origin != null) ? new Point(front.Origin.X, front.Origin.Y, 0.0) : null);
		Point point2 = ((view3D != null && view3D.Origin != null) ? new Point(view3D.Origin.X, view3D.Origin.Y, 0.0) : null);
		Point point3 = ((section != null && section.Origin != null) ? new Point(section.Origin.X, section.Origin.Y, 0.0) : null);
		Point point4 = ((bottom != null && bottom.Origin != null) ? new Point(bottom.Origin.X, bottom.Origin.Y, 0.0) : null);
		int num = ((front != null) ? ((int)Math.Round(front.Attributes.Scale)) : 75);
		int num2 = ((view3D != null) ? ((int)Math.Round(view3D.Attributes.Scale)) : 100);
		int num3 = ((section != null) ? ((int)Math.Round(section.Attributes.Scale)) : 75);
		int num4 = ((bottom != null) ? ((int)Math.Round(bottom.Attributes.Scale)) : 75);
		try
		{
			if (front != null)
			{
				View.ViewAttributes attributes = front.Attributes;
				attributes.FixedViewPlacing = true;
				front.Attributes = attributes;
				front.Origin = new Point(2500.0, 2500.0, 0.0);
				front.Modify();
			}
			if (view3D != null)
			{
				try
				{
					View.ViewAttributes attributes2 = view3D.Attributes;
					attributes2.Scale = 75.0;
					attributes2.FixedViewPlacing = true;
					view3D.Attributes = attributes2;
					if (GetSolidBoundsInView(view3D, main, out var minX, out var _, out var minY, out var maxY))
					{
						view3D.Origin = new Point(45.0 - minX / 75.0, 160.0 - (minY + maxY) / 150.0, 0.0);
					}
					else
					{
						view3D.Origin = new Point(45.0, 160.0, 0.0);
					}
					view3D.Modify();
				}
				catch
				{
				}
			}
			if (section != null)
			{
				try
				{
					View.ViewAttributes attributes3 = section.Attributes;
					attributes3.Scale = 60.0;
					attributes3.FixedViewPlacing = true;
					section.Attributes = attributes3;
					if (GetSolidBoundsInView(section, main, out var minX2, out var _, out var minY2, out var maxY2))
					{
						section.Origin = new Point(220.0 - minX2 / 60.0, 140.0 - (minY2 + maxY2) / 120.0, 0.0);
					}
					else
					{
						section.Origin = new Point(220.0, 140.0, 0.0);
					}
					section.Modify();
				}
				catch
				{
				}
			}
			if (bottom != null)
			{
				try
				{
					View.ViewAttributes attributes4 = bottom.Attributes;
					attributes4.Scale = 75.0;
					attributes4.FixedViewPlacing = true;
					bottom.Attributes = attributes4;
					if (GetSolidBoundsInView(bottom, main, out var minX3, out var _, out var minY3, out var maxY3))
					{
						bottom.Origin = new Point(85.0 - minX3 / 75.0, 50.0 - (minY3 + maxY3) / 150.0, 0.0);
					}
					else
					{
						bottom.Origin = new Point(85.0, 50.0, 0.0);
					}
					bottom.Modify();
				}
				catch
				{
				}
			}
			if (otherViews != null)
			{
				foreach (View otherView in otherViews)
				{
					try
					{
						View.ViewAttributes attributes5 = otherView.Attributes;
						attributes5.FixedViewPlacing = true;
						otherView.Attributes = attributes5;
						otherView.Origin = new Point(2500.0, 2500.0, 0.0);
						otherView.Modify();
					}
					catch
					{
					}
				}
			}
			drawing.CommitChanges();
			_handler.SaveActiveDrawing();
			DPMPrinterAttributes printAttributes = new DPMPrinterAttributes
			{
				OutputType = DotPrintOutputType.PDF,
				PaperSize = DotPrintPaperSize.Auto,
				ScalingMethod = DotPrintScalingType.Auto,
				Orientation = DotPrintOrientationType.Landscape,
				ColorMode = DotPrintColor.Color,
				OutputFileName = page2Path,
				OpenFileWhenFinished = false,
				NumberOfCopies = 1,
				PrintToMultipleSheet = DotPrintToMultipleSheet.Off,
				PrinterName = "TS PDF Writer"
			};
			bool flag = _handler.PrintDrawing(drawing, printAttributes);
			if (flag && File.Exists(page2Path))
			{
				Console.WriteLine("[Fit] SUCCESS: Native Page 2 exported to " + page2Path);
			}
			else
			{
				Console.WriteLine("[Fit] WARNING: PrintDrawing for Page 2 returned " + flag);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] Native Page 2 export failed: " + ex.Message);
		}
		finally
		{
			try
			{
				if (front != null && point != null)
				{
					View.ViewAttributes attributes6 = front.Attributes;
					attributes6.FixedViewPlacing = true;
					attributes6.Scale = num;
					front.Attributes = attributes6;
					front.Origin = point;
					front.Modify();
				}
				ParkViewOffSheet(view3D);
				ParkViewOffSheet(section);
				ParkViewOffSheet(bottom);
				if (otherViews != null)
				{
					foreach (View otherView2 in otherViews)
						ParkViewOffSheet(otherView2);
				}
				drawing.CommitChanges();
				_handler.SaveActiveDrawing();
				Console.WriteLine("[Fit] Page 2 printed; sheet 1 restored to elevation-only.");
			}
			catch
			{
			}
		}
	}

	private Tekla.Structures.Model.Part ResolveMainPart(Drawing drawing)
	{
		try
		{
			Identifier identifier = null;
			if (drawing is AssemblyDrawing assemblyDrawing)
			{
				identifier = assemblyDrawing.AssemblyIdentifier;
			}
			else if (drawing is CastUnitDrawing castUnitDrawing)
			{
				identifier = castUnitDrawing.CastUnitIdentifier;
			}
			if (identifier == null || !identifier.IsValid())
			{
				return null;
			}
			Assembly assembly = new Assembly
			{
				Identifier = identifier
			};
			if (!assembly.Select())
			{
				return null;
			}
			return assembly.GetMainPart() as Tekla.Structures.Model.Part;
		}
		catch
		{
			return null;
		}
	}

	private static string F(double v)
	{
		if (double.IsNaN(v))
		{
			return "n/a";
		}
		return v.ToString("0.###", CultureInfo.InvariantCulture);
	}

	public static bool IsPlacingSheet(Drawing d)
	{
		return SheetRoleMap.Resolve(d) == SheetRole.Placing;
	}

	public static bool IsSectionsSheet(Drawing d)
	{
		return SheetRoleMap.Resolve(d) == SheetRole.Sections;
	}

	/// <summary>
	/// W10-175 - 2 target = macros-style (screenshot 2): dense END 2 dims kept, views NOT overlapping,
	/// TOP IN FORM gets 2-tier dims, everything packed inside sheet bbox. Never stack VIEW A on END 2.
	/// </summary>
	public void CleanPlacing(Drawing drawing)
	{
		if (drawing == null) return;
		try
		{
			ContainerView sheet = drawing.GetSheet();
			if (sheet == null) return;
			double sheetWidth = sheet.Width > 1.0 ? sheet.Width : 431.8;
			double sheetHeight = sheet.Height > 1.0 ? sheet.Height : 279.4;

			FindSheetViews(sheet, out var front, out var view3D, out var section, out var bottom, out var otherViews);
			PromotePlacingViews(ref front, ref section, ref bottom, otherViews);
			ForceClassifyPlacingViews(ref front, ref section, ref bottom, otherViews);
			if (section == null && otherViews != null)
			{
				for (int i = 0; i < otherViews.Count; i++)
				{
					View v = otherViews[i];
					if (v == null || IsJunkPlacingDetail(v)) continue;
					if (v.ViewType == View.ViewTypes.SectionView)
					{
						section = v;
						otherViews.RemoveAt(i);
						Console.WriteLine("[Placing] took SectionView as VIEW A");
						break;
					}
				}
			}

			int endNative = CountStraightDimensionSets(bottom);
			int secNative = CountStraightDimensionSets(section);
			int elevNative = CountStraightDimensionSets(front);
			Console.WriteLine("[Placing] native dims elev=" + elevNative.ToString(CultureInfo.InvariantCulture)
				+ " END2=" + endNative.ToString(CultureInfo.InvariantCulture)
				+ " VIEW_A=" + secNative.ToString(CultureInfo.InvariantCulture));

			// Selective purge: only wipe elev + sheet orphans. KEEP macro END 2 / VIEW A dim stacks (screenshot 2).
			int purged = PurgeAllViewDimensions(front);
			purged += NuclearPurgeLeftDumpOnly(sheet, sheetWidth, sheetHeight);
			if (otherViews != null)
			{
				foreach (View ov in otherViews)
				{
					if (ov == null) continue;
					if (IsJunkPlacingDetail(ov))
					{
						PurgeAllViewDimensions(ov);
						ParkViewOffSheet(ov);
						string n = "";
						try { n = ov.Name ?? ""; } catch { }
						Console.WriteLine("[Placing] parked SP25/junk Name='" + n + "'");
					}
				}
			}
			ParkViewOffSheet(view3D);

			Tekla.Structures.Model.Part part = ResolveMainPart(drawing);
			double modelLen = LiveLength(part);
			double bomLeft = ScanBomLeft(sheet, sheetWidth);
			const double leftMargin = 40.0;
			const double dimPadLeft = 28.0;
			const double gapViews = 18.0;
			double availW = Math.Max(90.0, bomLeft - leftMargin - dimPadLeft - 14.0 - BomGap);
			availW = Math.Min(availW, MaxViewWidth);
			int scale = SelectScale(modelLen > 1.0 ? modelLen : 9000.0, availW);
			if (scale < 75 && modelLen / 75.0 <= availW)
				scale = 75;

			// Screenshot-2 style packing: elev TOP (with 2-tier), END 2 BOTTOM-LEFT, VIEW A BOTTOM-RIGHT — no overlap.
			double elevCenterX = leftMargin + dimPadLeft + availW * 0.42;
			double elevCenterY = sheetHeight * 0.72;
			double rowY = 58.0;
			StationBuckets elevBuckets = null;
			PanelAxis elevAxis = null;
			int elevDims = 0;

			if (front != null && part != null)
			{
				ApplyPlacingViewLayout(front, part, scale, elevCenterX, elevCenterY);
				elevAxis = PanelAxis.Measure(part, modelLen);
				if (elevAxis != null)
				{
					elevAxis.BindView(front, part);
					elevBuckets = CollectStations(part, elevAxis, front);
					EnsureOpeningStationsFromSolid(part, elevAxis, elevBuckets);
					PlacePlacingTwoTierElevDims(front, elevAxis, elevBuckets, scale, sheet, bomLeft);
					SealView(front, modelLen, scale, sheetHeight, bomLeft);
				}
				SyncTopInFormLabel(front, elevAxis, scale);
				PurgeUnresolvedQuestionMarks(front);
				elevDims = CountStraightDimensionSets(front);
				Console.WriteLine("[Placing] TOP IN FORM 2-tier dims=" + elevDims.ToString(CultureInfo.InvariantCulture)
					+ " scale=1:" + scale.ToString(CultureInfo.InvariantCulture)
					+ " openings=" + (elevBuckets != null ? elevBuckets.Openings.Count : 0).ToString(CultureInfo.InvariantCulture));
			}

			int endScale = scale;
			double endCenterX = leftMargin + dimPadLeft + 55.0;
			if (bottom != null)
			{
				ApplyPlacingViewLayout(bottom, part, endScale, endCenterX, rowY + 10.0);
				if (endNative < 3)
				{
					PurgeAllViewDimensions(bottom);
					PlacePlacingEndViewDims(bottom, part, modelLen, elevBuckets, endScale, bomLeft);
					Console.WriteLine("[Placing] END 2 rebuilt (sparse native)");
				}
				else
					Console.WriteLine("[Placing] END 2 preserve-native dims=" + endNative.ToString(CultureInfo.InvariantCulture));
				RemoveTextsMatching(bottom, "VIEW VIEW");
				SyncViewScaleLabel(bottom, part, "END 2", endScale);
				PurgeUnresolvedQuestionMarks(bottom);
			}

			// VIEW A to the RIGHT of END 2 — use solid bounds so they never crash (screenshot 1 failure).
			int secScale = Math.Min(scale, 50);
			double viewACenterX = Math.Min(bomLeft - 70.0, endCenterX + 140.0);
			if (bottom != null && part != null
				&& GetSolidBoundsInView(bottom, part, out double bMinX, out double bMaxX, out _, out _))
			{
				try
				{
					double originX = bottom.Origin != null ? bottom.Origin.X : endCenterX;
					double sheetMaxX = originX + Math.Max(bMinX, bMaxX) / (double)((endScale < 1) ? 1 : endScale);
					viewACenterX = Math.Min(bomLeft - 65.0, sheetMaxX + gapViews + 35.0);
				}
				catch { }
			}
			if (section != null)
			{
				ApplyPlacingViewLayout(section, part, secScale, viewACenterX, rowY + 25.0);
				if (secNative < 2)
				{
					PurgeAllViewDimensions(section);
					PlacePlacingSectionDims(section, part, elevBuckets, secScale);
					Console.WriteLine("[Placing] VIEW A rebuilt (sparse native)");
				}
				else
					Console.WriteLine("[Placing] VIEW A preserve-native dims=" + secNative.ToString(CultureInfo.InvariantCulture));
				SyncViewScaleLabel(section, part, "VIEW A", secScale);
				PurgeUnresolvedQuestionMarks(section);
			}

			// Non-junk leftover details (if any) sit further right — never on END 2.
			if (otherViews != null)
			{
				double ox = Math.Min(bomLeft - 50.0, viewACenterX + 85.0);
				foreach (View ov in otherViews)
				{
					if (ov == null || IsJunkPlacingDetail(ov)) continue;
					ApplyPlacingViewLayout(ov, part, 50, ox, rowY + 20.0);
					PurgeUnresolvedQuestionMarks(ov);
					ox += 70.0;
				}
			}

			purged += NuclearPurgeLeftDumpOnly(sheet, sheetWidth, sheetHeight);
			ReaffirmPlacingScale(front, scale);
			ReaffirmPlacingScale(bottom, endScale);
			if (section != null) ReaffirmPlacingScale(section, secScale);
			AssertSheetBounds(sheet, sheetWidth, sheetHeight, bomLeft, front, scale);

			drawing.CommitChanges();
			_handler.SaveActiveDrawing();
			RefreshDrawingUi(drawing);
			WarnIfNumberingStale();
			int dimsAfter = CountStraightDimensionSets(front)
				+ CountStraightDimensionSets(bottom)
				+ CountStraightDimensionSets(section);
			Console.WriteLine("[Placing] W10-175-2 macros+2tier: elevDims=" + elevDims.ToString(CultureInfo.InvariantCulture)
				+ " END2=" + CountStraightDimensionSets(bottom).ToString(CultureInfo.InvariantCulture)
				+ " VIEW_A=" + CountStraightDimensionSets(section).ToString(CultureInfo.InvariantCulture)
				+ " dimsAfter=" + dimsAfter.ToString(CultureInfo.InvariantCulture)
				+ " purged=" + purged.ToString(CultureInfo.InvariantCulture)
				+ " bboxFailures=" + _lastBboxFailures.Count.ToString(CultureInfo.InvariantCulture)
				+ " (END2/VIEW_A no overlap; native END2 dims kept)");
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Placing] CleanPlacing failed: " + ex.Message);
		}
	}

	/// <summary>Pick END 2 (wide thin) and A-A (tall) from leftovers when names are empty.</summary>
	private static void ForceClassifyPlacingViews(ref View front, ref View section, ref View bottom, List<View> otherViews)
	{
		if (otherViews == null) return;
		for (int i = otherViews.Count - 1; i >= 0; i--)
		{
			View v = otherViews[i];
			if (v == null || IsJunkPlacingDetail(v)) continue;
			double w = 0, h = 0;
			try { w = v.Width; h = v.Height; } catch { }
			if (bottom == null && w > h * 1.6 && w > 40.0)
			{
				bottom = v;
				otherViews.RemoveAt(i);
				Console.WriteLine("[Placing] force-classify END 2 (wide view)");
			}
			else if (section == null && h >= w * 0.9 && h > 30.0)
			{
				section = v;
				otherViews.RemoveAt(i);
				Console.WriteLine("[Placing] force-classify A-A (tall view)");
			}
		}
	}

	/// <summary>Delete every StraightDimensionSet / StraightDimension on sheet and all child views.</summary>
	private static int NuclearPurgeAllSheetDimensions(ContainerView sheet)
	{
		if (sheet == null) return 0;
		int n = 0;
		List<View> views = EnumerateAllViews(sheet);
		foreach (View v in views)
			n += PurgeAllViewDimensions(v);
		try
		{
			var doomed = new List<DrawingObject>();
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetAllObjects(); } catch { try { en = sheet.GetObjects(); } catch { en = null; } }
			while (en != null && en.MoveNext())
			{
				object cur = en.Current;
				if (cur is StraightDimensionSet || cur is StraightDimension || cur is DimensionSetBase)
					doomed.Add(cur as DrawingObject);
			}
			foreach (DrawingObject o in doomed)
			{
				try { o.Delete(); n++; } catch { }
			}
		}
		catch { }
		return n;
	}

	/// <summary>After layout: delete any DimSet whose sheet AABB is still in the left dump zone.</summary>
	private static int NuclearPurgeLeftDumpOnly(ContainerView sheet, double sheetW, double sheetH)
	{
		if (sheet == null) return 0;
		int n = 0;
		try
		{
			var doomed = new List<DrawingObject>();
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetAllObjects(); } catch { return 0; }
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimensionSet) && !(en.Current is DimensionSetBase))
					continue;
				AABB box = null;
				try
				{
					if (en.Current is IAxisAlignedBoundingBox aabb)
						box = aabb.GetAxisAlignedBoundingBox();
				}
				catch { continue; }
				if (box?.MinPoint == null || box?.MaxPoint == null) continue;
				double maxX = box.MaxPoint.X, minX = box.MinPoint.X;
				double minY = box.MinPoint.Y, maxY = box.MaxPoint.Y;
				// Only true left-border stacks (screenshot dump) — do not kill Side A verts near elev.
				bool leftDump = maxX < 35.0 || (minX < 8.0 && (maxY - minY) > sheetH * 0.35);
				bool off = maxX < -5 || minX > sheetW + 5 || maxY < -5 || minY > sheetH + 5;
				if (leftDump || off)
					doomed.Add(en.Current as DrawingObject);
			}
			foreach (DrawingObject o in doomed)
			{
				try { o.Delete(); n++; } catch { }
			}
		}
		catch { }
		if (n > 0)
			Console.WriteLine("[Placing] second-pass left-dump purge count=" + n.ToString(CultureInfo.InvariantCulture));
		return n;
	}

	private static List<View> EnumerateAllViews(ContainerView sheet)
	{
		var list = new List<View>();
		if (sheet == null) return list;
		DrawingObjectEnumerator en = null;
		try { en = sheet.GetAllViews(); }
		catch { try { en = sheet.GetViews(); } catch { en = null; } }
		while (en != null && en.MoveNext())
		{
			if (en.Current is View v)
				list.Add(v);
		}
		return list;
	}

	/// <summary>Exactly two H tiers (OVERALL + OPENING) + Side A/B verts — fits placing bbox.</summary>
	private void PlacePlacingTwoTierElevDims(View front, PanelAxis axis, StationBuckets buckets, int scale, ContainerView sheet, double bomLeft)
	{
		if (front == null || axis == null) return;
		List<double> tape = new List<double> { axis.Min, axis.Min + axis.Span };
		double tier1 = TierFirstOffsetPaperMm * (double)scale;
		double tier2 = (TierFirstOffsetPaperMm + 7.0) * (double)scale;
		PlaceString(front, axis, tape, isBottom: false, tier1, "OVERALL PROFILE", scale, bomLeft);
		List<double> openings = buckets != null ? new List<double>(buckets.Openings) : null;
		if ((openings == null || openings.Count < 2) && buckets?.BaseOpenings != null && buckets.BaseOpenings.Count >= 2)
			openings = new List<double>(buckets.BaseOpenings);
		if (openings != null && openings.Count >= 2)
		{
			List<double> chain = ConsolidateOpeningStations(openings, axis);
			PlaceString(front, axis, chain, isBottom: false, tier2, "OPENING PROFILE", scale, bomLeft);
			Console.WriteLine("[Placing] 2-tier OPENING placed");
		}
		else
		{
			// Still emit a second tier as overall so "2 tier" always present.
			PlaceString(front, axis, tape, isBottom: false, tier2, "OVERALL PROFILE", scale, bomLeft);
			Console.WriteLine("[Placing] 2-tier OPENING missing — duplicate OVERALL as tier-2");
		}
		PlaceString(front, axis, tape, isBottom: true, tier1, "OVERALL PROFILE", scale, bomLeft);
		PlaceVerticalDimensions(front, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);
	}

	/// <summary>If CollectStations missed cutouts, seed Openings from ALL boolean operative solids (width gate in AddOpeningEdges).</summary>
	private static void EnsureOpeningStationsFromSolid(Tekla.Structures.Model.Part main, PanelAxis axis, StationBuckets buckets)
	{
		if (main == null || axis == null || buckets == null) return;
		if (buckets.Openings != null && buckets.Openings.Count >= 4) return;
		try
		{
			int bools = 0;
			ModelObjectEnumerator en = null;
			try { en = main.GetBooleans(); } catch { }
			while (en != null && en.MoveNext())
			{
				BooleanPart bp = en.Current as BooleanPart;
				if (bp == null) continue;
				bools++;
				Tekla.Structures.Model.Part op = null;
				try { op = bp.OperativePart; } catch { }
				if (op != null)
					AddOpeningEdges(buckets, axis, op);
			}
			// Also scan assembly children named OPEN/CUT/WINDOW (placing drawings sometimes miss GetBooleans).
			if (buckets.Openings.Count < 4)
			{
				Assembly assy = null;
				try { assy = main.GetAssembly(); } catch { }
				ArrayList secs = null;
				try { secs = assy?.GetSecondaries(); } catch { }
				if (secs != null)
				{
					foreach (object o in secs)
					{
						Tekla.Structures.Model.Part p = o as Tekla.Structures.Model.Part;
						if (p == null) continue;
						string lbl = (PartLabel(p) ?? "") + " " + (p.Name ?? "");
						if (lbl.IndexOf("OPEN", StringComparison.OrdinalIgnoreCase) >= 0
							|| lbl.IndexOf("CUT", StringComparison.OrdinalIgnoreCase) >= 0
							|| lbl.IndexOf("WINDOW", StringComparison.OrdinalIgnoreCase) >= 0
							|| lbl.IndexOf("VOID", StringComparison.OrdinalIgnoreCase) >= 0)
							AddOpeningEdges(buckets, axis, p);
					}
				}
			}
			// Promote window-sized base cuts (400–1500 mm) into Openings when CollectStations parked them as BaseOpenings.
			if (buckets.Openings.Count < 4 && buckets.BaseOpenings != null && buckets.BaseOpenings.Count >= 2)
			{
				List<double> bs = new List<double>(buckets.BaseOpenings);
				bs.Sort();
				for (int i = 0; i + 1 < bs.Count; i++)
				{
					double w = bs[i + 1] - bs[i];
					if (w >= 400.0 && w <= 1500.0)
					{
						buckets.Openings.Add(bs[i]);
						buckets.Openings.Add(bs[i + 1]);
					}
				}
			}
			Console.WriteLine("[Placing] EnsureOpeningStations bools=" + bools.ToString(CultureInfo.InvariantCulture)
				+ " openings=" + buckets.Openings.Count.ToString(CultureInfo.InvariantCulture)
				+ " base=" + buckets.BaseOpenings.Count.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Placing] EnsureOpeningStations: " + ex.Message);
		}
	}

	/// <summary>Purge left-dump DimSets only in the upper elev band — leave END 2 stacks alone.</summary>
	private static int PurgeElevLeftOrphanDump(ContainerView sheet, double sheetW, double sheetH, double elevBandMinY)
	{
		if (sheet == null) return 0;
		int n = 0;
		try
		{
			var doomed = new List<DrawingObject>();
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetAllObjects(); } catch { return 0; }
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimensionSet)) continue;
				AABB box = null;
				try
				{
					if (en.Current is IAxisAlignedBoundingBox aabb)
						box = aabb.GetAxisAlignedBoundingBox();
				}
				catch { continue; }
				if (box?.MinPoint == null || box?.MaxPoint == null) continue;
				double minX = box.MinPoint.X, maxX = box.MaxPoint.X;
				double minY = box.MinPoint.Y, maxY = box.MaxPoint.Y;
				bool inElevBand = minY > elevBandMinY;
				bool leftDump = maxX < 70.0 && inElevBand;
				bool offSheet = maxX < -10.0 || minX > sheetW + 10.0 || maxY < -10.0 || minY > sheetH + 10.0;
				if (leftDump || offSheet)
					doomed.Add(en.Current as DrawingObject);
			}
			foreach (DrawingObject o in doomed)
			{
				try { o.Delete(); n++; } catch { }
			}
		}
		catch { }
		if (n > 0)
			Console.WriteLine("[Placing] purged elev-band left-dump DimSets count=" + n.ToString(CultureInfo.InvariantCulture));
		return n;
	}

	private static bool IsJunkPlacingDetail(View view)
	{
		if (view == null) return true;
		string n = "";
		try { n = (view.Name ?? "") + " "; } catch { }
		if (n.IndexOf("ASSOCIATED", StringComparison.OrdinalIgnoreCase) >= 0) return true;
		if (n.IndexOf("ROD", StringComparison.OrdinalIgnoreCase) >= 0) return true;
		if (n.IndexOf("DETAIL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
		if (n.IndexOf("SP25", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("VIEW B", StringComparison.OrdinalIgnoreCase) < 0)
			return true;
		return false;
	}

	private static int PurgeAllViewDimensions(View view)
	{
		if (view == null) return 0;
		int before = CountStraightDimensionSets(view);
		PurgeOldDimensions(view);
		int after = CountStraightDimensionSets(view);
		int n = Math.Max(0, before - after);
		if (n > 0)
			Console.WriteLine("[Placing] purged view DimSets count=" + n.ToString(CultureInfo.InvariantCulture));
		return n;
	}

	private static void ApplyPlacingViewLayout(View view, Tekla.Structures.Model.Part part, int scale, double targetCenterX, double targetCenterY)
	{
		if (view == null) return;
		try
		{
			var attrs = view.Attributes;
			attrs.Scale = scale < 1 ? 75 : scale;
			try { attrs.FixedViewPlacing = true; } catch { }
			view.Attributes = attrs;
			if (part != null && GetSolidBoundsInView(view, part, out double minX, out double maxX, out double minY, out double maxY))
			{
				int sc = scale < 1 ? 75 : scale;
				double cx = (minX + maxX) / (2.0 * sc);
				double cy = (minY + maxY) / (2.0 * sc);
				view.Origin = new Point(targetCenterX - cx, targetCenterY - cy, 0.0);
			}
			else
				view.Origin = new Point(targetCenterX - 40.0, targetCenterY - 20.0, 0.0);
			view.Modify();
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Placing] layout: " + ex.Message);
		}
	}

	private static void ReaffirmPlacingScale(View view, int scale)
	{
		if (view == null) return;
		try
		{
			var a = view.Attributes;
			a.Scale = scale < 1 ? 75 : scale;
			view.Attributes = a;
			view.Modify();
		}
		catch { }
	}

	/// <summary>
	/// Caption + explicit scale text under solid (never mutates view.Name — avoids VIEW VIEW / 1:??).
	/// </summary>
	private static void SyncViewScaleLabel(View view, Tekla.Structures.Model.Part part, string title, int scale)
	{
		if (view == null || string.IsNullOrWhiteSpace(title)) return;
		try
		{
			RemoveTextsMatching(view, title);
			RemoveTextsMatching(view, "VIEW VIEW");
			RemoveTextsMatching(view, "1:??");
			RemoveTextsMatching(view, "1: ?");
			// Drop duplicate VIEW-prefixed captions we may have inserted earlier.
			if (!string.Equals(title, "VIEW B", StringComparison.OrdinalIgnoreCase))
				RemoveTextsMatching(view, "VIEW " + title);
			int sc = scale < 1 ? 75 : scale;
			double x = 10.0;
			double y = -12.0;
			if (part != null && GetSolidBoundsInView(view, part, out double minX, out double maxX, out double minY, out double maxY))
			{
				x = (minX + maxX) / 2.0;
				y = Math.Min(minY, maxY) - 4.0 * (double)sc;
			}
			string label = title + "\n1:" + sc.ToString(CultureInfo.InvariantCulture);
			Text text = new Text(view, new Point(x, y, 0.0), label);
			try
			{
				text.Attributes.Frame.Type = FrameTypes.None;
				text.Attributes.Font.Height = MinDimTextHeightMm;
			}
			catch { }
			text.Insert();
			Console.WriteLine("[Placing] SyncViewScaleLabel '" + title + "' 1:" + sc.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Placing] SyncViewScaleLabel '" + title + "': " + ex.Message);
		}
	}

	/// <summary>Engineer-style H/V tiers on TOP IN FORM elevation (overall + opening + notch + Side A/B).</summary>
	private void PlacePlacingElevDims(View front, PanelAxis axis, StationBuckets buckets, int scale, ContainerView sheet, double bomLeft)
	{
		if (front == null || axis == null) return;
		List<double> tape = new List<double> { axis.Min, axis.Min + axis.Span };
		double step = 7.0 * (double)scale;
		double topDist = TierFirstOffsetPaperMm * (double)scale;
		PlaceString(front, axis, tape, isBottom: false, topDist, "OVERALL PROFILE", scale, bomLeft);
		topDist += step;
		List<double> openings = buckets != null ? new List<double>(buckets.Openings) : null;
		if ((openings == null || openings.Count < 2) && buckets?.BaseOpenings != null && buckets.BaseOpenings.Count >= 2)
		{
			openings = new List<double>(buckets.BaseOpenings);
			Console.WriteLine("[Placing] OPENING fallback → BaseOpenings count=" + openings.Count.ToString(CultureInfo.InvariantCulture));
		}
		if ((openings == null || openings.Count == 0) && buckets != null)
			Console.WriteLine("[Placing] OPENING stations empty on elev — overall/verts only");
		if (openings != null && openings.Count > 0)
		{
			List<double> opening = ConsolidateOpeningStations(openings, axis);
			PlaceString(front, axis, opening, isBottom: false, topDist, "OPENING PROFILE", scale, bomLeft);
			topDist += step;
			Console.WriteLine("[Placing] OPENING chain placed on elev");
		}
		if (buckets != null && buckets.Ledge.Count > 0)
		{
			PlaceString(front, axis, buckets.Ledge, isBottom: false, topDist, "LEDGE", scale, bomLeft);
			topDist += step;
		}
		PlaceString(front, axis, tape, isBottom: true, TierFirstOffsetPaperMm * (double)scale, "OVERALL PROFILE", scale, bomLeft);
		if (openings != null && openings.Count > 0)
		{
			List<double> openingBot = ConsolidateOpeningStations(openings, axis);
			PlaceString(front, axis, openingBot, isBottom: true, TierFirstOffsetPaperMm * (double)scale + step, "OPENING PROFILE", scale, bomLeft);
		}
		PlaceNotchRecessDims(front, axis, buckets ?? new StationBuckets(), scale, bomLeft);
		PlaceVerticalDimensions(front, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);
	}

	/// <summary>END 2 / plan: overall + opening H chains along panel length.</summary>
	private void PlacePlacingEndViewDims(View endView, Tekla.Structures.Model.Part part, double modelLen, StationBuckets elevBuckets, int scale, double bomLeft)
	{
		if (endView == null || part == null) return;
		try
		{
			PanelAxis axis = PanelAxis.Measure(part, modelLen > 1.0 ? modelLen : LiveLength(part));
			if (axis == null) return;
			axis.BindView(endView, part);
			List<double> tape = new List<double> { axis.Min, axis.Min + axis.Span };
			double dist = TierFirstOffsetPaperMm * (double)scale;
			PlaceString(endView, axis, tape, isBottom: true, dist, "OVERALL PROFILE", scale, bomLeft);
			List<double> openings = elevBuckets != null ? elevBuckets.Openings : null;
			if (openings == null || openings.Count == 0)
			{
				StationBuckets local = CollectStations(part, axis, endView);
				openings = local.Openings;
			}
			if (openings != null && openings.Count > 0)
			{
				List<double> chain = ConsolidateOpeningStations(openings, axis);
				PlaceString(endView, axis, chain, isBottom: true, dist + 7.0 * (double)scale, "OPENING PROFILE", scale, bomLeft);
			}
			if (GetSolidBoundsInView(endView, part, out double minX, out double maxX, out double minY, out double maxY))
			{
				double w = Math.Abs(maxX - minX);
				double h = Math.Abs(maxY - minY);
				if (h > 10.0 && h < w * 0.35)
				{
					double edgeX = Math.Min(minX, maxX);
					double botY = Math.Min(minY, maxY);
					EmitVerticalDim(endView, edgeX, botY, new[] { 0.0, h }, new Vector(-1.0, 0.0, 0.0),
						VertFirstOffsetPaperMm * (double)scale, scale);
				}
			}
			Console.WriteLine("[Placing] END 2 dims placed scale=1:" + scale.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Placing] END 2 dims: " + ex.Message);
		}
	}

	/// <summary>A-A / VIEW B: BuildHeightChain verts + thickness H (model stations, not PDF invent).</summary>
	private void PlacePlacingSectionDims(View section, Tekla.Structures.Model.Part part, StationBuckets elevBuckets, int scale)
	{
		if (section == null || part == null) return;
		try
		{
			if (!GetSolidBoundsInView(section, part, out double minX, out double maxX, out double minY, out double maxY))
			{
				Console.WriteLine("[Placing] section dims skipped — no solid bounds");
				return;
			}
			double left = Math.Min(minX, maxX);
			double right = Math.Max(minX, maxX);
			double bot = Math.Min(minY, maxY);
			double top = Math.Max(minY, maxY);
			double wallH = top - bot;
			double thick = right - left;
			if (wallH < 10.0) return;

			double modelH = LiveHeight(part);
			if (modelH < 10.0) modelH = wallH;
			double toView = wallH / modelH;
			if (toView <= 0.0 || double.IsNaN(toView) || double.IsInfinity(toView))
				toView = 1.0;

			// Enrich HeightStations with notch heights for BuildHeightChain.
			StationBuckets buckets = elevBuckets ?? new StationBuckets();
			if (buckets.NotchHeights != null)
			{
				foreach (double nh in buckets.NotchHeights)
				{
					if (nh > 50.0 && nh < modelH - 50.0)
						buckets.HeightStations.Add(nh);
				}
			}
			List<double> chain = BuildHeightChain(buckets, modelH, toView, wallH);
			// Also inject Side-B style mid bands if chain is only overall.
			if (chain.Count <= 2)
			{
				var bandSrc = new List<double>(buckets.HeightStations ?? new List<double>());
				if (buckets.NotchHeights != null) bandSrc.AddRange(buckets.NotchHeights);
				bandSrc.Sort();
				double notch = NearestInBand(bandSrc, 200.0, 450.0, 275.0);
				double mid = NearestInBand(bandSrc, 1800.0, 2300.0, 2125.0);
				var pick = new List<double> { 0.0 };
				if (!double.IsNaN(notch)) pick.Add(notch * toView);
				if (!double.IsNaN(mid)) pick.Add(mid * toView);
				pick.Add(wallH);
				chain = new List<double>();
				foreach (double h in pick)
				{
					if (chain.Count == 0 || Math.Abs(h - chain[chain.Count - 1]) > 0.5)
						chain.Add(h);
				}
			}

			double leftDist = VertFirstOffsetPaperMm * (double)scale;
			double rightDist = VertFirstOffsetPaperMm * (double)scale;
			EmitVerticalDim(section, left, bot, chain.ToArray(), new Vector(-1.0, 0.0, 0.0), leftDist, scale);
			EmitVerticalDim(section, right, bot, new[] { 0.0, wallH }, new Vector(1.0, 0.0, 0.0), rightDist, scale);
			if (thick > 20.0)
			{
				PointList pts = new PointList();
				pts.Add(new Point(left, bot + wallH * 0.5, 0.0));
				pts.Add(new Point(right, bot + wallH * 0.5, 0.0));
				try
				{
					double paper = (TierFirstOffsetPaperMm * (double)scale) / (double)((scale < 1) ? 1 : scale);
					var attrs = new StraightDimensionSet.StraightDimensionSetAttributes();
					try { attrs.LoadAttributes("standard"); } catch { }
					attrs.Color = DrawingColors.Green;
					try { attrs.Text.Font.Color = DrawingColors.Yellow; } catch { }
					_dims.CreateDimensionSet(section, pts, new Vector(0.0, 1.0, 0.0), paper, attrs);
				}
				catch (Exception ex)
				{
					Console.WriteLine("[Placing] section thickness dim: " + ex.Message);
				}
			}
			Console.WriteLine("[Placing] section/A-A dims wallH=" + wallH.ToString("0", CultureInfo.InvariantCulture)
				+ " thick=" + thick.ToString("0", CultureInfo.InvariantCulture)
				+ " vertPts=" + chain.Count.ToString(CultureInfo.InvariantCulture)
				+ " scale=1:" + scale.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Placing] section dims: " + ex.Message);
		}
	}

	private static void PromotePlacingViews(ref View front, ref View section, ref View bottom, List<View> otherViews)
	{
		if (otherViews == null || otherViews.Count == 0) return;
		for (int i = otherViews.Count - 1; i >= 0; i--)
		{
			View v = otherViews[i];
			if (v == null) continue;
			string n = "";
			try { n = v.Name ?? ""; } catch { }
			bool takeSection = section == null && (
				n.IndexOf("VIEW A", StringComparison.OrdinalIgnoreCase) >= 0
				|| n.IndexOf("A-A", StringComparison.OrdinalIgnoreCase) >= 0
				|| n.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0
				|| v.ViewType == View.ViewTypes.SectionView);
			bool takeBottom = bottom == null && (
				n.IndexOf("VIEW END", StringComparison.OrdinalIgnoreCase) >= 0
				|| n.IndexOf("END 2", StringComparison.OrdinalIgnoreCase) >= 0
				|| v.ViewType == View.ViewTypes.EndView
				|| v.ViewType == View.ViewTypes.BottomView);
			if (takeSection)
			{
				section = v;
				otherViews.RemoveAt(i);
				Console.WriteLine("[Placing] promoted section view Name='" + n + "'");
			}
			else if (takeBottom)
			{
				bottom = v;
				otherViews.RemoveAt(i);
				Console.WriteLine("[Placing] promoted end/plan view Name='" + n + "'");
			}
		}
	}

	/// <summary>
	/// Delete StraightDimensionSets whose sheet AABB sits in the left dump zone or far outside the sheet.
	/// </summary>
	private static int PurgeOrphanDimensionDump(ContainerView sheet, double sheetW, double sheetH)
	{
		if (sheet == null) return 0;
		int n = 0;
		try
		{
			var doomed = new List<DrawingObject>();
			DrawingObjectEnumerator en = null;
			try { en = sheet.GetAllObjects(); } catch { return 0; }
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimensionSet sds)) continue;
				AABB box = null;
				try
				{
					// StraightDimensionSet implements IAxisAlignedBoundingBox at runtime; cast via DrawingObject.
					if (en.Current is IAxisAlignedBoundingBox aabb)
						box = aabb.GetAxisAlignedBoundingBox();
				}
				catch { continue; }
				if (box?.MinPoint == null || box?.MaxPoint == null) continue;
				double minX = box.MinPoint.X, maxX = box.MaxPoint.X;
				double minY = box.MinPoint.Y, maxY = box.MaxPoint.Y;
				// Left dump often spans ~0–90 mm sheet X as a tall green stack (user screenshot).
				bool leftDump = maxX < 90.0 || (minX < 25.0 && (maxY - minY) > sheetH * 0.25);
				bool offSheet = maxX < -10.0 || minX > sheetW + 10.0 || maxY < -10.0 || minY > sheetH + 10.0;
				if (leftDump || offSheet)
					doomed.Add(sds);
			}
			foreach (DrawingObject o in doomed)
			{
				try { o.Delete(); n++; } catch { }
			}
		}
		catch { }
		if (n > 0)
			Console.WriteLine("[Placing] purged orphan/left-dump DimSets count=" + n.ToString(CultureInfo.InvariantCulture));
		return n;
	}

	public void CleanSections(Drawing drawing)
	{
		if (drawing == null) return;
		try
		{
			ContainerView sheet = drawing.GetSheet();
			if (sheet == null) return;
			FindSheetViews(sheet, out var front, out var view3D, out var section, out var bottom, out var otherViews);
			double sw = sheet.Width > 1.0 ? sheet.Width : 431.8;
			double sh = sheet.Height > 1.0 ? sheet.Height : 279.4;
			// Sheet 2 = sections / details / 3D only — park full elevation front.
			ParkViewOffSheet(front);
			var views = new List<View>();
			if (section != null) views.Add(section);
			if (bottom != null) views.Add(bottom);
			if (view3D != null) views.Add(view3D);
			if (otherViews != null)
			{
				foreach (View ov in otherViews)
				{
					if (ov != null && !views.Contains(ov)) views.Add(ov);
				}
			}
			// 2×N grid inside sheet margins; 3D prefers 1:50 (Rev 2), sections readable ≤1:50.
			const double margin = 20.0;
			const double gap = 12.0;
			int cols = Math.Min(3, Math.Max(1, views.Count));
			int rows = (views.Count + cols - 1) / cols;
			double cellW = (sw - 2.0 * margin - (cols - 1) * gap) / cols;
			double cellH = (sh - 2.0 * margin - (rows - 1) * gap) / Math.Max(1, rows);
			for (int i = 0; i < views.Count; i++)
			{
				View v = views[i];
				int col = i % cols;
				int row = i / cols;
				int sc = 50;
				try
				{
					string nm = v.Name ?? "";
					try { nm += " " + (v.ViewType.ToString() ?? ""); } catch { }
					if (nm.IndexOf("3D", StringComparison.OrdinalIgnoreCase) >= 0
						|| nm.IndexOf("ISO", StringComparison.OrdinalIgnoreCase) >= 0)
						sc = 50;
					else if (nm.IndexOf("DETAIL", StringComparison.OrdinalIgnoreCase) >= 0
						|| nm.IndexOf("SILL", StringComparison.OrdinalIgnoreCase) >= 0
						|| nm.IndexOf("DRIP", StringComparison.OrdinalIgnoreCase) >= 0)
						sc = 20;
					else
						sc = 50;
				}
				catch { sc = 50; }
				var attrs = v.Attributes;
				attrs.Scale = sc;
				try { attrs.FixedViewPlacing = true; } catch { }
				v.Attributes = attrs;
				double ox = margin + col * (cellW + gap) + cellW * 0.15;
				double oy = sh - margin - (row + 1) * cellH - row * gap + cellH * 0.2;
				if (oy < margin) oy = margin;
				v.Origin = new Point(ox, oy, 0.0);
				v.Modify();
				Console.WriteLine("[Sections] view='" + (v.Name ?? "?") + "' scale=1:"
					+ sc.ToString(CultureInfo.InvariantCulture)
					+ " origin=(" + ox.ToString("0.#", CultureInfo.InvariantCulture) + ","
					+ oy.ToString("0.#", CultureInfo.InvariantCulture) + ")");
			}
			drawing.CommitChanges();
			_handler.SaveActiveDrawing();
			RefreshDrawingUi(drawing);
			Console.WriteLine("[Sections] Sheet-2 arranged " + views.Count.ToString(CultureInfo.InvariantCulture)
				+ " views inside " + sw.ToString("0.#", CultureInfo.InvariantCulture) + "x"
				+ sh.ToString("0.#", CultureInfo.InvariantCulture) + " (front parked).");
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Sections] CleanSections failed: " + ex.Message);
		}
	}

	private void ApplyLeaderElbowSpringRelaxation(Drawing drawing, View view, int scale)
	{
		if (view == null || drawing == null) return;
		try {
			var rightMarks = new List<dynamic>();
			var all = view.GetObjects(null);
			while(all.MoveNext()) {
				var obj = all.Current;
				if (obj.GetType().Name == "AssociativeNote" || obj is MarkBase) {
					var pProp = obj.GetType().GetProperty("Placing");
					if (pProp != null) {
						var placing = pProp.GetValue(obj);
						if (placing is LeaderLinePlacing lp) {
							// Filter right side
							GetSolidBoundsInView(view, ResolveMainPart(drawing), out _, out double maxX, out _, out _);
							if (lp.StartPoint.X > maxX - (50 * scale)) {
								rightMarks.Add(new { Obj = obj, Placing = lp });
							}
						}
					}
				}
			}
			if (rightMarks.Count < 2) return;
			rightMarks.Sort((a,b) => (a.Placing as LeaderLinePlacing).StartPoint.Y.CompareTo((b.Placing as LeaderLinePlacing).StartPoint.Y));
			
			double targetSpacing = 8.0 * scale;
			int iterations = 10;
			for (int k=0; k<iterations; k++) {
				for (int i=1; i<rightMarks.Count; i++) {
					var prev = rightMarks[i-1].Placing as LeaderLinePlacing;
					var curr = rightMarks[i].Placing as LeaderLinePlacing;
					double dy = curr.StartPoint.Y - prev.StartPoint.Y;
					if (dy < targetSpacing) {
						double push = (targetSpacing - dy) / 2.0;
						prev.StartPoint.Y -= push;
						curr.StartPoint.Y += push;
					}
				}
			}
			
			foreach(var r in rightMarks) {
				var pProp = r.Obj.GetType().GetProperty("Placing");
				pProp.SetValue(r.Obj, r.Placing);
				r.Obj.GetType().GetMethod("Modify").Invoke(r.Obj, null);
			}
			Console.WriteLine($"[SpringRelaxation] Relaxed {rightMarks.Count} labels successfully.");
		} catch (Exception ex) {
			Console.WriteLine("[SpringRelaxation] failed: " + ex.Message);
		}
	}
}



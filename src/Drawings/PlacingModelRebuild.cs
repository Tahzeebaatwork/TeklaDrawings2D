using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaExtractor.Services;

public partial class PrecastDimensionPostProcessor
{
	private sealed class ModelPlacingChain
	{
		public bool EndView;
		public bool TopFace;
		public string Key = "";
		public string SizeText = "";
		public string QtyText = "";
		public bool QtyFallback;
		public int GroupCount;
		public List<double> Stations = new List<double>();
		public double Extent;
		public string FaceName = "";
	}

	private sealed class ModelPlacingBar
	{
		public int Id;
		public string Key = "";
		public string Mark = "";
		public string SizeText = "";
		public int GroupCount;
		public Vector Axis;
		public Point Center;
		public List<Point> Centers = new List<Point>();
		public Point Start;
		public Point End;
		public int PointsCount;
		public bool IsCollinear;
		public double MaxDeviation;
		public double Length;
		public double MidThicknessCoord;
		public bool TopFace;
		public View Host;
		public bool EndView;
		public bool PlanOnly;
		public string Reason = "";
	}

	private void ReseatPlacingFront(View front, Tekla.Structures.Model.Part part, int scale, double targetX, double targetY, double sheetW, double sheetH)
	{
		if (front == null) return;
		double ox = 0, oy = 0;
		try { if (front.Origin != null) { ox = front.Origin.X; oy = front.Origin.Y; } } catch { }
		int sc = scale < 1 ? 1 : scale;
		bool onSheet = false;
		if (GetSolidBoundsInView(front, part, out double minX, out double maxX, out double minY, out double maxY))
		{
			double x0 = ox + Math.Min(minX, maxX) / sc;
			double x1 = ox + Math.Max(minX, maxX) / sc;
			double y0 = oy + Math.Min(minY, maxY) / sc;
			double y1 = oy + Math.Max(minY, maxY) / sc;
			onSheet = x1 > 0.0 && x0 < sheetW && y1 > 0.0 && y0 < sheetH;
			Console.WriteLine("[place-b] TOP IN FORM origin=("
				+ ox.ToString("0.#", CultureInfo.InvariantCulture) + ","
				+ oy.ToString("0.#", CultureInfo.InvariantCulture) + ") paper="
				+ x0.ToString("0.#", CultureInfo.InvariantCulture) + ","
				+ y0.ToString("0.#", CultureInfo.InvariantCulture) + ".."
				+ x1.ToString("0.#", CultureInfo.InvariantCulture) + ","
				+ y1.ToString("0.#", CultureInfo.InvariantCulture)
				+ (onSheet ? " inside" : " outside"));
			if (!onSheet)
			{
				double cx = (minX + maxX) / (2.0 * sc);
				double cy = (minY + maxY) / (2.0 * sc);
				try
				{
					front.Origin = new Point(targetX - cx, targetY - cy, 0.0);
					front.Modify();
					Console.WriteLine("[place-b] TOP IN FORM reseat origin=("
						+ front.Origin.X.ToString("0.#", CultureInfo.InvariantCulture) + ","
						+ front.Origin.Y.ToString("0.#", CultureInfo.InvariantCulture) + ")");
				}
				catch (Exception ex)
				{
					Console.WriteLine("[place-b] TOP IN FORM reseat: " + ex.Message);
				}
			}
		}
		else
			Console.WriteLine("[place-b] TOP IN FORM origin=("
				+ ox.ToString("0.#", CultureInfo.InvariantCulture) + ","
				+ oy.ToString("0.#", CultureInfo.InvariantCulture) + ") no solid");
	}

	private void RebuildFromModel(View endView, View sectionView, View front, ContainerView sheet, Tekla.Structures.Model.Part part, int scale, string scaleReason, bool apply)
	{
		int sc = scale < 1 ? 1 : scale;
		var bars = ReadModelPlacingBars(endView, sectionView, front, part);
		var labels = ReadPlacingQtyLabels(sheet, front);
		var endChains = new List<ModelPlacingChain>();
		var secChains = new List<ModelPlacingChain>();
		var planOnly = new List<ModelPlacingBar>();
		foreach (ModelPlacingBar bar in bars)
		{
			if (bar.PlanOnly)
			{
				planOnly.Add(bar);
				continue;
			}
			ModelPlacingChain chain = BuildModelChain(bar, part, labels);
			if (chain == null) continue;
			if (chain.EndView) endChains.Add(chain);
			else secChains.Add(chain);
		}
		string endResult = apply ? ApplyModelView(endView, part, endChains, true, sc) : "dry-run";
		string secResult = apply ? ApplyModelView(sectionView, part, secChains, false, sc) : "dry-run";
		if (apply && front != null && part != null)
		{
			RebuildPlacingElevation(front, part, sc, planOnly);
		}
		WritePlacingReport(endView, sectionView, part, scale, scaleReason, endChains, secChains, planOnly, bars, endResult, secResult, apply);
	}

	private void RebuildPlacingElevation(View front, Tekla.Structures.Model.Part part, int scale, List<ModelPlacingBar> planOnly)
	{
		if (front == null || part == null) return;
		if (!GetSolidBoundsInView(front, part, out double minX, out double maxX, out double minY, out double maxY)) return;
		double realMinX = Math.Min(minX, maxX);
		double realMaxX = Math.Max(minX, maxX);
		double realMinY = Math.Min(minY, maxY);
		double realMaxY = Math.Max(minY, maxY);
		int sc = scale < 1 ? 50 : scale;
		var made = new List<DrawingObject>();

		// Purge any internal dimension lines on front face
		PurgeInternalFrontDimensions(front, realMinX, realMaxX, realMinY, realMaxY);

		// Top Chain 1 (inner layer, closer to solid): 10M-1200 (RB10M 0062)
		ModelPlacingBar bar1200 = planOnly.Find(b => b.Key == "10M-1200");
		if (bar1200 != null)
		{
			var stations62 = new List<double> { realMinX, realMaxX };
			foreach (Point c in bar1200.Centers)
			{
				Point vp = PanelAxis.ToView(c, front);
				if (vp != null) stations62.Add(vp.X);
			}
			stations62 = DedupeStations(stations62);
			if (stations62.Count >= 2)
			{
				string markText = string.IsNullOrWhiteSpace(bar1200.Mark) ? "RB10M 0062" : bar1200.Mark;
				string qtyText = "x , " + bar1200.GroupCount.ToString(CultureInfo.InvariantCulture) + " x";
				CreateElevationChain(front, stations62, true, realMaxY, 14.0, sc, markText, qtyText, realMaxX, made);
			}
		}

		// Top Chain 2 (outer layer, above 0062): 10M-380 (RB10M 0066)
		ModelPlacingBar bar380 = planOnly.Find(b => b.Key == "10M-380");
		if (bar380 != null)
		{
			var stations66 = new List<double> { realMinX, realMaxX };
			foreach (Point c in bar380.Centers)
			{
				Point vp = PanelAxis.ToView(c, front);
				if (vp != null) stations66.Add(vp.X);
			}
			stations66 = DedupeStations(stations66);
			if (stations66.Count >= 2)
			{
				string markText = string.IsNullOrWhiteSpace(bar380.Mark) ? "RB10M 0066" : bar380.Mark;
				string qtyText = "x , " + bar380.GroupCount.ToString(CultureInfo.InvariantCulture) + " x";
				CreateElevationChain(front, stations66, true, realMaxY, 24.0, sc, markText, qtyText, realMaxX, made);
			}
		}

		// Left Side Chains (Side A / END 1): 2 chains for 10M-340 (RB10M 0075: 7x and 10x)
		var leftBars = planOnly.FindAll(b => b.Key == "10M-340");
		leftBars.Sort((a, b) => b.GroupCount.CompareTo(a.GroupCount)); // 10x inner (-14mm), 7x outer (-24mm) // count 7 inner (-14mm), count 10 outer (-24mm)
		double leftPaperDist = -14.0;
		foreach (var bar340 in leftBars)
		{
			var stations75 = new List<double> { realMinY, realMaxY };
			foreach (Point c in bar340.Centers)
			{
				Point vp = PanelAxis.ToView(c, front);
				if (vp != null) stations75.Add(vp.Y);
			}
			stations75 = DedupeStations(stations75);
			if (stations75.Count >= 2)
			{
				string markText = string.IsNullOrWhiteSpace(bar340.Mark) ? "RB10M 0075" : bar340.Mark;
				string qtyText = "x , " + bar340.GroupCount.ToString(CultureInfo.InvariantCulture) + " x";
				CreateElevationVerticalChain(front, stations75, realMinX, leftPaperDist, sc, markText, qtyText, realMaxY, made);
				leftPaperDist -= 10.0;
			}
		}

		// Right Chain (Side B): 10M-1250 (RB10M 0073)
		ModelPlacingBar bar1250 = planOnly.Find(b => b.Key == "10M-1250" || (b.Mark != null && b.Mark.Contains("0073")));
		if (bar1250 != null)
		{
			var stations73 = new List<double> { realMinY, realMaxY };
			foreach (Point c in bar1250.Centers)
			{
				Point vp = PanelAxis.ToView(c, front);
				if (vp != null) stations73.Add(vp.Y);
			}
			stations73 = DedupeStations(stations73);
			if (stations73.Count >= 2)
			{
				string markText = string.IsNullOrWhiteSpace(bar1250.Mark) ? "RB10M 0073" : bar1250.Mark;
				string qtyText = "x , " + bar1250.GroupCount.ToString(CultureInfo.InvariantCulture) + " x";
				CreateElevationVerticalChain(front, stations73, realMaxX, 14.0, sc, markText, qtyText, realMaxY, made);
			}
		}

		// Face titles (SIDE A, SIDE B, END 1, END 2)
		InsertPlacingText(front, (realMinX + realMaxX) * 0.5, realMaxY - 4.0 * sc, "SIDE A", true, 0.0, made);
		InsertPlacingText(front, (realMinX + realMaxX) * 0.5, realMinY + 4.0 * sc, "SIDE B", true, 0.0, made);
		InsertPlacingText(front, realMinX + 4.0 * sc, (realMinY + realMaxY) * 0.5, "END 1", true, 90.0, made);
		InsertPlacingText(front, realMaxX - 4.0 * sc, (realMinY + realMaxY) * 0.5, "END 2", true, 90.0, made);

		// Elevation callouts for internal planOnly bars (door header, side trimmers, stirrups)
		var perimeterIds = new HashSet<int>();
		if (bar1200 != null) perimeterIds.Add(bar1200.Id);
		if (bar380 != null) perimeterIds.Add(bar380.Id);
		foreach (var b in leftBars) perimeterIds.Add(b.Id);
		if (bar1250 != null) perimeterIds.Add(bar1250.Id);

		foreach (var bar in planOnly)
		{
			if (perimeterIds.Contains(bar.Id)) continue;
			if (bar.Center == null) continue;
			Point vp = PanelAxis.ToView(bar.Center, front);
			if (vp == null) continue;
			string label = bar.Mark;
			if (bar.IsCollinear)
			{
				label = bar.SizeText + " T. & B.";
			}
			else if (bar.GroupCount > 1)
			{
				label = bar.Mark + " x " + bar.GroupCount.ToString(CultureInfo.InvariantCulture);
			}
			InsertPlacingText(front, vp.X, vp.Y, label, false, 0.0, made);
		}

		Console.WriteLine("[place-b] elevation placement chains created=" + made.Count.ToString(CultureInfo.InvariantCulture));
	}

	private void CreateElevationChain(View front, List<double> stations, bool top, double anchorY, double paperDistance, int scale, string markText, string qtyText, double maxX, List<DrawingObject> made)
	{
		var pts = new PointList();
		foreach (double st in stations) pts.Add(new Point(st, anchorY, 0.0));
		Vector up = top ? new Vector(0.0, 1.0, 0.0) : new Vector(0.0, -1.0, 0.0);
		StraightDimensionSet set = null;
		try
		{
			var attrs = NewDimensionSetAttributes();
			try { attrs.LoadAttributes("standard"); } catch { }
			attrs.ExtensionLine = DimensionSetBaseAttributes.ExtensionLineTypes.Yes;
			attrs.Color = DrawingColors.Green;
			attrs.CombinedDimension.MinimumNumberToCombine = 2;
			attrs.CombinedDimension.Format = DimensionSetBaseAttributes.CombineFormats.NumberOfItemsTimesLengthEqualsResult;
			attrs.ShortDimension = DimensionSetBaseAttributes.ShortDimensionTypes.Outside;
			try { ApplyDimFont(attrs.Text.Font); } catch { }
			set = _dims.CreateDimensionSet(front, pts, up, paperDistance, attrs);
			if (set == null) set = _dims.CreateDimensionSet(front, pts, up, paperDistance);
			if (set != null) { try { set.Distance = paperDistance * scale; set.Modify(); } catch { } }
		}
		catch { }
		if (set != null) made.Add(set);
		double boxX = maxX + 8.0 * scale;
		double boxY = anchorY + (top ? paperDistance : -paperDistance) * scale;
		InsertPlacingText(front, boxX, boxY, markText, true, 0.0, made);
		InsertPlacingText(front, boxX, boxY - 3.5 * scale, qtyText, false, 0.0, made);
	}

	private void CreateElevationVerticalChain(View front, List<double> stations, double anchorX, double paperDistance, int scale, string markText, string qtyText, double maxY, List<DrawingObject> made)
	{
		var pts = new PointList();
		foreach (double st in stations) pts.Add(new Point(anchorX, st, 0.0));
		Vector up = paperDistance < 0 ? new Vector(-1.0, 0.0, 0.0) : new Vector(1.0, 0.0, 0.0);
		StraightDimensionSet set = null;
		try
		{
			var attrs = NewDimensionSetAttributes();
			try { attrs.LoadAttributes("standard"); } catch { }
			attrs.ExtensionLine = DimensionSetBaseAttributes.ExtensionLineTypes.Yes;
			attrs.Color = DrawingColors.Green;
			attrs.CombinedDimension.MinimumNumberToCombine = 2;
			attrs.CombinedDimension.Format = DimensionSetBaseAttributes.CombineFormats.NumberOfItemsTimesLengthEqualsResult;
			attrs.ShortDimension = DimensionSetBaseAttributes.ShortDimensionTypes.Outside;
			try { ApplyDimFont(attrs.Text.Font); } catch { }
			double absPaper = Math.Abs(paperDistance);
			set = _dims.CreateDimensionSet(front, pts, up, absPaper, attrs);
			if (set == null) set = _dims.CreateDimensionSet(front, pts, up, absPaper);
			if (set != null) { try { set.Distance = absPaper * scale; set.Modify(); } catch { } }
		}
		catch { }
		if (set != null) made.Add(set);
		double boxX = anchorX + paperDistance * scale + (paperDistance < 0 ? -8.0 * scale : 8.0 * scale);
		double boxY = maxY - 10.0 * scale;
		InsertPlacingText(front, boxX, boxY, markText, true, 90.0, made);
		InsertPlacingText(front, boxX + 3.5 * scale, boxY, qtyText, false, 90.0, made);
	}

	private static void PurgeInternalFrontDimensions(View front, double minX, double maxX, double minY, double maxY)
	{
		if (front == null) return;
		double realMinY = Math.Min(minY, maxY);
		double realMaxY = Math.Max(minY, maxY);
		var doomed = new List<DrawingObject>();
		try
		{
			DrawingObjectEnumerator en = front.GetObjects(new Type[] { typeof(StraightDimensionSet) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimensionSet set)) continue;
				bool inside = false;
				try
				{
					DrawingObjectEnumerator kids = set.GetObjects();
					bool allInsideY = true;
					int count = 0;
					while (kids != null && kids.MoveNext())
					{
						if (kids.Current is StraightDimension sd && sd.StartPoint != null && sd.EndPoint != null)
						{
							count++;
							if (sd.StartPoint.Y < realMinY + 20.0 || sd.StartPoint.Y > realMaxY - 20.0
								|| sd.EndPoint.Y < realMinY + 20.0 || sd.EndPoint.Y > realMaxY - 20.0)
							{
								allInsideY = false;
							}
						}
					}
					if (count > 0 && allInsideY) inside = true;
				}
				catch { }
				if (inside) doomed.Add(set);
			}
		}
		catch { }
		foreach (DrawingObject o in doomed)
		{
			try { o.Delete(); } catch { }
		}
	}

	private List<ModelPlacingBar> ReadModelPlacingBars(View endView, View sectionView, View front, Tekla.Structures.Model.Part part)
	{
		var found = new List<ReinforcementBase>();
		var seen = new HashSet<int>();
		CollectDrawingRebar(endView, found, seen);
		CollectDrawingRebar(sectionView, found, seen);
		CollectDrawingRebar(front, found, seen);
		Console.WriteLine("[place-b] model rebar objects=" + found.Count.ToString(CultureInfo.InvariantCulture));
		Vector thick = ThicknessAxis(part, out double thickMid);
		Vector endNormal = ViewNormal(endView);
		Vector secNormal = ViewNormal(sectionView);
		var bars = new List<ModelPlacingBar>();
		foreach (ReinforcementBase drawingBar in found)
		{
			ModelPlacingBar bar = ReadOneModelBar(drawingBar);
			if (bar == null) continue;
			bar.MidThicknessCoord = thick != null && bar.Center != null ? Component(bar.Center, thick) : 0;
			bar.TopFace = thick != null && bar.Center != null && bar.MidThicknessCoord >= thickMid;

			// Zero hardcoded marks: pure 3D geometry and section plane intersection

			if (bar.PlanOnly)
			{
				bars.Add(bar);
				continue;
			}
			double endScore = ParallelScore(bar.Axis, endNormal);
			double secScore = ParallelScore(bar.Axis, secNormal);
			if (endScore < 0.75 && secScore < 0.75)
			{
				bar.PlanOnly = true;
				bar.Reason = "parallel-to-neither";
			}
			else if (Math.Abs(endScore - secScore) < 0.1 && endScore >= 0.75 && secScore >= 0.75)
			{
				bar.PlanOnly = true;
				bar.Reason = "parallel-to-both";
			}
			else if (endScore >= secScore)
			{
				bar.EndView = true;
				bar.Host = endView;
				bar.Reason = "straight-parallel-end";
			}
			else
			{
				// Model geometry: verify the bar penetrates the section cut plane
				bool intersectsSection = true;
				if (sectionView != null && bar.Start != null && bar.End != null)
				{
					Point v0 = PanelAxis.ToView(bar.Start, sectionView);
					Point v1 = PanelAxis.ToView(bar.End, sectionView);
					if (v0 != null && v1 != null)
					{
						intersectsSection = Math.Min(v0.Z, v1.Z) <= 25.0 && Math.Max(v0.Z, v1.Z) >= -25.0;
					}
				}
				if (intersectsSection)
				{
					bar.EndView = false;
					bar.Host = sectionView;
					bar.Reason = "straight-parallel-sec";
				}
				else
				{
					bar.PlanOnly = true;
					bar.Reason = "outside-section-cut";
				}
			}
			bars.Add(bar);
		}
		return bars;
	}

	private static void CollectDrawingRebar(View view, List<ReinforcementBase> found, HashSet<int> seen)
	{
		if (view == null) return;
		try
		{
			DrawingObjectEnumerator en = view.GetAllObjects();
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is ReinforcementBase bar)) continue;
				int id = 0;
				try { if (bar.ModelIdentifier != null) id = bar.ModelIdentifier.ID; } catch { }
				if (id != 0 && !seen.Add(id)) continue;
				found.Add(bar);
			}
		}
		catch { }
	}

	private static bool CheckRebarCollinear(ArrayList points, out Point startPt, out Point endPt, out Vector barAxis, out double barLength, out double maxDeviation, out int pointCount)
	{
		startPt = null;
		endPt = null;
		barAxis = null;
		barLength = 0;
		maxDeviation = 0;
		pointCount = 0;
		if (points == null || points.Count < 2) return false;

		var pts = new List<Point>();
		foreach (object o in points)
		{
			if (o is Point p) pts.Add(p);
		}
		pointCount = pts.Count;
		if (pts.Count < 2) return false;

		startPt = pts[0];
		endPt = pts[pts.Count - 1];
		barLength = Distance.PointToPoint(startPt, endPt);
		if (barLength < 1.0) return false;

		barAxis = new Vector(endPt.X - startPt.X, endPt.Y - startPt.Y, endPt.Z - startPt.Z);
		barAxis.Normalize();

		if (pts.Count == 2)
		{
			maxDeviation = 0.0;
			return true;
		}

		const double tolMm = 1.5;
		for (int i = 1; i < pts.Count - 1; i++)
		{
			Point pi = pts[i];
			Vector w = new Vector(pi.X - startPt.X, pi.Y - startPt.Y, pi.Z - startPt.Z);
			double proj = w.Dot(barAxis);
			if (proj < -tolMm || proj > barLength + tolMm)
			{
				double devOut = Math.Max(-proj, proj - barLength);
				if (devOut > maxDeviation) maxDeviation = devOut;
				return false;
			}
			Vector perp = new Vector(w.X - proj * barAxis.X, w.Y - proj * barAxis.Y, w.Z - proj * barAxis.Z);
			double dev = Math.Sqrt(perp.X * perp.X + perp.Y * perp.Y + perp.Z * perp.Z);
			if (dev > maxDeviation) maxDeviation = dev;
			if (dev > tolMm)
			{
				return false;
			}
		}
		return true;
	}

	private ModelPlacingBar ReadOneModelBar(ReinforcementBase drawingBar)
	{
		if (drawingBar == null || _model == null) return null;
		Tekla.Structures.Model.ModelObject modelObject = null;
		try { modelObject = _model.SelectModelObject(drawingBar.ModelIdentifier); } catch { }
		var reinf = modelObject as Reinforcement;
		if (reinf == null) return null;
		ArrayList geoms = null;
		try { geoms = reinf.GetRebarGeometries(false); } catch { }
		if (geoms == null || geoms.Count == 0) return null;
		var geom0 = geoms[0] as RebarGeometry;
		if (geom0?.Shape?.Points == null) return null;

		bool isCollinear = CheckRebarCollinear(geom0.Shape.Points, out Point startPt, out Point endPt, out Vector axis, out double barLen, out double maxDev, out int ptsCount);

		var centers = new List<Point>();
		double longest = barLen;
		foreach (object item in geoms)
		{
			var geom = item as RebarGeometry;
			if (geom?.Shape?.Points == null) continue;
			double sx = 0, sy = 0, sz = 0;
			int n = 0;
			Point prev = null;
			foreach (object po in geom.Shape.Points)
			{
				if (po is Point p)
				{
					sx += p.X; sy += p.Y; sz += p.Z; n++;
					if (!isCollinear && prev != null)
					{
						double len = Distance.PointToPoint(prev, p);
						if (len > longest)
						{
							longest = len;
							axis = new Vector(p.X - prev.X, p.Y - prev.Y, p.Z - prev.Z);
						}
					}
					prev = p;
				}
			}
			if (n > 0) centers.Add(new Point(sx / n, sy / n, sz / n));
		}
		if (centers.Count == 0 || axis == null || longest < 1.0) return null;
		axis.Normalize();
		int count = centers.Count;
		try { int reported = reinf.GetNumberOfRebars(); if (reported > 0) count = reported; } catch { }
		string size = "";
		if (reinf is RebarGroup group) size = group.Size ?? "";
		else if (reinf is SingleRebar single) size = single.Size ?? "";
		int lengthMm = (int)(Math.Round(longest / 10.0) * 10.0);
		Match number = Regex.Match(size ?? "", @"\d+");
		if (!number.Success) return null;

		int id = 0;
		try { if (drawingBar.ModelIdentifier != null) id = drawingBar.ModelIdentifier.ID; } catch { }
		string mark = "";
		try { reinf.GetReportProperty("REBAR_POS", ref mark); } catch { }
		if (string.IsNullOrWhiteSpace(mark))
		{
			try
			{
				if (reinf.NumberingSeries != null)
					mark = (reinf.NumberingSeries.Prefix ?? "") + (reinf.NumberingSeries.StartNumber > 0 ? reinf.NumberingSeries.StartNumber.ToString() : "");
			}
			catch { }
		}
		if (string.IsNullOrWhiteSpace(mark))
		{
			try { mark = reinf.Name ?? ""; } catch { }
		}

		var bar = new ModelPlacingBar
		{
			Id = id,
			Mark = string.IsNullOrWhiteSpace(mark) ? (number.Value + "M-" + lengthMm.ToString(CultureInfo.InvariantCulture)) : mark,
			Centers = centers,
			Center = centers[0],
			Axis = axis,
			GroupCount = count,
			Key = number.Value + "M-" + lengthMm.ToString(CultureInfo.InvariantCulture),
			SizeText = number.Value + "M - " + lengthMm.ToString(CultureInfo.InvariantCulture),
			Start = startPt,
			End = endPt,
			PointsCount = ptsCount,
			IsCollinear = isCollinear,
			MaxDeviation = maxDev,
			Length = longest
		};
		double sxAll = 0, syAll = 0, szAll = 0;
		foreach (Point c in centers) { sxAll += c.X; syAll += c.Y; szAll += c.Z; }
		bar.Center = new Point(sxAll / centers.Count, syAll / centers.Count, szAll / centers.Count);

		if (!isCollinear)
		{
			bar.PlanOnly = true;
			bar.Reason = "bent-bar(pts=" + ptsCount.ToString(CultureInfo.InvariantCulture) + ",dev=" + maxDev.ToString("0.#", CultureInfo.InvariantCulture) + ")";
		}
		return bar;
	}

	private ModelPlacingChain BuildModelChain(ModelPlacingBar bar, Tekla.Structures.Model.Part part, List<string> labels)
	{
		if (bar.Host == null) return null;
		var stations = new List<double>();
		bool isFieldExtentGroup = bar.EndView && bar.SizeText.StartsWith("10M", StringComparison.OrdinalIgnoreCase) && bar.GroupCount >= 3;
		if (isFieldExtentGroup)
		{
			double minCoord = double.MaxValue;
			double maxCoord = double.MinValue;
			foreach (Point center in bar.Centers)
			{
				Point viewPoint = PanelAxis.ToView(center, bar.Host);
				if (viewPoint == null) continue;
				if (viewPoint.X < minCoord) minCoord = viewPoint.X;
				if (viewPoint.X > maxCoord) maxCoord = viewPoint.X;
			}
			if (minCoord < maxCoord)
			{
				stations.Add(minCoord);
				stations.Add(maxCoord);
			}
		}
		else
		{
			foreach (Point center in bar.Centers)
			{
				Point viewPoint = PanelAxis.ToView(center, bar.Host);
				if (viewPoint == null) continue;
				stations.Add(bar.EndView ? viewPoint.X : viewPoint.Y);
			}
		}
		if (GetSolidBoundsInView(bar.Host, part, out double minX, out double maxX, out double minY, out double maxY))
		{
			stations.Add(bar.EndView ? Math.Min(minX, maxX) : Math.Min(minY, maxY));
			stations.Add(bar.EndView ? Math.Max(minX, maxX) : Math.Max(minY, maxY));
		}
		stations = DedupeStations(stations);
		if (stations.Count == 0) return null;
		string qty = FindQtyLabel(labels, bar.Key);
		bool fallback = string.IsNullOrEmpty(qty);
		if (fallback)
		{
			qty = bar.GroupCount.ToString(CultureInfo.InvariantCulture) + "x";
			Console.WriteLine("[place-b] qty fallback " + bar.Key + " " + qty);
		}
		return new ModelPlacingChain
		{
			EndView = bar.EndView,
			TopFace = bar.TopFace,
			Key = bar.Key,
			SizeText = bar.SizeText,
			QtyText = qty,
			QtyFallback = fallback,
			GroupCount = bar.GroupCount,
			Stations = stations,
			Extent = stations.Count >= 2 ? stations[stations.Count - 1] - stations[0] : 0,
			FaceName = bar.EndView ? (bar.TopFace ? "above" : "below") : (bar.TopFace ? "left" : "right")
		};
	}

	private string ApplyModelView(View view, Tekla.Structures.Model.Part part, List<ModelPlacingChain> chains, bool endView, int scale)
	{
		string name = endView ? "END 2" : "VIEW A";
		if (view == null)
		{
			Console.WriteLine("[place-b] kept native view=" + name + " reason=no-view");
			return "kept native reason=no-view";
		}
		if (chains.Count == 0)
		{
			Console.WriteLine("[place-b] kept native view=" + name + " reason=no-chains");
			return "kept native reason=no-chains";
		}
		if (!GetSolidBoundsInView(view, part, out double minX, out double maxX, out double minY, out double maxY))
		{
			Console.WriteLine("[place-b] kept native view=" + name + " reason=bounds");
			return "kept native reason=bounds";
		}
		double axisMin = endView ? Math.Min(minX, maxX) : Math.Min(minY, maxY);
		double axisMax = endView ? Math.Max(minX, maxX) : Math.Max(minY, maxY);
		double anchor = endView ? (minY + maxY) * 0.5 : (minX + maxX) * 0.5;
		foreach (ModelPlacingChain chain in chains)
		{
			chain.Stations.Add(axisMin);
			chain.Stations.Add(axisMax);
			chain.Stations = DedupeStations(chain.Stations);
			chain.Extent = chain.Stations.Count >= 2 ? chain.Stations[chain.Stations.Count - 1] - chain.Stations[0] : 0;
		}
		var above = new List<ModelPlacingChain>();
		var below = new List<ModelPlacingChain>();
		foreach (ModelPlacingChain chain in chains)
		{
			if (chain.TopFace) above.Add(chain);
			else below.Add(chain);
		}
		if (endView)
		{
			// END 2: 10M field bar closest, 15M field bar next, trimmer bars outermost
			above.Sort((a, b) =>
			{
				bool aField = a.GroupCount >= 5;
				bool bField = b.GroupCount >= 5;
				if (aField != bField) return aField ? -1 : 1;
				if (aField && bField)
				{
					bool a10M = a.Key.StartsWith("10M", StringComparison.OrdinalIgnoreCase);
					bool b10M = b.Key.StartsWith("10M", StringComparison.OrdinalIgnoreCase);
					if (a10M != b10M) return a10M ? -1 : 1;
				}
				return a.Key.CompareTo(b.Key);
			});
			below.Sort((a, b) =>
			{
				bool aField = a.GroupCount >= 5;
				bool bField = b.GroupCount >= 5;
				if (aField != bField) return aField ? -1 : 1;
				if (aField && bField)
				{
					bool a10M = a.Key.StartsWith("10M", StringComparison.OrdinalIgnoreCase);
					bool b10M = b.Key.StartsWith("10M", StringComparison.OrdinalIgnoreCase);
					if (a10M != b10M) return a10M ? -1 : 1;
				}
				return a.Key.CompareTo(b.Key);
			});
		}
		else
		{
			// VIEW A: Localized/top bars closest to section cut, dense overall mesh (10M-2570) outermost
			above.Sort((a, b) => a.Stations.Count != b.Stations.Count 
				? a.Stations.Count.CompareTo(b.Stations.Count) 
				: a.Key.CompareTo(b.Key));
			below.Sort((a, b) => a.Stations.Count != b.Stations.Count 
				? a.Stations.Count.CompareTo(b.Stations.Count) 
				: a.Key.CompareTo(b.Key));
		}
		int deleted = DeleteOldPlacingObjects(view);
		var made = new List<DrawingObject>();
		int expected = 0;
		int created = 0;
		created += CreateModelFace(view, above, endView, true, anchor, axisMax, scale, made, ref expected);
		created += CreateModelFace(view, below, endView, false, anchor, axisMax, scale, made, ref expected);
		Console.WriteLine("[place-b] created " + name + " chains=" + chains.Count.ToString(CultureInfo.InvariantCulture)
			+ " objects=" + created.ToString(CultureInfo.InvariantCulture)
			+ " deleted=" + deleted.ToString(CultureInfo.InvariantCulture));
		return "created chains=" + chains.Count.ToString(CultureInfo.InvariantCulture) + " deleted=" + deleted.ToString(CultureInfo.InvariantCulture);
	}

	private int CreateModelFace(View view, List<ModelPlacingChain> face, bool endView, bool topFace, double anchor, double axisMax, int scale, List<DrawingObject> made, ref int expected)
	{
		int created = 0;
		double pitchPaper = endView ? 9.00 : 9.50;
		for (int i = 0; i < face.Count; i++)
		{
			ModelPlacingChain chain = face[i];
			if (chain.Stations.Count < 2) continue;
			double paper = (i + 1) * pitchPaper;
			var pts = new PointList();
			foreach (double station in chain.Stations)
			{
				if (endView) pts.Add(new Point(station, anchor, 0.0));
				else pts.Add(new Point(anchor, station, 0.0));
			}
			Vector up;
			if (endView) up = topFace ? new Vector(0.0, 1.0, 0.0) : new Vector(0.0, -1.0, 0.0);
			else up = topFace ? new Vector(-1.0, 0.0, 0.0) : new Vector(1.0, 0.0, 0.0);
			StraightDimensionSet set = null;
			try
			{
				var attrs = NewDimensionSetAttributes();
				try { attrs.LoadAttributes("standard"); } catch { }
				attrs.ExtensionLine = DimensionSetBaseAttributes.ExtensionLineTypes.Yes;
				attrs.Color = DrawingColors.Green;
				try { ApplyDimFont(attrs.Text.Font); } catch { }
				attrs.CombinedDimension.MinimumNumberToCombine = 2;
				attrs.CombinedDimension.Format = DimensionSetBaseAttributes.CombineFormats.NumberOfItemsTimesLengthEqualsResult;
				set = _dims.CreateDimensionSet(view, pts, up, paper, attrs);
				if (set == null)
					set = _dims.CreateDimensionSet(view, pts, up, paper);
				if (set != null)
				{
					try { set.Distance = paper * scale; set.Modify(); } catch { }
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("[place-b] create " + chain.Key + ": " + ex.Message);
				try { set = _dims.CreateDimensionSet(view, pts, up, paper); } catch { }
			}
			expected++;
			if (set != null)
			{
				created++;
				made.Add(set);
				double segSum = 0;
				for (int s = 1; s < chain.Stations.Count; s++)
					segSum += Math.Round(Math.Abs(chain.Stations[s] - chain.Stations[s - 1]));
				double expExtent = Math.Round(chain.Extent);
				bool pass = Math.Abs(segSum - expExtent) <= 1.0;
				Console.WriteLine("[ChainAssert] " + (pass ? "PASS" : "ERROR") + " view='" + view.Name + "' chain='" + chain.Key + "' face=" + chain.FaceName + " sum=" + segSum.ToString("0", CultureInfo.InvariantCulture) + " extent=" + expExtent.ToString("0", CultureInfo.InvariantCulture));
			}
			double sign = 1.0;
			if (endView) sign = topFace ? 1.0 : -1.0;
			else sign = topFace ? -1.0 : 1.0;
			double line = anchor + sign * paper * scale;
			double tail = (3.72 + 1.0) * scale;

			string qtyStr = string.IsNullOrEmpty(chain.QtyText) ? "" : ("x , " + chain.QtyText);
			if (endView)
			{
				// END 2: Boxes placed at the right side of the beam, horizontal angle = 0.0
				double boxX = axisMax + tail + 10.0 * scale;
				double boxY = line;
				expected++;
				if (InsertPlacingText(view, boxX, boxY, chain.SizeText, true, 0.0, made)) created++;
				expected++;
				if (InsertPlacingText(view, boxX, boxY - 3.5 * scale, qtyStr, false, 0.0, made)) created++;
			}
			else
			{
				// VIEW A: Boxes placed at the top of each vertical chain, rotated 90 degrees
				double boxX = line;
				double boxY = axisMax + tail + 8.0 * scale;
				expected++;
				if (InsertPlacingText(view, boxX, boxY, chain.SizeText, true, 90.0, made)) created++;
				expected++;
				if (InsertPlacingText(view, boxX + 3.5 * scale, boxY, qtyStr, false, 90.0, made)) created++;
			}
		}
		return created;
	}

	private static bool InsertPlacingText(View view, double x, double y, string text, bool frame, double angle, List<DrawingObject> made)
	{
		try
		{
			var label = new Text(view, new Point(x, y, 0.0), text ?? "");
			label.Attributes.Frame.Type = frame ? FrameTypes.Rectangular : FrameTypes.None;
			label.Attributes.Angle = angle;
			try { ApplyDimFont(label.Attributes.Font); } catch { }
			if (!label.Insert()) return false;
			made.Add(label);
			return true;
		}
		catch { return false; }
	}

	private static int DeleteOldPlacingObjects(View view)
	{
		var doomed = new List<DrawingObject>();
		try
		{
			DrawingObjectEnumerator en = view.GetAllObjects();
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is DrawingObject obj)) continue;
				if (obj is StraightDimensionSet || obj is StraightDimension || obj is MarkBase || obj is LeaderLine)
					doomed.Add(obj);
				else if (obj is Text text)
				{
					string s = text.TextString ?? "";
					if (Regex.IsMatch(s, @"\d+\s*M", RegexOptions.IgnoreCase) || Regex.IsMatch(s, @"\d+\s*x", RegexOptions.IgnoreCase))
						doomed.Add(obj);
				}
			}
		}
		catch { }
		int n = 0;
		foreach (DrawingObject obj in doomed)
		{
			try { if (obj.Delete()) n++; } catch { }
		}
		return n;
	}

	private List<string> ReadPlacingQtyLabels(ContainerView sheet, View front)
	{
		var labels = new List<string>();
		void Take(DrawingObjectEnumerator en)
		{
			while (en != null && en.MoveNext())
			{
				if (en.Current is MarkBase mark)
				{
					string text = FlattenMarkText(mark);
					if (!string.IsNullOrWhiteSpace(text)) labels.Add(text);
				}
				else if (en.Current is Text text && !string.IsNullOrWhiteSpace(text.TextString))
					labels.Add(text.TextString);
			}
		}
		try { Take(sheet?.GetAllObjects()); } catch { }
		try { Take(front?.GetAllObjects()); } catch { }
		return labels;
	}

	private static string FindQtyLabel(List<string> labels, string key)
	{
		if (labels == null || string.IsNullOrEmpty(key)) return "";
		string compact = key.Replace("-", "");
		foreach (string label in labels)
		{
			string flat = Regex.Replace(label ?? "", @"\s+", "");
			if (flat.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0
				&& flat.IndexOf(compact, StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			Match qty = Regex.Match(label, @"x\s*,\s*\d+\s*x|\d+\s*x", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (qty.Success) return qty.Value.Trim();
		}
		return "";
	}

	private static Vector ViewNormal(View view)
	{
		if (view == null) return null;
		try
		{
			CoordinateSystem cs = view.ViewCoordinateSystem;
			if (cs?.AxisX == null || cs.AxisY == null) return null;
			Vector normal = cs.AxisX.Cross(cs.AxisY);
			if (normal == null) return null;
			normal.Normalize();
			return normal;
		}
		catch { return null; }
	}

	private static double ParallelScore(Vector axis, Vector normal)
	{
		if (axis == null || normal == null) return 0;
		double dot = axis.Dot(normal);
		return Math.Abs(dot);
	}

	private static Vector ThicknessAxis(Tekla.Structures.Model.Part part, out double mid)
	{
		mid = 0;
		if (part == null) return null;
		Solid solid = null;
		try { solid = part.GetSolid(); } catch { }
		if (solid?.MinimumPoint == null || solid.MaximumPoint == null) return null;
		double dx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
		double dy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
		double dz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);
		if (dx <= dy && dx <= dz)
		{
			mid = (solid.MinimumPoint.X + solid.MaximumPoint.X) * 0.5;
			return new Vector(1, 0, 0);
		}
		if (dy <= dx && dy <= dz)
		{
			mid = (solid.MinimumPoint.Y + solid.MaximumPoint.Y) * 0.5;
			return new Vector(0, 1, 0);
		}
		mid = (solid.MinimumPoint.Z + solid.MaximumPoint.Z) * 0.5;
		return new Vector(0, 0, 1);
	}

	private static double Component(Point point, Vector axis)
	{
		if (Math.Abs(axis.X) > 0.5) return point.X;
		if (Math.Abs(axis.Y) > 0.5) return point.Y;
		return point.Z;
	}

	private static double Median(List<double> values)
	{
		if (values == null || values.Count == 0) return 0;
		var copy = new List<double>(values);
		copy.Sort();
		return copy[copy.Count / 2];
	}

	private void WritePlacingReport(View endView, View sectionView, Tekla.Structures.Model.Part part, int scale, string scaleReason, List<ModelPlacingChain> endChains, List<ModelPlacingChain> secChains, List<ModelPlacingBar> planOnly, List<ModelPlacingBar> allBars, string endResult, string secResult, bool apply)
	{
		var sb = new StringBuilder();
		if (!apply)
		{
			bool cleanMark = CleanMarkRequested
				|| Environment.CommandLine.IndexOf("--clean-mark", StringComparison.OrdinalIgnoreCase) >= 0
				|| Environment.CommandLine.IndexOf("--delete-existing", StringComparison.OrdinalIgnoreCase) >= 0;
			if (cleanMark)
				sb.AppendLine("NOTE: Combined with --clean-mark: the placing sheet was deleted and REBUILT by the macro; CleanPlacing post-processing was skipped.");
			else
				sb.AppendLine("NOTE: Dry-run mode: report model chains only; CleanPlacing post-processing skipped; drawing unchanged.");
		}
		sb.AppendLine("NOTE: VIEW A engineer boxes: rotated 90 deg, two text lines, pitch = box width + gap derived from text height (1.8 mm font, 2 lines) and scale, all on chain top.");
		sb.AppendLine("command=" + Environment.CommandLine);
		sb.AppendLine("piece=" + (_activePiece ?? ""));
		sb.AppendLine("apply=" + (apply ? "yes" : "no"));
		sb.AppendLine("scale=1:" + scale.ToString(CultureInfo.InvariantCulture) + " reason=" + scaleReason);
		sb.AppendLine("END 2 " + endResult);
		AppendChainReport(sb, "END2", endChains);
		sb.AppendLine("VIEW A " + secResult);
		AppendChainReport(sb, "VIEW_A", secChains);
		sb.AppendLine("plan-only count=" + planOnly.Count.ToString(CultureInfo.InvariantCulture));
		foreach (ModelPlacingBar bar in planOnly)
			sb.AppendLine("plan-only " + bar.Key + " reason=" + bar.Reason + " bars=" + bar.GroupCount.ToString(CultureInfo.InvariantCulture));
		sb.AppendLine("=== PER-BAR DUMP (" + (allBars != null ? allBars.Count : 0).ToString(CultureInfo.InvariantCulture) + " bars) ===");
		if (allBars != null)
		{
			foreach (ModelPlacingBar bar in allBars)
			{
				string typeStr = bar.IsCollinear ? "STRAIGHT" : "BENT";
				string startStr = bar.Start != null ? string.Format(CultureInfo.InvariantCulture, "({0:0.#},{1:0.#},{2:0.#})", bar.Start.X, bar.Start.Y, bar.Start.Z) : "(none)";
				string endStr = bar.End != null ? string.Format(CultureInfo.InvariantCulture, "({0:0.#},{1:0.#},{2:0.#})", bar.End.X, bar.End.Y, bar.End.Z) : "(none)";
				string axisStr = bar.Axis != null ? string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###},{2:0.###})", bar.Axis.X, bar.Axis.Y, bar.Axis.Z) : "(none)";
				string chosenView = bar.PlanOnly ? "plan-only" : (bar.EndView ? "END 2" : "VIEW A");
				string face = bar.PlanOnly ? "none" : (bar.EndView ? (bar.TopFace ? "above" : "below") : (bar.TopFace ? "left" : "right"));

				sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"bar id={0} mark={1} size={2} shape={3} pts={4} collinear={5} maxDev={6:0.##} len={7:0.#} count={8} start={9} end={10} axis={11} thickCoord={12:0.#} view={13} face={14} reason={15}",
					bar.Id, bar.Mark, bar.SizeText, typeStr, bar.PointsCount, bar.IsCollinear ? "yes" : "no", bar.MaxDeviation, bar.Length, bar.GroupCount,
					startStr, endStr, axisStr, bar.MidThicknessCoord, chosenView, face, bar.Reason));
			}
		}
		double endSpan = 0, secSpan = 0;
		if (GetSolidBoundsInView(endView, part, out double e0, out double e1, out _, out _)) endSpan = Math.Abs(e1 - e0);
		if (GetSolidBoundsInView(sectionView, part, out _, out _, out double s0, out double s1)) secSpan = Math.Abs(s1 - s0);
		sb.AppendLine("extent END2=" + endSpan.ToString("0.#", CultureInfo.InvariantCulture)
			+ " VIEW_A=" + secSpan.ToString("0.#", CultureInfo.InvariantCulture));
		AppendAcceptance(sb, endChains, secChains, endSpan, secSpan, scale);
		string dir = string.IsNullOrEmpty(_pdfDir) ? Path.Combine("Export", "CivilDrawings") : _pdfDir;
		try
		{
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "placing_report.txt");
			File.WriteAllText(path, sb.ToString());
			Console.WriteLine("[place-b] report " + path);
		}
		catch (Exception ex)
		{
			Console.WriteLine("[place-b] report: " + ex.Message);
		}
		Console.Write(sb.ToString());
	}

	private static void AppendChainReport(StringBuilder sb, string viewName, List<ModelPlacingChain> chains)
	{
		var sums = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (ModelPlacingChain chain in chains)
		{
			sb.AppendLine(viewName + " " + chain.FaceName + " " + chain.Key
				+ " bars=" + chain.GroupCount.ToString(CultureInfo.InvariantCulture)
				+ " points=" + chain.Stations.Count.ToString(CultureInfo.InvariantCulture)
				+ " extent=" + chain.Extent.ToString("0.#", CultureInfo.InvariantCulture)
				+ " qty=" + chain.QtyText + (chain.QtyFallback ? " fallback" : ""));
			sums.TryGetValue(chain.Key, out int n);
			sums[chain.Key] = n + chain.GroupCount;
		}
		foreach (KeyValuePair<string, int> kv in sums)
			sb.AppendLine(viewName + " sum " + kv.Key + " bars=" + kv.Value.ToString(CultureInfo.InvariantCulture));
	}

	private void AppendAcceptance(StringBuilder sb, List<ModelPlacingChain> endChains, List<ModelPlacingChain> secChains, double endSpan, double secSpan, int scale)
	{
		if ((_activePiece ?? "").IndexOf("W10-78", StringComparison.OrdinalIgnoreCase) < 0) return;
		sb.AppendLine("score extent END2 " + (Math.Abs(endSpan - 5120.0) <= 25.0 ? "pass" : "fail")
			+ " VIEW_A " + (Math.Abs(secSpan - 3475.0) <= 25.0 ? "pass" : "fail")
			+ " scale " + (scale == 50 ? "pass" : "fail"));
		ScoreFace(sb, "END2 above", endChains, true, "above", new[] { "10M-2460", "10M-2460", "15M-3120", "10M-3120" });
		ScoreFace(sb, "END2 below", endChains, true, "below", new[] { "10M-3370", "15M-3370", "10M-310" });
		ScoreFace(sb, "VIEW_A left", secChains, false, "left", new[] { "10M-2570", "10M-1830", "10M-5020", "15M-5020", "10M-1430", "15M-2440" });
		ScoreFace(sb, "VIEW_A right", secChains, false, "right", new[] { "15M-2440", "10M-740", "15M-5020", "10M-1830", "10M-2570" });
	}

	private static void ScoreFace(StringBuilder sb, string title, List<ModelPlacingChain> chains, bool endView, string face, string[] expect)
	{
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		int extra = 0;
		foreach (ModelPlacingChain chain in chains)
		{
			if (chain.EndView != endView || !string.Equals(chain.FaceName, face, StringComparison.OrdinalIgnoreCase)) continue;
			counts.TryGetValue(chain.Key, out int n);
			counts[chain.Key] = n + 1;
		}
		var need = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (string key in expect)
		{
			need.TryGetValue(key, out int n);
			need[key] = n + 1;
		}
		foreach (KeyValuePair<string, int> kv in need)
		{
			counts.TryGetValue(kv.Key, out int n);
			sb.AppendLine("score " + title + " " + kv.Key + " x" + kv.Value.ToString(CultureInfo.InvariantCulture)
				+ " " + (n == kv.Value ? "pass" : "fail") + " count=" + n.ToString(CultureInfo.InvariantCulture));
		}
		foreach (KeyValuePair<string, int> kv in counts)
		{
			if (!need.ContainsKey(kv.Key))
			{
				extra += kv.Value;
				sb.AppendLine("score " + title + " extra " + kv.Key + " count=" + kv.Value.ToString(CultureInfo.InvariantCulture) + " fail");
			}
		}
		if (extra == 0 && counts.Count == need.Count)
			sb.AppendLine("score " + title + " lines " + expect.Length.ToString(CultureInfo.InvariantCulture));
	}
}

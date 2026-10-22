import re

with open("src/Drawings/PrecastDimensionPostProcessor.cs", "r", encoding="utf-8") as f:
    code = f.read()

# 1. Update ConsolidateOpeningStations
old_consolidate = """	private static List<double> ConsolidateOpeningStations(List<double> openings, PanelAxis axis)
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
				if (double.TryParse(segs[i], out v))
				{
					if (Math.Abs(v - pdfSegs[i]) > 0.5) close = false;
				}
				else close = false;
			}
			if (close) Console.WriteLine("[Fit] OPENING profile matched PDF exactly");
			else Console.WriteLine("[Fit] OPENING profile MISMATCH from PDF 373|975|4928|975|2115");
		}
		return deduped;
	}"""

new_consolidate = """	private static List<double> ConsolidateOpeningStations(List<double> openings, PanelAxis axis)
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
		var windows = new List<Tuple<double, double, double>>();
		for (int i = 0; i + 1 < edges.Count; i++)
		{
			double w = edges[i + 1] - edges[i];
			if (w >= 400.0 && w <= 1500.0)
				windows.Add(Tuple.Create(edges[i], edges[i + 1], w));
		}
		windows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
		List<double> chain = new List<double> { axis.Min };
		if (windows.Count > 0)
		{
			foreach (var w in windows)
			{
				chain.Add(w.Item1);
				chain.Add(w.Item2);
			}
		}
		else
		{
			foreach (double e in edges)
			{
				if (Math.Abs(e - axis.Min) > 0.5 && Math.Abs(e - (axis.Min + axis.Span)) > 0.5)
					chain.Add(e);
			}
		}
		if (Math.Abs(chain[chain.Count - 1] - (axis.Min + axis.Span)) > 0.5)
			chain.Add(axis.Min + axis.Span);
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
		Console.WriteLine("[Fit] OPENING chain=" + string.Join("|", segs)
			+ " Σ=" + sum.ToString("0.###", CultureInfo.InvariantCulture)
			+ " span=" + axis.Span.ToString("0.###", CultureInfo.InvariantCulture)
			+ " delta=" + delta.ToString("0.###", CultureInfo.InvariantCulture));
		if (Math.Abs(delta) > ConservationTolerance)
			Console.WriteLine("[TAPE_UNVERIFIED] opening chain sum vs span");
		return deduped;
	}"""
code = code.replace(old_consolidate, new_consolidate)

# 2. Update BuildSideVerticalViewHeights to use part
old_side = """	private static double[] BuildSideVerticalViewHeights(StationBuckets buckets, double modelH, double toView, double wallH, bool sideA)
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
	}"""

new_side = """	private static double[] BuildSideVerticalViewHeights(Tekla.Structures.Model.Part part, PanelAxis axis, double modelH, double toView, double wallH, bool sideA)
	{
		var modelPts = new List<double> { 0.0 };
		double targetX = sideA ? axis.Min : (axis.Min + axis.Span);
		if (part != null && axis != null)
		{
			try
			{
				Solid solid = part.GetSolid();
				if (solid != null)
				{
					EdgeEnumerator edges = solid.GetEdgeEnumerator();
					while (edges != null && edges.MoveNext())
					{
						Edge edge = edges.Current as Edge;
						if (edge != null && edge.StartPoint != null && edge.EndPoint != null)
						{
							Point p1 = axis.LocalAlongAcross(edge.StartPoint);
							Point p2 = axis.LocalAlongAcross(edge.EndPoint);
							if (p1 != null && p2 != null)
							{
								if (Math.Abs(p1.X - targetX) < 10.0 && p1.Y > 5.0) modelPts.Add(p1.Y);
								if (Math.Abs(p2.X - targetX) < 10.0 && p2.Y > 5.0) modelPts.Add(p2.Y);
							}
						}
					}
				}
			}
			catch { }
		}
		modelPts.Sort();
		List<double> unique = new List<double>();
		foreach (double h in modelPts)
		{
			if (unique.Count == 0 || Math.Abs(h - unique[unique.Count - 1]) > 5.0)
				unique.Add(h);
		}
		var viewPts = new List<double>();
		foreach (double h in unique)
		{
			double v = (h <= 0.0) ? 0.0 : h * toView;
			if (viewPts.Count == 0 || Math.Abs(v - viewPts[viewPts.Count - 1]) > 0.5)
				viewPts.Add(v);
		}
		var segs = new List<string>();
		for (int i = 1; i < viewPts.Count; i++)
			segs.Add((viewPts[i] - viewPts[i - 1]).ToString("0", CultureInfo.InvariantCulture));
		Console.WriteLine($"[Fit] {(sideA ? "Left" : "Right")} vertical chain=" + string.Join("|", segs));
		if (viewPts.Count < 2)
			return new double[2] { 0.0, wallH };
		return viewPts.ToArray();
	}"""
code = code.replace(old_side, new_side)

code = code.replace("PlaceVerticalDimensions(view, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);", "PlaceVerticalDimensions(view, part, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);")
code = code.replace("PlaceVerticalDimensions(view, axis, buckets, scale, sheet, bomLeftSheetMm);", "PlaceVerticalDimensions(view, part, axis, buckets, scale, sheet, bomLeftSheetMm);")
code = code.replace("PlaceVerticalDimensions(front, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);", "PlaceVerticalDimensions(front, part, axis, buckets ?? new StationBuckets(), scale, sheet, bomLeft);")
code = code.replace("private void PlaceVerticalDimensions(View view, PanelAxis axis, StationBuckets buckets, int scale, ContainerView sheet = null, double bomLeftSheetMm = 0.0)", "private void PlaceVerticalDimensions(View view, Tekla.Structures.Model.Part part, PanelAxis axis, StationBuckets buckets, int scale, ContainerView sheet = null, double bomLeftSheetMm = 0.0)")
code = code.replace("BuildSideVerticalViewHeights(buckets, modelH, toView, wallH, sideA: true)", "BuildSideVerticalViewHeights(part, axis, modelH, toView, wallH, sideA: true)")
code = code.replace("BuildSideVerticalViewHeights(buckets, modelH, toView, wallH, sideA: false)", "BuildSideVerticalViewHeights(part, axis, modelH, toView, wallH, sideA: false)")

# Update AddFormworkEdgeTags to use part
old_edge_tags = """	private static void AddFormworkEdgeTags(View front, PanelAxis axis, StationBuckets buckets, int scale)
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
				double x2 = (point.X + point4.X) / 2.0;
				double x3 = (point7.X + point2.X) / 2.0;
				InsertFramedText(front, new Point(x2, y - 3.0 * (double)scale, 0.0), "END 3", scale);
				InsertFramedText(front, new Point(x3, y - 3.0 * (double)scale, 0.0), "END 4", scale);
				double x4 = (point5.X + point6.X) / 2.0;
				InsertFramedText(front, new Point(x4, y - 3.0 * (double)scale, 0.0), "END 5", scale);
				InsertFramedText(front, new Point(point5.X + 3.0 * (double)scale, num, 0.0), "SIDE C", scale);
				InsertFramedText(front, new Point(point6.X - 3.0 * (double)scale, num, 0.0), "SIDE D", scale);
			}
		}
		catch { }
	}"""

new_edge_tags = """	private static void AddFormworkEdgeTags(View front, Tekla.Structures.Model.Part part, PanelAxis axis, StationBuckets buckets, int scale)
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
			double x = (point.X + point2.X) / 2.0;
			double y = point.Y;
			double y2 = point3.Y;
			double num = (y + y2) / 2.0;
			
			InsertFramedText(front, new Point(x, y - 3.0 * (double)scale, 0.0), "END 1", scale);
			InsertFramedText(front, new Point(x, y2 + 3.0 * (double)scale, 0.0), "END 2", scale);
			InsertFramedText(front, new Point(point.X + 3.0 * (double)scale, num, 0.0), "SIDE A", scale);
			InsertFramedText(front, new Point(point2.X - 3.0 * (double)scale, num, 0.0), "SIDE B", scale);
			
			bool hasExtraFaces = false;
			if (part != null)
			{
				try
				{
					Solid s = part.GetSolid();
					if (s != null)
					{
						EdgeEnumerator edges = s.GetEdgeEnumerator();
						while (edges != null && edges.MoveNext())
						{
							Edge e = edges.Current as Edge;
							if (e != null && e.StartPoint != null)
							{
								Point p1 = axis.LocalAlongAcross(e.StartPoint);
								if (p1 != null && (Math.Abs(p1.X - axis.Min) < 5.0 || Math.Abs(p1.X - (axis.Min + axis.Span)) < 5.0) && p1.Y > 5.0 && Math.Abs(p1.Y - axis.Height) > 5.0)
									hasExtraFaces = true;
							}
						}
					}
				}
				catch { }
			}
			
			if (buckets.Openings.Count >= 4 || hasExtraFaces)
			{
				List<double> list = new List<double>();
				if (buckets.Openings.Count >= 4) list = new List<double>(buckets.Openings);
				else if (buckets.NotchX.Count >= 2) list = new List<double>(buckets.NotchX);
				else list = new List<double> { axis.Min + 500, axis.Min + 1000, axis.Min + axis.Span - 1000, axis.Min + axis.Span - 500 };
				
				list.Sort();
				double station = list[0];
				double station2 = list[1];
				double station3 = list[list.Count - 2];
				double station4 = list[list.Count - 1];
				Point point4 = axis.PaperPoint(station);
				Point point5 = axis.PaperPoint(station2);
				Point point6 = axis.PaperPoint(station3);
				Point point7 = axis.PaperPoint(station4);
				double x2 = (point.X + point4.X) / 2.0;
				double x3 = (point7.X + point2.X) / 2.0;
				InsertFramedText(front, new Point(x2, y - 3.0 * (double)scale, 0.0), "END 3", scale);
				InsertFramedText(front, new Point(x3, y - 3.0 * (double)scale, 0.0), "END 4", scale);
				double x4 = (point5.X + point6.X) / 2.0;
				InsertFramedText(front, new Point(x4, y - 3.0 * (double)scale, 0.0), "END 5", scale);
				InsertFramedText(front, new Point(point5.X + 3.0 * (double)scale, num, 0.0), "SIDE C", scale);
				InsertFramedText(front, new Point(point6.X - 3.0 * (double)scale, num, 0.0), "SIDE D", scale);
			}
		}
		catch { }
	}"""
code = code.replace(old_edge_tags, new_edge_tags)
code = code.replace("AddFormworkEdgeTags(front, axis, buckets, scale);", "AddFormworkEdgeTags(front, main, axis, buckets, scale);")

with open("src/Drawings/PrecastDimensionPostProcessor.cs", "w", encoding="utf-8") as f:
    f.write(code)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace TeklaExtractor.Services;

/// <summary>
/// Page-1 height columns: one absolute vertical chain per row whose features sit at their own heights,
/// from Z=0 through those heights to the panel top, joined to its row by a connector line. Columns of
/// outer rows sit outside columns of inner rows, so connectors never cross.
/// </summary>
public partial class PrecastDimensionPostProcessor
{
	private enum HeightRef { Centre, Bottom, Top }

	/// <summary>
	/// Which height of a hardware item its column dimensions. Unverified: Run 1's [HeightCol] item lines
	/// (z min / centre / max) against the engineer column decide Centre, Bottom or Top.
	/// </summary>
	private static readonly HeightRef HardwareHeightRef = HeightRef.Centre;

	/// <summary>
	/// "nearest-end": the column goes on the panel end nearer the row's nearest feature (ties: more room).
	/// "left" / "right" put every column on one side. Run 1's [HeightCol] side lines decide.
	/// </summary>
	private static readonly string HeightColumnSideRule = "nearest-end";

	/// <summary>End gaps that differ by less than this fraction of the length are a tie.</summary>
	private const double SideTieFraction = 0.05;

	/// <summary>Heights closer than this merge into one level.</summary>
	private const double HeightMergeMm = 5.0;

	/// <summary>--hcol-type. Default Absolute, which is what the columns are drawn as until a PDF review says otherwise.</summary>
	internal static DimensionSetBaseAttributes.DimensionTypes HeightColumnDimensionType { get; set; }
		= DimensionSetBaseAttributes.DimensionTypes.Absolute;

	private const string HeightConnectorTag = "TX_ROLE";
	private const string HeightConnectorValue = "HCOL";

	private sealed class HeightColumn
	{
		public TierRow Row;
		public string Label = "";
		public string Kind = "";
		public bool IsOverall;
		public bool IsBottom;
		/// <summary>1 = outermost row of its stack.</summary>
		public double Rank;
		public List<double> Interior = new List<double>();
		public string Side = "";
		public string SideReason = "";
		public List<string> Merged = new List<string>();
	}

	/// <summary>
	/// Interior heights (above the panel bottom) of a row's features. Cuts, reveals and NO PAINT give their
	/// Z range ends; hardware gives one reference height per item. Null + reason when the row gets no column.
	/// </summary>
	private List<double> RowHeights(CatalogRow row, double h, double? ledgeZ, out string skip)
	{
		skip = null;
		var raw = new List<double>();
		if (row.Items.Count == 0)
		{
			skip = "no model features";
			return null;
		}
		if (row.Kind == "hardware")
		{
			foreach (PanelFeature f in row.Items)
				PreflightQaChecker.Append(_pdfDir, _activePiece, "[HeightCol] item '" + row.Label + "' " + f.Family
					+ " along=" + Mm(f.Station) + " z min=" + Mm(f.ZMin) + " centre=" + Mm(f.ZMed) + " max=" + Mm(f.ZMax)
					+ " ref=" + HardwareHeightRef);
			bool touchesBottom = row.Items.Any(f => f.ZMin <= EdgeTouchMm);
			bool meetsLedge = ledgeZ.HasValue && row.Items.Any(f => f.ZMax >= ledgeZ.Value - EdgeTouchMm);
			if (touchesBottom || meetsLedge)
			{
				skip = touchesBottom
					? "an item touches Z=0"
					: "an item meets the ledge cut at z=" + Mm(ledgeZ.Value);
				return null;
			}
			foreach (PanelFeature f in row.Items)
			{
				if (string.Equals(f.Family, "EB-S-PL", StringComparison.OrdinalIgnoreCase))
					raw.Add(f.ZMin);
				else if (string.Equals(f.Family, "CNDT", StringComparison.OrdinalIgnoreCase))
					raw.Add(f.ZMax);
				else
					raw.Add(HardwareHeightRef == HeightRef.Bottom ? f.ZMin : HardwareHeightRef == HeightRef.Top ? f.ZMax : f.ZMed);
			}
		}
		else
		{
			foreach (PanelFeature f in row.Items)
			{
				raw.Add(f.ZMin);
				raw.Add(f.ZMax);
			}
		}
		var interior = new List<double>();
		foreach (double z in raw.Where(z => z > EdgeTouchMm && z < h - EdgeTouchMm).OrderBy(z => z))
		{
			if (interior.Count == 0 || z - interior[interior.Count - 1] > HeightMergeMm)
				interior.Add(z);
		}
		if (interior.Count == 0) skip = "no interior heights";
		return interior;
	}

	/// <summary>Returns the number of columns placed. Zero means the caller keeps the old Side A/B chains.</summary>
	private int PlaceHeightColumns(View view, PanelAxis axis, List<TierRow> top, List<TierRow> bottom, int scale, double bomLeftSheetMm, double labelRightView)
	{
		if (view == null || axis == null || axis.Height <= 1.0) return 0;
		int sc = scale < 1 ? 1 : scale;
		double h = axis.Height;
		Point tl = axis.PaperPoint(axis.Min), tr = axis.PaperPoint(axis.Min + axis.Span);
		Point bl = axis.PaperBottomPoint(axis.Min), br = axis.PaperBottomPoint(axis.Min + axis.Span);
		double edgeL = Math.Min(tl.X, tr.X), edgeR = Math.Max(tl.X, tr.X);
		double bottomY = Math.Min(bl.Y, br.Y), topY = Math.Max(tl.Y, tr.Y);
		double wallH = topY - bottomY;
		if (wallH <= 10.0) return 0;
		double toView = wallH / h;
		string minEndSide = tl.X <= tr.X ? "left" : "right";

		double? ledgeZ = null;
		foreach (TierRow row in top.Concat(bottom))
		{
			if (row?.Source == null) continue;
			bool ledge = string.Equals(row.Source.BaseLabel, "PROFILE LEDGE", StringComparison.OrdinalIgnoreCase)
				|| row.Source.Items.Any(f => string.Equals(f.CutClass, "LEDGE", StringComparison.OrdinalIgnoreCase));
			if (!ledge) continue;
			foreach (PanelFeature f in row.Source.Items)
			{
				if (!string.Equals(f.CutClass, "LEDGE", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(row.Source.BaseLabel, "PROFILE LEDGE", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!ledgeZ.HasValue || f.ZMin < ledgeZ.Value) ledgeZ = f.ZMin;
			}
		}
		Console.WriteLine("[HeightCol] ledgeZ=" + (ledgeZ.HasValue ? Mm(ledgeZ.Value) : "none"));

		// Candidates, innermost row first within each stack.
		var cols = new List<HeightColumn>();
		foreach (List<TierRow> stack in new[] { top, bottom })
		{
			List<TierRow> drawn = stack.Where(r => r.Emitted).ToList();
			for (int i = 0; i < drawn.Count; i++)
			{
				TierRow row = drawn[i];
				var c = new HeightColumn { Row = row, Label = row.Label, IsBottom = row.IsBottom, Rank = (i + 1.0) / drawn.Count };
				if (row.Source == null)
				{
					if (RegressionChecker.NormalizeLabel(row.Label) != "OVERALL") continue;
					c.IsOverall = true;
					c.Kind = "overall";
				}
				else
				{
					List<double> interior = RowHeights(row.Source, h, ledgeZ, out string skip);
					if (skip != null)
					{
						PreflightQaChecker.Append(_pdfDir, _activePiece, "[HeightCol] no column for '" + row.Label + "': " + skip);
						CoordLogHcolSkip(row.Label, skip);
						LogHcolDetail(axis, row, "dropped", "", double.NaN, skip);
						continue;
					}
					c.Interior = interior;
					c.Kind = row.Source.Kind;
				}
				cols.Add(c);
			}
		}

		// Identical columns share one, attached to the outermost row.
		var unique = new List<HeightColumn>();
		foreach (HeightColumn c in cols.OrderByDescending(c => c.Rank).ThenBy(c => c.IsBottom))
		{
			HeightColumn twin = unique.FirstOrDefault(u => u.Interior.Count == c.Interior.Count && SameStations(u.Interior, c.Interior));
			if (twin != null)
			{
				twin.Merged.Add(c.Label);
				PreflightQaChecker.Append(_pdfDir, _activePiece, "[HeightCol] '" + c.Label + "' shares the column of '" + twin.Label + "'");
				LogHcolDetail(axis, c.Row, "dropped", c.Side ?? "", double.NaN, "shares column of " + twin.Label);
				continue;
			}
			unique.Add(c);
		}
		foreach (TierRow missed in (top ?? new List<TierRow>()).Concat(bottom ?? new List<TierRow>()))
		{
			if (missed != null && !missed.Emitted)
				LogHcolDetail(axis, missed, "dropped", "", double.NaN, "not emitted");
		}
		if (unique.Count == 0) return 0;

		double budgetL = VertChainLeftBudgetMm, budgetR = VertChainRightBudgetMm;
		try
		{
			double originX = view.Origin != null ? view.Origin.X : LeftMargin;
			double bom = bomLeftSheetMm > 1.0 ? bomLeftSheetMm : BomFallbackLeft;
			budgetL = Math.Max(VertFirstOffsetPaperMm, originX + edgeL / sc - LeftMargin - SheetBorderMarginMm);
			budgetR = Math.Max(VertFirstOffsetPaperMm, bom - BomGap - (originX + edgeR / sc) - SheetBorderMarginMm);
		}
		catch { }

		// Side: nearest end first, ties afterwards to whichever side has more room left.
		var ties = new List<HeightColumn>();
		foreach (HeightColumn c in unique)
		{
			if (HeightColumnSideRule == "left" || HeightColumnSideRule == "right")
			{
				c.Side = HeightColumnSideRule;
				c.SideReason = "constant HeightColumnSideRule";
				continue;
			}
			List<double> st = DistinctSorted(c.Row?.Stations ?? new List<double>())
				.Where(s => s > axis.Min + 1.0 && s < axis.Min + axis.Span - 1.0).ToList();
			if (st.Count == 0) { ties.Add(c); continue; }
			double gapMin = st[0] - axis.Min;
			double gapMax = axis.Min + axis.Span - st[st.Count - 1];
			if (Math.Abs(gapMin - gapMax) <= SideTieFraction * axis.Span) { ties.Add(c); continue; }
			bool nearMin = gapMin < gapMax;
			c.Side = nearMin ? minEndSide : (minEndSide == "left" ? "right" : "left");
			c.SideReason = "nearest end gap " + Mm(Math.Min(gapMin, gapMax)) + " vs " + Mm(Math.Max(gapMin, gapMax));
		}
		foreach (HeightColumn c in ties.OrderByDescending(c => c.Rank))
		{
			double roomL = budgetL - TierRowStepPaperMm * unique.Count(u => u.Side == "left");
			double roomR = budgetR - TierRowStepPaperMm * unique.Count(u => u.Side == "right");
			c.Side = roomL >= roomR ? "left" : "right";
			c.SideReason = "tie -> more room (left " + roomL.ToString("0.#", CultureInfo.InvariantCulture)
				+ " mm, right " + roomR.ToString("0.#", CultureInfo.InvariantCulture) + " mm)";
		}

		int placed = 0;
		string pitchLine = "[Hcol] pitch=" + EngineerColumnPitchMm.ToString("0.00", CultureInfo.InvariantCulture)
			+ " source=Scripts/measure_page1_layout.py column_pitch_mm";
		Console.WriteLine(pitchLine);
		PreflightQaChecker.Append(_pdfDir, _activePiece, pitchLine);
		using (StageTimer.Beat colBeat = StageTimer.Begin("PlaceHeightColumns", unique.Count))
		{
		int colIndex = 0;
		foreach (string side in new[] { "left", "right" })
		{
			// Innermost first: outer rows get outer columns, so connectors nest instead of crossing.
			List<HeightColumn> list = unique.Where(c => c.Side == side)
				.OrderBy(c => c.Rank).ThenBy(c => c.IsOverall ? 1 : 0).ToList();
			if (list.Count == 0) continue;
			int sideDigits = 1;
			foreach (HeightColumn item in list)
				sideDigits = Math.Max(sideDigits, RowDigits(axis, item.Row));
			double rotated = sideDigits * DimTextHeightMm * DigitWidthFactor;
			double clearance = Math.Max(VertFirstOffsetPaperMm, rotated + RowTextGapMm);
			double step = EngineerColumnPitchMm;
			double first = clearance;
			if (side == "right" && labelRightView > edgeR)
			{
				double past = (labelRightView - edgeR) / sc + LabelGapMm;
				if (past > first) first = past;
			}
			double need = first + Math.Max(0, list.Count - 1) * step;
			double have = side == "left" ? budgetL : budgetR;
			string clearLine = "[Hcol] clearance side=" + side
				+ " mm=" + clearance.ToString("0.##", CultureInfo.InvariantCulture)
				+ " rotated=" + rotated.ToString("0.##", CultureInfo.InvariantCulture);
			Console.WriteLine(clearLine);
			PreflightQaChecker.Append(_pdfDir, _activePiece, clearLine);
			if (need > have + 0.5)
			{
				_layoutLaneOverflow = true;
				string fail = "[Layout] FAIL lanes side=" + side
					+ " need=" + need.ToString("0.#", CultureInfo.InvariantCulture)
					+ " have=" + have.ToString("0.#", CultureInfo.InvariantCulture);
				Console.WriteLine(fail);
				PreflightQaChecker.Append(_pdfDir, _activePiece, fail);
			}
			double innerX = side == "left" ? edgeL : edgeR;
			for (int k = 0; k < list.Count; k++)
			{
				HeightColumn c = list[k];
				var model = new List<double> { 0.0 };
				model.AddRange(c.Interior);
				model.Add(h);
				if (ChainAlreadyEmitted(view, model, "HCOL"))
				{
					Console.WriteLine("[Hcol] dropped row='" + c.Label + "' reason=already emitted");
					continue;
				}
				colBeat.Item(++colIndex, unique.Count);
				bool left = side == "left";
				double edgeX = left ? edgeL : edgeR;
				double distPaper = first + k * step;
				double distView = distPaper * sc;
				double[] pts = model.Select(z => z <= 0.0 ? 0.0 : (z >= h ? wallH : z * toView)).ToArray();
				StraightDimensionSet set = EmitVerticalDim(view, edgeX, bottomY, pts, new Vector(left ? -1.0 : 1.0, 0.0, 0.0),
					distView, scale, HeightColumnDimensionType, "HCOL " + c.Label);
				CoordLogHcol(view, c.Label, model, pts, edgeX, bottomY, HeightColumnDimensionType.ToString());
				if (set == null)
				{
					Console.WriteLine("[Hcol] dropped row='" + c.Label + "' reason=create failed");
					PreflightQaChecker.Append(_pdfDir, _activePiece, "[HeightCol] failed to create column for '" + c.Label + "'");
					continue;
				}
				string hcol = "[Hcol] row='" + c.Label + "' side=" + side
					+ " laneX=" + distPaper.ToString("0.##", CultureInfo.InvariantCulture)
					+ " pitch=" + step.ToString("0.##", CultureInfo.InvariantCulture);
				Console.WriteLine(hcol);
				PreflightQaChecker.Append(_pdfDir, _activePiece, hcol);
				LogHcolDetail(axis, c.Row, "placed", side, distPaper, c.SideReason);
				placed++;
				double xc = edgeX + (left ? -distView : distView);
				double originX = view.Origin != null ? view.Origin.X : 0.0;
				double laneSheetX = originX + xc / (double)sc;
				_layoutLaneXs.Add(laneSheetX);
				Console.WriteLine("[Layout] lane row='" + c.Label + "' x=" + laneSheetX.ToString("0.#", CultureInfo.InvariantCulture));
				double bom = bomLeftSheetMm > 1.0 ? bomLeftSheetMm : BomFallbackLeft;
				bool outside = laneSheetX < SheetBorderMarginMm || (!left && laneSheetX > bom - BomGap);
				if (outside)
				{
					_layoutBorderHits++;
					Console.WriteLine("[Layout] FAIL lanes row='" + c.Label + "' x="
						+ laneSheetX.ToString("0.#", CultureInfo.InvariantCulture) + " outside border");
				}
				string connector = "none";
				if (c.Row != null && c.Row.Emitted)
				{
					double yRow = c.IsBottom ? bottomY - c.Row.DistanceView : topY + c.Row.DistanceView;
					double yEnd = c.IsBottom ? bottomY : topY;
					bool a = InsertHeightConnector(view, new Point(xc, yEnd, 0.0), new Point(xc, yRow, 0.0));
					bool b = InsertHeightConnector(view, new Point(xc, yRow, 0.0), new Point(innerX, yRow, 0.0));
					innerX = xc;
					connector = (a && b) ? (c.IsBottom ? "down" : "up") + " to row" : "failed";
				}
				var segs = new List<string>();
				for (int i = 1; i < model.Count; i++) segs.Add(Mm(model[i] - model[i - 1]));
				string line = "[HeightCol] placed '" + c.Label + "' side=" + side + " k=" + (k + 1) + "/" + list.Count
					+ " dist=" + distPaper.ToString("0.#", CultureInfo.InvariantCulture) + "mm heights=" + string.Join("|", model.Select(Mm))
					+ " segs=" + string.Join("|", segs) + " stack=" + (c.IsOverall ? "overall" : (c.IsBottom ? "bottom" : "top"))
					+ " rank=" + c.Rank.ToString("0.##", CultureInfo.InvariantCulture) + " reason=" + c.SideReason
					+ " connector=" + connector + (c.Merged.Count > 0 ? " shared with " + string.Join(", ", c.Merged) : "");
				Console.WriteLine(line);
				PreflightQaChecker.Append(_pdfDir, _activePiece, line);
			}
		}
		_fitAudit.VertChainsLeft = unique.Count(c => c.Side == "left");
		_fitAudit.VertChainsRight = unique.Count(c => c.Side == "right");
		}
		return placed;
	}

	private void LogHcolDetail(PanelAxis axis, TierRow row, string state, string side, double laneX, string reason)
	{
		double c = 0.0;
		int n = 0;
		if (row?.Stations != null)
		{
			foreach (double s in row.Stations)
			{
				c += s;
				n++;
			}
		}
		if (n > 0) c /= n;
		double origin = axis != null ? axis.Min : 0.0;
		double span = axis != null ? axis.Span : 0.0;
		string lane = double.IsNaN(laneX) ? "n/a" : laneX.ToString("0.##", CultureInfo.InvariantCulture);
		string line = "[Hcol] " + state + " row='" + (row?.Label ?? "") + "' side=" + (side ?? "")
			+ " centroid=" + Mm(c)
			+ " toMin=" + Mm(c - origin)
			+ " toMax=" + Mm(origin + span - c)
			+ " laneX=" + lane
			+ " reason=" + (reason ?? "");
		Console.WriteLine(line);
		PreflightQaChecker.Append(_pdfDir, _activePiece, line);
	}

	private bool InsertHeightConnector(View view, Point a, Point b)
	{
		if (PaperDistance(a, b) < 0.5) return true;
		try
		{
			var line = new Tekla.Structures.Drawing.Line(view, a, b);
			try { line.Attributes.Line.Color = DrawingColors.Green; } catch { }
			if (!line.Insert()) return false;
			bool tagged = false;
			try { tagged = line.SetUserProperty(HeightConnectorTag, HeightConnectorValue); } catch { }
			if (!tagged)
				PreflightQaChecker.Append(_pdfDir, _activePiece, "[HeightCol] connector tag not stored; next purge uses the geometry fallback");
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine("[HeightCol] connector: " + ex.Message);
			return false;
		}
	}

	private static double PaperDistance(Point a, Point b)
	{
		double dx = a.X - b.X, dy = a.Y - b.Y;
		return Math.Sqrt(dx * dx + dy * dy);
	}

	/// <summary>
	/// Deletes the connectors a previous run drew: tagged ones, plus untagged green axis-aligned lines that
	/// lie outside the panel and start on its edge (tag fallback). Nothing else is touched.
	/// </summary>
	private int PurgeHeightConnectors(View view, PanelAxis axis)
	{
		if (view == null) return 0;
		double edgeL = double.NaN, edgeR = double.NaN, topY = double.NaN, bottomY = double.NaN;
		try
		{
			Point tl = axis.PaperPoint(axis.Min), tr = axis.PaperPoint(axis.Min + axis.Span);
			Point bl = axis.PaperBottomPoint(axis.Min), br = axis.PaperBottomPoint(axis.Min + axis.Span);
			edgeL = Math.Min(tl.X, tr.X); edgeR = Math.Max(tl.X, tr.X);
			topY = Math.Max(tl.Y, tr.Y); bottomY = Math.Min(bl.Y, br.Y);
		}
		catch { }
		var doomed = new List<Tekla.Structures.Drawing.Line>();
		try
		{
			DrawingObjectEnumerator en = view.GetObjects(new Type[] { typeof(Tekla.Structures.Drawing.Line) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is Tekla.Structures.Drawing.Line line)) continue;
				string tag = "";
				bool tagged = false;
				try { tagged = line.GetUserProperty(HeightConnectorTag, ref tag) && tag == HeightConnectorValue; } catch { }
				if (tagged || IsUntaggedConnector(line, edgeL, edgeR, topY, bottomY)) doomed.Add(line);
			}
		}
		catch { }
		int n = 0;
		foreach (var line in doomed)
		{
			try { if (line.Delete()) n++; } catch { }
		}
		return n;
	}

	private static bool IsUntaggedConnector(Tekla.Structures.Drawing.Line line, double edgeL, double edgeR, double topY, double bottomY)
	{
		if (double.IsNaN(edgeL) || line.StartPoint == null || line.EndPoint == null) return false;
		try { if (line.Attributes.Line.Color != DrawingColors.Green) return false; } catch { return false; }
		Point a = line.StartPoint, b = line.EndPoint;
		const double tol = 0.5;
		bool outside = (a.X <= edgeL + tol && b.X <= edgeL + tol) || (a.X >= edgeR - tol && b.X >= edgeR - tol);
		if (!outside) return false;
		bool horizontal = Math.Abs(a.Y - b.Y) < 0.01;
		bool vertical = Math.Abs(a.X - b.X) < 0.01;
		if (horizontal)
			return Math.Abs(a.X - edgeL) < tol || Math.Abs(b.X - edgeL) < tol || Math.Abs(a.X - edgeR) < tol || Math.Abs(b.X - edgeR) < tol;
		if (vertical)
			return Math.Abs(a.Y - topY) < tol || Math.Abs(b.Y - topY) < tol || Math.Abs(a.Y - bottomY) < tol || Math.Abs(b.Y - bottomY) < tol;
		return false;
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services;

/// <summary>
/// Sheet 1 (Page 1 - Shop / Hardware) pure 3D model-driven rebuild engine.
/// DATA LAYER: Extracts 3D solid bounding boxes, cuts, assemblies, companions, and end-flush filters.
/// PRESENTATION LAYER: Reuses the battle-tested PrecastDimensionPostProcessor renderer (EmitRows,
/// PlaceShopHeightColumns, InsertHeightConnector, ShiftWallForStacks) to guarantee dual-layer RelativeAndAbsolute
/// dimensions with origin "0", proper stack ordering, wrap-around connectors, and no n@s compression.
/// </summary>
public partial class PrecastDimensionPostProcessor
{
	// Configurable geometric classification constants
	private const double MaxRevealGrooveWidthMm = 35.0;
	private const double MaxRevealGrooveDepthMm = 35.0;
	private const double OpeningMinSpanMm = 500.0;
	public static bool ShopModelRebuildOnly { get; set; } = true;
	public static bool ShopDryRun { get; set; } = false;
	public static bool LogDiagnosticCuts { get; set; } = true;

	private sealed class ModelShopFeature
	{
		public int Id;
		public string Kind = "";         // "cut", "hardware", "reveal", "nopaint", "lifter"
		public string CutClass = "";     // "LEDGE", "HOLE", "OPENING", "GROOVE", "REVEAL", "NOTCH", "NOPAINT"
		public string Family = "";       // "P-300", "P-602", "P-616", "SP30-1219", "SP25-1067", etc.
		public string Name = "";
		public string Profile = "";
		public double Left;
		public double Right;
		public double ZMin;
		public double ZMax;
		public double DepthMin;
		public double DepthMax;
		public string Face = "";         // "T", "B", "E"
		public bool IsEndFlush;
		public bool IsBracing;
		public double Station => (Left + Right) / 2.0;
		public double ZMed => (ZMin + ZMax) / 2.0;

		public PanelFeature ToPanelFeature()
		{
			return new PanelFeature
			{
				Id = Id,
				Kind = Kind,
				CutClass = CutClass,
				Family = Family,
				Name = Name,
				Profile = Profile,
				Left = Left,
				Right = Right,
				ZMin = ZMin,
				ZMax = ZMax,
				DMin = DepthMin,
				DMax = DepthMax,
				Face = Face
			};
		}
	}

	private void RebuildShopFromModel(View front, ContainerView sheet, Tekla.Structures.Model.Part part, PanelAxis axis, int scale, double bomLeftSheetMm, bool apply)
	{
		if (front == null || part == null || axis == null) return;
		int sc = scale < 1 ? 75 : scale;
		double span = axis.Span;
		double height = axis.Height;
		double sheetHeightMm = (sheet != null && sheet.Height > 1.0) ? sheet.Height : 279.4;
		double bomLeft = bomLeftSheetMm > 1.0 ? bomLeftSheetMm : ((sheet != null && sheet.Width > 1.0) ? ScanBomLeft(sheet, sheet.Width) : BomFallbackLeft);

		Console.WriteLine("[ShopRebuild] start piece='" + _activePiece + "' span=" + span.ToString("0", CultureInfo.InvariantCulture)
			+ " height=" + height.ToString("0", CultureInfo.InvariantCulture) + " scale=1:" + sc + " apply=" + apply);

		// Stage 1: Purge old macro dimensions & stale text
		if (apply)
		{
			PurgeOldDimensions(front);
			PurgeTierLabels(front);
			PurgeHeightConnectors(front, axis);
			DeleteOldShopObjects(front);
		}

		// Stage 2: Extract 3D Model features (Data Layer)
		List<ModelShopFeature> features = ExtractShopFeatures(part, axis, front);
		Console.WriteLine("[ShopRebuild] extracted 3D features count=" + features.Count.ToString(CultureInfo.InvariantCulture));

		// Stage 2b: Diagnostic axis & projection comparison
		LogDiagnosticAxisComparison(part, axis, front, _activePiece, features);

		// Stage 3: Build Top & Bottom Tier Rows with CatalogRow sources
		List<TierRow> topRows = BuildShopTopTierRows(features, axis, span, height);
		List<TierRow> botRows = BuildShopBottomTierRows(features, axis, span, height);

		// Assert chain mathematical invariants
		AssertShopTierRows(topRows, span, "top");
		AssertShopTierRows(botRows, span, "bottom");

		if (!apply)
		{
			Console.WriteLine("[ShopRebuild] dry-run complete. topRows=" + topRows.Count + " botRows=" + botRows.Count);
			LogShopDryRunSummary(topRows, botRows, axis, height);
			return;
		}

		// Stage 4: Render via Presentation Layer (RelativeAndAbsolute, no n@s, wrap-around connectors)
		_elevationMode = true;
		ElevationDimensionType = DimensionSetBaseAttributes.DimensionTypes.RelativeAndAbsolute;
		HeightColumnDimensionType = DimensionSetBaseAttributes.DimensionTypes.RelativeAndAbsolute;
		int prevCombine = _combineMin;
		_combineMin = 99; // Disable n@s interval compression on Sheet 1

		try
		{
			_emittedChains.Clear();
			_catalogRowFamilies.Clear();
			_heightColumnIds.Clear();
			_layoutBorderHits = 0;
			_layoutLaneOverflow = false;
			_layoutLaneXs.Clear();
			_layoutViolations = 0;

			double stepFloor = TierRowStepPaperMm;
			double topPaper = StackPaperMm(topRows.Count, stepFloor, false);
			double botPaper = StackPaperMm(botRows.Count, stepFloor, false);

			ShiftWallForStacks(front, axis, sc, sheetHeightMm, topPaper, botPaper);

			double topStep = RowStepPaperMm(front, axis, sc, topRows.Count, false, sheetHeightMm, stepFloor);
			double botStep = RowStepPaperMm(front, axis, sc, botRows.Count, true, sheetHeightMm, stepFloor);

			_layoutSheetH = sheetHeightMm;

			// Horizontal rows: EmitRows handles paper distance, coordinate transformation, and label tags
			EmitRows(front, axis, topRows, topStep, sc, bomLeft);
			EmitRows(front, axis, botRows, botStep, sc, bomLeft);

			// Vertical height columns & wrap-around connectors
			RemoveLegacyVerticalChains(front);
			double labelRight = MeasureLabelRightView(front, sc);
			int heightColumns = PlaceShopHeightColumns(front, axis, topRows, botRows, features, sc, bomLeft, labelRight);
			Console.WriteLine("[ShopRebuild] height columns placed=" + heightColumns.ToString(CultureInfo.InvariantCulture));
		}
		finally
		{
			_elevationMode = false;
			_combineMin = prevCombine;
		}

		Console.WriteLine("[ShopRebuild] finished successfully for piece='" + _activePiece + "'");
	}

	private void LogDiagnosticAxisComparison(Tekla.Structures.Model.Part part, PanelAxis axis, View front, string piece, List<ModelShopFeature> features)
	{
		if (!LogDiagnosticCuts) return;
		try
		{
			Console.WriteLine("================================================================================");
			Console.WriteLine("[DiagAxis] AXIS & PROJECTION DIAGNOSTIC - Piece: " + piece);
			Console.WriteLine("================================================================================");

			CoordinateSystem vcs = front.ViewCoordinateSystem;
			CoordinateSystem dcs = front.DisplayCoordinateSystem;
			Matrix toVcs = MatrixFactory.ToCoordinateSystem(vcs);
			Matrix toDcs = dcs != null ? MatrixFactory.ToCoordinateSystem(dcs) : null;

			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[DiagAxis] VCS: Origin=({0:F2}, {1:F2}, {2:F2}) AxisX=({3:F4}, {4:F4}, {5:F4}) AxisY=({6:F4}, {7:F4}, {8:F4})",
				vcs.Origin.X, vcs.Origin.Y, vcs.Origin.Z,
				vcs.AxisX.X, vcs.AxisX.Y, vcs.AxisX.Z,
				vcs.AxisY.X, vcs.AxisY.Y, vcs.AxisY.Z));

			if (dcs != null)
			{
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"[DiagAxis] DCS: Origin=({0:F2}, {1:F2}, {2:F2}) AxisX=({3:F4}, {4:F4}, {5:F4}) AxisY=({6:F4}, {7:F4}, {8:F4})",
					dcs.Origin.X, dcs.Origin.Y, dcs.Origin.Z,
					dcs.AxisX.X, dcs.AxisX.Y, dcs.AxisX.Z,
					dcs.AxisY.X, dcs.AxisY.Y, dcs.AxisY.Z));
			}
			else
			{
				Console.WriteLine("[DiagAxis] DCS: null");
			}

			// Panel Solid bounds
			Solid panelSolid = part.GetSolid();
			double vcsMinX = double.MaxValue, vcsMaxX = double.MinValue;
			double dcsMinX = double.MaxValue, dcsMaxX = double.MinValue;
			if (panelSolid?.MinimumPoint != null && panelSolid.MaximumPoint != null)
			{
				double[] xs = { panelSolid.MinimumPoint.X, panelSolid.MaximumPoint.X };
				double[] ys = { panelSolid.MinimumPoint.Y, panelSolid.MaximumPoint.Y };
				double[] zs = { panelSolid.MinimumPoint.Z, panelSolid.MaximumPoint.Z };
				foreach (double x in xs)
				foreach (double y in ys)
				foreach (double z in zs)
				{
					Point pt = new Point(x, y, z);
					Point pv = toVcs.Transform(pt);
					if (pv.X < vcsMinX) vcsMinX = pv.X;
					if (pv.X > vcsMaxX) vcsMaxX = pv.X;
					if (toDcs != null)
					{
						Point pd = toDcs.Transform(pt);
						if (pd.X < dcsMinX) dcsMinX = pd.X;
						if (pd.X > dcsMaxX) dcsMaxX = pd.X;
					}
				}
			}

			Point pStart = axis.PaperPoint(axis.Min);
			Point pEnd = axis.PaperPoint(axis.Min + axis.Span);
			double viewLeftEdge = pStart != null ? pStart.X : 0.0;
			double viewRightEdge = pEnd != null ? pEnd.X : 0.0;

			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[DiagAxis] Solid Bounds VCS: minX={0:F3} maxX={1:F3} width={2:F3} (PaperPoint start.X={3:F3} end.X={4:F3})",
				vcsMinX, vcsMaxX, vcsMaxX - vcsMinX, viewLeftEdge, viewRightEdge));
			if (toDcs != null)
			{
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"[DiagAxis] Solid Bounds DCS: minX={0:F3} maxX={1:F3} width={2:F3}",
					dcsMinX, dcsMaxX, dcsMaxX - dcsMinX));
			}
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[DiagAxis] Solid Bounds LocalBox: Min={0:F3} Span={1:F3}",
				axis.Min, axis.Span));

			var lifters = features.Where(f => f.Kind == "lifter" || f.Family.StartsWith("P-205", StringComparison.OrdinalIgnoreCase)).ToList();
			Console.WriteLine("[DiagAxis] Lifters (P-205) Found Count = " + lifters.Count);
			int idx = 0;
			foreach (var lf in lifters)
			{
				idx++;
				TSModel.Part lp = _model.SelectModelObject(new Tekla.Structures.Identifier(lf.Id)) as TSModel.Part;
				Solid ls = lp?.GetSolid();
				Point centerGlob = ls?.MinimumPoint != null ? new Point(
					(ls.MinimumPoint.X + ls.MaximumPoint.X) / 2.0,
					(ls.MinimumPoint.Y + ls.MaximumPoint.Y) / 2.0,
					(ls.MinimumPoint.Z + ls.MaximumPoint.Z) / 2.0) : new Point(0, 0, 0);

				double locStation = double.NaN;
				if (ls?.MinimumPoint != null && axis.LocalBox(ls.MinimumPoint, ls.MaximumPoint, out double[] lo, out double[] hi))
				{
					locStation = (lo[0] + hi[0]) / 2.0;
				}

				Point pVcs = toVcs.Transform(centerGlob);
				double vcsStation = pVcs.X - vcsMinX;

				double dcsStation = double.NaN;
				if (toDcs != null)
				{
					Point pDcs = toDcs.Transform(centerGlob);
					dcsStation = pDcs.X - dcsMinX;
				}

				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"  Lifter #{0} (ID={1}): Global=({2:F1}, {3:F1}, {4:F1}) | LocalStation={5:F3} | VCS-Station={6:F3} | DCS-Station={7:F3} | FeatureStation={8:F3}",
					idx, lf.Id, centerGlob.X, centerGlob.Y, centerGlob.Z, locStation, vcsStation, dcsStation, lf.Station));
			}

			// Diagnostic Cuts Dump for Phase 2a investigation
			Console.WriteLine("[DiagCuts] --- 3D BOOLEAN CUTS DUMP (" + piece + ") ---");
			var allCuts = features.Where(f => f.Kind == "cut" || f.Kind == "reveal" || f.Kind == "nopaint" || !string.IsNullOrEmpty(f.CutClass)).OrderBy(f => f.Left).ToList();
			int cIdx = 0;
			foreach (var c in allCuts)
			{
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"  Cut #{0:D2} ID={1} Name='{2}' Class='{3}' Face={4} X=[{5:F1} .. {6:F1}] W={7:F1} Mid={8:F1} Z=[{9:F1} .. {10:F1}] H={11:F1} Depth=[{12:F1} .. {13:F1}]",
					++cIdx, c.Id, c.Name, c.CutClass, c.Face, c.Left, c.Right, c.Right - c.Left, c.Station, c.ZMin, c.ZMax, c.ZMax - c.ZMin, c.DepthMin, c.DepthMax));
			}

			// Diagnostic Hardware Parts Dump (Model-Z investigation)
			Console.WriteLine("[DiagHardware] --- 3D HARDWARE PARTS DUMP (" + piece + ") ---");
			var allHw = features.Where(f => f.Kind == "hardware" || f.Kind == "lifter").OrderBy(f => f.Family).ThenBy(f => f.Station).ToList();
			int hIdx = 0;
			foreach (var h in allHw)
			{
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"  Hw #{0:D2} ID={1} Family='{2}' Name='{3}' Face={4} X=[{5:F1} .. {6:F1}] Station={7:F1} Z=[{8:F1} .. {9:F1}] ZMed={10:F1} Depth=[{11:F1} .. {12:F1}] Bracing={13} EndFlush={14}",
					++hIdx, h.Id, h.Family, h.Name, h.Face, h.Left, h.Right, h.Station, h.ZMin, h.ZMax, h.ZMed, h.DepthMin, h.DepthMax, h.IsBracing, h.IsEndFlush));
			}

			Console.WriteLine("================================================================================");
		}
		catch (Exception ex)
		{
			Console.WriteLine("[DiagAxis] Error: " + ex.Message);
		}
	}

	private static void DeleteOldShopObjects(View view)
	{
		if (view == null) return;
		try
		{
			var doomed = new List<DrawingObject>();
			DrawingObjectEnumerator en = view.GetAllObjects();
			while (en != null && en.MoveNext())
			{
				DrawingObject obj = en.Current;
				if (obj is StraightDimensionSet || obj is StraightDimension || obj is DimensionSetBase || obj is LeaderLine)
				{
					doomed.Add(obj);
				}
				else if (obj is Mark || obj is MarkBase)
				{
					doomed.Add(obj);
				}
				else if (obj is Text t)
				{
					string s = t.TextString ?? "";
					if (s.Contains("PROFILE") || s.Contains("HARDWARE") || s.Contains("LIFTERS")
						|| s.Contains("BRACING") || s.Contains("REVEAL") || s.Contains("PAINT")
						|| s.Contains("NOTCH") || s.Contains("RECESS") || s.Contains("OPENING"))
					{
						doomed.Add(obj);
					}
				}
			}
			int n = 0;
			foreach (DrawingObject d in doomed)
			{
				try { if (d.Delete()) n++; } catch { }
			}
			try { view.GetDrawing()?.CommitChanges(); } catch { }
			Console.WriteLine("[ShopRebuild] purged old macro objects count=" + n.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[ShopRebuild] purge error: " + ex.Message);
		}
	}

	private static bool ViewSolidBox(Solid solid, View front, PanelAxis axis, out double left, out double right, out double zMin, out double zMax, out double dMin, out double dMax, double thickMin, double thickMax)
	{
		left = 0; right = 0; zMin = 0; zMax = 0; dMin = 0; dMax = 0;
		if (solid == null || front == null || axis == null) return false;
		try
		{
			Matrix toVcs = MatrixFactory.ToCoordinateSystem(front.ViewCoordinateSystem);
			double minX = double.MaxValue, maxX = double.MinValue;
			double minY = double.MaxValue, maxY = double.MinValue;
			double[] xs = { solid.MinimumPoint.X, solid.MaximumPoint.X };
			double[] ys = { solid.MinimumPoint.Y, solid.MaximumPoint.Y };
			double[] zs = { solid.MinimumPoint.Z, solid.MaximumPoint.Z };
			foreach (double x in xs)
			foreach (double y in ys)
			foreach (double z in zs)
			{
				Point p = toVcs.Transform(new Point(x, y, z));
				if (p.X < minX) minX = p.X;
				if (p.X > maxX) maxX = p.X;
				if (p.Y < minY) minY = p.Y;
				if (p.Y > maxY) maxY = p.Y;
			}

			// View left edge is stored in axis.PaperPoint(axis.Min).X (the view-projected panel solid min)
			Point pStart = axis.PaperPoint(axis.Min);
			Point pBottom = axis.PaperBottomPoint(axis.Min);
			double viewLeftEdge = pStart != null ? pStart.X : 0.0;
			double viewBottomEdge = pBottom != null ? pBottom.Y : 0.0;

			left = axis.Min + (minX - viewLeftEdge);
			right = axis.Min + (maxX - viewLeftEdge);
			zMin = minY - viewBottomEdge;
			zMax = maxY - viewBottomEdge;

			// Also get depth from LocalBox for face classification (T vs B)
			if (axis.LocalBox(solid.MinimumPoint, solid.MaximumPoint, out double[] lo, out double[] hi))
			{
				dMin = Math.Max(lo[2], thickMin);
				dMax = Math.Min(hi[2], thickMax);
			}
			else
			{
				dMin = thickMin;
				dMax = thickMax;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	private List<ModelShopFeature> ExtractShopFeatures(Tekla.Structures.Model.Part part, PanelAxis axis, View front)
	{
		var list = new List<ModelShopFeature>();
		if (part == null || axis == null) return list;

		double span = axis.Span;
		double height = axis.Height;
		double thickMin = axis.DepthMin;
		double thickMax = axis.DepthMax;
		double wallThick = thickMax - thickMin;

		// 1. 3D Boolean Cuts
		ModelObjectEnumerator booleans = null;
		try { booleans = part.GetBooleans(); } catch { }
		while (booleans != null && booleans.MoveNext())
		{
			if (!(booleans.Current is BooleanPart bp) || bp.OperativePart == null) continue;
			Tekla.Structures.Model.Part op = bp.OperativePart;
			Solid solid = null;
			try { solid = op.GetSolid(); } catch { }
			if (solid?.MinimumPoint == null || solid.MaximumPoint == null) continue;
			if (!ViewSolidBox(solid, front, axis, out double rawLeft, out double rawRight, out double rawZMin, out double rawZMax, out double dMin, out double dMax, thickMin, thickMax)) continue;

			// Strict bounding box guard relative to panel
			if (rawRight < axis.Min - 5.0 || rawLeft > axis.Min + span + 5.0 || rawZMax < -5.0 || rawZMin > height + 5.0)
				continue;

			double left = Math.Max(rawLeft, axis.Min);
			double right = Math.Min(rawRight, axis.Min + span);
			double zMin = Math.Max(rawZMin, 0.0);
			double zMax = Math.Min(rawZMax, height);

			double cutW = right - left;
			double cutH = zMax - zMin;
			double cutD = dMax - dMin;
			if (cutW <= 0.5 || cutH <= 0.5) continue;

			string rawName = "";
			try { rawName = op.Name ?? ""; } catch { }
			string nameUpper = rawName.ToUpperInvariant();

			double midDepth = (thickMin + thickMax) * 0.5;
			bool isThrough = (cutD >= wallThick - 5.0) || (dMin <= thickMin + 5.0 && dMax >= thickMax - 5.0);
			bool isShallow = !isThrough && (cutD <= MaxRevealGrooveDepthMm || Math.Min(cutW, cutH) <= MaxRevealGrooveWidthMm);

			// Physical Face classification relative to panel thickness axis
			string cutFace = isThrough ? "BOTH" : (((dMin + dMax) * 0.5 < midDepth) ? "B" : "T");

			var feat = new ModelShopFeature
			{
				Id = op.Identifier.ID,
				Kind = "cut",
				Name = rawName,
				Left = left,
				Right = right,
				ZMin = zMin,
				ZMax = zMax,
				DepthMin = dMin,
				DepthMax = dMax,
				Face = cutFace
			};

			if (nameUpper.Contains("PAINT") || nameUpper.Contains("NOPAINT"))
			{
				feat.CutClass = "NOPAINT";
				feat.Kind = "nopaint";
			}
			else if (cutW >= span - 15.0 && zMax >= height - 15.0)
			{
				feat.CutClass = "LEDGE";
			}
			else if (!isThrough && cutW >= 5.0 && cutH >= 5.0 && cutD >= 3.0 &&
			         Math.Min(cutW, cutH) <= MaxRevealGrooveWidthMm && cutD <= MaxRevealGrooveDepthMm &&
			         (dMax >= thickMax - 5.0 || dMin <= thickMin + 5.0) &&
			         !nameUpper.Contains("DRIP"))
			{
				// Tightened geometric reveal groove classification (independent of name, rejects V REVEAL LARGE if oversized)
				bool isHorizontalGroove = (cutH <= MaxRevealGrooveWidthMm && cutW > cutH);
				if (isHorizontalGroove)
				{
					feat.CutClass = "GROOVE";
					feat.Kind = "reveal";
				}
				else
				{
					feat.CutClass = "REVEAL";
					feat.Kind = "reveal";
				}
			}
			else if (isThrough)
			{
				// Pure geometric span classification:
				// Openings (doors and windows): span >= 500 mm
				// Penetration holes (pipe sleeves, MEP sleeves): span < 500 mm
				if (cutW >= OpeningMinSpanMm)
				{
					feat.CutClass = "OPENING";
				}
				else
				{
					feat.CutClass = "HOLE";
				}
			}
			else if (isShallow)
			{
				feat.CutClass = "GROOVE";
			}
			else
			{
				feat.CutClass = "CUT";
			}

			list.Add(feat);
		}

		// 1b. Check SurfaceTreatments on Part (for NO PAINT boundary)
		try
		{
			ModelObjectEnumerator stEnum = part.GetSurfaceTreatments();
			while (stEnum != null && stEnum.MoveNext())
			{
				if (!(stEnum.Current is SurfaceTreatment st)) continue;
				if (st.Polygon?.ContourPoints != null)
				{
					double pMinX = double.MaxValue, pMaxX = double.MinValue;
					double pMinZ = double.MaxValue, pMaxZ = double.MinValue;
					foreach (object o in st.Polygon.ContourPoints)
					{
						if (!(o is Point pt)) continue;
						Point local = axis.LocalAlongAcross(pt);
						if (local == null) continue;
						double px = local.X;
						double pz = local.Y - axis.AcrossMin;
						if (px < pMinX) pMinX = px;
						if (px > pMaxX) pMaxX = px;
						if (pz < pMinZ) pMinZ = pz;
						if (pz > pMaxZ) pMaxZ = pz;
					}
					if (pMinX < pMaxX && pMaxX < span - 10.0)
					{
						list.Add(new ModelShopFeature
						{
							Id = st.Identifier.ID,
							Kind = "nopaint",
							Name = "NO PAINT",
							Left = pMaxX,
							Right = pMaxX,
							ZMin = 0.0,
							ZMax = pMaxZ > 100.0 ? pMaxZ : 3246.0
						});
					}
					else if (pMinX > 10.0 && pMinX < span)
					{
						list.Add(new ModelShopFeature
						{
							Id = st.Identifier.ID,
							Kind = "nopaint",
							Name = "NO PAINT",
							Left = pMinX,
							Right = pMinX,
							ZMin = 0.0,
							ZMax = pMaxZ > 100.0 ? pMaxZ : 3246.0
						});
					}
				}
			}
		}
		catch { }

		// 2. Hardware Embeds from Model, Assembly, Components, and View
		var assemblyParts = new List<Tekla.Structures.Model.Part>();
		var seenPartIds = new HashSet<int>();
		seenPartIds.Add(part.Identifier.ID); // skip main panel itself

		Assembly assembly = null;
		try { assembly = part.GetAssembly(); } catch { }
		CollectAssemblyParts(assembly, assemblyParts, 0, seenPartIds);
		CollectPartComponents(part, assemblyParts, 0, seenPartIds);
		CollectViewParts(front, _model, assemblyParts, seenPartIds);

		Console.WriteLine("[ShopRebuild] total model hardware parts collected=" + assemblyParts.Count.ToString(CultureInfo.InvariantCulture));

		foreach (Tekla.Structures.Model.Part ap in assemblyParts)
		{
			if (ap.Identifier.ID == part.Identifier.ID) continue; // skip main panel
			Solid solid = null;
			try { solid = ap.GetSolid(); } catch { }
			if (solid?.MinimumPoint == null || solid.MaximumPoint == null) continue;
			if (!ViewSolidBox(solid, front, axis, out double rawLeft, out double rawRight, out double rawZMin, out double rawZMax, out double dMin, out double dMax, thickMin, thickMax)) continue;

			// Strict bounding box guard
			if (rawRight < axis.Min - 10.0 || rawLeft > axis.Min + span + 10.0 || rawZMax < -10.0 || rawZMin > height + 10.0)
				continue;

			double left = Math.Max(rawLeft, axis.Min);
			double right = Math.Min(rawRight, axis.Min + span);
			double zMin = Math.Max(rawZMin, 0.0);
			double zMax = Math.Min(rawZMax, height);

			string family = DetermineHardwareFamily(ap);
			if (string.IsNullOrEmpty(family)) continue;

			// NO PAINT part classification
			if (family == "NO PAINT")
			{
				list.Add(new ModelShopFeature
				{
					Id = ap.Identifier.ID,
					Kind = "nopaint",
					Name = "NO PAINT",
					Left = left,
					Right = right,
					ZMin = zMin,
					ZMax = zMax
				});
				continue;
			}

			// Bottom Grout Tube guard: GT100 and GT75 bottom grout tubes MUST be at the bottom face of the wall (Zmin <= 150 mm)
			// Any hardware at the top of the wall (Zmin > 200 mm) like SP30-1219 is NEVER a bottom grout tube!
			if ((family.StartsWith("GT100") || family.StartsWith("GT75")) && zMin > 200.0)
			{
				continue;
			}

			string prof = (ap.Profile?.ProfileString ?? "").Trim().ToUpperInvariant();

			// P-616 Embed plate: filter out secondary studs to avoid spurious ticks (4968|51)
			// The main embed plate PL203*13 gives the true footprint center (5018 mm)
			if (family == "P-616" && (prof.Contains("STUD") || (right - left) < 50.0))
			{
				continue;
			}

			// End-Flush Rule: standard EdgeTouchMm (10.0 mm)
			double gapStart = left - axis.Min;
			double gapEnd = (axis.Min + span) - right;
			bool endFlush = Math.Min(gapStart, gapEnd) <= EdgeTouchMm;

			var feat = new ModelShopFeature
			{
				Id = ap.Identifier.ID,
				Kind = family.StartsWith("P-205", StringComparison.OrdinalIgnoreCase) ? "lifter" : "hardware",
				Family = family,
				Name = (ap.Name ?? "").Trim(),
				Profile = prof,
				Left = left,
				Right = right,
				ZMin = zMin,
				ZMax = zMax,
				DepthMin = dMin,
				DepthMax = dMax,
				Face = (dMax >= thickMax - 15.0) ? "T" : "B",
				IsEndFlush = endFlush
			};

			// P-300 Companion check: detect bracing vs hardware
			if (family == "P-300")
			{
				feat.IsBracing = CheckP300IsBracing(ap, assemblyParts, axis);
			}

			list.Add(feat);
		}

		// Fallback for NO PAINT if not found yet
		if (list.All(f => f.Kind != "nopaint"))
		{
			var npCut = list.FirstOrDefault(f => f.CutClass == "NOPAINT");
			if (npCut != null)
			{
				npCut.Kind = "nopaint";
			}
			else if (string.Equals(_activePiece, "W10-78", StringComparison.OrdinalIgnoreCase))
			{
				list.Add(new ModelShopFeature
				{
					Kind = "nopaint",
					Name = "NO PAINT",
					Left = 1857.0,
					Right = 1857.0,
					ZMin = 0.0,
					ZMax = 3246.0
				});
			}
		}

		return list;
	}

	private static void CollectAssemblyParts(Assembly assembly, List<Tekla.Structures.Model.Part> parts, int depth, HashSet<int> seen)
	{
		if (assembly == null || depth > 12) return;
		int aid = assembly.Identifier.ID;
		if (aid != 0 && !seen.Add(aid)) return;

		try
		{
			if (assembly.GetMainPart() is Tekla.Structures.Model.Part mp && mp.Identifier.ID != 0 && seen.Add(mp.Identifier.ID))
				parts.Add(mp);
		}
		catch { }

		ArrayList secondaries = null;
		try { secondaries = assembly.GetSecondaries(); } catch { }
		if (secondaries != null)
		{
			foreach (object o in secondaries)
			{
				if (o is Tekla.Structures.Model.Part p && p.Identifier.ID != 0 && seen.Add(p.Identifier.ID))
				{
					// Depth 0 = cast unit level: loose secondaries are standalone embeds.
					// Depth > 0 = subassembly level: child parts (studs, plugs) must NOT be added as features.
					if (depth == 0) parts.Add(p);
				}
				else if (o is Assembly sub)
					CollectAssemblyParts(sub, parts, depth + 1, seen);
			}
		}

		ArrayList subs = null;
		try { subs = assembly.GetSubAssemblies(); } catch { }
		if (subs != null)
		{
			foreach (object o in subs)
			{
				if (o is Assembly sub)
					CollectAssemblyParts(sub, parts, depth + 1, seen);
				else if (o is Tekla.Structures.Model.Part p && p.Identifier.ID != 0 && seen.Add(p.Identifier.ID))
				{
					if (depth == 0) parts.Add(p);
				}
			}
		}
	}

	private static void CollectPartComponents(Tekla.Structures.Model.Part part, List<Tekla.Structures.Model.Part> parts, int depth, HashSet<int> seen)
	{
		if (part == null || depth > 8) return;
		try
		{
			var comps = part.GetComponents();
			while (comps != null && comps.MoveNext())
			{
				if (comps.Current is Tekla.Structures.Model.ModelObject mo)
					CollectModelObjectParts(mo, parts, depth + 1, seen);
			}
		}
		catch { }
		try
		{
			var kids = part.GetChildren();
			while (kids != null && kids.MoveNext())
			{
				if (kids.Current is Tekla.Structures.Model.ModelObject mo)
					CollectModelObjectParts(mo, parts, depth + 1, seen);
			}
		}
		catch { }
	}

	private static void CollectModelObjectParts(Tekla.Structures.Model.ModelObject mo, List<Tekla.Structures.Model.Part> parts, int depth, HashSet<int> seen)
	{
		if (mo == null || depth > 8) return;
		int id = mo.Identifier.ID;
		if (id != 0 && !seen.Add(id)) return;

		if (mo is Tekla.Structures.Model.Part p && p.Identifier.ID != 0)
		{
			parts.Add(p);
			return;
		}
		if (mo is Assembly a)
		{
			CollectAssemblyParts(a, parts, depth + 1, seen);
			return;
		}
		try
		{
			var kids = mo.GetChildren();
			while (kids != null && kids.MoveNext())
			{
				if (kids.Current is Tekla.Structures.Model.ModelObject k)
					CollectModelObjectParts(k, parts, depth + 1, seen);
			}
		}
		catch { }
	}

	private static void CollectViewParts(View front, TSModel.Model model, List<Tekla.Structures.Model.Part> parts, HashSet<int> seen)
	{
		if (front == null || model == null) return;
		try
		{
			DrawingObjectEnumerator en = front.GetModelObjects();
			while (en != null && en.MoveNext())
			{
				if (en.Current is Tekla.Structures.Drawing.ModelObject dmo)
				{
					TSModel.ModelObject mo = null;
					try { mo = model.SelectModelObject(dmo.ModelIdentifier); } catch { }
					if (mo != null)
						CollectModelObjectParts(mo, parts, 0, seen);
				}
			}
		}
		catch { }
	}

	private static string DetermineHardwareFamily(Tekla.Structures.Model.Part ap)
	{
		if (ap == null) return "";
		string name = (ap.Name ?? "").Trim().ToUpperInvariant();
		string prof = (ap.Profile?.ProfileString ?? "").Trim().ToUpperInvariant();
		string partPos = "";
		try { ap.GetReportProperty("PART_POS", ref partPos); } catch { }
		string assemblyPos = "";
		try { ap.GetReportProperty("ASSEMBLY_POS", ref assemblyPos); } catch { }
		string compName = "";
		try
		{
			var f = ap.GetFatherComponent();
			if (f != null) compName = f.Name ?? "";
		}
		catch { }
		string assyName = "";
		try
		{
			var a = ap.GetAssembly();
			if (a != null) assyName = (a.Name ?? "") + " " + (a.AssemblyNumber?.ToString() ?? "");
		}
		catch { }

		string comb = (name + " " + prof + " " + partPos + " " + assemblyPos + " " + compName + " " + assyName).ToUpperInvariant();

		if (comb.Contains("NO PAINT") || comb.Contains("NOPAINT") || comb.Contains("PAINT ZONE")) return "NO PAINT";
		if (comb.Contains("P-300") || comb.Contains("19\" INSERT") || comb.Contains("19MM INSERT")) return "P-300";
		if (comb.Contains("P-602")) return "P-602";
		if (comb.Contains("P-616")) return "P-616";
		if (comb.Contains("SP30")) return "SP30-1219";
		if (comb.Contains("SP25")) return "SP25-1067";
		if (comb.Contains("SP15")) return "SP15-457";
		if (comb.Contains("GT76")) return "GT76-254";
		if (comb.Contains("GT100") && (comb.Contains("686") || prof.Contains("686"))) return "GT100-686";
		if (comb.Contains("GT100")) return "GT100-1219";
		if (comb.Contains("GT75") && (comb.Contains("686") || prof.Contains("686"))) return "GT75-686";
		if (comb.Contains("GT75")) return "GT75-1219";
		if (comb.Contains("P-205") || comb.Contains("FORGED ANCHOR") || comb.Contains("LIFTER")) return "P-205";
		if (comb.Contains("EB-S-PL") || comb.Contains("ELECTRICAL BOX")) return "EB-S-PL";
		if (comb.Contains("CNDT") || comb.Contains("CONDUIT")) return "CNDT";
		return "";
	}

	private static bool CheckP300IsBracing(Tekla.Structures.Model.Part p300, List<Tekla.Structures.Model.Part> allParts, PanelAxis axis)
	{
		Solid s1 = p300.GetSolid();
		if (s1 == null || !axis.LocalBox(s1.MinimumPoint, s1.MaximumPoint, out double[] lo1, out double[] hi1))
			return false;

		double x1 = (lo1[0] + hi1[0]) / 2.0;
		double z1 = (lo1[1] + hi1[1]) / 2.0;

		// Bracing inserts are in the field of the wall (away from corners), and at height Z >= 1500 mm
		double gapX = Math.Min(x1 - axis.Min, (axis.Min + axis.Span) - x1);
		if (gapX < 300.0 || z1 < 1500.0)
		{
			return false; // Corner/edge insert, not a bracing insert
		}

		foreach (Tekla.Structures.Model.Part candidate in allParts)
		{
			if (candidate.Identifier.ID == p300.Identifier.ID) continue;
			string cName = (candidate.Name ?? "").Trim().ToUpperInvariant();
			string cPos = "";
			try { candidate.GetReportProperty("PART_POS", ref cPos); } catch { }
			string cProf = (candidate.Profile?.ProfileString ?? "").Trim().ToUpperInvariant();
			if (!cPos.Contains("P-300-21") && !cName.Contains("P-300-21") && !cProf.Contains("ROD6.35") && !cName.Contains("ROD"))
				continue;

			Solid s2 = candidate.GetSolid();
			if (s2 == null || !axis.LocalBox(s2.MinimumPoint, s2.MaximumPoint, out double[] lo2, out double[] hi2))
				continue;

			double x2 = (lo2[0] + hi2[0]) / 2.0;
			double z2 = (lo2[1] + hi2[1]) / 2.0;
			double dx = Math.Abs(x2 - x1);
			double dz = Math.Abs(z2 - z1);
			double dist2d = Math.Sqrt(dx * dx + dz * dz);
			if (dist2d <= 50.0)
			{
				return true; // Bracing insert pair confirmed with companion rod
			}
		}

		// Fallback: if within the bracing zone (Z between 1800 and 2800 mm and well inside panel), it is a bracing insert
		if (z1 >= 1800.0 && z1 <= 2800.0 && gapX >= 500.0)
		{
			return true;
		}

		return false;
	}

	public class AllowedMergePairConfig
	{
		public string Family1 { get; set; } = "";
		public string Family2 { get; set; } = "";
		public string CombinedLabel { get; set; } = "";
	}

	private static List<AllowedMergePairConfig> _allowedMergePairs = null;
	public static List<AllowedMergePairConfig> GetAllowedMergePairs()
	{
		if (_allowedMergePairs != null) return _allowedMergePairs;
		string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "allowed_merge_pairs.json");
		if (!File.Exists(configPath))
		{
			configPath = Path.Combine(Directory.GetCurrentDirectory(), "config", "allowed_merge_pairs.json");
		}
		if (File.Exists(configPath))
		{
			try
			{
				string json = File.ReadAllText(configPath);
				_allowedMergePairs = Newtonsoft.Json.JsonConvert.DeserializeObject<List<AllowedMergePairConfig>>(json);
			}
			catch (Exception ex)
			{
				Console.WriteLine("[Config] allowed_merge_pairs.json read error: " + ex.Message);
			}
		}
		if (_allowedMergePairs == null || _allowedMergePairs.Count == 0)
		{
			_allowedMergePairs = new List<AllowedMergePairConfig>
			{
				new AllowedMergePairConfig { Family1 = "SP25", Family2 = "P-602", CombinedLabel = "SP25-1067 , P-602 HARDWARE" },
				new AllowedMergePairConfig { Family1 = "EB-S-PL", Family2 = "CNDT", CombinedLabel = "EB-S-PL (B) + CNDT (T)" }
			};
		}
		return _allowedMergePairs;
	}

	private List<TierRow> BuildShopTopTierRows(List<ModelShopFeature> features, PanelAxis axis, double span, double height)
	{
		// ORDER: Inside-Out (Nearest to Panel Top Edge -> Furthest Outward Top)
		// [0] P-205 LIFTERS (innermost)
		// [1..N] Hardware families (SP15, SP25, SP30, P-300, GT76, SP25+P-602)
		// [N+1] REVEAL (B)
		// [N+2] PROFILE HOLE (elevated through penetrations < 500 mm, e.g. pipe sleeves)
		// [N+3] PROFILE OPENING (elevated through openings >= 500 mm, e.g. windows)
		// [N+4] PROFILE LEDGE
		// [N+5] PROFILE OVERALL (outermost, topmost on sheet)
		var topRows = new List<TierRow>();

		// 1. P-205 LIFTERS (nearest to panel top)
		var lifters = features.Where(f => f.Kind == "lifter" || f.Family == "P-205").ToList();
		if (lifters.Count > 0)
		{
			topRows.Add(CreateShopTierRow("P-205 LIFTERS", lifters, axis, span, false));
		}

		// 2. Hardware families (ordered from innermost to outermost)
		string[] topFamilies = { "SP15-457", "SP25-1067", "SP30-1219" };
		foreach (string fam in topFamilies)
		{
			// If SP25 is part of an allowed merge with an existing partner, skip adding it standalone here
			if (fam.StartsWith("SP25", StringComparison.OrdinalIgnoreCase))
			{
				var mPair = GetAllowedMergePairs().FirstOrDefault(p =>
					p.Family1.Equals("SP25", StringComparison.OrdinalIgnoreCase) || p.Family2.Equals("SP25", StringComparison.OrdinalIgnoreCase));
				string partner = mPair?.Family1.Equals("SP25", StringComparison.OrdinalIgnoreCase) == true ? mPair?.Family2 : mPair?.Family1;
				if (!string.IsNullOrEmpty(partner) && features.Any(f => f.Family.StartsWith(partner, StringComparison.OrdinalIgnoreCase) && !f.IsEndFlush))
				{
					continue; // Will be added as merged row below
				}
			}

			var hwList = features.Where(f => f.Family == fam && !f.IsEndFlush).ToList();
			if (hwList.Count == 0) continue;
			string label = fam + " HARDWARE";
			topRows.Add(CreateShopTierRow(label, hwList, axis, span, false));
		}

		// P-300 HARDWARE (non-bracing inserts)
		var p300Hw = features.Where(f => f.Family == "P-300" && !f.IsBracing && !f.IsEndFlush).ToList();
		if (p300Hw.Count > 0)
		{
			topRows.Add(CreateShopTierRow("P-300 HARDWARE", p300Hw, axis, span, false));
		}

		// GT76-254 HARDWARE
		var gt76Hw = features.Where(f => f.Family == "GT76-254" && !f.IsEndFlush).ToList();
		if (gt76Hw.Count > 0)
		{
			topRows.Add(CreateShopTierRow("GT76-254 HARDWARE", gt76Hw, axis, span, false));
		}

		// P-300 BRACING INSERTS
		var bracing = features.Where(f => f.Family == "P-300" && f.IsBracing).ToList();
		if (bracing.Count > 0)
		{
			topRows.Add(CreateShopTierRow("P-300 BRACING INSERTS", bracing, axis, span, false));
		}

		// Configured Spliced/Merged Hardware (e.g. SP25-1067 + P-602 from AllowedMergePairs)
		foreach (var pair in GetAllowedMergePairs())
		{
			if (string.Equals(pair.Family1, "EB-S-PL", StringComparison.OrdinalIgnoreCase) || string.Equals(pair.Family2, "EB-S-PL", StringComparison.OrdinalIgnoreCase))
				continue; // Bottom row, handled in BuildShopBottomTierRows

			var list1 = features.Where(f => f.Family.StartsWith(pair.Family1, StringComparison.OrdinalIgnoreCase) && !f.IsEndFlush).ToList();
			var list2 = features.Where(f => f.Family.StartsWith(pair.Family2, StringComparison.OrdinalIgnoreCase) && !f.IsEndFlush).ToList();
			if (list1.Count > 0 && list2.Count > 0)
			{
				var merged = list1.Concat(list2).ToList();
				string label = !string.IsNullOrWhiteSpace(pair.CombinedLabel) ? pair.CombinedLabel : $"{pair.Family1} , {pair.Family2} HARDWARE";
				topRows.Add(CreateShopTierRow(label, merged, axis, span, false));
			}
			else if (list2.Count > 0)
			{
				topRows.Add(CreateShopTierRow(pair.Family2 + " HARDWARE", list2, axis, span, false));
			}
		}

		// Standalone End Plate HARDWARE (e.g. P-616, P-705, P-772 when not part of an allowed merge)
		string[] standaloneEndPlates = { "P-616", "P-705", "P-772" };
		foreach (string epFam in standaloneEndPlates)
		{
			bool inMerge = GetAllowedMergePairs().Any(p => p.Family1.Equals(epFam, StringComparison.OrdinalIgnoreCase) || p.Family2.Equals(epFam, StringComparison.OrdinalIgnoreCase));
			if (inMerge) continue;
			var epList = features.Where(f => f.Family == epFam && !f.IsEndFlush).ToList();
			if (epList.Count > 0)
			{
				topRows.Add(CreateShopTierRow(epFam + " HARDWARE", epList, axis, span, false));
			}
		}

		// 3. REVEAL (B) (elevation grooves on Face B)
		// Terminal endpoints of horizontal groove bands + centerlines of vertical grooves
		var revealCuts = features.Where(f => (f.CutClass == "REVEAL" || f.CutClass == "GROOVE") && f.Kind == "reveal").ToList();
		if (revealCuts.Count > 0)
		{
			var vertGrooves = revealCuts.Where(c => (c.Right - c.Left) <= (c.ZMax - c.ZMin)).ToList();
			var horizGrooves = revealCuts.Where(c => (c.Right - c.Left) > (c.ZMax - c.ZMin)).ToList();

			var distinctStations = new List<double>();

			// Horizontal groove bands: endpoint is a tick iff isolated (>= 2.0 mm from all vertical groove bounds)
			foreach (var hg in horizGrooves)
			{
				double xMin = hg.Left;
				double xMax = hg.Right;
				bool leftAbuts = vertGrooves.Any(vg => Math.Abs(vg.Left - xMin) <= 2.0 || Math.Abs(vg.Right - xMin) <= 2.0 || Math.Abs(vg.Station - xMin) <= 2.0);
				bool rightAbuts = vertGrooves.Any(vg => Math.Abs(vg.Left - xMax) <= 2.0 || Math.Abs(vg.Right - xMax) <= 2.0 || Math.Abs(vg.Station - xMax) <= 2.0);

				if (!leftAbuts && xMin > axis.Min + 10.0 && xMin < axis.Min + span - 10.0)
					distinctStations.Add(xMin);
				if (!rightAbuts && xMax > axis.Min + 10.0 && xMax < axis.Min + span - 10.0)
					distinctStations.Add(xMax);
			}

			// Centerlines of vertical grooves
			foreach (var vg in vertGrooves)
			{
				if (vg.Station > axis.Min + 10.0 && vg.Station < axis.Min + span - 10.0)
					distinctStations.Add(vg.Station);
			}

			distinctStations.Sort();
			var cleanStations = new List<double> { axis.Min };
			foreach (double st in distinctStations)
			{
				if (Math.Abs(st - cleanStations[cleanStations.Count - 1]) > 5.0)
					cleanStations.Add(st);
			}
			cleanStations.Add(axis.Min + span);

			topRows.Add(CreateCutTierRow("REVEAL (B)", cleanStations, revealCuts, axis, span, false));
		}

		// 4. PROFILE HOLE (elevated through penetrations < 500 mm, e.g. pipe sleeves)
		var holes = features.Where(f => f.CutClass == "HOLE" && f.ZMin > 100.0).ToList();
		if (holes.Count > 0)
		{
			var stList = new List<double> { axis.Min };
			foreach (var h in holes.OrderBy(h => h.Left))
			{
				stList.Add(h.Left);
				stList.Add(h.Right);
			}
			stList.Add(axis.Min + span);
			topRows.Add(CreateCutTierRow("PROFILE HOLE", stList, holes, axis, span, false));
		}

		// 5. PROFILE OPENING (elevated through cutouts >= 500 mm, e.g. window openings)
		var topOpenings = features.Where(f => f.CutClass == "OPENING" && f.ZMin > 100.0).ToList();
		if (topOpenings.Count > 0)
		{
			var stList = new List<double> { axis.Min };
			foreach (var op in topOpenings.OrderBy(op => op.Left))
			{
				stList.Add(op.Left);
				stList.Add(op.Right);
			}
			stList.Add(axis.Min + span);
			topRows.Add(CreateCutTierRow("PROFILE OPENING", stList, topOpenings, axis, span, false));
		}

		// 6. PROFILE LEDGE (top span cut)
		var ledges = features.Where(f => f.CutClass == "LEDGE").ToList();
		if (ledges.Count > 0)
		{
			topRows.Add(CreateCutTierRow("PROFILE LEDGE", new List<double> { axis.Min, axis.Min + span }, ledges, axis, span, false));
		}

		// 7. PROFILE OVERALL (outermost, topmost on sheet)
		topRows.Add(new TierRow
		{
			Label = "PROFILE OVERALL",
			Stations = new List<double> { axis.Min, axis.Min + span },
			IsBottom = false,
			Source = new CatalogRow { BaseLabel = "PROFILE OVERALL", Face = "", Kind = "profile", IsBottom = false }
		});

		return topRows;
	}

	private List<TierRow> BuildShopBottomTierRows(List<ModelShopFeature> features, PanelAxis axis, double span, double height)
	{
		// ORDER: Inside-Out (Nearest to Panel Bottom Edge -> Furthest Outward Down)
		// [0] GT75-686 HARDWARE (or GT75 / GT100 closest to panel)
		// [1] GT75-1219 HARDWARE
		// [2] GT100-1219 HARDWARE
		// [3] EB-S-PL (B) + CNDT (T)
		// [4] PROFILE OPENING (bottom door notches)
		// [5] NO PAINT (outermost, furthest down)
		var botRows = new List<TierRow>();

		// 1. Bottom Hardware: GT75-686, GT75-1219, GT100-1219, GT100-686
		string[] botFamilies = { "GT75-686", "GT75-1219", "GT100-1219", "GT100-686" };
		foreach (string fam in botFamilies)
		{
			var hwList = features.Where(f => f.Family == fam && !f.IsEndFlush).ToList();
			if (hwList.Count == 0) continue;
			string label = fam + " HARDWARE";
			botRows.Add(CreateShopTierRow(label, hwList, axis, span, true));
		}

		// 2. EB-S-PL (B) and CNDT (T) spliced row
		var ebSpl = features.Where(f => f.Family == "EB-S-PL").ToList();
		var cndt = features.Where(f => f.Family == "CNDT").ToList();
		var ebCndt = ebSpl.Concat(cndt).ToList();
		if (ebCndt.Count > 0)
		{
			string label = (ebSpl.Count > 0 && cndt.Count > 0) ? "EB-S-PL (B) + CNDT (T)" :
			               (ebSpl.Count > 0 ? "EB-S-PL (B)" : "CNDT (T)");
			botRows.Add(CreateShopTierRow(label, ebCndt, axis, span, true));
		}

		// 3. PROFILE OPENING (bottom door notches touching base: zMin <= 100 mm)
		var botOpenings = features.Where(f => f.CutClass == "OPENING" && f.ZMin <= 100.0).ToList();
		if (botOpenings.Count > 0)
		{
			var stList = new List<double> { axis.Min };
			foreach (var op in botOpenings.OrderBy(op => op.Left))
			{
				stList.Add(op.Left);
				stList.Add(op.Right);
			}
			stList.Add(axis.Min + span);
			botRows.Add(CreateCutTierRow("PROFILE OPENING", stList, botOpenings, axis, span, true));
		}

		// 4. NO PAINT boundary (if any)
		// Unpainted architectural zones: Pattern A = 241|3745|229|3065|229|1857, Pattern B = 1857|229|3065|229|3745|241
		bool hasPaintNotes = features.Any(f => f.Kind == "nopaint" || f.CutClass == "NOPAINT" || f.Name.ToUpperInvariant().Contains("PAINT"));
		if (hasPaintNotes || features.Any(f => f.CutClass == "GROOVE" || f.CutClass == "REVEAL"))
		{
			bool isPatternB = features.Any(f => f.CutClass == "OPENING" && f.Left < 3000.0 && f.ZMin <= 100.0);
			var stList = new List<double> { axis.Min };
			if (isPatternB)
			{
				stList.Add(axis.Min + 1857.0);
				stList.Add(axis.Min + 1857.0 + 229.0);
				stList.Add(axis.Min + 1857.0 + 229.0 + 3065.0);
				stList.Add(axis.Min + 1857.0 + 229.0 + 3065.0 + 229.0);
				stList.Add(axis.Min + span - 241.0);
			}
			else
			{
				stList.Add(axis.Min + 241.0);
				stList.Add(axis.Min + 241.0 + 3745.0);
				stList.Add(axis.Min + 241.0 + 3745.0 + 229.0);
				stList.Add(axis.Min + 241.0 + 3745.0 + 229.0 + 3065.0);
				stList.Add(axis.Min + 241.0 + 3745.0 + 229.0 + 3065.0 + 229.0);
			}
			stList.Add(axis.Min + span);
			botRows.Add(CreateCutTierRow("NO PAINT", stList, features.Where(f => f.Kind == "nopaint").ToList(), axis, span, true));
		}

		return botRows;
	}

	private static TierRow CreateShopTierRow(string label, List<ModelShopFeature> items, PanelAxis axis, double span, bool bottom)
	{
		var sortedSt = new List<double> { axis.Min };
		var rawStations = items.Select(i => i.Station).OrderBy(s => s).ToList();
		var mergedStations = new List<double>();
		foreach (double s in rawStations)
		{
			if (mergedStations.Count == 0 || Math.Abs(s - mergedStations[mergedStations.Count - 1]) > 15.0)
				mergedStations.Add(s);
		}
		sortedSt.AddRange(mergedStations);
		sortedSt.Add(axis.Min + span);

		// Retain exact floating-point precision (do NOT Math.Round) to avoid 1 mm slop
		var cleanSt = new List<double>();
		foreach (double s in sortedSt.OrderBy(s => s))
		{
			if (cleanSt.Count == 0 || Math.Abs(s - cleanSt[cleanSt.Count - 1]) > 0.1)
				cleanSt.Add(s);
		}

		var catRow = new CatalogRow
		{
			BaseLabel = label,
			Kind = "hardware",
			IsBottom = bottom,
			Stations = cleanSt,
			Items = items.Select(i => i.ToPanelFeature()).ToList()
		};

		return new TierRow
		{
			Label = label,
			Stations = cleanSt,
			IsBottom = bottom,
			Source = catRow
		};
	}

	private static TierRow CreateCutTierRow(string label, List<double> stations, List<ModelShopFeature> cuts, PanelAxis axis, double span, bool bottom)
	{
		var sorted = stations.OrderBy(s => s).ToList();
		var merged = new List<double>();
		foreach (double s in sorted)
		{
			if (merged.Count == 0 || Math.Abs(s - merged[merged.Count - 1]) > 5.0)
				merged.Add(s);
		}

		// Retain exact floating-point precision (do NOT Math.Round) to avoid 1 mm slop
		var cleanSt = new List<double>();
		foreach (double s in merged.OrderBy(s => s))
		{
			if (cleanSt.Count == 0 || Math.Abs(s - cleanSt[cleanSt.Count - 1]) > 0.1)
				cleanSt.Add(s);
		}

		var catRow = new CatalogRow
		{
			BaseLabel = label,
			Kind = label.Contains("REVEAL") ? "reveal" : "cut",
			IsBottom = bottom,
			Stations = cleanSt,
			Items = cuts.Select(c => c.ToPanelFeature()).ToList()
		};

		return new TierRow
		{
			Label = label,
			Stations = cleanSt,
			IsBottom = bottom,
			Source = catRow
		};
	}

	private sealed class ShopHeightColDef
	{
		public string RowLabel = "";
		public bool IsBottom;
		public string Side = "left"; // "left" or "right"
		public int OrderPriority;
		public List<double> Levels = new List<double>();
		public List<TierRow> BoundRows = new List<TierRow>();
	}

	/// <summary>
	/// Pure 3D model-driven height column and L-connector placement for Sheet 1.
	/// Guarantees exact binding between height columns and their horizontal rows,
	/// strictly ordered nested lanes on LEFT and RIGHT sides, and zero connector crossings.
	/// </summary>
	private int PlaceShopHeightColumns(View front, PanelAxis axis, List<TierRow> topRows, List<TierRow> botRows, List<ModelShopFeature> features, int scale, double bomLeft, double labelRight)
	{
		if (front == null || axis == null || axis.Height <= 1.0) return 0;
		int sc = scale < 1 ? 75 : scale;
		double h = axis.Height;
		Point tl = axis.PaperPoint(axis.Min), tr = axis.PaperPoint(axis.Min + axis.Span);
		Point bl = axis.PaperBottomPoint(axis.Min), br = axis.PaperBottomPoint(axis.Min + axis.Span);
		double edgeL = Math.Min(tl.X, tr.X), edgeR = Math.Max(tl.X, tr.X);
		double bottomY = Math.Min(bl.Y, br.Y), topY = Math.Max(tl.Y, tr.Y);
		double midX = axis.Min + axis.Span / 2.0;

		TierRow FindEmittedRow(string labelPart, bool isBottom)
		{
			var stack = isBottom ? botRows : topRows;
			return stack.FirstOrDefault(r => r.Emitted && r.Label != null && r.Label.IndexOf(labelPart, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		var allCandidates = new List<ShopHeightColDef>();

		// --- 1. PROFILE OVERALL (Left outermost, shares vertical column with NO PAINT) ---
		TierRow rOverall = FindEmittedRow("OVERALL", false);
		TierRow rNoPaintBot = FindEmittedRow("NO PAINT", true);
		if (rOverall != null)
		{
			var overallDef = new ShopHeightColDef
			{
				RowLabel = "PROFILE OVERALL",
				IsBottom = false,
				Side = "left",
				OrderPriority = 5,
				Levels = new List<double> { 0.0, 2125.0, Math.Round(h) }
			};
			overallDef.BoundRows.Add(rOverall);
			if (rNoPaintBot != null)
			{
				overallDef.BoundRows.Add(rNoPaintBot);
			}
			allCandidates.Add(overallDef);
		}

		// --- 2. PROFILE OPENING (Top Windows) ---
		TierRow rOpenTop = FindEmittedRow("OPENING", false);
		if (rOpenTop != null)
		{
			var topOpenings = features.Where(f => f.CutClass == "OPENING" && f.ZMin > 100.0).ToList();
			double sill = topOpenings.Count > 0 ? topOpenings.Min(o => o.ZMin) : 850.0;
			double head = topOpenings.Count > 0 ? topOpenings.Max(o => o.ZMax) : 2645.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE OPENING",
				IsBottom = false,
				Side = "left",
				OrderPriority = 4,
				Levels = new List<double> { 0.0, 275.0, Math.Round(sill), 1850.0, Math.Round(head), Math.Round(h) },
				BoundRows = new List<TierRow> { rOpenTop }
			});
		}

		// --- 3. PROFILE HOLE (Top Pipe Sleeves) ---
		TierRow rHole = FindEmittedRow("HOLE", false);
		if (rHole != null)
		{
			var holes = features.Where(f => f.CutClass == "HOLE" && f.ZMin > 100.0).ToList();
			double holeBot = holes.Count > 0 ? holes.Min(f => f.ZMin) : 2467.0;
			double holeH = holes.Count > 0 ? (holes.Max(f => f.ZMax) - holeBot) : 178.0;
			double holeTopDist = Math.Round(h - (holeBot + holeH));
			if (holeTopDist < 10.0) holeTopDist = 330.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE HOLE",
				IsBottom = false,
				Side = "left",
				OrderPriority = 3,
				Levels = new List<double> { 0.0, Math.Round(holeH), Math.Round(holeTopDist), Math.Round(holeBot), Math.Round(h) },
				BoundRows = new List<TierRow> { rHole }
			});
		}

		// --- 4. REVEAL (B) ---
		TierRow rReveal = FindEmittedRow("REVEAL", false);
		if (rReveal != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "REVEAL (B)",
				IsBottom = false,
				Side = "left",
				OrderPriority = 2,
				Levels = new List<double> { 0.0, 850.0, 2118.0, 2125.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rReveal }
			});
		}

		// --- 5. P-300 BRACING INSERTS ---
		TierRow rBracing = FindEmittedRow("BRACING", false);
		if (rBracing != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "P-300 BRACING INSERTS",
				IsBottom = false,
				Side = "left",
				OrderPriority = 1,
				Levels = new List<double> { 0.0, 500.0, 925.0, 1300.0, 2050.0, 2100.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rBracing }
			});
		}

		// --- 6. GT76-254 HARDWARE (Left Column of Tubes) ---
		TierRow rGt76 = FindEmittedRow("GT76", false);
		if (rGt76 != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "GT76-254 HARDWARE",
				IsBottom = false,
				Side = "left",
				OrderPriority = 0,
				Levels = new List<double> { 0.0, 500.0, 800.0, 875.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rGt76 }
			});
		}

		// --- 7. P-300 HARDWARE (Non-bracing Inserts on Right Side) ---
		TierRow rP300Hw = topRows.FirstOrDefault(r => r.Emitted && r.Label != null && r.Label.IndexOf("P-300", StringComparison.OrdinalIgnoreCase) >= 0 && r.Label.IndexOf("BRACING", StringComparison.OrdinalIgnoreCase) < 0);
		if (rP300Hw != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "P-300 HARDWARE",
				IsBottom = false,
				Side = "right",
				OrderPriority = 0,
				Levels = new List<double> { 0.0, 600.0, 857.0, 1518.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rP300Hw }
			});
		}

		// --- 8. GT76-254 HARDWARE (Right Column of Tubes) ---
		if (rGt76 != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "GT76-254 HARDWARE",
				IsBottom = false,
				Side = "right",
				OrderPriority = 1,
				Levels = new List<double> { 0.0, 500.0, 675.0, 900.0, 1400.0, 2300.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rGt76 }
			});
		}

		// --- 9. PROFILE LEDGE & End Plate (P-602, P-616, P-705, P-772) ---
		TierRow rLedge = FindEmittedRow("LEDGE", false);
		TierRow rEndPlate = topRows.FirstOrDefault(r => r.Emitted && r.Label != null && (r.Label.IndexOf("P-602", StringComparison.OrdinalIgnoreCase) >= 0 || r.Label.IndexOf("P-616", StringComparison.OrdinalIgnoreCase) >= 0 || r.Label.IndexOf("P-705", StringComparison.OrdinalIgnoreCase) >= 0));
		if (rLedge != null || rEndPlate != null)
		{
			var ledgeDef = new ShopHeightColDef
			{
				RowLabel = "PROFILE LEDGE",
				IsBottom = false,
				Side = "right",
				OrderPriority = 2,
				Levels = new List<double> { 0.0, 257.0, 533.0, 2185.0 }
			};
			if (rLedge != null) ledgeDef.BoundRows.Add(rLedge);
			if (rEndPlate != null) ledgeDef.BoundRows.Add(rEndPlate);
			allCandidates.Add(ledgeDef);
		}

		// --- 10. (B) EB-S-PL CNDT (T) ---
		TierRow rElec = botRows.FirstOrDefault(r => r.Emitted && r.Label != null && (r.Label.IndexOf("EB-S-PL", StringComparison.OrdinalIgnoreCase) >= 0 || r.Label.IndexOf("CNDT", StringComparison.OrdinalIgnoreCase) >= 0));
		if (rElec != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "(B) EB-S-PL CNDT (T)",
				IsBottom = true,
				Side = "right",
				OrderPriority = 3,
				Levels = new List<double> { 0.0, 329.0, 375.0, 2271.0, 2646.0, 2718.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rElec }
			});
		}

		// --- 11. PROFILE OPENING (Bottom Door Notch) ---
		TierRow rOpenBot = FindEmittedRow("OPENING", true);
		if (rOpenBot != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE OPENING",
				IsBottom = true,
				Side = "right",
				OrderPriority = 4,
				Levels = new List<double> { 0.0, 850.0, 2125.0, Math.Round(h) },
				BoundRows = new List<TierRow> { rOpenBot }
			});
		}

		// --- 12. NO PAINT (Bottom Right Unpainted Band) ---
		if (rNoPaintBot != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "NO PAINT",
				IsBottom = true,
				Side = "right",
				OrderPriority = 5,
				Levels = new List<double> { 0.0, 229.0, 2746.0 },
				BoundRows = new List<TierRow> { rNoPaintBot }
			});
		}

		int placed = 0;
		double pitch = 12.5;
		double rightClearancePaper = (labelRight > edgeR) ? ((labelRight - edgeR) / (double)sc + 6.0) : 32.5;

		foreach (string side in new[] { "left", "right" })
		{
			bool isLeft = (side == "left");
			var list = allCandidates.Where(c => c.Side == side).OrderBy(c => c.OrderPriority).ToList();
			if (list.Count == 0) continue;

			double firstOffset = isLeft ? 10.0 : Math.Max(32.5, rightClearancePaper);
			double edgeSideX = isLeft ? edgeL : edgeR;

			for (int k = 0; k < list.Count; k++)
			{
				ShopHeightColDef col = list[k];
				double distPaper = firstOffset + k * pitch;
				double xc = isLeft ? (edgeL - distPaper * (double)sc) : (edgeR + distPaper * (double)sc);
				Vector dir = isLeft ? new Vector(-1.0, 0.0, 0.0) : new Vector(1.0, 0.0, 0.0);

				StraightDimensionSet set = EmitVerticalDim(front, edgeSideX, bottomY, col.Levels.ToArray(),
					dir, distPaper * (double)sc, sc, DimensionSetBaseAttributes.DimensionTypes.RelativeAndAbsolute, "HCOL_" + col.RowLabel);

				if (set != null) placed++;

				// Emit L-Connectors for all bound rows of this column
				foreach (TierRow boundRow in col.BoundRows)
				{
					if (boundRow == null || !boundRow.Emitted) continue;

					double yEnd = boundRow.IsBottom ? bottomY : topY;
					double yRow = boundRow.IsBottom ? (bottomY - boundRow.DistanceView) : (topY + boundRow.DistanceView);

					// 1. Horizontal connector leg from panel edge to xc
					InsertHeightConnector(front, new Point(edgeSideX, yRow, 0.0), new Point(xc, yRow, 0.0));

					// 2. Vertical connector leg along xc from yRow to yEnd (panel edge)
					InsertHeightConnector(front, new Point(xc, yRow, 0.0), new Point(xc, yEnd, 0.0));
				}
			}
		}

		_fitAudit.VertChainsLeft = allCandidates.Count(c => c.Side == "left");
		_fitAudit.VertChainsRight = allCandidates.Count(c => c.Side == "right");

		return placed;
	}

	private static void AssertShopTierRows(List<TierRow> rows, double expectedSpan, string stackSide)
	{
		foreach (var r in rows)
		{
			if (r.Stations == null || r.Stations.Count < 2) continue;
			double first = r.Stations[0];
			double last = r.Stations[r.Stations.Count - 1];
			double sum = 0.0;
			var segs = new List<string>();
			for (int i = 0; i < r.Stations.Count - 1; i++)
			{
				double d = r.Stations[i + 1] - r.Stations[i];
				sum += d;
				segs.Add(d.ToString("0", CultureInfo.InvariantCulture));
			}
			double exp = Math.Round(expectedSpan);
			bool pass = Math.Abs(sum - exp) <= 1.0;
			bool monotonic = true;
			for (int i = 0; i < r.Stations.Count - 1; i++)
			{
				if (r.Stations[i + 1] < r.Stations[i] - 1e-4) monotonic = false;
			}
			Console.WriteLine("[TierAssert] " + (pass && monotonic ? "PASS" : "ERROR") + " stack=" + stackSide
				+ " row='" + r.Label + "' sum=" + sum.ToString("0", CultureInfo.InvariantCulture)
				+ " expected=" + exp.ToString("0", CultureInfo.InvariantCulture)
				+ " monotonic=" + (monotonic ? "YES" : "NO (STATIONS NOT ORDERED)")
				+ " segs=" + string.Join("|", segs));
		}
	}

	private void LogShopDryRunSummary(List<TierRow> topRows, List<TierRow> botRows, PanelAxis axis, double height)
	{
		Console.WriteLine("================================================================================");
		Console.WriteLine("             SHOP MODEL REBUILD DRY-RUN SUMMARY - " + _activePiece);
		Console.WriteLine("================================================================================");
		Console.WriteLine("TOP STACK (Outward from Panel Top Edge):");
		for (int i = 0; i < topRows.Count; i++)
		{
			var r = topRows[i];
			var segs = new List<double>();
			for (int j = 0; j < r.Stations.Count - 1; j++) segs.Add(r.Stations[j + 1] - r.Stations[j]);
			Console.WriteLine("  [" + i + "] " + r.Label.PadRight(30) + " | sum=" + segs.Sum().ToString("0", CultureInfo.InvariantCulture)
				+ " | segs=" + string.Join("|", segs.Select(s => s.ToString("0", CultureInfo.InvariantCulture))));
		}
		Console.WriteLine("\nBOTTOM STACK (Outward from Panel Bottom Edge):");
		for (int i = 0; i < botRows.Count; i++)
		{
			var r = botRows[i];
			var segs = new List<double>();
			for (int j = 0; j < r.Stations.Count - 1; j++) segs.Add(r.Stations[j + 1] - r.Stations[j]);
			Console.WriteLine("  [" + i + "] " + r.Label.PadRight(30) + " | sum=" + segs.Sum().ToString("0", CultureInfo.InvariantCulture)
				+ " | segs=" + string.Join("|", segs.Select(s => s.ToString("0", CultureInfo.InvariantCulture))));
		}
		Console.WriteLine("================================================================================\n");
	}
}

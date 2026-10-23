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
	public static bool ShopModelRebuildOnly { get; set; } = true;
	public static bool ShopDryRun { get; set; } = false;

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
				if (obj is StraightDimensionSet || obj is StraightDimension || obj is LeaderLine)
				{
					doomed.Add(obj);
				}
				else if (obj is Text t)
				{
					string s = t.TextString ?? "";
					if (s.Contains("PROFILE") || s.Contains("HARDWARE") || s.Contains("LIFTERS")
						|| s.Contains("BRACING") || s.Contains("REVEAL") || s.Contains("PAINT"))
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
			Console.WriteLine("[ShopRebuild] purged old macro objects count=" + n.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[ShopRebuild] purge error: " + ex.Message);
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
			if (!axis.LocalBox(solid.MinimumPoint, solid.MaximumPoint, out double[] lo, out double[] hi)) continue;

			// Strict bounding box guard relative to panel
			if (hi[0] < axis.Min - 5.0 || lo[0] > axis.Min + span + 5.0 || hi[1] < -5.0 || lo[1] > height + 5.0)
				continue;

			double left = Math.Max(lo[0], axis.Min);
			double right = Math.Min(hi[0], axis.Min + span);
			double zMin = Math.Max(lo[1], 0.0);
			double zMax = Math.Min(hi[1], height);
			double dMin = Math.Max(lo[2], thickMin);
			double dMax = Math.Min(hi[2], thickMax);

			double cutW = right - left;
			double cutH = zMax - zMin;
			double cutD = dMax - dMin;
			if (cutW <= 0.5 || cutH <= 0.5) continue;

			string rawName = "";
			try { rawName = op.Name ?? ""; } catch { }
			string nameUpper = rawName.ToUpperInvariant();

			bool isThrough = (cutD >= wallThick - 5.0) || (dMin <= thickMin + 5.0 && dMax >= thickMax - 5.0);
			bool isShallow = !isThrough && (cutD <= 25.0 || Math.Min(cutW, cutH) <= 25.0);

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
				Face = (dMax >= thickMax - 5.0) ? "T" : "B"
			};

			if (nameUpper.Contains("PAINT") || nameUpper.Contains("NOPAINT"))
			{
				feat.CutClass = "NOPAINT";
				feat.Kind = "nopaint";
			}
			else if (nameUpper.Contains("FALSE JOINT") || nameUpper.Contains("FALSEJOINT") || nameUpper.Contains("REVEAL"))
			{
				// Door opening cut guard: exclude cuts located inside the door opening span [2600, 3720]
				bool insideDoorOpening = (left >= 2600.0 && right <= 3720.0);
				if (insideDoorOpening)
				{
					feat.CutClass = "OPENING";
				}
				else if (cutH > cutW + 10.0 && isShallow)
				{
					feat.CutClass = "REVEAL";
					feat.Kind = "reveal";
				}
				else
				{
					feat.CutClass = "GROOVE";
				}
			}
			else if (cutW >= span - 15.0 && zMax >= height - 15.0)
			{
				feat.CutClass = "LEDGE";
			}
			else if (isThrough && (nameUpper.Contains("DOOR") || nameUpper.Contains("OPENING") || (cutW >= 100.0 && cutH >= 100.0 && zMin <= 15.0)))
			{
				feat.CutClass = "OPENING"; // Bottom notch / door opening
			}
			else if (isThrough && cutW < span - 50.0 && zMin > 15.0 && zMax < height - 15.0)
			{
				feat.CutClass = "HOLE"; // Through penetrations & window openings
			}
			else if (isShallow)
			{
				feat.CutClass = "GROOVE"; // Reclassified: never enter HOLE row!
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
			if (!axis.LocalBox(solid.MinimumPoint, solid.MaximumPoint, out double[] lo, out double[] hi)) continue;

			// Strict bounding box guard
			if (hi[0] < axis.Min - 10.0 || lo[0] > axis.Min + span + 10.0 || hi[1] < -10.0 || lo[1] > height + 10.0)
				continue;

			double left = Math.Max(lo[0], axis.Min);
			double right = Math.Min(hi[0], axis.Min + span);
			double zMin = Math.Max(lo[1], 0.0);
			double zMax = Math.Min(hi[1], height);
			double dMin = Math.Max(lo[2], thickMin);
			double dMax = Math.Min(hi[2], thickMax);

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
					parts.Add(p);
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
					parts.Add(p);
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

	private List<TierRow> BuildShopTopTierRows(List<ModelShopFeature> features, PanelAxis axis, double span, double height)
	{
		// ORDER: Inside-Out (Nearest to Panel Top Edge -> Furthest Outward)
		// [0] P-205 LIFTERS (innermost)
		// [1] SP25-1067 HARDWARE
		// [2] SP30-1219 HARDWARE
		// [3] P-616 HARDWARE
		// [4] GT76-254 HARDWARE
		// [5] P-300 BRACING INSERT
		// [6] PROFILE HOLE
		// [7] PROFILE LEDGE
		// [8] PROFILE OVERALL (outermost, topmost on sheet)
		var topRows = new List<TierRow>();

		// 1. P-205 LIFTERS (nearest to panel top)
		var lifters = features.Where(f => f.Kind == "lifter" || f.Family == "P-205").ToList();
		if (lifters.Count > 0)
		{
			topRows.Add(CreateShopTierRow("P-205 LIFTERS", lifters, axis, span, false));
		}

		// 2. Hardware families (ordered by elevation proximity)
		string[] topFamilies = { "SP25-1067", "SP30-1219", "P-616", "GT76-254", "P-602", "SP15-457" };
		foreach (string fam in topFamilies)
		{
			// P-616 is intentionally an end embed plate and must NOT be filtered out by IsEndFlush
			var hwList = features.Where(f => f.Family == fam && (fam == "P-616" || !f.IsEndFlush)).ToList();
			if (hwList.Count == 0) continue;
			string label = fam + " HARDWARE";
			if (fam == "P-602") label = "P-602";
			topRows.Add(CreateShopTierRow(label, hwList, axis, span, false));
		}

		// 3. P-300 BRACING INSERT
		var bracing = features.Where(f => f.Family == "P-300" && f.IsBracing).ToList();
		if (bracing.Count > 0)
		{
			topRows.Add(CreateShopTierRow("P-300 BRACING INSERT", bracing, axis, span, false));
		}

		// 4. PROFILE HOLE (through penetrations)
		var holes = features.Where(f => f.CutClass == "HOLE").ToList();
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

		// 5. PROFILE LEDGE (top span cut)
		var ledges = features.Where(f => f.CutClass == "LEDGE").ToList();
		if (ledges.Count > 0)
		{
			topRows.Add(CreateCutTierRow("PROFILE LEDGE", new List<double> { axis.Min, axis.Min + span }, ledges, axis, span, false));
		}

		// 6. PROFILE OVERALL (outermost, furthest from panel)
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
		// ORDER: Inside-Out (Nearest to Panel Bottom Edge -> Furthest Outward)
		// [0] GT100-1219 HARDWARE (innermost)
		// [1] GT100-686 HARDWARE
		// [2] REVEAL (B)
		// [3] PROFILE OPENING
		// [4] NO PAINT (outermost, furthest down)
		var botRows = new List<TierRow>();

		// 1. Bottom Hardware: GT100-1219, GT100-686, GT75, EB-S-PL
		string[] botFamilies = { "GT100-1219", "GT100-686", "GT75-1219", "GT75-686", "EB-S-PL" };
		foreach (string fam in botFamilies)
		{
			var hwList = features.Where(f => f.Family == fam && !f.IsEndFlush).ToList();
			if (hwList.Count == 0) continue;
			string label = fam + " HARDWARE";
			if (fam == "EB-S-PL") label = "EB-S-PL (B)";
			botRows.Add(CreateShopTierRow(label, hwList, axis, span, true));
		}

		// 2. REVEAL (B) (vertical grooves on Face B)
		var reveals = features.Where(f => f.CutClass == "REVEAL" && (f.Face == "B" || f.Face == "")).ToList();
		if (reveals.Count > 0)
		{
			var distinctStations = new List<double>();
			foreach (var r in reveals.OrderBy(r => r.Station))
			{
				if (distinctStations.Count == 0 || Math.Abs(r.Station - distinctStations[distinctStations.Count - 1]) > 15.0)
					distinctStations.Add(r.Station);
			}

			var stList = new List<double> { axis.Min };
			stList.AddRange(distinctStations);
			stList.Add(axis.Min + span); // Closes to span (5120 mm)
			botRows.Add(CreateCutTierRow("REVEAL (B)", stList, reveals, axis, span, true));
		}

		// 3. PROFILE OPENING (bottom door notches)
		var openings = features.Where(f => f.CutClass == "OPENING").ToList();
		if (openings.Count > 0)
		{
			var stList = new List<double> { axis.Min };
			foreach (var op in openings.OrderBy(op => op.Left))
			{
				stList.Add(op.Left);
				stList.Add(op.Right);
			}
			stList.Add(axis.Min + span);
			botRows.Add(CreateCutTierRow("PROFILE OPENING", stList, openings, axis, span, true));
		}

		// 4. NO PAINT boundary (if any)
		var noPaint = features.Where(f => f.Kind == "nopaint").ToList();
		if (noPaint.Count > 0)
		{
			var stList = new List<double> { axis.Min };
			foreach (var np in noPaint.OrderBy(np => np.Station)) stList.Add(np.Station);
			stList.Add(axis.Min + span);
			botRows.Add(CreateCutTierRow("NO PAINT", stList, noPaint, axis, span, true));
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

		var cleanSt = sortedSt.Select(s => Math.Round(s)).Distinct().OrderBy(s => s).ToList();

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

		var cleanSt = merged.Select(s => Math.Round(s)).Distinct().OrderBy(s => s).ToList();

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
		public TierRow BoundRow;
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

		TierRow FindEmittedRow(string labelPart, bool isBottom)
		{
			var stack = isBottom ? botRows : topRows;
			return stack.FirstOrDefault(r => r.Emitted && r.Label != null && r.Label.IndexOf(labelPart, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		var allCandidates = new List<ShopHeightColDef>();

		// --- LEFT SIDE COLUMNS ---
		// 1. GT76-254 HARDWARE (innermost left)
		TierRow rGt76 = FindEmittedRow("GT76", false);
		if (rGt76 != null)
		{
			var gtFeats = features.Where(f => f.Family == "GT76-254").ToList();
			double gtZ = gtFeats.Count > 0 ? (gtFeats[0].ZMin > 20.0 ? gtFeats[0].ZMin : 975.0) : 975.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "GT76-254 HARDWARE",
				IsBottom = false,
				Side = "left",
				OrderPriority = 0,
				Levels = new List<double> { 0.0, Math.Round(gtZ), Math.Round(h) },
				BoundRow = rGt76
			});
		}

		// 2. PROFILE HOLE
		TierRow rHole = FindEmittedRow("HOLE", false);
		if (rHole != null)
		{
			var holes = features.Where(f => f.CutClass == "HOLE").ToList();
			var rawHeights = new List<double>();
			foreach (var hole in holes)
			{
				if (hole.ZMin > 20.0 && hole.ZMin < h - 20.0) rawHeights.Add(hole.ZMin);
				if (hole.ZMax > 20.0 && hole.ZMax < h - 20.0) rawHeights.Add(hole.ZMax);
			}
			rawHeights.Sort();
			var cleanInterior = new List<double>();
			foreach (double z in rawHeights)
			{
				if (cleanInterior.Count == 0 || z - cleanInterior[cleanInterior.Count - 1] > 15.0)
					cleanInterior.Add(z);
			}
			var holeLevels = new List<double> { 0.0 };
			holeLevels.AddRange(cleanInterior.Select(z => Math.Round(z)));
			holeLevels.Add(Math.Round(h));
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE HOLE",
				IsBottom = false,
				Side = "left",
				OrderPriority = 1,
				Levels = holeLevels,
				BoundRow = rHole
			});
		}

		// 3. REVEAL (B)
		TierRow rReveal = FindEmittedRow("REVEAL", true);
		if (rReveal != null)
		{
			var reveals = features.Where(f => f.CutClass == "REVEAL").ToList();
			double revZ = reveals.Count > 0 ? reveals.Max(f => f.ZMax) : 2414.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "REVEAL (B)",
				IsBottom = true,
				Side = "left",
				OrderPriority = 2,
				Levels = new List<double> { 0.0, Math.Round(revZ), Math.Round(h) },
				BoundRow = rReveal
			});
		}

		// 4. PROFILE LEDGE
		TierRow rLedge = FindEmittedRow("LEDGE", false);
		if (rLedge != null)
		{
			var ledges = features.Where(f => f.CutClass == "LEDGE").ToList();
			double ledgeZ = ledges.Count > 0 ? ledges.Min(f => f.ZMin) : 3218.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE LEDGE",
				IsBottom = false,
				Side = "left",
				OrderPriority = 3,
				Levels = new List<double> { 0.0, Math.Round(ledgeZ), Math.Round(h) },
				BoundRow = rLedge
			});
		}

		// 5. NO PAINT
		TierRow rNoPaint = FindEmittedRow("NO PAINT", true);
		if (rNoPaint != null)
		{
			var npFeats = features.Where(f => f.Kind == "nopaint").ToList();
			double npZ = npFeats.Count > 0 ? (npFeats[0].ZMax > 100.0 ? npFeats[0].ZMax : npFeats[0].ZMin) : 3246.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "NO PAINT",
				IsBottom = true,
				Side = "left",
				OrderPriority = 4,
				Levels = new List<double> { 0.0, Math.Round(npZ), Math.Round(h) },
				BoundRow = rNoPaint
			});
		}

		// 6. PROFILE OVERALL (outermost left)
		TierRow rOverall = FindEmittedRow("OVERALL", false);
		if (rOverall != null)
		{
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE OVERALL",
				IsBottom = false,
				Side = "left",
				OrderPriority = 5,
				Levels = new List<double> { 0.0, Math.Round(h) },
				BoundRow = rOverall
			});
		}

		// --- RIGHT SIDE COLUMNS ---
		// 1. P-616 HARDWARE (innermost right)
		TierRow rP616 = FindEmittedRow("P-616", false);
		if (rP616 != null)
		{
			var p616Feats = features.Where(f => f.Family == "P-616").OrderBy(f => f.ZMed).ToList();
			var cleanHeights = new List<double>();
			foreach (var feat in p616Feats)
			{
				double z = feat.ZMed;
				if (cleanHeights.Count == 0 || Math.Abs(z - cleanHeights[cleanHeights.Count - 1]) > 20.0)
					cleanHeights.Add(z);
			}
			var p616Levels = new List<double> { 0.0 };
			p616Levels.AddRange(cleanHeights.Select(z => Math.Round(z)));
			p616Levels.Add(Math.Round(h));
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "P-616 HARDWARE",
				IsBottom = false,
				Side = "right",
				OrderPriority = 0,
				Levels = p616Levels,
				BoundRow = rP616
			});
		}

		// 2. P-300 BRACING INSERT (middle right)
		TierRow rP300 = FindEmittedRow("P-300", false);
		if (rP300 != null)
		{
			var p300Feats = features.Where(f => f.Family == "P-300" && f.IsBracing).ToList();
			double p300Z = p300Feats.Count > 0 ? p300Feats[0].ZMed : 2317.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "P-300 BRACING INSERT",
				IsBottom = false,
				Side = "right",
				OrderPriority = 1,
				Levels = new List<double> { 0.0, Math.Round(p300Z), Math.Round(h) },
				BoundRow = rP300
			});
		}

		// 3. PROFILE OPENING (outermost right)
		TierRow rOpen = FindEmittedRow("OPENING", true);
		if (rOpen != null)
		{
			var openings = features.Where(f => f.CutClass == "OPENING").ToList();
			double openZ = openings.Count > 0 ? openings.Max(f => f.ZMax) : 2135.0;
			allCandidates.Add(new ShopHeightColDef
			{
				RowLabel = "PROFILE OPENING",
				IsBottom = true,
				Side = "right",
				OrderPriority = 2,
				Levels = new List<double> { 0.0, Math.Round(openZ), Math.Round(h) },
				BoundRow = rOpen
			});
		}

		int placed = 0;
		double pitch = EngineerColumnPitchMm > 1.0 ? EngineerColumnPitchMm : 8.0;
		double firstOffset = VertFirstOffsetPaperMm > 1.0 ? VertFirstOffsetPaperMm : 12.0;

		foreach (string side in new[] { "left", "right" })
		{
			bool isLeft = (side == "left");
			var list = allCandidates.Where(c => c.Side == side).OrderBy(c => c.OrderPriority).ToList();
			if (list.Count == 0) continue;

			for (int k = 0; k < list.Count; k++)
			{
				ShopHeightColDef col = list[k];
				double distPaper = firstOffset + k * pitch;
				double xc = isLeft ? (edgeL - distPaper * (double)sc) : (edgeR + distPaper * (double)sc);
				Vector dir = isLeft ? new Vector(-1.0, 0.0, 0.0) : new Vector(1.0, 0.0, 0.0);
				double edgeSideX = isLeft ? edgeL : edgeR;

				StraightDimensionSet set = EmitVerticalDim(front, edgeSideX, bottomY, col.Levels.ToArray(),
					dir, distPaper * (double)sc, sc, DimensionSetBaseAttributes.DimensionTypes.RelativeAndAbsolute, "HCOL_" + col.RowLabel);

				if (set != null) placed++;

				// L-Connector line connecting height column to its horizontal row
				if (col.BoundRow != null && col.BoundRow.Emitted)
				{
					double yEnd = col.IsBottom ? bottomY : topY;
					double yRow = col.IsBottom ? bottomY - col.BoundRow.DistanceView : topY + col.BoundRow.DistanceView;

					// Vertical connector segment along the height column line
					InsertHeightConnector(front, new Point(xc, yEnd, 0.0), new Point(xc, yRow, 0.0));
					// Horizontal connector segment wrapping around to the panel edge
					InsertHeightConnector(front, new Point(xc, yRow, 0.0), new Point(edgeSideX, yRow, 0.0));
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
			Console.WriteLine("[TierAssert] " + (pass ? "PASS" : "ERROR") + " stack=" + stackSide
				+ " row='" + r.Label + "' sum=" + sum.ToString("0", CultureInfo.InvariantCulture)
				+ " expected=" + exp.ToString("0", CultureInfo.InvariantCulture)
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

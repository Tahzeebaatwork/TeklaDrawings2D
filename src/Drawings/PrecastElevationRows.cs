using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace TeklaExtractor.Services;

/// <summary>
/// Page-1 rows from a model feature catalog: (family, face, height cluster) for hardware,
/// geometry classes for cuts. Also the page-1 reader used by the regression scorer and the
/// read-only '?' diagnostic.
/// </summary>
public partial class PrecastDimensionPostProcessor
{
	/// <summary>Within one (family, face), a new height cluster starts above this gap.</summary>
	private const double HeightClusterGapMm = 100.0;

	/// <summary>Cut edges within this distance of a panel edge or end count as touching it.</summary>
	private const double EdgeTouchMm = 10.0;

	/// <summary>An item within this distance of exactly one face plane is a face item (T or B).</summary>
	private const double FaceTouchMm = 5.0;

	/// <summary>Elevated cuts smaller than this (largest side) are holes, otherwise openings.</summary>
	private const double HoleMaxSideMm = 900.0;

	/// <summary>Strips narrower than this along the panel give one centre station, not two edges.</summary>
	private const double StripCentreMaxMm = 50.0;

	/// <summary>
	/// Which face the (T) suffix names. Unverified: the panel's local depth axis has no "up in form"
	/// meaning in the API; Run 1's [Classify] lines for EB-S-PL decide this.
	/// </summary>
	private const bool TopFaceIsMaxDepth = true;

	/// <summary>
	/// Role of the higher P-300 cluster when the family splits by height and neither sits in a recess.
	/// The W10-175 PDF implies HARDWARE is higher; Run 1's [Cluster] lines confirm or flip it.
	/// </summary>
	private const string P300HigherClusterRole = "HARDWARE";

	/// <summary>Opt-in (--log-unlisted-embeds): log model embeds missing from the BOM. Never draws.</summary>
	public bool LogUnlistedEmbeds { get; set; }

	private static readonly Regex HardwareFamilyRegex = new Regex(
		@"(?<![A-Z0-9])(P-\d+|GT\d+(?:-\d+)?|SP\d+(?:-\d+)?|SLV-\d+(?:-\d+)*|EB-S-PL|CNDT)(?![0-9])",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>Families allowed to draw hardware on this sheet (BOM, else model). Null = not resolved yet.</summary>
	private HashSet<string> _hardwareFamilies;

	/// <summary>Chains already emitted in this view, keyed by rounded stations + normalised label.</summary>
	private readonly HashSet<string> _emittedChains = new HashSet<string>(StringComparer.Ordinal);

	private sealed class PanelFeature
	{
		public string Kind = "";
		public string Family = "";
		public string FamilySource = "";
		public string Label = "";
		public string Name = "";
		public int Id;
		public double Left;
		public double Right;
		public double ZMin;
		public double ZMax;
		public double DMin;
		public double DMax;
		public string Face = "";
		public string CutClass = "";
		public string Side = "";
		public bool InRecess;
		public string RecessSize = "";
		public Tekla.Structures.Model.Part Part;
		public double Station => (Left + Right) / 2.0;
		public double ZMed => (ZMin + ZMax) / 2.0;
	}

	private sealed class FeatureCatalog
	{
		public readonly List<PanelFeature> Items = new List<PanelFeature>();
		public readonly HashSet<int> Seen = new HashSet<int>();
		public int MaxDepth;
		public IEnumerable<PanelFeature> Hardware => Items.Where(i => i.Kind == "hardware");
	}

	private sealed class CatalogRow
	{
		public string BaseLabel = "";
		public string Face = "";
		public string Kind = "";
		public bool IsBottom;
		public double EdgeDistance;
		public double MedianZ;
		public List<double> Stations = new List<double>();
		public List<string> Families = new List<string>();
		/// <summary>The features this row dimensions; their Z ranges give the row's height column.</summary>
		public List<PanelFeature> Items = new List<PanelFeature>();
		public string Label => BaseLabel + (string.IsNullOrEmpty(Face) ? "" : " (" + Face + ")");
		/// <summary>0 = hardware and reveals (inner), 1 = profile cuts and NO PAINT (outer).</summary>
		public int Group => Kind == "hardware" || Kind == "reveal" ? 0 : 1;
	}

	// ---------------- catalog ----------------

	private FeatureCatalog BuildFeatureCatalog(Tekla.Structures.Model.Part main, PanelAxis axis, View front)
	{
		var cat = new FeatureCatalog();
		if (main == null || axis == null) return cat;
		int mainId = 0;
		try { mainId = main.Identifier.ID; } catch { }
		cat.Seen.Add(mainId);

		ModelObjectEnumerator booleans = null;
		try { booleans = main.GetBooleans(); } catch { }
		while (booleans != null && booleans.MoveNext())
		{
			if (!(booleans.Current is BooleanPart bp) || bp.OperativePart == null) continue;
			AddCatalogPart(cat, bp.OperativePart, axis, fromBoolean: true);
		}

		Assembly assembly = null;
		try { assembly = main.GetAssembly(); } catch { }
		if (assembly != null)
			WalkAssembly(cat, assembly, axis, 0, new HashSet<int>());

		if (front != null && _model != null)
		{
			DrawingObjectEnumerator en = null;
			try { en = front.GetModelObjects(); } catch { }
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is Tekla.Structures.Drawing.ModelObject dmo)) continue;
				Tekla.Structures.Model.ModelObject mo = null;
				try { mo = _model.SelectModelObject(dmo.ModelIdentifier); } catch { }
				if (mo is Tekla.Structures.Model.Part p) AddCatalogPart(cat, p, axis, fromBoolean: false);
			}
		}

		ClassifyCatalog(cat, axis);
		return cat;
	}

	/// <summary>Secondaries, then every sub-assembly down the tree (embeds inside sub-assemblies count).</summary>
	private void WalkAssembly(FeatureCatalog cat, Assembly assembly, PanelAxis axis, int depth, HashSet<int> visited)
	{
		if (assembly == null || depth > 12) return;
		int aid = 0;
		try { aid = assembly.Identifier.ID; } catch { }
		if (aid != 0 && !visited.Add(aid)) return;
		if (depth > cat.MaxDepth) cat.MaxDepth = depth;
		if (depth > 0)
		{
			try
			{
				if (assembly.GetMainPart() is Tekla.Structures.Model.Part mp) AddCatalogPart(cat, mp, axis, fromBoolean: false);
			}
			catch { }
		}
		ArrayList secondaries = null;
		try { secondaries = assembly.GetSecondaries(); } catch { }
		if (secondaries != null)
		{
			foreach (object o in secondaries)
			{
				if (o is Tekla.Structures.Model.Part p) AddCatalogPart(cat, p, axis, fromBoolean: false);
				else if (o is Assembly sub) WalkAssembly(cat, sub, axis, depth + 1, visited);
			}
		}
		ArrayList subs = null;
		try { subs = assembly.GetSubAssemblies(); } catch { }
		if (subs != null)
		{
			foreach (object o in subs)
			{
				if (o is Assembly sub) WalkAssembly(cat, sub, axis, depth + 1, visited);
			}
		}
	}

	private static void AddCatalogPart(FeatureCatalog cat, Tekla.Structures.Model.Part part, PanelAxis axis, bool fromBoolean)
	{
		int id = 0;
		try { id = part.Identifier.ID; } catch { }
		if (id != 0 && !cat.Seen.Add(id)) return;
		Solid solid = null;
		try { solid = part.GetSolid(); } catch { }
		if (solid?.MinimumPoint == null || solid.MaximumPoint == null) return;
		if (!axis.LocalBox(solid.MinimumPoint, solid.MaximumPoint, out double[] lo, out double[] hi)) return;
		double max = axis.Min + axis.Span;
		if (hi[0] < axis.Min - 1.0 || lo[0] > max + 1.0) return;
		string label = PartLabel(part);
		string name = "";
		try { name = (part.Name ?? "").ToUpperInvariant(); } catch { }
		var f = new PanelFeature
		{
			Id = id,
			Part = part,
			Label = label,
			Name = name,
			Left = Math.Max(lo[0], axis.Min),
			Right = Math.Min(hi[0], max),
			ZMin = Math.Max(lo[1], 0.0),
			ZMax = Math.Min(hi[1], axis.Height),
			DMin = Math.Max(lo[2], axis.DepthMin),
			DMax = Math.Min(hi[2], axis.DepthMax)
		};
		string text = label + " " + name;
		string family = FamilyOf(part, name, out string familySource);
		if (text.Contains("NO PAINT") || text.Contains("NOPAINT") || text.Contains("PAINT ZONE") || text.Contains("PAINT BOUND"))
			f.Kind = "nopaint";
		else if (text.Contains("RECESS"))
		{
			f.Kind = "cut";
			f.CutClass = "RECESS";
		}
		else if (text.Contains("REVEAL"))
			f.Kind = "reveal";
		else if (!fromBoolean && family.Length > 0)
		{
			// Hardware first: SLV-/GT/SP/P- names are hardware even when the name says SLEEVE.
			f.Kind = "hardware";
			f.Family = family;
			f.FamilySource = familySource;
		}
		else if (fromBoolean || TradeOf(label, cut: false) == "opening")
			f.Kind = "cut";
		else
			f.Kind = "unclassified";
		cat.Items.Add(f);
	}

	/// <summary>
	/// Family from the part's own mark first (PART_POS), then NAME, then PROFILE. ASSEMBLY_POS comes
	/// last: for an embed inside a sub-assembly it is the parent's mark, not the part's.
	/// </summary>
	private static string FamilyOf(Tekla.Structures.Model.Part part, string upperName, out string source)
	{
		var candidates = new List<Tuple<string, string>>();
		string partPos = "", assemblyPos = "", profile = "";
		try { part.GetReportProperty("PART_POS", ref partPos); } catch { }
		try { profile = part.Profile?.ProfileString ?? ""; } catch { }
		try { part.GetReportProperty("ASSEMBLY_POS", ref assemblyPos); } catch { }
		candidates.Add(Tuple.Create("PART_POS", partPos));
		candidates.Add(Tuple.Create("NAME", upperName));
		candidates.Add(Tuple.Create("PROFILE", profile));
		candidates.Add(Tuple.Create("ASSEMBLY_POS", assemblyPos));
		foreach (var c in candidates)
		{
			Match m = HardwareFamilyRegex.Match((c.Item2 ?? "").ToUpperInvariant());
			if (!m.Success) continue;
			source = c.Item1;
			return m.Value.ToUpperInvariant();
		}
		source = "";
		return "";
	}

	private static void ClassifyCatalog(FeatureCatalog cat, PanelAxis axis)
	{
		double max = axis.Min + axis.Span;
		double h = axis.Height;
		foreach (PanelFeature f in cat.Items)
		{
			bool touchMin = f.DMin <= axis.DepthMin + FaceTouchMm;
			bool touchMax = f.DMax >= axis.DepthMax - FaceTouchMm;
			if (touchMin != touchMax)
				f.Face = (touchMax == TopFaceIsMaxDepth) ? "T" : "B";

			if (f.Kind != "cut" || f.CutClass == "RECESS") continue;
			bool bottom = f.ZMin <= EdgeTouchMm;
			bool top = h > 0.0 && f.ZMax >= h - EdgeTouchMm;
			bool left = f.Left <= axis.Min + EdgeTouchMm;
			bool right = f.Right >= max - EdgeTouchMm;
			string n = f.Name + " " + f.Label;
			if (bottom || top)
			{
				f.Side = bottom ? "bottom" : "top";
				if (left && right) f.CutClass = "LEDGE";
				else if (left || right) f.CutClass = "NOTCH";
				else f.CutClass = "OPENING";
			}
			else
			{
				f.Side = "top";
				if (n.Contains("HOLE") || n.Contains("SLEEVE") || n.Contains("BLOCKOUT")) f.CutClass = "HOLE";
				else if (n.Contains("OPENING") || n.Contains("WINDOW") || n.Contains("DOOR")) f.CutClass = "OPENING";
				else f.CutClass = Math.Max(f.Right - f.Left, f.ZMax - f.ZMin) < HoleMaxSideMm ? "HOLE" : "OPENING";
			}
		}

		List<PanelFeature> recesses = cat.Items.Where(i => i.Kind == "cut" && i.CutClass == "RECESS").ToList();
		foreach (PanelFeature hw in cat.Hardware)
		{
			double dMed = (hw.DMin + hw.DMax) / 2.0;
			foreach (PanelFeature r in recesses)
			{
				if (hw.Station < r.Left - FaceTouchMm || hw.Station > r.Right + FaceTouchMm) continue;
				if (hw.ZMed < r.ZMin - FaceTouchMm || hw.ZMed > r.ZMax + FaceTouchMm) continue;
				if (dMed < r.DMin - 50.0 || dMed > r.DMax + 50.0) continue;
				hw.InRecess = true;
				hw.RecessSize = Mm(r.Right - r.Left) + "x" + Mm(r.ZMax - r.ZMin) + "x" + Mm(r.DMax - r.DMin) + "mm";
				break;
			}
		}
	}

	private static string Mm(double v) => Math.Round(v, 0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);

	// ---------------- rows ----------------

	private List<CatalogRow> BuildCatalogRows(FeatureCatalog cat, PanelAxis axis, HashSet<string> families)
	{
		var rows = new List<CatalogRow>();
		double h = axis.Height;

		// Hardware: (family, face, in-recess) then height clusters.
		foreach (var grp in cat.Hardware.GroupBy(f => f.Family + "|" + f.Face + "|" + (f.InRecess ? f.RecessSize : "")))
		{
			PanelFeature first = grp.First();
			if (families == null || !BomHasFamily(families, new[] { first.Family }))
			{
				Console.WriteLine("[Elev] skip hardware '" + first.Family + "': not in " + (families == null ? "any source" : "BOM/model family list"));
				continue;
			}
			List<List<PanelFeature>> clusters = HeightClusters(grp.ToList());
			for (int c = 0; c < clusters.Count; c++)
			{
				List<PanelFeature> cl = clusters[c];
				var row = new CatalogRow
				{
					Kind = "hardware",
					Face = first.Face,
					Families = { first.Family },
					Stations = cl.Select(f => f.Station).ToList(),
					Items = cl
				};
				double med = Median(cl.Select(f => f.ZMed));
				row.MedianZ = med;
				row.IsBottom = h > 0.0 && med < h / 2.0;
				row.EdgeDistance = row.IsBottom ? med : h - med;
				row.BaseLabel = first.InRecess
					? first.Family + " HARDWARE W/ RECESS " + first.RecessSize
					: first.Family;
				rows.Add(row);
				PreflightQaChecker.Append(_pdfDir, _activePiece, "[Cluster] " + first.Family + " face=" + (first.Face.Length > 0 ? first.Face : "-")
					+ " k=" + (c + 1) + "/" + clusters.Count + " z=" + Mm(cl.Min(f => f.ZMin)) + ".." + Mm(cl.Max(f => f.ZMax))
					+ " median=" + Mm(med) + " recess=" + (first.InRecess ? first.RecessSize : "no")
					+ " stations=" + string.Join("|", row.Stations.OrderBy(s => s).Select(Mm)));
			}
		}
		NameHardwareRoles(rows);

		// Cuts: one row per (class, side). Recesses stay with the notch/recess dims.
		foreach (var grp in cat.Items.Where(i => i.Kind == "cut" && i.CutClass != "RECESS" && i.CutClass.Length > 0).GroupBy(i => i.CutClass + "|" + i.Side))
		{
			List<PanelFeature> cuts = grp.ToList();
			var row = new CatalogRow
			{
				Kind = "cut",
				BaseLabel = "PROFILE " + cuts[0].CutClass,
				IsBottom = cuts[0].Side == "bottom",
				Stations = cuts.SelectMany(EdgeStations).ToList(),
				Items = cuts
			};
			row.EdgeDistance = row.IsBottom ? cuts.Min(f => f.ZMin) : h - cuts.Max(f => f.ZMax);
			rows.Add(row);
		}

		// Reveals by face, NO PAINT as one row; both by median height.
		foreach (var grp in cat.Items.Where(i => i.Kind == "reveal").GroupBy(i => i.Face))
			rows.Add(GeometryRow("reveal", "REVEAL", grp.Key, grp.ToList(), h));
		List<PanelFeature> noPaint = cat.Items.Where(i => i.Kind == "nopaint").ToList();
		if (noPaint.Count > 0)
			rows.Add(GeometryRow("nopaint", "NO PAINT", "", noPaint, h));

		foreach (string want in new[] { "PROFILE LEDGE", "PROFILE NOTCH", "PROFILE HOLE", "PROFILE OPENING", "REVEAL", "NO PAINT" })
		{
			if (!rows.Any(r => r.BaseLabel == want))
				PreflightQaChecker.Append(_pdfDir, _activePiece, "[Elev] row empty: " + want + " (no matching geometry in the model)");
		}

		return MergeIdenticalRows(rows);
	}

	private static IEnumerable<double> EdgeStations(PanelFeature f)
	{
		if (f.Right - f.Left < StripCentreMaxMm) return new[] { f.Station };
		return new[] { f.Left, f.Right };
	}

	private static CatalogRow GeometryRow(string kind, string label, string face, List<PanelFeature> items, double h)
	{
		var row = new CatalogRow { Kind = kind, BaseLabel = label, Face = face ?? "", Stations = items.SelectMany(EdgeStations).ToList(), Items = items };
		double med = Median(items.Select(f => f.ZMed));
		row.MedianZ = med;
		row.IsBottom = h > 0.0 && med < h / 2.0;
		row.EdgeDistance = row.IsBottom ? med : h - med;
		return row;
	}

	private static List<List<PanelFeature>> HeightClusters(List<PanelFeature> items)
	{
		var result = new List<List<PanelFeature>>();
		foreach (PanelFeature f in items.OrderBy(i => i.ZMed))
		{
			if (result.Count == 0 || f.ZMed - result[result.Count - 1].Last().ZMed > HeightClusterGapMm)
				result.Add(new List<PanelFeature>());
			result[result.Count - 1].Add(f);
		}
		return result;
	}

	private static double Median(IEnumerable<double> values)
	{
		List<double> v = values.OrderBy(x => x).ToList();
		if (v.Count == 0) return 0.0;
		return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
	}

	/// <summary>
	/// P-205 = LIFTERS. P-300 outside a recess: one cluster = BRACING INSERTS; several = the higher one
	/// takes P300HigherClusterRole, the rest the other role.
	/// </summary>
	private void NameHardwareRoles(List<CatalogRow> rows)
	{
		foreach (CatalogRow r in rows.Where(r => r.Kind == "hardware" && r.BaseLabel == "P-205"))
			r.BaseLabel = "P-205 LIFTERS";
		List<CatalogRow> p300 = rows.Where(r => r.Kind == "hardware" && r.BaseLabel == "P-300").ToList();
		if (p300.Count == 0) return;
		if (p300.Count == 1)
		{
			p300[0].BaseLabel = "P-300 BRACING INSERTS";
			return;
		}
		string lowerRole = P300HigherClusterRole == "HARDWARE" ? "BRACING INSERTS" : "HARDWARE";
		CatalogRow highest = p300.OrderByDescending(r => r.MedianZ).First();
		foreach (CatalogRow r in p300)
			r.BaseLabel = "P-300 " + (r == highest ? P300HigherClusterRole : lowerRole);
		PreflightQaChecker.Append(_pdfDir, _activePiece, "[P300] " + p300.Count + " clusters; higher=" + P300HigherClusterRole
			+ " (constant P300HigherClusterRole; check the [Cluster] z values against the PDF)");
	}

	/// <summary>Hardware rows on the same side and face with identical stations (±1 mm) become one chain.</summary>
	private static List<CatalogRow> MergeIdenticalRows(List<CatalogRow> rows)
	{
		var result = new List<CatalogRow>();
		foreach (CatalogRow r in rows)
		{
			if (r.Kind == "hardware")
			{
				CatalogRow twin = result.FirstOrDefault(o => o.Kind == "hardware" && o.IsBottom == r.IsBottom && o.Face == r.Face
					&& SameStations(o.Stations, r.Stations));
				if (twin != null)
				{
					twin.Families.AddRange(r.Families);
					twin.Items.AddRange(r.Items);
					var bases = twin.BaseLabel.Split(new[] { " + " }, StringSplitOptions.None).Concat(new[] { r.BaseLabel })
						.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
					twin.BaseLabel = string.Join(" + ", bases);
					Console.WriteLine("[Elev] merged identical-station rows into '" + twin.Label + "'");
					continue;
				}
			}
			result.Add(r);
		}
		return result;
	}

	private static bool SameStations(List<double> a, List<double> b)
	{
		List<double> x = DistinctSorted(a), y = DistinctSorted(b);
		if (x.Count != y.Count) return false;
		for (int i = 0; i < x.Count; i++)
			if (Math.Abs(x[i] - y[i]) > 1.0) return false;
		return true;
	}

	private static List<double> DistinctSorted(List<double> v)
	{
		var outList = new List<double>();
		foreach (double s in v.OrderBy(s => s))
			if (outList.Count == 0 || s - outList[outList.Count - 1] > 1.0) outList.Add(s);
		return outList;
	}

	private void LogCatalog(FeatureCatalog cat, PanelAxis axis, string familySource)
	{
		PreflightQaChecker.Append(_pdfDir, _activePiece, "[Classify] panel length=" + Mm(axis.Span) + " height=" + Mm(axis.Height)
			+ " depth=" + Mm(axis.DepthMax - axis.DepthMin) + " items=" + cat.Items.Count + " assemblyDepth=" + cat.MaxDepth
			+ " T-face=" + (TopFaceIsMaxDepth ? "max depth" : "min depth") + " families from " + familySource);
		foreach (PanelFeature f in cat.Items.OrderBy(i => i.Kind).ThenBy(i => i.Left))
		{
			string key = f.Kind == "hardware" ? f.Family + "|" + (f.Face.Length > 0 ? f.Face : "-") + (f.InRecess ? "|recess" : "")
				: f.Kind == "cut" ? f.CutClass + "|" + f.Side : f.Kind;
			PreflightQaChecker.Append(_pdfDir, _activePiece, "[Classify] kind=" + f.Kind
				+ " family=" + (f.Family.Length > 0 ? f.Family + " via " + f.FamilySource : "-")
				+ " name='" + f.Name.Trim() + "' face=" + (f.Face.Length > 0 ? f.Face : "-")
				+ " along=" + Mm(f.Left - axis.Min) + ".." + Mm(f.Right - axis.Min)
				+ " z=" + Mm(f.ZMin) + ".." + Mm(f.ZMax) + " median=" + Mm(f.ZMed)
				+ " depth=" + Mm(f.DMin - axis.DepthMin) + ".." + Mm(f.DMax - axis.DepthMin)
				+ " recess=" + (f.InRecess ? f.RecessSize : "no")
				+ " cut=" + (f.CutClass.Length > 0 ? f.CutClass : "-")
				+ " rowkey=" + key
				+ (f.Kind == "unclassified" ? " (logged only, never drawn) label='" + f.Label.Trim() + "'" : ""));
		}
	}

	// ---------------- BOM source ----------------

	/// <summary>BOM text tokens; if none, hardware families from the model walk; if none, empty (no hardware rows).</summary>
	private HashSet<string> ResolveHardwareFamilies(ContainerView sheet, double bomLeft, FeatureCatalog cat, out string source)
	{
		HashSet<string> bom = CollectBomMarkTokens(sheet, bomLeft);
		var model = new HashSet<string>(cat.Hardware.Select(f => f.Family), StringComparer.OrdinalIgnoreCase);
		if (bom.Count > 0)
		{
			source = "BOM text (" + bom.Count + " tokens)";
			if (LogUnlistedEmbeds)
			{
				foreach (var g in cat.Hardware.GroupBy(f => f.Family))
				{
					if (!BomHasFamily(bom, new[] { g.Key }))
						PreflightQaChecker.Append(_pdfDir, _activePiece, "[BOM] model embed not in BOM: " + g.Key + " x" + g.Count());
				}
			}
			PreflightQaChecker.Append(_pdfDir, _activePiece, "[BOM] source=BOM text tokens=" + string.Join(",", bom.OrderBy(t => t)));
			return bom;
		}
		if (model.Count > 0)
		{
			source = "model walk (" + model.Count + " families, depth " + cat.MaxDepth + ")";
			Console.WriteLine("[BOM] text unreadable - using model hardware list (" + model.Count + " families, depth " + cat.MaxDepth + ")");
			PreflightQaChecker.Append(_pdfDir, _activePiece, "[BOM] text unreadable - using model hardware list (" + model.Count
				+ " families, depth " + cat.MaxDepth + "): " + string.Join(",", model.OrderBy(t => t)));
			return model;
		}
		source = "none";
		Console.WriteLine("[BOM] no hardware in BOM or model - no hardware rows");
		PreflightQaChecker.Append(_pdfDir, _activePiece, "[BOM] no hardware in BOM or model - no hardware rows");
		return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Hardware drawing outside the rows (callouts, ordinates, section rows) uses the same gate.</summary>
	private bool HardwareAllowed(string labelOrFamily)
	{
		HashSet<string> families = _hardwareFamilies;
		if (families == null && !string.IsNullOrEmpty(_activePiece))
			_familiesByPiece.TryGetValue(_activePiece, out families);
		if (families == null) return false;
		Match m = HardwareFamilyRegex.Match((labelOrFamily ?? "").ToUpperInvariant());
		return m.Success && BomHasFamily(families, new[] { m.Value.ToUpperInvariant() });
	}

	/// <summary>Page 1 resolves the family list; the sections and placing sheets of the same piece reuse it.</summary>
	private readonly Dictionary<string, HashSet<string>> _familiesByPiece = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

	/// <summary>Hardware parts of the piece from the recursive assembly walk (sections use these).</summary>
	private List<PanelFeature> PieceHardware(Tekla.Structures.Model.Part main, out HashSet<string> families, out string source)
	{
		var cat = new FeatureCatalog();
		families = null;
		source = "none";
		if (main == null) return new List<PanelFeature>();
		try { cat.Seen.Add(main.Identifier.ID); } catch { }
		PanelAxis axis = PanelAxis.Measure(main, LiveLength(main));
		Assembly assy = null;
		try { assy = main.GetAssembly(); } catch { }
		if (axis != null && assy != null)
			WalkAssembly(cat, assy, axis, 0, new HashSet<int>());
		if (!string.IsNullOrEmpty(_activePiece) && _familiesByPiece.TryGetValue(_activePiece, out families))
			source = "page 1 family list";
		else
		{
			families = new HashSet<string>(cat.Hardware.Select(f => f.Family), StringComparer.OrdinalIgnoreCase);
			source = families.Count > 0 ? "model walk" : "none";
		}
		return cat.Hardware.ToList();
	}

	// ---------------- duplicate guard ----------------

	private bool ChainAlreadyEmitted(View view, List<double> stations, string label)
	{
		string key = RuntimeHelpers.GetHashCode(view).ToString(CultureInfo.InvariantCulture) + "|"
			+ string.Join(",", stations.Select(s => Math.Round(s, 0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture)))
			+ "|" + RegressionChecker.NormalizeLabel(label);
		if (_emittedChains.Add(key)) return false;
		Console.WriteLine("[Elev] duplicate chain skipped '" + (label ?? "") + "'");
		PreflightQaChecker.Append(_pdfDir, _activePiece, "[Elev] duplicate chain skipped '" + (label ?? "") + "' " + key.Substring(key.IndexOf('|') + 1));
		return true;
	}

	/// <summary>Dimension sets placed directly on the sheet (outside any view) by the macro.</summary>
	private static int PurgeSheetLevelDimensions(ContainerView sheet)
	{
		if (sheet == null) return 0;
		var doomed = new List<DrawingObject>();
		try
		{
			DrawingObjectEnumerator en = sheet.GetObjects();
			while (en != null && en.MoveNext())
			{
				if (en.Current is StraightDimensionSet set) doomed.Add(set);
			}
		}
		catch { }
		int n = 0;
		foreach (DrawingObject o in doomed)
		{
			try { if (o.Delete()) n++; } catch { }
		}
		return n;
	}

	// ---------------- page-1 reader ----------------

	/// <summary>
	/// Reads the horizontal dimension rows actually present in the front view (ours or the macro's),
	/// as stations along the panel, with the nearest tag text at the right end.
	/// </summary>
	private List<RegressionChecker.Row> ReadElevationRows(View view, PanelAxis axis, int scale, List<RegressionChecker.Row> columns = null)
	{
		var rows = new List<RegressionChecker.Row>();
		if (view == null || axis == null) return rows;
		int sc = scale < 1 ? 1 : scale;
		Point p0 = axis.PaperPoint(axis.Min);
		Point p1 = axis.PaperPoint(axis.Min + axis.Span);
		if (p0 == null || p1 == null) return rows;
		double ax = p1.X - p0.X, ay = p1.Y - p0.Y, len2 = ax * ax + ay * ay;
		if (len2 < 1e-6) return rows;
		Vector up = axis.Up ?? new Vector(0.0, 1.0, 0.0);

		var sets = new Dictionary<DimensionSetBase, List<StraightDimension>>();
		try
		{
			DrawingObjectEnumerator en = view.GetObjects(new Type[] { typeof(StraightDimension) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimension sd)) continue;
				DimensionSetBase set = null;
				try { set = sd.GetDimensionSet(); } catch { }
				if (set == null) continue;
				if (!sets.TryGetValue(set, out var list)) sets[set] = list = new List<StraightDimension>();
				list.Add(sd);
			}
		}
		catch { }

		var texts = new List<Tuple<Point, string>>();
		try
		{
			DrawingObjectEnumerator en = view.GetObjects(new Type[] { typeof(Text) });
			while (en != null && en.MoveNext())
			{
				if (en.Current is Text t && t.InsertionPoint != null && !string.IsNullOrWhiteSpace(t.TextString))
					texts.Add(Tuple.Create(t.InsertionPoint, t.TextString.Trim()));
			}
		}
		catch { }

		// Height columns: model height = (view Y - panel bottom) / (view height per model mm).
		Point b0 = axis.PaperBottomPoint(axis.Min), b1 = axis.PaperBottomPoint(axis.Min + axis.Span);
		double bottomY = Math.Min(b0.Y, b1.Y);
		double wallView = Math.Max(p0.Y, p1.Y) - bottomY;
		double toView = axis.Height > 1.0 && wallView > 1.0 ? wallView / axis.Height : 1.0;
		var foundColumns = new List<Tuple<RegressionChecker.Row, double>>();

		var found = new List<Tuple<RegressionChecker.Row, double>>();
		foreach (var kv in sets)
		{
			StraightDimension first = kv.Value[0];
			Vector dir = null;
			try { dir = first.UpDirection; } catch { }
			if (dir == null) continue;
			double dot = dir.X * up.X + dir.Y * up.Y;
			// Rows measure along the panel: their up direction is across it. Columns point sideways.
			if (Math.Abs(dot) < 0.7 * Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y))
			{
				if (columns == null) continue;
				var levels = new List<double>();
				foreach (StraightDimension sd in kv.Value)
				{
					foreach (Point p in new[] { sd.StartPoint, sd.EndPoint })
						if (p != null) levels.Add((p.Y - bottomY) / toView);
				}
				List<double> lv = DistinctSorted(levels);
				if (lv.Count < 2) continue;
				var csegs = new List<double>();
				for (int i = 1; i < lv.Count; i++) csegs.Add(lv[i] - lv[i - 1]);
				double cdist = 0.0;
				try { cdist = Math.Abs(first.Distance); } catch { }
				foundColumns.Add(Tuple.Create(new RegressionChecker.Row
				{
					Label = "(column)",
					Side = dir.X < 0 ? "left" : "right",
					Segments = csegs
				}, cdist));
				continue;
			}
			var st = new List<double>();
			foreach (StraightDimension sd in kv.Value)
			{
				foreach (Point p in new[] { sd.StartPoint, sd.EndPoint })
				{
					if (p == null) continue;
					double t = ((p.X - p0.X) * ax + (p.Y - p0.Y) * ay) / len2;
					st.Add(axis.Min + t * axis.Span);
				}
			}
			List<double> stations = DistinctSorted(st);
			if (stations.Count < 2) continue;
			var segs = new List<double>();
			for (int i = 1; i < stations.Count; i++) segs.Add(stations[i] - stations[i - 1]);
			double distance = 0.0;
			try { distance = Math.Abs(first.Distance); } catch { }
			bool top = dot > 0;
			Point edge = top ? axis.PaperPoint(stations[stations.Count - 1]) : axis.PaperBottomPoint(stations[stations.Count - 1]);
			double sign = top ? 1.0 : -1.0;
			Point lineEnd = new Point(edge.X + up.X * sign * distance, edge.Y + up.Y * sign * distance, 0.0);
			string label = NearestTag(texts, lineEnd, sc);
			found.Add(Tuple.Create(new RegressionChecker.Row
			{
				Label = label ?? "(untagged)",
				Side = top ? "top" : "bottom",
				Segments = segs
			}, distance));
		}

		foreach (string side in new[] { "top", "bottom" })
		{
			List<Tuple<RegressionChecker.Row, double>> stack = found.Where(f => f.Item1.Side == side).OrderBy(f => f.Item2).ToList();
			for (int i = 0; i < stack.Count; i++)
			{
				// Fixture order: top outer->inner, bottom inner->outer.
				stack[i].Item1.Order = side == "top" ? stack.Count - i : i + 1;
				rows.Add(stack[i].Item1);
			}
		}
		if (columns != null)
		{
			foreach (string side in new[] { "left", "right" })
			{
				List<Tuple<RegressionChecker.Row, double>> stack = foundColumns.Where(c => c.Item1.Side == side).OrderBy(c => c.Item2).ToList();
				for (int i = 0; i < stack.Count; i++)
				{
					// Order 1 = column nearest the panel.
					stack[i].Item1.Order = i + 1;
					columns.Add(stack[i].Item1);
				}
			}
		}
		return rows;
	}

	private static string NearestTag(List<Tuple<Point, string>> texts, Point lineEnd, int sc)
	{
		string best = null;
		double bestScore = double.MaxValue;
		double maxDy = 15.0 * sc;
		foreach (var t in texts)
		{
			double dx = t.Item1.X - lineEnd.X;
			double dy = Math.Abs(t.Item1.Y - lineEnd.Y);
			if (dy > maxDy || dx < -15.0 * sc || dx > 120.0 * sc) continue;
			double score = dy * 4.0 + Math.Abs(dx);
			if (score < bestScore)
			{
				bestScore = score;
				best = t.Item2;
			}
		}
		return best;
	}

	private void ScorePage1(View front, PanelAxis axis, int scale)
	{
		try
		{
			var columns = new List<RegressionChecker.Row>();
			List<RegressionChecker.Row> rows = ReadElevationRows(front, axis, scale, columns);
			int overall = rows.Count(r => RegressionChecker.NormalizeLabel(r.Label) == "OVERALL");
			PreflightQaChecker.Append(_pdfDir, _activePiece, "[Elev] page-1 rows read=" + rows.Count + " overall rows=" + overall
				+ " (top " + rows.Count(r => r.Side == "top" && RegressionChecker.NormalizeLabel(r.Label) == "OVERALL") + ")"
				+ " height columns read=" + columns.Count + " (left " + columns.Count(c => c.Side == "left")
				+ ", right " + columns.Count(c => c.Side == "right") + ")");
			RegressionChecker.Score(_activePiece, rows, _pdfDir, MicrosDirectory());
			RegressionChecker.ScoreHeights(_activePiece, columns, _pdfDir, MicrosDirectory());
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Regression] page-1 scoring failed: " + ex.Message);
		}
	}

	// ---------------- '?' diagnostic ----------------

	/// <summary>
	/// Logs every Mark/Text showing '?' and every magenta Symbol on the sheet and in its views.
	/// Deletes nothing. The API exposes no creator, so the related model object stands in for it.
	/// </summary>
	private void LogQuestionMarkObjects(ContainerView sheet, string context)
	{
		if (sheet == null || string.IsNullOrWhiteSpace(_activePiece)) return;
		var lines = new List<string>();
		int marks = 0, texts = 0, symbols = 0;
		var hosts = new List<ViewBase> { sheet };
		try
		{
			DrawingObjectEnumerator ve = sheet.GetAllViews();
			while (ve != null && ve.MoveNext())
				if (ve.Current is ViewBase vb) hosts.Add(vb);
		}
		catch { }
		var seen = new HashSet<int>();
		foreach (ViewBase host in hosts)
		{
			string viewName = host is View v ? (v.Name ?? "") + "/" + SafeViewType(v) : "sheet";
			DrawingObjectEnumerator en = null;
			try { en = host.GetObjects(); } catch { }
			while (en != null && en.MoveNext())
			{
				DrawingObject o = en.Current;
				if (o == null || !seen.Add(RuntimeHelpers.GetHashCode(o))) continue;
				try
				{
					if (o is MarkBase mb)
					{
						string shown = FlattenMarkText(mb);
						if (shown.IndexOf('?') < 0) continue;
						marks++;
						lines.Add("[qmark] type=" + o.GetType().FullName + " view=" + viewName + " at=" + Pt(mb.InsertionPoint)
							+ " text='" + shown + "' content=" + DescribeContent(mb) + " related=" + DescribeRelated(o) + " source=native");
					}
					else if (o is Text t)
					{
						string s = t.TextString ?? "";
						if (s.IndexOf('?') < 0) continue;
						texts++;
						string color = "";
						try { color = t.Attributes.Font.Color.ToString(); } catch { }
						lines.Add("[qmark] type=" + o.GetType().FullName + " view=" + viewName + " at=" + Pt(t.InsertionPoint)
							+ " text='" + s + "' color=" + color + " related=" + DescribeRelated(o) + " source=native (our code never inserts '?')");
					}
					else if (o is Symbol sym)
					{
						string color = "";
						try { color = sym.Attributes.Color.ToString(); } catch { }
						if (color.IndexOf("Magenta", StringComparison.OrdinalIgnoreCase) < 0) continue;
						symbols++;
						string info = "";
						try { info = sym.SymbolInfo.SymbolFile + "#" + sym.SymbolInfo.SymbolIndex.ToString(CultureInfo.InvariantCulture); } catch { }
						lines.Add("[qmark] type=" + o.GetType().FullName + " view=" + viewName + " at=" + Pt(sym.InsertionPoint)
							+ " symbol=" + info + " color=" + color + " related=" + DescribeRelated(o));
					}
				}
				catch (Exception ex)
				{
					lines.Add("[qmark] read failed on " + o.GetType().Name + ": " + ex.Message);
				}
			}
		}
		lines.Insert(0, "# " + context + " " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture)
			+ " marks=" + marks + " texts=" + texts + " magentaSymbols=" + symbols);
		try { File.AppendAllLines(PreflightQaChecker.QmarksPath(_pdfDir, _activePiece), lines); } catch { }
		Console.WriteLine("[qmark] " + context + ": marks=" + marks + " texts=" + texts + " magentaSymbols=" + symbols + " (logged only)");
	}

	private static string SafeViewType(View v)
	{
		try { return v.ViewType.ToString(); } catch { return "?"; }
	}

	private static string Pt(Point p) => p == null ? "-" : "(" + Mm(p.X) + "," + Mm(p.Y) + ")";

	private static string DescribeContent(MarkBase mb)
	{
		if (!(mb is Mark m) || m.Attributes?.Content == null) return "-";
		var sb = new StringBuilder();
		foreach (object el in m.Attributes.Content)
		{
			if (sb.Length > 0) sb.Append(",");
			if (el is PropertyElement pe)
			{
				string color = "";
				try { color = pe.Font.Color.ToString(); } catch { }
				sb.Append("Property:" + (pe.Name ?? "") + "='" + (pe.Value ?? "") + "'" + (color.Length > 0 ? ":" + color : ""));
			}
			else if (el is TextElement te)
			{
				string color = "";
				try { color = te.Font.Color.ToString(); } catch { }
				sb.Append("Text:'" + (te.Value ?? "") + "'" + (color.Length > 0 ? ":" + color : ""));
			}
			else sb.Append(el?.GetType().Name ?? "null");
		}
		return sb.ToString();
	}

	private string DescribeRelated(DrawingObject o)
	{
		var parts = new List<string>();
		try
		{
			DrawingObjectEnumerator rel = o.GetRelatedObjects();
			while (rel != null && rel.MoveNext() && parts.Count < 4)
			{
				if (!(rel.Current is Tekla.Structures.Drawing.ModelObject dmo))
				{
					if (rel.Current != null) parts.Add(rel.Current.GetType().Name);
					continue;
				}
				Tekla.Structures.Model.ModelObject mo = null;
				try { mo = _model.SelectModelObject(dmo.ModelIdentifier); } catch { }
				if (mo == null)
				{
					parts.Add(dmo.GetType().Name + "(model object not found)");
					continue;
				}
				string guid = "", name = "", partPos = "", asmPos = "";
				try { guid = mo.Identifier.GUID.ToString(); } catch { }
				try { mo.GetReportProperty("NAME", ref name); } catch { }
				try { mo.GetReportProperty("PART_POS", ref partPos); } catch { }
				try { mo.GetReportProperty("ASSEMBLY_POS", ref asmPos); } catch { }
				parts.Add(mo.GetType().Name + "{guid=" + guid + " name=" + name + " PART_POS=" + partPos + " ASSEMBLY_POS=" + asmPos + "}");
			}
		}
		catch { }
		return parts.Count == 0 ? "none" : string.Join(";", parts);
	}

	// ---------------- combined dimensions ----------------

	/// <summary>
	/// Replaces '*' with '@' in any TextElement of each StraightDimension.Value. Native combined text is
	/// composed by Tekla at render time, so Value is usually empty; the counts say which case this is.
	/// </summary>
	private void RewriteCombinedStars(View view, string context)
	{
		if (view == null) return;
		int total = 0, empty = 0, edited = 0;
		try
		{
			DrawingObjectEnumerator en = view.GetObjects(new Type[] { typeof(StraightDimension) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimension sd)) continue;
				total++;
				ContainerElement value = null;
				try { value = sd.Value; } catch { }
				if (value == null || value.Count == 0)
				{
					empty++;
					continue;
				}
				bool changed = false;
				foreach (object el in value)
				{
					if (el is TextElement te && (te.Value ?? "").IndexOf('*') >= 0)
					{
						te.Value = te.Value.Replace('*', '@');
						changed = true;
					}
				}
				if (changed)
				{
					try { if (sd.Modify()) edited++; } catch { }
				}
			}
		}
		catch { }
		string line = "[Combine] " + context + " '*'->'@' edited=" + edited + " emptyValue=" + empty + " total=" + total
			+ (edited == 0 && total > 0 ? " (no editable text: fallback is our n@s=total callouts + XS_COMBINED_BOLT_DIM_CHARACTER)" : "");
		Console.WriteLine(line);
		PreflightQaChecker.Append(_pdfDir, _activePiece, line);
	}
}

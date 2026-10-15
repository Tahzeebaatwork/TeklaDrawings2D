using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace TeklaExtractor.Services;

/// <summary>
/// diag1 coordinate / unit diagnostic. Log only: nothing here moves, resizes or restyles a drawing object.
/// The calibration probe creates scratch views and sets and deletes them again in a finally block.
/// </summary>
public partial class PrecastDimensionPostProcessor
{
	/// <summary>--coord-check: [CoordCheck] lines + coordcheck_&lt;runtag&gt;.csv. Commits before every read-back.</summary>
	public static bool CoordCheckEnabled { get; set; }

	/// <summary>--coord-probe: one calibration probe on the first page-1 sheet of the run.</summary>
	public static bool CoordProbeEnabled { get; set; }

	/// <summary>
	/// --coord-probe-keep: leave one probe set per scale and type plus reference lines in the drawing, so the
	/// exported PDF shows where the dimension line really sits. The sheet then carries probe objects.
	/// </summary>
	public static bool CoordProbeKeep { get; set; }

	private const int CoordOtherDimLimit = 10;
	private const double CoordRatioTol = 0.001;
	private const double CoordDigitWidthFactor = 0.7;
	private const string CoordProbeViewPrefix = "CC-PROBE";

	private static string _coordCsvNewline = Environment.NewLine;
	private static bool _coordProbeDone;
	private static int _coordOtherDims;
	private static string _coordScaleTrace = "";
	private static readonly HashSet<string> _coordHeaderPieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private string _coordWhere = "other";

	private static readonly string CoordCsvColumns =
		"piece,kind,where,label,type,scale,points,our_len_mm,value_mm,ratio,dist_passed,dist_view,dist_read,"
		+ "offset_view,offset_paper,text_h,min_seg_paper,overlap_n,flags,detail";

	// ---------------- output ----------------

	private string CoordCsvPath()
	{
		string tag = string.IsNullOrWhiteSpace(RegressionChecker.RunTag) ? "untagged" : RegressionChecker.RunTag;
		foreach (char ch in Path.GetInvalidFileNameChars()) tag = tag.Replace(ch, '_');
		string dir = string.IsNullOrWhiteSpace(_pdfDir) ? "." : _pdfDir;
		return Path.Combine(dir, "coordcheck_" + tag + ".csv");
	}

	private void CoordLine(string line)
	{
		CoordHeader();
		string text = "[CoordCheck] " + line;
		Console.WriteLine(text);
		PreflightQaChecker.Append(_pdfDir, _activePiece, text);
	}

	private void CoordCsv(string kind, string where, string label = "", string type = "", object scale = null, object points = null,
		object ourLen = null, object value = null, object ratio = null, object distPassed = null, object distView = null,
		object distRead = null, object offsetView = null, object offsetPaper = null, object textH = null, object minSegPaper = null,
		object overlap = null, string flags = "", string detail = "")
	{
		CoordHeader();
		try
		{
			var sb = new StringBuilder();
			sb.Append(CsvCell(_activePiece));
			foreach (object c in new[] { kind, where, label, type, scale, points, ourLen, value, ratio, distPassed, distView,
				distRead, offsetView, offsetPaper, textH, minSegPaper, overlap, flags, detail })
				sb.Append(',').Append(CsvCell(c));
			File.AppendAllText(CoordCsvPath(), sb.ToString() + _coordCsvNewline);
		}
		catch { }
	}

	private static string CsvCell(object v)
	{
		string s = v switch
		{
			null => "",
			double d => double.IsNaN(d) ? "" : d.ToString("0.###", CultureInfo.InvariantCulture),
			_ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
		};
		return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
	}

	/// <summary>
	/// Git commit, Tekla version, piece, run tag and time, once per piece in the log and the CSV. The first write
	/// for a piece drops only that piece's earlier rows of the kinds this run writes again (probe rows only on a
	/// probe run), keeps every other line byte for byte, and swaps the file in atomically.
	/// </summary>
	private void CoordHeader()
	{
		string piece = _activePiece ?? "";
		if (!_coordHeaderPieces.Add(piece)) return;
		string csv = CoordCsvPath();
		string header = "header git=" + GitCommit() + " tekla=" + TeklaVersion() + " piece=" + piece
			+ " run=" + (string.IsNullOrWhiteSpace(RegressionChecker.RunTag) ? "untagged" : RegressionChecker.RunTag)
			+ " time=" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
		string tmp = csv + ".tmp";
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csv)));
			string existing = File.Exists(csv) ? File.ReadAllText(csv) : "";
			var drop = new List<string>(CoordCsvMerge.RunKinds);
			if (CoordProbeEnabled) drop.AddRange(CoordCsvMerge.ProbeKinds);
			string merged = CoordCsvMerge.Merge(existing, piece, drop, CoordCsvColumns, header, out bool keptProbe, out string newline);
			_coordCsvNewline = newline;
			if (keptProbe) header += " probe=kept-from-earlier-run";
			File.WriteAllText(tmp, merged, new UTF8Encoding(false));
			if (File.Exists(csv)) File.Replace(tmp, csv, null);
			else File.Move(tmp, csv);
		}
		catch (Exception ex)
		{
			try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
			Console.WriteLine("[CoordCheck] csv rewrite failed, old file kept: " + ex.Message);
		}
		Console.WriteLine("[CoordCheck] " + header);
		PreflightQaChecker.Append(_pdfDir, piece, "[CoordCheck] " + header);
	}

	private static string GitCommit()
	{
		foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
		{
			try
			{
				var dir = new DirectoryInfo(start);
				while (dir != null)
				{
					string head = Path.Combine(dir.FullName, ".git", "HEAD");
					if (File.Exists(head))
					{
						string h = File.ReadAllText(head).Trim();
						if (!h.StartsWith("ref:", StringComparison.Ordinal)) return h;
						string refName = h.Substring(4).Trim();
						string refFile = Path.Combine(dir.FullName, ".git", refName.Replace('/', Path.DirectorySeparatorChar));
						if (File.Exists(refFile)) return File.ReadAllText(refFile).Trim();
						string packed = Path.Combine(dir.FullName, ".git", "packed-refs");
						if (File.Exists(packed))
						{
							string hit = File.ReadAllLines(packed).FirstOrDefault(l => l.EndsWith(" " + refName, StringComparison.Ordinal));
							if (hit != null) return hit.Split(' ')[0];
						}
						return "unknown";
					}
					dir = dir.Parent;
				}
			}
			catch { }
		}
		return "unknown";
	}

	private static string TeklaVersion()
	{
		try
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = asm.GetType("Tekla.Structures.TeklaStructuresInfo", false);
				var m = t?.GetMethod("GetCurrentProgramVersion", Type.EmptyTypes);
				if (m != null && m.Invoke(null, null) is string v && v.Length > 0) return v;
			}
		}
		catch { }
		try { return "drawing-api " + typeof(View).Assembly.GetName().Version; } catch { return "unknown"; }
	}

	private static string P(Point p) => p == null ? "null"
		: "(" + p.X.ToString("0.#", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.#", CultureInfo.InvariantCulture)
		+ "," + p.Z.ToString("0.#", CultureInfo.InvariantCulture) + ")";

	private static string V(Vector v) => v == null ? "null"
		: "(" + v.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + v.Y.ToString("0.###", CultureInfo.InvariantCulture)
		+ "," + v.Z.ToString("0.###", CultureInfo.InvariantCulture) + ")";

	private static string N(double v, string fmt = "0.###") => double.IsNaN(v) ? "NaN" : v.ToString(fmt, CultureInfo.InvariantCulture);

	private static bool CoordCommit(ViewBase view)
	{
		try
		{
			Drawing d = view?.GetDrawing() ?? new DrawingHandler().GetActiveDrawing();
			return d != null && d.CommitChanges();
		}
		catch { return false; }
	}

	// ---------------- views ----------------

	/// <summary>Scale, frame, both coordinate systems, work plane, bbox ratios, handedness and the scale trace.</summary>
	private void CoordLogView(View view, Tekla.Structures.Model.Part part, PanelAxis axis, int scale, string where)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!CoordCheckEnabled || view == null) return;
		_coordWhere = where;
		try
		{
			int sc = scale < 1 ? 1 : scale;
			double attrScale = double.NaN;
			try { attrScale = view.Attributes.Scale; } catch { }
			CoordinateSystem vcs = view.ViewCoordinateSystem, dcs = view.DisplayCoordinateSystem;
			var sb = new StringBuilder("view where=" + where + " type=" + view.ViewType + " name='" + (view.Name ?? "") + "'");
			sb.Append(" attrScale=").Append(N(attrScale)).Append(" scaleArg=").Append(sc);
			if (!double.IsNaN(attrScale) && Math.Abs(attrScale - sc) > 0.01) sb.Append(" SCALE-MISMATCH");
			sb.Append(" frameWxH=").Append(N(view.Width, "0.#")).Append("x").Append(N(view.Height, "0.#"));
			sb.Append(" origin=").Append(P(view.Origin));
			sb.Append(" VCS o=").Append(P(vcs?.Origin)).Append(" x=").Append(V(vcs?.AxisX)).Append(" y=").Append(V(vcs?.AxisY));
			sb.Append(" DCS o=").Append(P(dcs?.Origin)).Append(" x=").Append(V(dcs?.AxisX)).Append(" y=").Append(V(dcs?.AxisY));
			sb.Append(" workPlane=").Append(WorkPlaneText());
			CoordLine(sb.ToString());

			// Handedness: determinant of global -> view, which global axis feeds sheet X / Y, display direction.
			Matrix toView = MatrixFactory.ToCoordinateSystem(vcs);
			double det = Det3(toView);
			string hx = DominantAxis(vcs?.AxisX), hy = DominantAxis(vcs?.AxisY);
			string displayX = "n/a";
			try
			{
				Matrix viewToGlobal = MatrixFactory.FromCoordinateSystem(vcs);
				Matrix globalToDisplay = MatrixFactory.ToCoordinateSystem(dcs);
				Point a = globalToDisplay.Transform(viewToGlobal.Transform(new Point(0, 0, 0)));
				Point b = globalToDisplay.Transform(viewToGlobal.Transform(new Point(1000, 0, 0)));
				displayX = b.X > a.X + 1e-6 ? "left-to-right" : (b.X < a.X - 1e-6 ? "right-to-left" : "zero");
			}
			catch { }
			string station0 = "n/a";
			if (axis != null)
			{
				try
				{
					Point s0 = axis.PaperPoint(axis.Min), s1 = axis.PaperPoint(axis.Min + axis.Span);
					station0 = Math.Abs(s1.X - s0.X) >= Math.Abs(s1.Y - s0.Y)
						? (s0.X <= s1.X ? "left" : "right")
						: (s0.Y <= s1.Y ? "bottom" : "top");
				}
				catch { }
			}
			CoordLine("hand where=" + where + " det=" + N(det, "0.####") + (det < 0 ? " MIRRORED" : "")
				+ " sheetH<-global" + hx + " sheetV<-global" + hy + " viewX(display)=" + displayX + " station0=" + station0);
			CoordCsv("hand", where, view.Name ?? "", scale: sc, flags: det < 0 ? "MIRRORED" : "",
				detail: "det=" + N(det, "0.####") + " H<-" + hx + " V<-" + hy + " dispX=" + displayX + " station0=" + station0);

			// Part bbox: global, view-local, ratios.
			if (part != null)
			{
				Tekla.Structures.Model.Solid solid = null;
				try { solid = part.GetSolid(); } catch { }
				if (solid?.MinimumPoint != null && solid.MaximumPoint != null)
				{
					Point gmin = solid.MinimumPoint, gmax = solid.MaximumPoint;
					double[] g = { gmax.X - gmin.X, gmax.Y - gmin.Y, gmax.Z - gmin.Z };
					double lx0 = double.MaxValue, lx1 = double.MinValue, ly0 = double.MaxValue, ly1 = double.MinValue, lz0 = double.MaxValue, lz1 = double.MinValue;
					foreach (double x in new[] { gmin.X, gmax.X })
						foreach (double y in new[] { gmin.Y, gmax.Y })
							foreach (double z in new[] { gmin.Z, gmax.Z })
							{
								Point q = toView.Transform(new Point(x, y, z));
								lx0 = Math.Min(lx0, q.X); lx1 = Math.Max(lx1, q.X);
								ly0 = Math.Min(ly0, q.Y); ly1 = Math.Max(ly1, q.Y);
								lz0 = Math.Min(lz0, q.Z); lz1 = Math.Max(lz1, q.Z);
							}
					double[] l = { lx1 - lx0, ly1 - ly0, lz1 - lz0 };
					double[] gs = g.OrderBy(v => v).ToArray(), ls = l.OrderBy(v => v).ToArray();
					string ratios = string.Join("|", Enumerable.Range(0, 3).Select(i => gs[i] > 1e-6 ? N(ls[i] / gs[i], "0.####") : "n/a"));
					bool off = Enumerable.Range(0, 3).Any(i => gs[i] > 1.0 && Math.Abs(ls[i] / gs[i] - 1.0) > CoordRatioTol);
					double paperW = l[0] / sc, paperH = l[1] / sc;
					var bb = new StringBuilder("bbox where=" + where + " global min=" + P(gmin) + " max=" + P(gmax));
					bb.Append(" local x=").Append(N(lx0, "0.#")).Append("..").Append(N(lx1, "0.#"))
						.Append(" y=").Append(N(ly0, "0.#")).Append("..").Append(N(ly1, "0.#"))
						.Append(" z=").Append(N(lz0, "0.#")).Append("..").Append(N(lz1, "0.#"));
					bb.Append(" ratio(local/model sorted)=").Append(ratios).Append(off ? " RATIO!=1" : "");
					bb.Append(" paperWxH=").Append(N(paperW, "0.#")).Append("x").Append(N(paperH, "0.#"));
					bb.Append(" paper/model=").Append(l[0] > 1e-6 ? N(paperW / l[0], "0.######") : "n/a").Append(" expect=").Append(N(1.0 / sc, "0.######"));
					if (axis != null)
					{
						try
						{
							Point tl = axis.PaperPoint(axis.Min), tr = axis.PaperPoint(axis.Min + axis.Span);
							Point bl = axis.PaperBottomPoint(axis.Min);
							double spanView = PaperDistance(tl, tr), wallH = Math.Abs(tl.Y - bl.Y);
							double rSpan = axis.Span > 1e-6 ? spanView / axis.Span : double.NaN;
							double rH = axis.Height > 1e-6 ? wallH / axis.Height : double.NaN;
							bb.Append(" axis span=").Append(N(axis.Span, "0.#")).Append(" view=").Append(N(spanView, "0.#"))
								.Append(" ratio=").Append(N(rSpan, "0.####"));
							bb.Append(" height=").Append(N(axis.Height, "0.#")).Append(" view=").Append(N(wallH, "0.#"))
								.Append(" toView=").Append(N(rH, "0.####"));
							if ((!double.IsNaN(rSpan) && Math.Abs(rSpan - 1.0) > CoordRatioTol) || (!double.IsNaN(rH) && Math.Abs(rH - 1.0) > CoordRatioTol))
							{
								bb.Append(" AXIS-RATIO!=1");
								off = true;
							}
						}
						catch { }
					}
					CoordLine(bb.ToString());
					CoordCsv("view", where, view.Name ?? "", scale: sc, ourLen: N(l[0], "0.#"), value: N(g.Max(), "0.#"), ratio: ratios,
						flags: off ? "RATIO!=1" : "",
						detail: "paper=" + N(paperW, "0.#") + "x" + N(paperH, "0.#") + " frame=" + N(view.Width, "0.#") + "x" + N(view.Height, "0.#"));
				}
			}
			if (_coordScaleTrace.Length > 0) CoordLine("scale where=" + where + " " + _coordScaleTrace);
		}
		catch (Exception ex) { CoordLine("view error: " + ex.Message); }
		}
	}

	private static string WorkPlaneText()
	{
		try
		{
			var tp = new Tekla.Structures.Model.Model().GetWorkPlaneHandler().GetCurrentTransformationPlane();
			Matrix m = tp.TransformationMatrixToGlobal;
			bool identity = true;
			for (int r = 0; r < 4 && identity; r++)
				for (int c = 0; c < 3 && identity; c++)
					identity = Math.Abs(m[r, c] - (r == c ? 1.0 : 0.0)) < 1e-6;
			return identity ? "global" : "NON-GLOBAL";
		}
		catch { return "unknown"; }
	}

	private static double Det3(Matrix m)
	{
		try
		{
			return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
				- m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
				+ m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
		}
		catch { return double.NaN; }
	}

	private static string DominantAxis(Vector v)
	{
		if (v == null) return "?";
		double ax = Math.Abs(v.X), ay = Math.Abs(v.Y), az = Math.Abs(v.Z);
		if (ax >= ay && ax >= az) return (v.X < 0 ? "-" : "+") + "X";
		if (ay >= az) return (v.Y < 0 ? "-" : "+") + "Y";
		return (v.Z < 0 ? "-" : "+") + "Z";
	}

	/// <summary>Layout budget for page 1: panel on paper vs sheet, rows, step and stack extents.</summary>
	private void CoordLogLayout(View view, PanelAxis axis, ContainerView sheet, int scale, int topRows, int bottomRows, double topStep, double botStep)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!CoordCheckEnabled || view == null || axis == null) return;
		try
		{
			int sc = scale < 1 ? 1 : scale;
			double sw = sheet != null ? sheet.Width : double.NaN, sh = sheet != null ? sheet.Height : double.NaN;
			double pw = axis.Span / sc, ph = axis.Height / sc;
			double topStack = topRows > 0 ? TierFirstOffsetPaperMm + topStep * (topRows - 1) : 0.0;
			double botStack = bottomRows > 0 ? TierFirstOffsetPaperMm + botStep * (bottomRows - 1) : 0.0;
			double share = sw > 0 && sh > 0 ? pw * ph / (sw * sh) : double.NaN;
			string line = "layout where=" + _coordWhere + " panelPaper=" + N(pw, "0.#") + "x" + N(ph, "0.#") + " sheet=" + N(sw, "0.#") + "x" + N(sh, "0.#")
				+ " rows top=" + topRows + " bottom=" + bottomRows + " step top=" + N(topStep, "0.##") + " bottom=" + N(botStep, "0.##")
				+ " stack top=" + N(topStack, "0.#") + " bottom=" + N(botStack, "0.#") + " vertical total=" + N(ph + topStack + botStack, "0.#")
				+ " panelShare=" + N(share, "0.###");
			CoordLine(line);
			CoordCsv("layout", _coordWhere, scale: sc, detail: line.Substring(7));
		}
		catch { }
		}
	}

	/// <summary>Called by SelectScale; the last trace (the chosen scale) is logged with the next view.</summary>
	private static void CoordTraceScale(string trace)
	{
		if (CoordCheckEnabled) _coordScaleTrace = trace ?? "";
	}

	// ---------------- dimension sets ----------------

	/// <summary>
	/// Commit, then read back one set and compare with what we passed. Invalid read-backs get
	/// READBACK-INVALID and no ratios. Page 1 logs every set; other sheets the first 10.
	/// </summary>
	private void CoordLogDim(View view, StraightDimensionSet set, PointList pts, Vector up, double distPassed, double distView, int scale, string tag)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!CoordCheckEnabled || view == null || set == null || pts == null || pts.Count < 2) return;
		bool page1 = _coordWhere == "page1";
		if (!page1 && ++_coordOtherDims > CoordOtherDimLimit) return;
		try
		{
			int sc = scale < 1 ? 1 : scale;
			bool committed = CoordCommit(view);
			try { set.Select(); } catch { }
			string type = "?";
			double textH = double.NaN;
			try { type = set.Attributes.DimensionType.ToString(); } catch { }
			try { textH = set.Attributes.Text.Font.Height; } catch { }
			double distRead = double.NaN;
			try { distRead = set.Distance; } catch { }

			// Dimension direction: perpendicular to up, in the view plane.
			Vector dir = up != null && Math.Abs(up.X) > Math.Abs(up.Y) ? new Vector(0, 1, 0) : new Vector(1, 0, 0);
			var along = new List<double>();
			foreach (Point p in pts) along.Add(p.X * dir.X + p.Y * dir.Y);
			double ourLen = Math.Abs(along[along.Count - 1] - along[0]);
			var segs = new List<double>();
			for (int i = 1; i < along.Count; i++) segs.Add(Math.Abs(along[i] - along[i - 1]));

			// Children as Tekla reads them back: geometric length, stored Distance, printed text.
			List<KidReadback> kids = ReadKids(set, dir);
			double childSum = kids.Sum(k => k.Length);
			double offsetView = kids.Count > 0 ? kids[0].Distance : double.NaN;
			var printed = kids.Select(k => k.Printed).Where(v => !double.IsNaN(v)).ToList();
			bool absoluteType = type.IndexOf("Absolute", StringComparison.OrdinalIgnoreCase) >= 0;
			// Absolute should read back cumulative values (max = |A-B|); if the children carry segments instead,
			// their sum matches and the set is flagged ABS-READS-RELATIVE.
			double printedValue = double.NaN;
			bool absReadsRelative = false;
			if (printed.Count > 0)
			{
				double sum = printed.Sum(), max = printed.Max();
				absReadsRelative = absoluteType && printed.Count > 1 && Math.Abs(sum - ourLen) < Math.Abs(max - ourLen);
				printedValue = absoluteType && !absReadsRelative ? max : sum;
			}

			var flags = new List<string>();
			double value = !double.IsNaN(printedValue) ? printedValue : (kids.Count > 0 ? childSum : double.NaN);
			double ratio = double.NaN;
			bool invalid = !committed || kids.Count == 0 || double.IsNaN(distRead) || Math.Abs(distRead) < 1e-9;
			if (invalid)
				flags.Add("READBACK-INVALID");
			else if (!double.IsNaN(value) && ourLen > 1e-6)
			{
				ratio = value / ourLen;
				if (Math.Abs(ratio - 1.0) > CoordRatioTol) flags.Add("RATIO!=1");
			}
			if (printed.Count == 0) flags.Add("PRINTED-TEXT-UNREAD");
			if (absReadsRelative) flags.Add("ABS-READS-RELATIVE");

			// Overlap predictor (page 1): printed digits vs segment length on paper.
			double minSegPaper = segs.Count > 0 ? segs.Min() / sc : double.NaN;
			int overlaps = 0;
			if (page1 && !double.IsNaN(textH))
			{
				for (int i = 0; i < segs.Count; i++)
				{
					double label = absoluteType ? Math.Abs(along[i + 1] - along[0]) : segs[i];
					int digits = Math.Max(1, Math.Round(label, 0).ToString("0", CultureInfo.InvariantCulture).Length);
					if (segs[i] / sc < digits * CoordDigitWidthFactor * textH) overlaps++;
				}
				if (overlaps > 0) flags.Add("TEXT-OVERLAP-LIKELY");
			}

			var gl = new List<string>();
			try
			{
				Matrix toGlobal = MatrixFactory.FromCoordinateSystem(view.ViewCoordinateSystem);
				gl.Add(P(toGlobal.Transform(pts[0])));
				gl.Add(P(toGlobal.Transform(pts[pts.Count - 1])));
			}
			catch { }

			string line = "dim where=" + _coordWhere + " label='" + (tag ?? "") + "' type=" + type + " scale=" + sc
				+ " pts=" + pts.Count + " A=" + P(pts[0]) + " B=" + P(pts[pts.Count - 1]) + " (view-local)"
				+ " Aglobal=" + (gl.Count > 0 ? gl[0] : "?") + " Bglobal=" + (gl.Count > 1 ? gl[1] : "?")
				+ " |A-B|=" + N(ourLen, "0.#") + " segs=" + string.Join("|", segs.Select(s => N(s, "0.#")))
				+ " kids=" + kids.Count + " [" + string.Join("|", kids.Take(12).Select(k => k.Text)) + "] value=" + N(value, "0.#") + " ratio=" + N(ratio, "0.####")
				+ " distPassed=" + N(distPassed) + " distView=" + N(distView) + " distRead=" + N(distRead)
				+ " kidDistance=" + N(offsetView, "0.#") + " (/scale=" + N(offsetView / sc, "0.##") + ") textH=" + N(textH, "0.##")
				+ " minSegPaper=" + N(minSegPaper, "0.##") + " overlap=" + overlaps + "/" + segs.Count
				+ (committed ? "" : " commit=false") + (flags.Count > 0 ? " " + string.Join(" ", flags) : "");
			CoordLine(line);
			CoordCsv("dim", _coordWhere, tag ?? "", type, sc, pts.Count, N(ourLen, "0.#"), N(value, "0.#"), invalid ? "" : N(ratio, "0.####"),
				distPassed, distView, distRead, invalid ? double.NaN : offsetView, invalid ? double.NaN : offsetView / sc, textH,
				minSegPaper, overlaps, string.Join(" ", flags), "segs=" + string.Join("|", segs.Select(s => N(s, "0.#"))));		}
		catch (Exception ex) { CoordLine("dim error '" + (tag ?? "") + "': " + ex.Message); }
		}
	}

	private static string SafeId(object o)
	{
		if (o == null) return "";
		try
		{
			object id = o.GetType().GetMethod("GetIdentifier", Type.EmptyTypes)?.Invoke(o, null);
			object num = id?.GetType().GetProperty("ID")?.GetValue(id, null);
			if (num != null) return Convert.ToString(num, CultureInfo.InvariantCulture);
		}
		catch { }
		return o.GetHashCode().ToString(CultureInfo.InvariantCulture);
	}

	private sealed class KidReadback
	{
		public double Length = double.NaN;
		public double Distance = double.NaN;
		public double Printed = double.NaN;
		public string Text = "";
	}

	/// <summary>
	/// Child dimensions of a set. Dimension sets expose no bounding box in this API, so the line offset is the
	/// child's stored Distance and the value is the child's printed text (Value), parsed as a number.
	/// </summary>
	private static List<KidReadback> ReadKids(StraightDimensionSet set, Vector dir)
	{
		var list = new List<KidReadback>();
		try
		{
			DrawingObjectEnumerator en = set.GetObjects(new[] { typeof(StraightDimension) });
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is StraightDimension sd)) continue;
				var k = new KidReadback();
				try
				{
					k.Length = Math.Abs((sd.EndPoint.X - sd.StartPoint.X) * dir.X + (sd.EndPoint.Y - sd.StartPoint.Y) * dir.Y);
				}
				catch { }
				try { k.Distance = sd.Distance; } catch { }
				string txt = "";
				try
				{
					var sb = new StringBuilder();
					FlattenMarkContainer(sd.Value, sb);
					txt = sb.ToString().Trim();
				}
				catch { }
				var m = System.Text.RegularExpressions.Regex.Match(txt.Replace(" ", ""), @"-?\d+(\.\d+)?$");
				if (m.Success) k.Printed = double.Parse(m.Value, CultureInfo.InvariantCulture);
				string ends = "";
				try { ends = N(sd.StartPoint.X, "0") + "," + N(sd.StartPoint.Y, "0") + "->" + N(sd.EndPoint.X, "0") + "," + N(sd.EndPoint.Y, "0"); } catch { }
				k.Text = N(k.Length, "0.#") + (txt.Length > 0 ? "='" + txt + "'" : "") + " d=" + N(k.Distance, "0.#") + " " + ends;
				list.Add(k);
			}
		}
		catch { }
		return list;
	}

	// ---------------- rows and height columns ----------------

	/// <summary>Which model objects feed a row: id, name, family + source, along range and Z range.</summary>
	private void CoordLogRowSources(CatalogRow row)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!CoordCheckEnabled || row == null) return;
		var sb = new StringBuilder("row '" + row.Label + "' kind=" + row.Kind + " " + (row.IsBottom ? "bottom" : "top")
			+ " stations=" + string.Join("|", row.Stations.Select(s => N(s, "0.#"))) + " items=" + row.Items.Count);
		foreach (PanelFeature f in row.Items)
			sb.Append(" {id=").Append(f.Id).Append(" name='").Append(f.Name).Append("' family=").Append(f.Family)
				.Append(" via ").Append(string.IsNullOrEmpty(f.FamilySource) ? "?" : f.FamilySource)
				.Append(" kind=").Append(f.Kind).Append(string.IsNullOrEmpty(f.CutClass) ? "" : " cut=" + f.CutClass)
				.Append(" along=").Append(N(f.Left, "0.#")).Append("..").Append(N(f.Right, "0.#"))
				.Append(" z=").Append(N(f.ZMin, "0.#")).Append("..").Append(N(f.ZMax, "0.#")).Append("}");
		CoordLine(sb.ToString());
		}
	}

	/// <summary>Per column: model Z, view-local Y, global Z, type, point count and the values each type would print.</summary>
	private void CoordLogHcol(View view, string label, List<double> model, double[] pts, double edgeX, double bottomY, string type)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!CoordCheckEnabled || model == null || pts == null) return;
		try
		{
			var global = new List<string>();
			try
			{
				Matrix toGlobal = MatrixFactory.FromCoordinateSystem(view.ViewCoordinateSystem);
				foreach (double y in pts) global.Add(N(toGlobal.Transform(new Point(edgeX, bottomY + y, 0.0)).Z, "0.#"));
			}
			catch { }
			var rel = new List<string>();
			for (int i = 1; i < model.Count; i++) rel.Add(N(model[i] - model[i - 1], "0"));
			var abs = model.Skip(1).Select(z => N(z - model[0], "0")).ToList();
			bool absolute = type.IndexOf("Absolute", StringComparison.OrdinalIgnoreCase) >= 0;
			bool only2 = model.Count <= 2;
			CoordLine("hcol '" + label + "' type=" + type + " pts=" + model.Count
				+ " modelZ=" + string.Join("|", model.Select(z => N(z, "0.#")))
				+ " viewY=" + string.Join("|", pts.Select(y => N(bottomY + y, "0.#")))
				+ " globalZ=" + string.Join("|", global)
				+ " prints=" + string.Join("|", absolute ? abs : rel) + " (relative would print " + string.Join("|", rel) + ")"
				+ (only2 ? " HCOL-ONLY-2PTS" : ""));
			CoordCsv("hcol", _coordWhere, label, type, points: model.Count, flags: only2 ? "HCOL-ONLY-2PTS" : "",
				detail: "prints=" + string.Join("|", absolute ? abs : rel));
		}
		catch { }
		}
	}

	private void CoordLogHcolSkip(string label, string reason)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!CoordCheckEnabled) return;
		CoordLine("hcol-skip '" + label + "': " + reason);
		CoordCsv("hcol-skip", _coordWhere, label, detail: reason);
		}
	}

	// ---------------- calibration probe ----------------

	private static readonly DimensionSetBaseAttributes.DimensionTypes[] CoordProbeTypes =
		{ DimensionSetBaseAttributes.DimensionTypes.Relative, DimensionSetBaseAttributes.DimensionTypes.Absolute };

	/// <summary>
	/// Scratch views at 1:75 and 1:20 copying the front view's coordinate system. Per view, type, mode and passed
	/// distance (10, 100), one set through (0,y0) (400,y0) (1000,y0): Relative should print 400|600 and Absolute
	/// 400|1000. "create" passes the distance to CreateDimensionSet; "modify" sets Distance afterwards, as the
	/// page-1 code does. The RULE line compares the read-back Distance with what was passed. Cleanup is in finally.
	/// </summary>
	private void RunCoordProbe(ContainerView sheet, View front)
	{
		using (CoordCheckEnabled ? StageTimer.StartCoord() : null)
		{
		if (!(CoordProbeEnabled || CoordProbeKeep) || _coordProbeDone || sheet == null || front == null) return;
		_coordProbeDone = true;
		string savedWhere = _coordWhere;
		_coordWhere = "probe";
		var views = new List<View>();
		var doomed = new List<DrawingObject>();
		var read = new Dictionary<string, double>();
		var printedByCase = new Dictionary<string, string>();
		try
		{
			int idx = 0;
			foreach (int sc in new[] { 75, 20 })
			{
				View v = null;
				try
				{
					CoordinateSystem cs = front.ViewCoordinateSystem;
					v = new View(sheet, cs, cs, new AABB(new Point(-200, -60.0 * sc, -500), new Point(1200, 120.0 * sc, 500)));
					v.Name = CoordProbeViewPrefix + "-" + sc.ToString(CultureInfo.InvariantCulture);
					v.Attributes.Scale = sc;
					v.Origin = new Point(30.0 + 60.0 * idx++, 60.0, 0.0);
					if (!v.Insert()) { CoordLine("probe scale=" + sc + " scratch view insert failed"); continue; }
					views.Add(v);
				}
				catch (Exception ex) { CoordLine("probe scale=" + sc + " view error: " + ex.Message); continue; }

				foreach (DimensionSetBaseAttributes.DimensionTypes type in CoordProbeTypes)
				{
					double y0 = type == DimensionSetBaseAttributes.DimensionTypes.Absolute ? -40.0 * sc : 0.0;
					foreach (string mode in new[] { "create", "modify" })
						foreach (double passed in new[] { 10.0, 100.0 })
						{
							string key = sc + "|" + type + "|" + mode + "|" + N(passed);
							bool keep = CoordProbeKeep && mode == "create" && passed >= 100.0;
							StraightDimensionSet set = null;
							try
							{
								var pts = new PointList { new Point(0, y0, 0), new Point(400, y0, 0), new Point(1000, y0, 0) };
								var attrs = NewDimensionSetAttributes();
								try { attrs.LoadAttributes("standard"); } catch { }
								try { attrs.DimensionType = type; } catch { }
								set = _dims.CreateDimensionSet(v, pts, new Vector(0, 1, 0), mode == "create" ? passed : 1.0, attrs);
								if (set == null) { CoordLine("probe " + key + " create returned null"); continue; }
								if (mode == "modify")
								{
									try { set.Distance = passed; set.Modify(); } catch { }
								}
								bool committed = CoordCommit(v);
								try { set.Select(); } catch { }
								double dist = double.NaN, textH = double.NaN;
								try { dist = set.Distance; } catch { }
								try { textH = set.Attributes.Text.Font.Height; } catch { }
								List<KidReadback> kids = ReadKids(set, new Vector(1, 0, 0));
								double kidDist = kids.Count > 0 ? kids[0].Distance : double.NaN;
								string printed = string.Join("|", kids.Select(k => double.IsNaN(k.Printed) ? "?" : N(k.Printed, "0")));
								bool invalid = !committed || double.IsNaN(dist) || kids.Count == 0;
								if (!invalid) read[key] = !double.IsNaN(kidDist) ? kidDist : dist;
								printedByCase[key] = printed;
								CoordLine("probe scale=" + sc + " type=" + type + " mode=" + mode + " passed=" + N(passed)
									+ " setDistance=" + N(dist) + " kidDistance=" + N(kidDist) + " printed=" + printed
									+ " kids=[" + string.Join("|", kids.Select(k => k.Text)) + "] text=" + N(textH, "0.##")
									+ (keep ? " KEPT" : "") + (invalid ? " READBACK-INVALID" : ""));
								CoordCsv("probe", "probe", mode, type.ToString(), sc, 3, ourLen: 1000.0, value: printed, distPassed: passed,
									distRead: dist, offsetView: invalid ? double.NaN : kidDist, offsetPaper: invalid ? double.NaN : kidDist / sc,
									textH: textH, flags: invalid ? "READBACK-INVALID" : "", detail: "kids=" + string.Join("|", kids.Select(k => k.Text)));
								if (keep)
								{
									ProbeReference(v, y0 + passed, "y=" + N(passed, "0") + " view units", doomed);
									ProbeReference(v, y0 + passed * sc, "y=" + N(passed, "0") + " paper mm", doomed);
									set = null;
								}
							}
							catch (Exception ex) { CoordLine("probe " + key + " error: " + ex.Message); }
							finally
							{
								if (set != null) { try { set.Delete(); } catch { doomed.Add(set); } }
							}
						}
				}

				foreach (DimensionSetBaseAttributes.DimensionTypes type in CoordProbeTypes)
					foreach (string mode in new[] { "create", "modify" })
					{
						string k10 = sc + "|" + type + "|" + mode + "|10", k100 = sc + "|" + type + "|" + mode + "|100";
						if (!read.TryGetValue(k10, out double r10) || !read.TryGetValue(k100, out double r100))
						{
							CoordLine("probe RULE scale=" + sc + " type=" + type + " mode=" + mode + " undetermined (invalid read-back)");
							continue;
						}
						double k = (r100 - r10) / 90.0;
						string rule = Math.Abs(k - 1.0) < 0.15 ? "stored as passed (same unit as Distance)"
							: Math.Abs(k - sc) < 0.15 * sc ? "stored = passed x scale (argument is paper mm, Distance is view units)"
							: Math.Abs(k - 1.0 / sc) < 0.15 / sc ? "stored = passed / scale" : "unclear";
						CoordLine("probe RULE scale=" + sc + " type=" + type + " mode=" + mode + " read(10)=" + N(r10) + " read(100)=" + N(r100)
							+ " k=" + N(k, "0.###") + " -> " + rule);
						CoordCsv("probe-rule", "probe", mode, type.ToString(), sc, flags: rule, detail: "k=" + N(k, "0.###"));
					}
				printedByCase.TryGetValue(sc + "|Relative|create|10", out string pr);
				printedByCase.TryGetValue(sc + "|Absolute|create|10", out string pa);
				CoordLine("probe TYPES scale=" + sc + " relative printed=" + (pr ?? "?") + " absolute printed=" + (pa ?? "?")
					+ " (expect 400|600 vs 400|1000)" + (pr != null && pr == pa ? " SAME-OUTPUT" : ""));
			}
		}
		catch (Exception ex) { CoordLine("probe error: " + ex.Message); }
		finally
		{
			if (!CoordProbeKeep)
			{
				foreach (DrawingObject o in doomed) { try { o.Delete(); } catch { } }
				foreach (View v in views) { try { v.Delete(); } catch { } }
			}
			CoordCommit(front);
			int leftover = 0;
			try
			{
				DrawingObjectEnumerator en = sheet.GetAllViews();
				while (en != null && en.MoveNext())
					if (en.Current is View v && (v.Name ?? "").StartsWith(CoordProbeViewPrefix, StringComparison.Ordinal)) leftover++;
			}
			catch { }
			int expect = CoordProbeKeep ? views.Count : 0;
			CoordLine("probe leftover=" + leftover + " expect=" + expect + (leftover == expect ? "" : " PROBE-LEFTOVER")
				+ (CoordProbeKeep ? " (kept for the PDF: measure the dimension line against the two reference lines)" : ""));
			_coordWhere = savedWhere;
		}
		}
	}

	/// <summary>Keep mode only: a red reference line across the probe set at view-local Y, with a label.</summary>
	private static void ProbeReference(View v, double y, string label, List<DrawingObject> created)
	{
		try
		{
			var line = new Tekla.Structures.Drawing.Line(v, new Point(-100, y, 0), new Point(1100, y, 0));
			try { line.Attributes.Line.Color = DrawingColors.Red; } catch { }
			if (line.Insert()) created.Add(line);
			var text = new Text(v, new Point(1120, y, 0), label);
			if (text.Insert()) created.Add(text);
		}
		catch { }
	}
}

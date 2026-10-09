using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
	/// <summary>
	/// Writes preflight_{piece}.log before any view is touched. Logs only:
	/// nothing here edits a bar, a station, or a dimension.
	/// </summary>
	public static class PreflightQaChecker
	{
		public static string LogPath(string dir, string piece)
		{
			string folder = string.IsNullOrWhiteSpace(dir) ? "." : dir;
			return Path.Combine(folder, "preflight_" + CivilDrawingSupport.Sanitize(piece) + ".log");
		}

		/// <summary>Model-side checks. Starts a fresh log for the piece.</summary>
		public static void Run(TSModel.Part part, string piece, string dir)
		{
			if (part == null || string.IsNullOrWhiteSpace(piece)) return;
			string path = LogPath(dir, piece);
			try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); } catch { }
			try { File.WriteAllText(path, "# preflight " + piece + " " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + Environment.NewLine); }
			catch { return; }
			try { File.WriteAllText(QmarksPath(dir, piece), "# qmarks " + piece + " " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + Environment.NewLine); }
			catch { }

			int bars = 0;
			int warnings = 0;
			foreach (CivilBbsBar bar in ReadBars(part, piece))
			{
				bars++;
				if (!IsBent(bar)) continue;
				string id = BbsKey(bar);
				string shape = (bar.ShapeCode ?? "").Trim();
				if (shape.Length == 0)
					warnings += Append(dir, piece, "SHAPE empty on bent bar " + id);
				else if (shape.IndexOf("UNKNOWN", StringComparison.OrdinalIgnoreCase) >= 0)
					warnings += Append(dir, piece, "SHAPE Unknown on bent bar " + id);
				if (!bar.PinRadiusMm.HasValue || bar.PinRadiusMm.Value <= 0.0)
					warnings += Append(dir, piece, "pin/radius missing on bent bar " + id);
				double legs = LegSum(bar);
				double cut = bar.TotalLengthMm ?? 0.0;
				if (legs > 0.0 && cut > 0.0 && legs < cut - 0.5)
					warnings += Append(dir, piece, "legs " + Mm(legs) + " < cut length " + Mm(cut) + " on bent bar " + id);
			}
			Append(dir, piece, "bars=" + bars.ToString(CultureInfo.InvariantCulture)
				+ " warnings=" + warnings.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("[Preflight] " + piece + " bars=" + bars + " warnings=" + warnings + " → " + path);
		}

		/// <summary>
		/// Rebar shown in the placing views vs the BBS (model) quantity, keyed by mark for bent bars
		/// and by size + length for straights, which carry no mark.
		/// </summary>
		public static void CompareViewToBbs(TSModel.Model model, TSModel.Part part, IEnumerable<View> views, string piece, string dir)
		{
			if (model == null || part == null || views == null) return;
			var bbs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (CivilBbsBar bar in ReadBars(part, piece))
				Bump(bbs, BbsKey(bar), Math.Max(1, bar.Quantity));

			var shown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var seen = new HashSet<int>();
			foreach (View view in views)
			{
				if (view == null) continue;
				DrawingObjectEnumerator en = null;
				try { en = view.GetObjects(new[] { typeof(ReinforcementBase) }); } catch { }
				while (en != null && en.MoveNext())
				{
					var rb = en.Current as ReinforcementBase;
					if (rb == null) continue;
					TSModel.Reinforcement reinf = null;
					try { reinf = model.SelectModelObject(rb.ModelIdentifier) as TSModel.Reinforcement; } catch { }
					if (reinf == null) continue;
					int id = 0;
					try { id = reinf.Identifier.ID; } catch { }
					if (id != 0 && !seen.Add(id)) continue;
					CivilBbsBar bar = CivilDrawingSupport.ReadReinforcement(reinf, piece, "view", "");
					Bump(shown, BbsKey(bar), Math.Max(1, bar.Quantity));
				}
			}

			int mismatches = 0;
			foreach (string key in bbs.Keys.Union(shown.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
			{
				bbs.TryGetValue(key, out int want);
				shown.TryGetValue(key, out int have);
				if (want == have) continue;
				mismatches++;
				Append(dir, piece, "BBS qty " + key + " view=" + have.ToString(CultureInfo.InvariantCulture)
					+ " bbs=" + want.ToString(CultureInfo.InvariantCulture));
			}
			Append(dir, piece, "BBS compare keys=" + bbs.Count.ToString(CultureInfo.InvariantCulture)
				+ " mismatches=" + mismatches.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("[Preflight] " + piece + " placing vs BBS mismatches=" + mismatches);
		}

		/// <summary>
		/// Each segment is rounded the way Tekla prints it (ToEven) before summing. A 1 mm gap is
		/// rounding (logged within-1); 2 mm or more is a [RowClosure] MISS. The drawing keeps the real values.
		/// </summary>
		public static void LogChainDrift(string dir, string piece, string label, IList<double> stations, double bboxLength)
		{
			if (stations == null || stations.Count < 2 || bboxLength <= 0.0) return;
			double sum = 0.0;
			var segs = new List<string>();
			for (int i = 1; i < stations.Count; i++)
			{
				double seg = Math.Round(Math.Abs(stations[i] - stations[i - 1]), 0, MidpointRounding.ToEven);
				sum += seg;
				segs.Add(seg.ToString("0", CultureInfo.InvariantCulture));
			}
			double box = Math.Round(bboxLength, 0, MidpointRounding.ToEven);
			double gap = Math.Abs(sum - box);
			if (gap < 1.0) return;
			Append(dir, piece, (gap <= 1.0 ? "[RowClosure] within-1 " : "[RowClosure] MISS ")
				+ "label=" + (label ?? "chain") + " " + string.Join("|", segs)
				+ " sum=" + sum.ToString("0", CultureInfo.InvariantCulture)
				+ " overall=" + box.ToString("0", CultureInfo.InvariantCulture));
		}

		public static string QmarksPath(string dir, string piece)
		{
			string folder = string.IsNullOrWhiteSpace(dir) ? "." : dir;
			return Path.Combine(folder, "qmarks_" + CivilDrawingSupport.Sanitize(piece) + ".log");
		}

		public static int Append(string dir, string piece, string line)
		{
			if (string.IsNullOrWhiteSpace(piece)) return 0;
			try
			{
				File.AppendAllText(LogPath(dir, piece), line + Environment.NewLine);
				return 1;
			}
			catch { return 0; }
		}

		/// <summary>Bent bars by mark, straights by size + length (e.g. 10M-STR 3120).</summary>
		public static string BbsKey(CivilBbsBar bar)
		{
			if (bar == null) return "";
			if (IsBent(bar))
				return CivilDrawingSupport.FirstNonEmpty(bar.Mark, bar.Size + "-BENT");
			double len = Math.Round(bar.TotalLengthMm ?? 0.0, 0, MidpointRounding.ToEven);
			return (bar.Size ?? "").Trim() + "-STR " + len.ToString("0", CultureInfo.InvariantCulture);
		}

		public static bool IsBent(CivilBbsBar bar)
		{
			return bar != null && !string.Equals(bar.ShapeType, "straight", StringComparison.OrdinalIgnoreCase);
		}

		private static IEnumerable<CivilBbsBar> ReadBars(TSModel.Part part, string piece)
		{
			var list = new List<CivilBbsBar>();
			TSModel.ModelObjectEnumerator en = null;
			try { en = part.GetReinforcements(); } catch { }
			while (en != null && en.MoveNext())
			{
				if (!(en.Current is TSModel.Reinforcement reinf)) continue;
				try { list.Add(CivilDrawingSupport.ReadReinforcement(reinf, piece, "model", "")); }
				catch { }
			}
			return list;
		}

		private static double LegSum(CivilBbsBar bar)
		{
			double[] dims = { bar.A ?? 0, bar.B ?? 0, bar.C ?? 0, bar.D ?? 0, bar.E ?? 0, bar.F ?? 0, bar.G ?? 0 };
			double sum = dims.Where(d => d > 0).Sum();
			if (sum > 0.0) return sum;
			for (int i = 1; i < bar.Geometry.Count; i++)
			{
				CivilXyz a = bar.Geometry[i - 1];
				CivilXyz b = bar.Geometry[i];
				double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
				sum += Math.Sqrt(dx * dx + dy * dy + dz * dz);
			}
			return sum;
		}

		private static void Bump(Dictionary<string, int> map, string key, int n)
		{
			if (string.IsNullOrWhiteSpace(key)) return;
			map.TryGetValue(key, out int v);
			map[key] = v + n;
		}

		private static string Mm(double v)
		{
			return Math.Round(v, 0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
		}
	}
}

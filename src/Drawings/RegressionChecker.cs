using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TeklaExtractor.Services
{
	/// <summary>
	/// Scores the page-1 rows actually present on a drawing against an engineer fixture
	/// (tests/regression/{tuning|holdout}/{piece}.json). Read-only: fixtures are never drawing input.
	/// </summary>
	public static class RegressionChecker
	{
		public sealed class Row
		{
			public string Label = "";
			public string Side = "top";
			public int Order;
			public List<double> Segments = new List<double>();
			public List<string> Aliases = new List<string>();
			public int SumTol;
			public string KnownDeviation;
			public string Note;
			/// <summary>Row's height column, panel bottom to top or top to bottom (either is scored).</summary>
			public List<double> Heights = new List<double>();
			public string HeightSide;
		}

		public sealed class Fixture
		{
			public string Piece = "";
			public string Set = "";
			public double LengthMm;
			public double HeightMm;
			public string Path = "";
			public List<Row> Rows = new List<Row>();
			public Dictionary<string, int> Bom = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>Explicit fixture root (--fixtures). Otherwise tests/regression is searched upward from cwd and the exe.</summary>
		public static string FixtureRoot { get; set; }

		/// <summary>Run tag written to regression_scores.csv (--run-tag).</summary>
		public static string RunTag { get; set; }

		public static Fixture Load(string piece)
		{
			if (string.IsNullOrWhiteSpace(piece)) return null;
			foreach (string root in CandidateRoots())
			{
				foreach (string sub in new[] { "tuning", "holdout", "" })
				{
					string path = System.IO.Path.Combine(root, sub, piece.Trim() + ".json");
					if (!File.Exists(path)) continue;
					try { return Parse(path); }
					catch (Exception ex)
					{
						Console.WriteLine("[Regression] fixture " + path + " unreadable: " + ex.Message);
						return null;
					}
				}
			}
			return null;
		}

		private static IEnumerable<string> CandidateRoots()
		{
			if (!string.IsNullOrWhiteSpace(FixtureRoot)) yield return FixtureRoot;
			var starts = new List<string>();
			try { starts.Add(Directory.GetCurrentDirectory()); } catch { }
			try { starts.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string start in starts)
			{
				DirectoryInfo d = null;
				try { d = new DirectoryInfo(start); } catch { }
				for (int i = 0; d != null && i < 8; i++, d = d.Parent)
				{
					string cand = System.IO.Path.Combine(d.FullName, "tests", "regression");
					if (Directory.Exists(cand) && seen.Add(cand)) yield return cand;
				}
			}
		}

		private static Fixture Parse(string path)
		{
			JObject o = JObject.Parse(File.ReadAllText(path));
			var f = new Fixture
			{
				Piece = (string)o["piece"] ?? "",
				Set = (string)o["set"] ?? "",
				LengthMm = (double?)o["lengthMm"] ?? 0.0,
				HeightMm = (double?)o["heightMm"] ?? 0.0,
				Path = path
			};
			if (o["rows"] is JArray rows)
			{
				foreach (JToken r in rows)
				{
					var row = new Row
					{
						Label = (string)r["label"] ?? "",
						Side = ((string)r["side"] ?? "top").ToLowerInvariant(),
						Order = (int?)r["order"] ?? 0,
						SumTol = (int?)r["sumTol"] ?? 0,
						KnownDeviation = (string)r["knownDeviation"],
						Note = (string)r["note"]
					};
					if (r["segments"] is JArray segs)
						row.Segments = segs.Select(s => (double)s).ToList();
					if (r["aliases"] is JArray al)
						row.Aliases = al.Select(a => (string)a).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
					if (r["heights"] is JArray hs)
						row.Heights = hs.Select(v => (double)v).ToList();
					row.HeightSide = ((string)r["heightSide"])?.ToLowerInvariant();
					f.Rows.Add(row);
				}
			}
			if (o["bom"] is JObject bom)
			{
				foreach (var kv in bom)
					f.Bom[kv.Key] = (int)kv.Value;
			}
			return f;
		}

		/// <summary>Rows whose segments do not close to the panel length (within sumTol) are fixture errors.</summary>
		public static bool SelfCheck(Fixture f, Row row, out double sum)
		{
			sum = row.Segments.Sum();
			return f.LengthMm <= 0.0 || Math.Abs(sum - f.LengthMm) <= row.SumTol + 0.01;
		}

		// ---------- labels ----------

		private static readonly HashSet<string> IgnoredWords = new HashSet<string>(StringComparer.Ordinal) { "PROFILE", "HARDWARE" };

		/// <summary>Upper-case word set: punctuation dropped, plural S dropped, PROFILE/HARDWARE ignored, order ignored.</summary>
		public static string NormalizeLabel(string label)
		{
			return string.Join(" ", LabelWords(label));
		}

		private static SortedSet<string> LabelWords(string label)
		{
			string u = (label ?? "").ToUpperInvariant();
			u = Regex.Replace(u, @"[^A-Z0-9\-]+", " ");
			var words = new SortedSet<string>(StringComparer.Ordinal);
			foreach (string raw in u.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string w = raw.Trim('-');
				if (w.Length == 0 || IgnoredWords.Contains(w)) continue;
				if (w.Length > 3 && w.EndsWith("S", StringComparison.Ordinal) && !w.EndsWith("SS", StringComparison.Ordinal))
					w = w.Substring(0, w.Length - 1);
				words.Add(w);
			}
			return words;
		}

		/// <summary>2 = same words, 1 = one word set contains the other, 0 = no match.</summary>
		private static int LabelScore(Row f, string emitted)
		{
			SortedSet<string> e = LabelWords(emitted);
			if (e.Count == 0) return 0;
			int best = 0;
			foreach (string cand in new[] { f.Label }.Concat(f.Aliases))
			{
				SortedSet<string> w = LabelWords(cand);
				if (w.Count == 0) continue;
				if (w.SetEquals(e)) return 2;
				if (w.IsSubsetOf(e) || e.IsSubsetOf(w)) best = 1;
			}
			return best;
		}

		// ---------- scoring ----------

		public static List<double> RoundSegments(IList<double> segs)
		{
			return segs.Select(s => Math.Round(s, 0, MidpointRounding.ToEven)).ToList();
		}

		private static double StationCost(List<double> expected, List<double> got)
		{
			if (expected.Count != got.Count)
			{
				double ce = 0, cg = 0, diff = 0;
				int n = Math.Min(expected.Count, got.Count);
				for (int i = 0; i < n; i++) { ce += expected[i]; cg += got[i]; diff += Math.Abs(ce - cg); }
				return 10000.0 + diff;
			}
			double cost = 0;
			for (int i = 0; i < expected.Count; i++) cost += Math.Abs(expected[i] - got[i]);
			return cost;
		}

		/// <summary>
		/// Scores emitted rows against the fixture for this piece. Writes per-row lines to the preflight log
		/// and one summary line to regression_scores.csv in csvDir. No-op when no fixture exists.
		/// </summary>
		public static void Score(string piece, IList<Row> emitted, string logDir, string csvDir)
		{
			Fixture f = Load(piece);
			if (f == null)
			{
				PreflightQaChecker.Append(logDir, piece, "[Regression] no fixture for " + piece + " (emitted rows=" + (emitted?.Count ?? 0) + ")");
				foreach (Row e in emitted ?? new List<Row>())
					PreflightQaChecker.Append(logDir, piece, "[Regression] row " + e.Side + " #" + e.Order + " '" + e.Label + "' " + string.Join("|", RoundSegments(e.Segments)));
				return;
			}
			emitted = emitted ?? new List<Row>();
			Log(logDir, piece, "[Regression] fixture " + f.Path + " set=" + f.Set + " rows=" + f.Rows.Count + " emitted=" + emitted.Count);

			var expected = new List<Row>();
			foreach (Row r in f.Rows)
			{
				if (!SelfCheck(f, r, out double sum))
				{
					Log(logDir, piece, "[Regression] FIXTURE-ERROR '" + r.Label + "' sum=" + sum.ToString("0", CultureInfo.InvariantCulture)
						+ " length=" + f.LengthMm.ToString("0", CultureInfo.InvariantCulture) + " (excluded from score)");
					continue;
				}
				expected.Add(r);
			}

			// Candidate pairs: label must match (or the emitted row is untagged); cost = label penalty + stations + side.
			var pairs = new List<Tuple<double, int, int, int>>();
			for (int i = 0; i < expected.Count; i++)
			{
				List<double> want = RoundSegments(expected[i].Segments);
				for (int j = 0; j < emitted.Count; j++)
				{
					bool untagged = string.IsNullOrWhiteSpace(emitted[j].Label) || emitted[j].Label.StartsWith("(untagged", StringComparison.OrdinalIgnoreCase);
					int ls = untagged ? 0 : LabelScore(expected[i], emitted[j].Label);
					if (ls == 0 && !untagged) continue;
					double cost = StationCost(want, RoundSegments(emitted[j].Segments));
					if (untagged && cost >= 10000.0) continue;
					cost += ls == 2 ? 0.0 : (ls == 1 ? 100.0 : 5000.0);
					if (!string.Equals(expected[i].Side, emitted[j].Side, StringComparison.OrdinalIgnoreCase)) cost += 50.0;
					pairs.Add(Tuple.Create(cost, i, j, ls));
				}
			}
			pairs.Sort((a, b) => a.Item1.CompareTo(b.Item1));
			var fToE = new Dictionary<int, Tuple<int, int>>();
			var usedE = new HashSet<int>();
			foreach (var p in pairs)
			{
				if (fToE.ContainsKey(p.Item2) || usedE.Contains(p.Item3)) continue;
				fToE[p.Item2] = Tuple.Create(p.Item3, p.Item4);
				usedE.Add(p.Item3);
			}

			// #1/#2 names for fixture rows that share a normalised label.
			var labelCounts = expected.GroupBy(r => NormalizeLabel(r.Label)).ToDictionary(g => g.Key, g => g.Count());
			var labelSeen = new Dictionary<string, int>();

			int pass = 0, within1 = 0, known = 0, fail = 0, missing = 0;
			for (int i = 0; i < expected.Count; i++)
			{
				Row want = expected[i];
				string key = NormalizeLabel(want.Label);
				string name = want.Label;
				if (labelCounts[key] > 1)
				{
					labelSeen.TryGetValue(key, out int k);
					labelSeen[key] = ++k;
					name += "#" + k.ToString(CultureInfo.InvariantCulture);
				}
				List<double> ws = RoundSegments(want.Segments);
				if (!fToE.TryGetValue(i, out var hit))
				{
					if (want.KnownDeviation != null) { known++; Log(logDir, piece, "[Regression] KNOWN-DEVIATION " + name + " (missing) " + want.KnownDeviation); }
					else { missing++; Log(logDir, piece, "[Regression] MISSING " + name + " " + want.Side + " expected=" + string.Join("|", ws)); }
					continue;
				}
				Row got = emitted[hit.Item1];
				List<double> gs = RoundSegments(got.Segments);
				string result;
				string detail = "";
				bool sameCount = ws.Count == gs.Count;
				double maxDiff = sameCount ? ws.Zip(gs, (a, b) => Math.Abs(a - b)).DefaultIfEmpty(0).Max() : double.MaxValue;
				bool sideOk = string.Equals(want.Side, got.Side, StringComparison.OrdinalIgnoreCase);
				bool labelOk = hit.Item2 > 0;
				if (sameCount && maxDiff < 0.5) result = "PASS";
				else if (sameCount && maxDiff <= 1.0)
				{
					result = "PASS-within-1";
					detail = " diffs=[" + string.Join(", ", ws.Zip(gs, (a, b) => Tuple.Create(a, b)).Where(t => Math.Abs(t.Item1 - t.Item2) >= 0.5)
						.Select(t => t.Item2.ToString("0", CultureInfo.InvariantCulture) + " vs " + t.Item1.ToString("0", CultureInfo.InvariantCulture))) + "]";
				}
				else result = "FAIL";
				if (result != "FAIL" && !sideOk) { result = "FAIL side"; detail += " side=" + got.Side + " expected " + want.Side; }
				if (result != "FAIL" && !result.StartsWith("FAIL", StringComparison.Ordinal) && !labelOk) { result = "FAIL label"; detail += " (untagged, stations match)"; }
				if (result.StartsWith("FAIL", StringComparison.Ordinal) && want.KnownDeviation != null) result = "KNOWN-DEVIATION";

				if (result == "PASS") pass++;
				else if (result == "PASS-within-1") within1++;
				else if (result == "KNOWN-DEVIATION") known++;
				else fail++;

				string line = "[Regression] " + result + " " + name + detail;
				if (result.StartsWith("FAIL", StringComparison.Ordinal) || result == "KNOWN-DEVIATION")
					line += " expected=" + string.Join("|", ws) + " got=" + string.Join("|", gs) + " as '" + got.Label + "' " + got.Side;
				if (want.Order > 0 && got.Order > 0 && want.Order != got.Order)
					line += " (ORDER info: expected " + want.Order + ", got " + got.Order + ")";
				Log(logDir, piece, line);
			}
			int extra = 0;
			for (int j = 0; j < emitted.Count; j++)
			{
				if (usedE.Contains(j)) continue;
				extra++;
				Log(logDir, piece, "[Regression] EXTRA '" + emitted[j].Label + "' " + emitted[j].Side + " " + string.Join("|", RoundSegments(emitted[j].Segments)));
			}
			string summary = "pass=" + pass + " within1=" + within1 + " known=" + known + " fail=" + fail + " missing=" + missing + " extra=" + extra
				+ " of " + expected.Count;
			Log(logDir, piece, "[Regression] SUMMARY " + piece + " (" + f.Set + ") " + summary);
			AppendScore(csvDir, piece, f.Set, pass, within1, known, fail, missing, extra, expected.Count);
		}

		/// <summary>Cumulative levels from 0, read bottom-up (reverse=false) or top-down (reverse=true).</summary>
		private static List<double> Levels(IList<double> segments, bool reverse)
		{
			IEnumerable<double> seq = reverse ? segments.Reverse() : segments;
			var levels = new List<double> { 0.0 };
			double s = 0.0;
			foreach (double v in seq) { s += v; levels.Add(s); }
			return levels;
		}

		/// <summary>(fraction of the longer list matched within 1 mm, summed diff of matched levels).</summary>
		private static Tuple<double, double> LevelMatch(List<double> want, List<double> got)
		{
			var used = new HashSet<int>();
			int matched = 0;
			double diff = 0.0;
			foreach (double w in want)
			{
				int best = -1;
				for (int k = 0; k < got.Count; k++)
				{
					if (used.Contains(k) || Math.Abs(w - got[k]) > 1.0) continue;
					if (best < 0 || Math.Abs(w - got[k]) < Math.Abs(w - got[best])) best = k;
				}
				if (best < 0) continue;
				used.Add(best);
				matched++;
				diff += Math.Abs(w - got[best]);
			}
			return Tuple.Create(matched / (double)Math.Max(want.Count, got.Count), diff);
		}

		/// <summary>
		/// Scores the vertical height columns on page 1 against fixture rows that carry heights[].
		/// Columns pair by levels in either direction; the side is info only. No-op without fixture heights.
		/// </summary>
		public static void ScoreHeights(string piece, IList<Row> columns, string logDir, string csvDir)
		{
			Fixture f = Load(piece);
			if (f == null) return;
			columns = columns ?? new List<Row>();
			var expected = new List<Row>();
			foreach (Row r in f.Rows.Where(r => r.Heights.Count > 0))
			{
				double sum = r.Heights.Sum();
				if (f.HeightMm > 0.0 && Math.Abs(sum - f.HeightMm) > r.SumTol + 0.01)
				{
					Log(logDir, piece, "[Regression] HCOL FIXTURE-ERROR '" + r.Label + "' sum=" + sum.ToString("0", CultureInfo.InvariantCulture)
						+ " height=" + f.HeightMm.ToString("0", CultureInfo.InvariantCulture) + " (excluded from score)");
					continue;
				}
				expected.Add(r);
			}
			if (expected.Count == 0) return;
			var gotLevels = columns.Select(c => Levels(RoundSegments(c.Segments), false)).ToList();
			Log(logDir, piece, "[Regression] HCOL fixture columns=" + expected.Count + " drawn columns=" + columns.Count);

			var pairs = new List<Tuple<double, int, int, bool>>();
			for (int i = 0; i < expected.Count; i++)
			{
				foreach (bool reverse in new[] { false, true })
				{
					List<double> want = Levels(RoundSegments(expected[i].Heights), reverse);
					for (int j = 0; j < columns.Count; j++)
					{
						var m = LevelMatch(want, gotLevels[j]);
						if (m.Item1 >= 0.5) pairs.Add(Tuple.Create((1.0 - m.Item1) * 1000.0 + m.Item2, i, j, reverse));
					}
				}
			}
			pairs.Sort((a, b) => a.Item1.CompareTo(b.Item1));
			var hit = new Dictionary<int, Tuple<int, bool>>();
			var used = new HashSet<int>();
			foreach (var p in pairs)
			{
				if (hit.ContainsKey(p.Item2) || used.Contains(p.Item3)) continue;
				hit[p.Item2] = Tuple.Create(p.Item3, p.Item4);
				used.Add(p.Item3);
			}

			int pass = 0, within1 = 0, known = 0, fail = 0, missing = 0;
			for (int i = 0; i < expected.Count; i++)
			{
				Row want = expected[i];
				if (!hit.TryGetValue(i, out var h))
				{
					if (want.KnownDeviation != null) known++; else missing++;
					Log(logDir, piece, "[Regression] HCOL " + (want.KnownDeviation != null ? "KNOWN-DEVIATION" : "MISSING") + " '" + want.Label
						+ "' expected levels=" + string.Join("|", Levels(RoundSegments(want.Heights), false)));
					continue;
				}
				List<double> w = Levels(RoundSegments(want.Heights), h.Item2);
				List<double> g = gotLevels[h.Item1];
				string result;
				if (w.Count == g.Count)
				{
					double maxDiff = w.Zip(g, (a, b) => Math.Abs(a - b)).Max();
					result = maxDiff < 0.5 ? "PASS" : (maxDiff <= 1.0 ? "PASS-within-1" : "FAIL");
				}
				else result = "FAIL";
				if (result == "FAIL" && want.KnownDeviation != null) result = "KNOWN-DEVIATION";
				if (result == "PASS") pass++;
				else if (result == "PASS-within-1") within1++;
				else if (result == "KNOWN-DEVIATION") known++;
				else fail++;
				Row got = columns[h.Item1];
				string line = "[Regression] HCOL " + result + " '" + want.Label + "' (" + (h.Item2 ? "fixture top-down" : "fixture bottom-up") + ")"
					+ " side=" + got.Side + (want.HeightSide != null && want.HeightSide != got.Side ? " (info: fixture side " + want.HeightSide + ")" : "");
				if (result != "PASS")
					line += " expected=" + string.Join("|", w) + " got=" + string.Join("|", g);
				Log(logDir, piece, line);
			}
			int extra = 0;
			for (int j = 0; j < columns.Count; j++)
			{
				if (used.Contains(j)) continue;
				extra++;
				Log(logDir, piece, "[Regression] HCOL EXTRA " + columns[j].Side + " #" + columns[j].Order + " levels=" + string.Join("|", gotLevels[j]));
			}
			Log(logDir, piece, "[Regression] HCOL SUMMARY " + piece + " (" + f.Set + ") pass=" + pass + " within1=" + within1 + " known=" + known
				+ " fail=" + fail + " missing=" + missing + " extra=" + extra + " of " + expected.Count);
			AppendScore(csvDir, piece, f.Set + ":heights", pass, within1, known, fail, missing, extra, expected.Count);
		}

		private static void Log(string dir, string piece, string line)
		{
			Console.WriteLine(line);
			PreflightQaChecker.Append(dir, piece, line);
		}

		public static void AppendScore(string csvDir, string piece, string set, int pass, int within1, int known, int fail, int missing, int extra, int total)
		{
			try
			{
				string dir = string.IsNullOrWhiteSpace(csvDir) ? "." : csvDir;
				Directory.CreateDirectory(dir);
				string path = System.IO.Path.Combine(dir, "regression_scores.csv");
				if (!File.Exists(path))
					File.WriteAllText(path, "run,time,piece,set,pass,within1,known,fail,missing,extra,total" + Environment.NewLine);
				string run = string.IsNullOrWhiteSpace(RunTag) ? "untagged" : RunTag.Replace(",", ";");
				File.AppendAllText(path, string.Join(",", run, DateTime.Now.ToString("s", CultureInfo.InvariantCulture), piece, set,
					pass, within1, known, fail, missing, extra, total) + Environment.NewLine);
			}
			catch { }
		}
	}
}

using System;
using System.Collections.Generic;

namespace TeklaExtractor.Modeling
{
	/// <summary>
	/// Names boolean-cut features the fitter already walks.
	/// A cut 400–1500 mm wide is an opening. A P-205 / lifter label is a lifter.
	/// </summary>
	public static class FeatureDetector
	{
		public const double OpeningMinMm = 400.0;
		public const double OpeningMaxMm = 1500.0;

		public static bool IsOpeningWidth(double widthMm)
		{
			return widthMm >= OpeningMinMm && widthMm <= OpeningMaxMm;
		}

		public static bool IsLifter(string label)
		{
			if (string.IsNullOrEmpty(label)) return false;
			string u = label.ToUpperInvariant();
			return u.IndexOf("P-205", StringComparison.Ordinal) >= 0
				|| u.IndexOf("LIFTER", StringComparison.Ordinal) >= 0;
		}

		public static string ChainTag(string kind)
		{
			if (string.IsNullOrEmpty(kind)) return "OVERALL PROFILE";
			string u = kind.ToUpperInvariant();
			if (u.IndexOf("OPEN", StringComparison.Ordinal) >= 0) return "OPENING PROFILE";
			if (IsLifter(u)) return "P-205 LIFTERS";
			if (u.IndexOf("REVEAL", StringComparison.Ordinal) >= 0) return "REVEAL (B)";
			if (u.IndexOf("BRAC", StringComparison.Ordinal) >= 0) return "P-300 BRACING";
			return "OVERALL PROFILE";
		}

		/// <summary>
		/// Every qualifying window, left to right. The pier is the gap between windows.
		/// One window yields edge|window|edge. Hardware gaps under 400 mm are not windows.
		/// </summary>
		public static List<double> OpeningChain(IList<double> rawEdges, double axisMin, double axisMax)
		{
			var chain = new List<double>();
			if (axisMax < axisMin) return chain;
			var edges = new List<double>();
			if (rawEdges != null)
			{
				var sorted = new List<double>(rawEdges);
				sorted.Sort();
				foreach (double s in sorted)
				{
					if (edges.Count == 0 || Math.Abs(s - edges[edges.Count - 1]) > 1.0)
						edges.Add(s);
				}
			}

			var windows = new List<Tuple<double, double>>();
			for (int i = 0; i + 1 < edges.Count; i++)
			{
				double w = edges[i + 1] - edges[i];
				if (IsOpeningWidth(w))
					windows.Add(Tuple.Create(edges[i], edges[i + 1]));
			}
			windows.Sort((a, b) => a.Item1.CompareTo(b.Item1));

			chain.Add(axisMin);
			if (windows.Count == 0)
			{
				foreach (double e in edges)
				{
					if (e > axisMin + 0.5 && e < axisMax - 0.5)
						chain.Add(e);
				}
			}
			else
			{
				foreach (var w in windows)
				{
					if (w.Item1 > chain[chain.Count - 1] + 0.5 && w.Item1 < axisMax - 0.5)
						chain.Add(w.Item1);
					if (w.Item2 > chain[chain.Count - 1] + 0.5 && w.Item2 < axisMax - 0.5)
						chain.Add(w.Item2);
				}
			}
			if (Math.Abs(chain[chain.Count - 1] - axisMax) > 0.5)
				chain.Add(axisMax);
			return chain;
		}
	}
}

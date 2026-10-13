using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TeklaExtractor.Services;

/// <summary>
/// Rewrite of coordcheck_&lt;runtag&gt;.csv when a piece is run again. Each --mark run is its own process, so
/// a rerun drops only that piece's rows of the kinds it will write again, plus its old header line.
/// Every other line is kept byte for byte, including its line ending. No Tekla types, so it is testable alone.
/// </summary>
public static class CoordCsvMerge
{
	/// <summary>Kinds every --coord-check run writes for a piece.</summary>
	public static readonly string[] RunKinds = { "view", "hand", "layout", "dim", "hcol", "hcol-skip" };

	/// <summary>Kinds only a probe run writes.</summary>
	public static readonly string[] ProbeKinds = { "probe", "probe-rule" };

	/// <summary>
	/// Returns the new file text: the column line first (added when missing), the kept lines unchanged, then
	/// "# " + header with the file's own newline. keptProbe is true when probe rows of the piece survive.
	/// </summary>
	public static string Merge(string existing, string piece, ICollection<string> dropKinds, string columns, string header, out bool keptProbe, out string newline)
	{
		keptProbe = false;
		existing = existing ?? "";
		newline = existing.Contains("\r\n") ? "\r\n" : (existing.Contains("\n") ? "\n" : Environment.NewLine);
		var headerOfPiece = new Regex(@"^#\s+header\b.*(^|\s)piece=" + Regex.Escape(piece ?? "") + @"(\s|$)", RegexOptions.CultureInvariant);

		var sb = new StringBuilder();
		int pos = 0;
		bool first = true;
		while (pos < existing.Length)
		{
			int nl = existing.IndexOf('\n', pos);
			int end = nl < 0 ? existing.Length : nl + 1;
			string raw = existing.Substring(pos, end - pos);
			pos = end;
			string line = raw.TrimEnd('\r', '\n');
			if (first)
			{
				first = false;
				if (line != columns) sb.Append(columns).Append(newline);
				else { sb.Append(raw); EnsureNewline(sb, raw, newline); continue; }
			}
			if (line.Length == 0) continue;
			if (line.StartsWith("#", StringComparison.Ordinal))
			{
				if (headerOfPiece.IsMatch(line)) continue;
			}
			else
			{
				List<string> cells = SplitCsv(line);
				string rowPiece = cells.Count > 0 ? cells[0] : "";
				string kind = cells.Count > 1 ? cells[1] : "";
				if (string.Equals(rowPiece, piece ?? "", StringComparison.OrdinalIgnoreCase))
				{
					if (dropKinds != null && dropKinds.Contains(kind)) continue;
					if (Array.IndexOf(ProbeKinds, kind) >= 0) keptProbe = true;
				}
			}
			sb.Append(raw);
			EnsureNewline(sb, raw, newline);
		}
		if (first) sb.Append(columns).Append(newline);
		if (!string.IsNullOrEmpty(header))
			sb.Append("# ").Append(header).Append(keptProbe ? " probe=kept-from-earlier-run" : "").Append(newline);
		return sb.ToString();
	}

	/// <summary>A last line without a newline gets the file's own, so the header never joins it.</summary>
	private static void EnsureNewline(StringBuilder sb, string raw, string newline)
	{
		if (!raw.EndsWith("\n", StringComparison.Ordinal)) sb.Append(newline);
	}

	public static List<string> SplitCsv(string line)
	{
		var cells = new List<string>();
		var cur = new StringBuilder();
		bool quoted = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			if (quoted)
			{
				if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
				else if (c == '"') quoted = false;
				else cur.Append(c);
			}
			else if (c == '"') quoted = true;
			else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
			else cur.Append(c);
		}
		cells.Add(cur.ToString());
		return cells;
	}
}

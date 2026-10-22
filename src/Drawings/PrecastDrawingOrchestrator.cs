using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
	/// <summary>
	/// Per piece: preflight log first, then sheets in booklet order
	/// (elevation, sections, rebar, BBS last). Missing roles are skipped, so 3-page packs work.
	/// </summary>
	public sealed class PrecastDrawingOrchestrator
	{
		private readonly string _logDir;
		private readonly HashSet<string> _preflighted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public PrecastDrawingOrchestrator(string logDir)
		{
			_logDir = logDir;
		}

		public static List<Drawing> Order(IEnumerable<Drawing> drawings)
		{
			var list = new List<Drawing>();
			if (drawings == null) return list;
			list.AddRange(drawings.Where(d => d != null));
			return list
				.Select((d, i) => new { D = d, I = i, Mark = SafeMark(d), Name = SafeName(d) })
				.OrderBy(x => SheetRoleMap.PieceMark(x.Mark), StringComparer.OrdinalIgnoreCase)
				.ThenBy(x => SheetRoleMap.BookletRank(SheetRoleMap.Resolve(x.Mark, x.Name)))
				.ThenBy(x => SheetRoleMap.SuffixNumber(x.Mark) ?? 99)
				.ThenBy(x => x.I)
				.Select(x => x.D)
				.ToList();
		}

		/// <summary>Runs the model checks once per piece, before any of its views are changed.</summary>
		public void Preflight(string piece, TSModel.Part main)
		{
			if (string.IsNullOrWhiteSpace(piece) || main == null) return;
			if (!_preflighted.Add(piece)) return;
			PreflightQaChecker.Run(main, piece, _logDir);
		}

		private static string SafeMark(Drawing d)
		{
			try { return d.Mark ?? ""; } catch { return ""; }
		}

		private static string SafeName(Drawing d)
		{
			try { return d.Name ?? ""; } catch { return ""; }
		}
	}
}

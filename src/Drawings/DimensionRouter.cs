using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace TeklaExtractor.Services
{
	/// <summary>
	/// Running chains stay on the elevation. Drip, chamfer, cover, and thickness go to page 2.
	/// A segment under 70 mm stays on the elevation only when it is part of a chain that sums to the overall.
	/// </summary>
	public static class DimensionRouter
	{
		public const double MicroMaxMm = 70.0;
		public const double TapeToleranceMm = 3.0;

		public enum Destination
		{
			Elevation,
			Section
		}

		public static bool IsDetailKind(string kind)
		{
			if (string.IsNullOrEmpty(kind)) return false;
			string u = kind.ToUpperInvariant();
			return u.IndexOf("DRIP", StringComparison.Ordinal) >= 0
				|| u.IndexOf("CHAMFER", StringComparison.Ordinal) >= 0
				|| u.IndexOf("COVER", StringComparison.Ordinal) >= 0
				|| u.IndexOf("THICK", StringComparison.Ordinal) >= 0;
		}

		public static Destination Route(double lengthMm, bool partOfConservingTape, string kind)
		{
			if (IsDetailKind(kind)) return Destination.Section;
			if (partOfConservingTape) return Destination.Elevation;
			if (lengthMm < MicroMaxMm) return Destination.Section;
			return Destination.Elevation;
		}

		/// <summary>
		/// True when the stations run from the start of the panel to the end, so every segment
		/// (including those under 70 mm) is required for the printed sum.
		/// </summary>
		public static bool IsConservingTape(IList<double> stations, double span)
		{
			if (stations == null || stations.Count < 2 || span <= 1.0) return false;
			double covered = stations[stations.Count - 1] - stations[0];
			return Math.Abs(covered - span) <= TapeToleranceMm;
		}

		public static bool KeepChainOnElevation(IList<double> stations, double span)
		{
			if (stations == null || stations.Count < 2) return false;
			if (IsConservingTape(stations, span)) return true;
			for (int i = 1; i < stations.Count; i++)
			{
				if (Math.Abs(stations[i] - stations[i - 1]) < MicroMaxMm)
					return false;
			}
			return true;
		}

		public static void Remember(string directory, string piece, string label, double lengthMm)
		{
			if (string.IsNullOrWhiteSpace(piece) || string.IsNullOrWhiteSpace(label)) return;
			try
			{
				string dir = string.IsNullOrEmpty(directory)
					? Path.Combine("Export", "CivilDrawings", "new with macros")
					: directory;
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "section_micros_" + Sanitize(piece) + ".txt");
				string line = label.Replace("\t", " ") + "\t"
					+ lengthMm.ToString("0.###", CultureInfo.InvariantCulture);
				File.AppendAllText(path, line + Environment.NewLine);
				Console.WriteLine("[Router] section " + label + " " + lengthMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm");
			}
			catch (Exception ex)
			{
				Console.WriteLine("[Router] remember failed: " + ex.Message);
			}
		}

		public static void Clear(string directory, string piece)
		{
			try
			{
				string path = MicrosPath(directory, piece);
				if (path != null && File.Exists(path)) File.Delete(path);
			}
			catch { }
		}

		/// <summary>
		/// Attach remembered micro-dims to an existing section view.
		/// Creates an A-A view only when the sheet has no section yet.
		/// </summary>
		public static void ApplyPending(string directory, Drawing drawing, ContainerView sheet)
		{
			if (drawing == null || sheet == null) return;
			string piece = "";
			try { piece = SheetRoleMap.PieceMark(drawing.Mark ?? ""); } catch { }
			string path = MicrosPath(directory, piece);
			if (path == null || !File.Exists(path)) return;
			string[] lines;
			try { lines = File.ReadAllLines(path); }
			catch { return; }
			if (lines == null || lines.Length == 0) return;

			View host = FindSection(sheet);
			if (host == null)
			{
				host = CreateSection(sheet);
				if (host != null)
					Console.WriteLine("[Router] created A-A for micro-dims");
			}
			if (host == null)
			{
				Console.WriteLine("[Router] no section view for " + piece);
				return;
			}

			double y = 8.0;
			int n = 0;
			foreach (string raw in lines)
			{
				if (string.IsNullOrWhiteSpace(raw)) continue;
				string[] parts = raw.Split('\t');
				string label = parts[0].Trim();
				string len = parts.Length > 1 ? parts[1].Trim() : "";
				string text = string.IsNullOrEmpty(len) ? label : label + " " + len;
				try
				{
					var t = new Text(host, new Point(12.0, y, 0.0), text);
					try
					{
						t.Attributes.Frame.Type = FrameTypes.None;
						t.Attributes.Font.Height = 2.5;
					}
					catch { }
					t.Insert();
					n++;
					y += 6.0;
				}
				catch (Exception ex)
				{
					Console.WriteLine("[Router] micro text: " + ex.Message);
				}
			}
			Console.WriteLine("[Router] attached " + n.ToString(CultureInfo.InvariantCulture) + " micro-dims on section");
		}

		private static string MicrosPath(string directory, string piece)
		{
			if (string.IsNullOrWhiteSpace(piece)) return null;
			string dir = string.IsNullOrEmpty(directory)
				? Path.Combine("Export", "CivilDrawings", "new with macros")
				: directory;
			return Path.Combine(dir, "section_micros_" + Sanitize(piece) + ".txt");
		}

		private static string Sanitize(string piece)
		{
			foreach (char c in Path.GetInvalidFileNameChars())
				piece = piece.Replace(c, '_');
			return piece.Trim();
		}

		private static View FindSection(ContainerView sheet)
		{
			try
			{
				DrawingObjectEnumerator en = sheet.GetAllViews();
				while (en != null && en.MoveNext())
				{
					if (en.Current is View v && v.ViewType == View.ViewTypes.SectionView)
						return v;
				}
			}
			catch { }
			return null;
		}

		private static View CreateSection(ContainerView sheet)
		{
			try
			{
				View front = null;
				DrawingObjectEnumerator en = sheet.GetAllViews();
				while (en != null && en.MoveNext())
				{
					if (en.Current is View v && v.ViewType == View.ViewTypes.FrontView)
					{
						front = v;
						break;
					}
				}
				if (front == null) return null;
				CoordinateSystem cs = front.ViewCoordinateSystem;
				var box = new AABB(new Point(-80, -80, -500), new Point(80, 80, 500));
				var view = new View(sheet, cs, cs, box);
				view.Name = "A-A";
				view.Attributes.Scale = 50;
				Console.WriteLine("[Sections] router inserted A-A at 1:50 (no section view was on the sheet)");
				view.Origin = new Point(40.0, 40.0, 0.0);
				if (!view.Insert()) return null;
				return view;
			}
			catch (Exception ex)
			{
				Console.WriteLine("[Router] A-A create: " + ex.Message);
				return null;
			}
		}
	}
}

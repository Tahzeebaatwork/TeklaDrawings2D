using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace TeklaExtractor.Services;

public partial class PrecastDimensionPostProcessor
{
	public static bool StageCensus { get; set; }
	public static bool StageSnapshots { get; set; }
	public static bool CleanMarkRequested { get; set; }

	public static class PlacingStageAudit
	{
		private static StageSnapshotState _lastState = null;
		private static readonly object _fileLock = new object();

		public static void Reset()
		{
			_lastState = null;
		}

		public static void RecordStage(string stageName, Drawing drawing, ContainerView sheet, View front, View bottom, View section, DrawingHandler handler)
		{
			if (!StageCensus && !StageSnapshots)
				return;

			if (drawing == null)
				return;

			try
			{
				if (StageCensus)
				{
					ExecuteCensus(stageName, drawing, sheet, front, bottom, section);
				}

				if (StageSnapshots)
				{
					ExecuteSnapshot(stageName, drawing);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("[StageAudit] " + stageName + " failed: " + ex.Message);
			}
		}

		private static void ExecuteCensus(string stageName, Drawing drawing, ContainerView sheet, View front, View bottom, View section)
		{
			var currentState = new StageSnapshotState { StageName = stageName };

			currentState.Views["TOP IN FORM"] = ExtractViewItems(front, "TOP IN FORM");
			currentState.Views["END 2"] = ExtractViewItems(bottom, "END 2");
			currentState.Views["VIEW A"] = ExtractViewItems(section, "VIEW A");
			currentState.Views["sheet level"] = ExtractSheetLevelItems(sheet);

			foreach (var kv in currentState.Views)
			{
				int totalObjs = kv.Value.Count;
				var typeCounts = kv.Value.GroupBy(x => x.TypeName)
					.Select(g => g.Key + ":" + g.Count().ToString(CultureInfo.InvariantCulture));
				Console.WriteLine("[StageCensus] " + stageName + " (" + kv.Key + "): "
					+ totalObjs.ToString(CultureInfo.InvariantCulture) + " objects (" + string.Join(", ", typeCounts) + ")");
			}

			StageDiffSummary diffSummary = ComputeDiff(_lastState, currentState);
			Console.WriteLine("[StageDiff] " + stageName + ": " + diffSummary.FormattedDiffLine);

			WriteCensusCsv(stageName, currentState, diffSummary);

			_lastState = currentState;
		}

		private static void ExecuteSnapshot(string stageName, Drawing drawing)
		{
			try
			{
				string exportDir = ResolveExportDir();
				string stagesDir = Path.Combine(exportDir, "stages");
				Directory.CreateDirectory(stagesDir);
				string pdfPath = Path.Combine(stagesDir, stageName + ".pdf");

				try { drawing.CommitChanges(); } catch { }
				var printer = new DrawingGenerator();
				bool ok = printer.ExportToPdf(drawing, pdfPath);
				if (ok && File.Exists(pdfPath))
				{
					Console.WriteLine("[StageSnapshot] PDF → " + pdfPath);
				}
				else
				{
					Console.WriteLine("[StageSnapshot] PDF failed for " + stageName + " → " + pdfPath);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("[StageSnapshot] " + stageName + ": " + ex.Message);
			}
		}

		private static List<StageAuditItem> ExtractViewItems(View view, string viewName)
		{
			var list = new List<StageAuditItem>();
			if (view == null) return list;

			foreach (DrawingObject o in EnumerateViewObjects(view))
			{
				if (o == null) continue;
				StageAuditItem item = BuildAuditItem(o, viewName);
				if (item != null)
					list.Add(item);
			}
			return list;
		}

		private static List<StageAuditItem> ExtractSheetLevelItems(ContainerView sheet)
		{
			var list = new List<StageAuditItem>();
			if (sheet == null) return list;

			DrawingObjectEnumerator en = null;
			try { en = sheet.GetObjects(); } catch { }
			while (en != null && en.MoveNext())
			{
				if (en.Current is DrawingObject o && !(o is View))
				{
					StageAuditItem item = BuildAuditItem(o, "sheet level");
					if (item != null)
						list.Add(item);
				}
			}
			return list;
		}

		private static int GetDrawingObjectId(DrawingObject obj)
		{
			if (obj == null) return 0;
			try
			{
				var prop = obj.GetType().GetProperty("Identifier");
				if (prop != null)
				{
					object ident = prop.GetValue(obj, null);
					if (ident != null)
					{
						object id = ident.GetType().GetProperty("ID")?.GetValue(ident, null);
						if (id is int n && n > 0) return n;
					}
				}
			}
			catch { }
			try
			{
				var pModel = obj.GetType().GetProperty("ModelIdentifier");
				if (pModel != null)
				{
					object mId = pModel.GetValue(obj, null);
					if (mId != null)
					{
						object idVal = mId.GetType().GetProperty("ID")?.GetValue(mId, null);
						if (idVal is int mid && mid > 0) return mid;
					}
				}
			}
			catch { }
			int stored = 0;
			try { obj.GetUserProperty("TX_ID", ref stored); } catch { }
			return stored;
		}

		private static StageAuditItem BuildAuditItem(DrawingObject o, string viewName)
		{
			if (o == null) return null;
			string typeName = o.GetType().Name;
			var item = new StageAuditItem
			{
				ViewName = viewName,
				TypeName = typeName,
				Id = GetDrawingObjectId(o)
			};

			if (o is StraightDimensionSet sds)
			{
				var pts = new List<Point>();
				try
				{
					DrawingObjectEnumerator kids = sds.GetObjects();
					while (kids != null && kids.MoveNext())
					{
						if (kids.Current is StraightDimension sd)
						{
							if (sd.StartPoint != null) pts.Add(sd.StartPoint);
							if (sd.EndPoint != null) pts.Add(sd.EndPoint);
						}
					}
				}
				catch { }

				var uniq = new List<Point>();
				foreach (Point p in pts)
				{
					if (!uniq.Any(u => Math.Abs(u.X - p.X) < 1.0 && Math.Abs(u.Y - p.Y) < 1.0 && Math.Abs(u.Z - p.Z) < 1.0))
						uniq.Add(p);
				}
				item.PointCount = uniq.Count;
				double maxDist = 0;
				for (int i = 0; i < uniq.Count; i++)
				{
					for (int j = i + 1; j < uniq.Count; j++)
					{
						double d = Math.Sqrt(Math.Pow(uniq[i].X - uniq[j].X, 2) + Math.Pow(uniq[i].Y - uniq[j].Y, 2) + Math.Pow(uniq[i].Z - uniq[j].Z, 2));
						if (d > maxDist) maxDist = d;
					}
				}
				item.Extent = Math.Round(maxDist, 1);
				if (uniq.Count > 0)
				{
					item.X = uniq[0].X;
					item.Y = uniq[0].Y;
				}
			}
			else if (o is Text t)
			{
				item.Text = (t.TextString ?? "").Trim();
				if (t.InsertionPoint != null)
				{
					item.X = t.InsertionPoint.X;
					item.Y = t.InsertionPoint.Y;
				}
			}
			else if (o is MarkBase mb)
			{
				item.Text = (FlattenMarkText(mb) ?? "").Trim();
				try
				{
					Point ip = mb.InsertionPoint;
					if (ip != null)
					{
						item.X = ip.X;
						item.Y = ip.Y;
					}
				}
				catch { }
			}
			else
			{
				item.Text = ReadPlacingObjectText(o);
				if (TryObjectBox(o, out double minX, out double minY, out double maxX, out double maxY))
				{
					item.X = (minX + maxX) * 0.5;
					item.Y = (minY + maxY) * 0.5;
				}
			}

			return item;
		}

		private static StageDiffSummary ComputeDiff(StageSnapshotState oldState, StageSnapshotState newState)
		{
			var summary = new StageDiffSummary();
			if (oldState == null)
			{
				summary.FormattedDiffLine = "+0 created, -0 deleted, 0 moved (baseline after macro)";
				return summary;
			}

			int totalCreated = 0;
			int totalDeleted = 0;
			int totalMoved = 0;
			var viewDetails = new List<string>();

			string[] viewNames = { "TOP IN FORM", "END 2", "VIEW A", "sheet level" };
			foreach (string viewName in viewNames)
			{
				oldState.Views.TryGetValue(viewName, out var oldItems);
				newState.Views.TryGetValue(viewName, out var newItems);
				oldItems = oldItems ?? new List<StageAuditItem>();
				newItems = newItems ?? new List<StageAuditItem>();

				var viewResult = DiffItems(oldItems, newItems);
				summary.PerView[viewName] = viewResult;
				totalCreated += viewResult.Created;
				totalDeleted += viewResult.Deleted;
				totalMoved += viewResult.Moved;

				if (viewResult.Created > 0 || viewResult.Deleted > 0 || viewResult.Moved > 0)
				{
					var typeParts = viewResult.ByType
						.Where(b => b.Value.Created > 0 || b.Value.Deleted > 0 || b.Value.Moved > 0)
						.Select(b => b.Key + ": +" + b.Value.Created.ToString(CultureInfo.InvariantCulture)
							+ " created, -" + b.Value.Deleted.ToString(CultureInfo.InvariantCulture)
							+ " deleted, " + b.Value.Moved.ToString(CultureInfo.InvariantCulture) + " moved");
					viewDetails.Add(viewName + " (+" + viewResult.Created.ToString(CultureInfo.InvariantCulture)
						+ ", -" + viewResult.Deleted.ToString(CultureInfo.InvariantCulture)
						+ ", " + viewResult.Moved.ToString(CultureInfo.InvariantCulture) + " moved: " + string.Join("; ", typeParts) + ")");
				}
			}

			summary.TotalCreated = totalCreated;
			summary.TotalDeleted = totalDeleted;
			summary.TotalMoved = totalMoved;

			var sb = new StringBuilder();
			sb.Append("+" + totalCreated.ToString(CultureInfo.InvariantCulture)
				+ " created, -" + totalDeleted.ToString(CultureInfo.InvariantCulture)
				+ " deleted, " + totalMoved.ToString(CultureInfo.InvariantCulture)
				+ " moved, by type and view");
			if (viewDetails.Count > 0)
				sb.Append(": ").Append(string.Join("; ", viewDetails));

			summary.FormattedDiffLine = sb.ToString();
			return summary;
		}

		private static ViewDiffResult DiffItems(List<StageAuditItem> oldItems, List<StageAuditItem> newItems)
		{
			var result = new ViewDiffResult();
			var allTypes = oldItems.Select(x => x.TypeName).Concat(newItems.Select(x => x.TypeName)).Distinct();

			foreach (string type in allTypes)
			{
				var pList = oldItems.Where(x => x.TypeName == type).ToList();
				var cList = newItems.Where(x => x.TypeName == type).ToList();

				bool[] matchedP = new bool[pList.Count];
				bool[] matchedC = new bool[cList.Count];
				int typeMoved = 0;

				// Pass 1: Match by positive ID
				for (int i = 0; i < pList.Count; i++)
				{
					if (pList[i].Id <= 0) continue;
					for (int j = 0; j < cList.Count; j++)
					{
						if (matchedC[j] || cList[j].Id <= 0) continue;
						if (pList[i].Id == cList[j].Id)
						{
							matchedP[i] = true;
							matchedC[j] = true;
							double d = Math.Sqrt(Math.Pow(cList[j].X - pList[i].X, 2) + Math.Pow(cList[j].Y - pList[i].Y, 2));
							if (d > 0.5) typeMoved++;
							break;
						}
					}
				}

				// Pass 2: Match by exact Text (for Text / Mark)
				if (type == "Text" || type.IndexOf("Mark", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					for (int i = 0; i < pList.Count; i++)
					{
						if (matchedP[i] || string.IsNullOrEmpty(pList[i].Text)) continue;
						for (int j = 0; j < cList.Count; j++)
						{
							if (matchedC[j] || string.IsNullOrEmpty(cList[j].Text)) continue;
							if (string.Equals(pList[i].Text, cList[j].Text, StringComparison.Ordinal))
							{
								matchedP[i] = true;
								matchedC[j] = true;
								double d = Math.Sqrt(Math.Pow(cList[j].X - pList[i].X, 2) + Math.Pow(cList[j].Y - pList[i].Y, 2));
								if (d > 0.5) typeMoved++;
								break;
							}
						}
					}
				}

				// Pass 3: Match by PointCount and Extent for StraightDimensionSet
				if (type == "StraightDimensionSet")
				{
					for (int i = 0; i < pList.Count; i++)
					{
						if (matchedP[i]) continue;
						for (int j = 0; j < cList.Count; j++)
						{
							if (matchedC[j]) continue;
							if (pList[i].PointCount == cList[j].PointCount && Math.Abs(pList[i].Extent - cList[j].Extent) < 2.0)
							{
								matchedP[i] = true;
								matchedC[j] = true;
								double d = Math.Sqrt(Math.Pow(cList[j].X - pList[i].X, 2) + Math.Pow(cList[j].Y - pList[i].Y, 2));
								if (d > 0.5) typeMoved++;
								break;
							}
						}
					}
				}

				// Pass 4: Match remaining items of same type by closest distance
				for (int i = 0; i < pList.Count; i++)
				{
					if (matchedP[i]) continue;
					int bestJ = -1;
					double bestDist = double.MaxValue;
					for (int j = 0; j < cList.Count; j++)
					{
						if (matchedC[j]) continue;
						double d = Math.Sqrt(Math.Pow(cList[j].X - pList[i].X, 2) + Math.Pow(cList[j].Y - pList[i].Y, 2));
						if (d < bestDist)
						{
							bestDist = d;
							bestJ = j;
						}
					}
					if (bestJ >= 0 && bestDist < 100.0)
					{
						matchedP[i] = true;
						matchedC[bestJ] = true;
						if (bestDist > 0.5) typeMoved++;
					}
				}

				int typeDeleted = matchedP.Count(m => !m);
				int typeCreated = matchedC.Count(m => !m);

				result.ByType[type] = (typeCreated, typeDeleted, typeMoved);
				result.Created += typeCreated;
				result.Deleted += typeDeleted;
				result.Moved += typeMoved;
			}

			return result;
		}

		private static void WriteCensusCsv(string stageName, StageSnapshotState state, StageDiffSummary diffSummary)
		{
			string exportDir = ResolveExportDir();
			string csvPath = Path.Combine(exportDir, "stage_census.csv");

			lock (_fileLock)
			{
				bool isFirst = !File.Exists(csvPath) || string.Equals(stageName, "00_macro", StringComparison.OrdinalIgnoreCase);
				var sb = new StringBuilder();

				if (isFirst)
				{
					sb.AppendLine("Stage,View,Type,Count,Points,Extent,Text,Diff");
				}

				string[] viewNames = { "TOP IN FORM", "END 2", "VIEW A", "sheet level" };
				foreach (string viewName in viewNames)
				{
					state.Views.TryGetValue(viewName, out var items);
					items = items ?? new List<StageAuditItem>();
					var typeGroups = items.GroupBy(x => x.TypeName).OrderBy(g => g.Key);

					foreach (var grp in typeGroups)
					{
						string type = grp.Key;
						int count = grp.Count();
						string points = "";
						string extent = "";
						string text = "";

						if (type == "StraightDimensionSet")
						{
							points = string.Join("; ", grp.Select(x => x.PointCount.ToString(CultureInfo.InvariantCulture)));
							extent = string.Join("; ", grp.Select(x => x.Extent.ToString("0.#", CultureInfo.InvariantCulture)));
						}
						else if (type == "Text" || type.IndexOf("Mark", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							text = string.Join(" | ", grp.Select(x => x.Text).Where(s => !string.IsNullOrEmpty(s)).Take(50));
						}

						string diffStr = "-";
						if (diffSummary.PerView.TryGetValue(viewName, out var vd) && vd.ByType.TryGetValue(type, out var td))
						{
							diffStr = "+" + td.Created.ToString(CultureInfo.InvariantCulture) + " created, -"
								+ td.Deleted.ToString(CultureInfo.InvariantCulture) + " deleted, "
								+ td.Moved.ToString(CultureInfo.InvariantCulture) + " moved";
						}

						sb.AppendLine(EscapeCsv(stageName) + ","
							+ EscapeCsv(viewName) + ","
							+ EscapeCsv(type) + ","
							+ count.ToString(CultureInfo.InvariantCulture) + ","
							+ EscapeCsv(points) + ","
							+ EscapeCsv(extent) + ","
							+ EscapeCsv(text) + ","
							+ EscapeCsv(diffStr));
					}

					string viewDiffLine = "-";
					if (diffSummary.PerView.TryGetValue(viewName, out var vdiff))
					{
						viewDiffLine = "+" + vdiff.Created.ToString(CultureInfo.InvariantCulture) + " created, -"
							+ vdiff.Deleted.ToString(CultureInfo.InvariantCulture) + " deleted, "
							+ vdiff.Moved.ToString(CultureInfo.InvariantCulture) + " moved";
					}
					sb.AppendLine(EscapeCsv(stageName) + "," + EscapeCsv(viewName) + ",DIFF,0,,,," + EscapeCsv(viewDiffLine));
				}

				sb.AppendLine(EscapeCsv(stageName) + ",ALL,DIFF,0,,,," + EscapeCsv(diffSummary.FormattedDiffLine));

				if (isFirst)
					File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
				else
					File.AppendAllText(csvPath, sb.ToString(), Encoding.UTF8);

				Console.WriteLine("[StageCensus] CSV updated → " + csvPath);
			}
		}

		private static string EscapeCsv(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			s = s.Replace("\r", " ").Replace("\n", " ");
			if (s.IndexOfAny(new[] { ',', '"', ';' }) >= 0)
				return "\"" + s.Replace("\"", "\"\"") + "\"";
			return s;
		}

		public static string ResolveExportDir()
		{
			string cwd = Directory.GetCurrentDirectory();
			string probe1 = Path.Combine(cwd, "Export");
			if (Directory.Exists(probe1)) return probe1;

			string dir = AppDomain.CurrentDomain.BaseDirectory;
			for (int i = 0; i < 5; i++)
			{
				if (string.IsNullOrEmpty(dir)) break;
				string probe2 = Path.Combine(dir, "Export");
				if (Directory.Exists(probe2)) return probe2;
				dir = Path.GetDirectoryName(dir);
			}
			return probe1;
		}

		private sealed class StageSnapshotState
		{
			public string StageName = "";
			public Dictionary<string, List<StageAuditItem>> Views = new Dictionary<string, List<StageAuditItem>>(StringComparer.OrdinalIgnoreCase);
		}

		private sealed class StageAuditItem
		{
			public string ViewName = "";
			public string TypeName = "";
			public int Id;
			public string Text = "";
			public int PointCount;
			public double Extent;
			public double X;
			public double Y;
		}

		private sealed class ViewDiffResult
		{
			public int Created;
			public int Deleted;
			public int Moved;
			public Dictionary<string, (int Created, int Deleted, int Moved)> ByType = new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase);
		}

		private sealed class StageDiffSummary
		{
			public Dictionary<string, ViewDiffResult> PerView = new Dictionary<string, ViewDiffResult>(StringComparer.OrdinalIgnoreCase);
			public int TotalCreated;
			public int TotalDeleted;
			public int TotalMoved;
			public string FormattedDiffLine = "";
		}
	}
}

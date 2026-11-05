using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Drawing.UI;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Read-only Drawing Object Auditor.
    /// Inspects all drawing objects on the specified sheet/view without modifying or saving anything.
    /// Dumps a detailed CSV report for forensic analysis of associativity, object types, and origin.
    /// </summary>
    public static class DrawingObjectAuditor
    {
        public sealed class AuditRow
        {
            public string SheetMark { get; set; } = "";
            public string ViewName { get; set; } = "";
            public int ObjectId { get; set; }
            public string ObjectType { get; set; } = "";
            public string Color { get; set; } = "";
            public string TextContent { get; set; } = "";
            public string Points { get; set; } = "";
            public string TagDetails { get; set; } = "";
            public int RelatedCount { get; set; }
            public string RelatedDetails { get; set; } = "";
            public string Provenance { get; set; } = "";
            public string ExtraInfo { get; set; } = "";
        }

        public static int AuditSheet(DrawingHandler handler, Model model, string targetMark, string outputCsvPath)
        {
            if (handler == null)
            {
                Console.WriteLine("[Audit] DrawingHandler is null.");
                return 1;
            }

            Drawing activeDrawing = handler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                Console.WriteLine("[Audit] No active drawing open in Tekla. Please open drawing: " + targetMark);
                return 1;
            }

            string currentMark = "";
            try { currentMark = activeDrawing.Mark ?? activeDrawing.Name ?? ""; } catch { }

            Console.WriteLine("[Audit] Initial active drawing mark: '" + currentMark + "'");
            if (!string.IsNullOrEmpty(targetMark) && !currentMark.Contains(targetMark))
            {
                Console.WriteLine("[Audit] Active drawing does not match target '" + targetMark + "'. Searching model drawings...");
                DrawingEnumerator dwgs = handler.GetDrawings();
                Drawing found = null;
                while (dwgs != null && dwgs.MoveNext())
                {
                    Drawing d = dwgs.Current;
                    if (d != null && (d.Mark ?? "").Contains(targetMark))
                    {
                        if ((d.Mark ?? "").Contains("- 1") || found == null)
                        {
                            found = d;
                            if ((d.Mark ?? "").Contains("- 1")) break;
                        }
                    }
                }
                if (found != null)
                {
                    Console.WriteLine("[Audit] Switching active drawing to: '" + (found.Mark ?? "") + "'");
                    try { handler.SetActiveDrawing(found, true); } catch { }
                    activeDrawing = handler.GetActiveDrawing() ?? found;
                    currentMark = activeDrawing.Mark ?? found.Mark ?? "";
                }
                else
                {
                    Console.WriteLine("[Audit] WARNING: Target drawing '" + targetMark + "' not found. Auditing active drawing anyway.");
                }
            }
            Console.WriteLine("[Audit] Auditing drawing: '" + currentMark + "'");

            ContainerView sheet = activeDrawing.GetSheet();
            if (sheet == null)
            {
                Console.WriteLine("[Audit] Sheet is null.");
                return 1;
            }

            List<AuditRow> rows = new List<AuditRow>();

            // 1. Audit Sheet-level objects
            AuditViewObjects(sheet, "SHEET", currentMark, rows, model);

            // 2. Audit all Views on Sheet
            DrawingObjectEnumerator viewEnum = sheet.GetViews();
            while (viewEnum != null && viewEnum.MoveNext())
            {
                if (viewEnum.Current is Tekla.Structures.Drawing.View v)
                {
                    string vName = "";
                    try { vName = v.Name ?? "UNNAMED_VIEW"; } catch { vName = "VIEW"; }
                    AuditViewObjects(v, vName, currentMark, rows, model);
                }
            }

            // Export to CSV
            try
            {
                string dir = Path.GetDirectoryName(outputCsvPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("SheetMark,ViewName,ObjectId,ObjectType,Color,TextContent,Points,TagDetails,RelatedCount,RelatedDetails,Provenance,ExtraInfo");

                foreach (var r in rows)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "\"{0}\",\"{1}\",{2},\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\",{8},\"{9}\",\"{10}\",\"{11}\"",
                        EscapeCsv(r.SheetMark),
                        EscapeCsv(r.ViewName),
                        r.ObjectId,
                        EscapeCsv(r.ObjectType),
                        EscapeCsv(r.Color),
                        EscapeCsv(r.TextContent),
                        EscapeCsv(r.Points),
                        EscapeCsv(r.TagDetails),
                        r.RelatedCount,
                        EscapeCsv(r.RelatedDetails),
                        EscapeCsv(r.Provenance),
                        EscapeCsv(r.ExtraInfo)));
                }

                File.WriteAllText(outputCsvPath, sb.ToString(), Encoding.UTF8);
                Console.WriteLine("[Audit] SUCCESS: Exported " + rows.Count + " objects to: " + outputCsvPath);

                // Print Summary
                PrintSummary(rows);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Audit] Error writing CSV: " + ex.Message);
                return 1;
            }
        }

        private static void AuditViewObjects(ViewBase view, string viewName, string sheetMark, List<AuditRow> rows, Model model)
        {
            if (view == null) return;

            DrawingObjectEnumerator en = view.GetAllObjects();
            while (en != null && en.MoveNext())
            {
                DrawingObject obj = en.Current;
                if (obj == null) continue;

                AuditRow row = new AuditRow
                {
                    SheetMark = sheetMark,
                    ViewName = viewName,
                    ObjectId = GetObjectId(obj),
                    ObjectType = obj.GetType().Name
                };

                // Check related objects
                List<string> relatedList = new List<string>();
                try
                {
                    DrawingObjectEnumerator relEnum = obj.GetRelatedObjects();
                    while (relEnum != null && relEnum.MoveNext())
                    {
                        DrawingObject rel = relEnum.Current;
                        if (rel == null) continue;
                        string relType = rel.GetType().Name;
                        int modelId = 0;
                        string partPos = "";

                        // If it's a Drawing ModelObject (Part, Rebar, etc.)
                        if (rel is Tekla.Structures.Drawing.ModelObject dmo)
                        {
                            try { modelId = dmo.ModelIdentifier.ID; } catch { }
                            if (model != null && modelId > 0)
                            {
                                try
                                {
                                    Tekla.Structures.Model.ModelObject mo = model.SelectModelObject(dmo.ModelIdentifier);
                                    if (mo is Tekla.Structures.Model.Part mp)
                                    {
                                        mp.GetReportProperty("PART_POS", ref partPos);
                                    }
                                }
                                catch { }
                            }
                        }

                        string item = relType;
                        if (modelId > 0) item += "(ModelId=" + modelId + (string.IsNullOrEmpty(partPos) ? "" : " Pos=" + partPos) + ")";
                        relatedList.Add(item);
                    }
                }
                catch { }

                row.RelatedCount = relatedList.Count;
                row.RelatedDetails = string.Join("; ", relatedList);

                // Extract details by specific type
                if (obj is StraightDimensionSet sds)
                {
                    row.Color = GetDimensionColor(sds);
                    row.TextContent = GetDimensionSegments(sds);
                    row.Points = GetDimensionPoints(sds);
                    row.TagDetails = GetDimensionTagDetails(sds);
                    try { row.ExtraInfo = "DimType=" + sds.Attributes.DimensionType + " Dist=" + sds.Distance.ToString("F1", CultureInfo.InvariantCulture); } catch { }
                    // Provenance: Our generator creates unassociated points along top/bottom line
                    if (row.RelatedCount == 0)
                        row.Provenance = "LikelyOurGenerator (Unassociated PointList)";
                    else
                        row.Provenance = "LikelyMacroNative (Associative to Model Parts)";
                }
                else if (obj is StraightDimension sd)
                {
                    row.Color = "SubDim";
                    try { row.TextContent = sd.Distance.ToString("F1", CultureInfo.InvariantCulture); } catch { }
                }
                else if (obj is Tekla.Structures.Drawing.Text txt)
                {
                    row.TextContent = txt.TextString ?? "";
                    try { row.Color = txt.Attributes.Font.Color.ToString(); } catch { }
                    try { row.Points = string.Format(CultureInfo.InvariantCulture, "({0:F1}, {1:F1})", txt.InsertionPoint.X, txt.InsertionPoint.Y); } catch { }
                    try { row.ExtraInfo = "Frame=" + txt.Attributes.Frame.Type; } catch { }
                    row.Provenance = "TextObject";
                }
                else if (obj is MarkBase mb)
                {
                    row.TextContent = GetMarkText(mb);
                    try { row.Points = string.Format(CultureInfo.InvariantCulture, "({0:F1}, {1:F1})", mb.InsertionPoint.X, mb.InsertionPoint.Y); } catch { }
                    row.Provenance = row.RelatedCount > 0 ? "MacroNativeMark" : "StandaloneMark";
                }
                else if (obj is Symbol sym)
                {
                    try { row.TextContent = sym.SymbolInfo.SymbolFile + "#" + sym.SymbolInfo.SymbolIndex; } catch { }
                    try { row.Points = string.Format(CultureInfo.InvariantCulture, "({0:F1}, {1:F1})", sym.InsertionPoint.X, sym.InsertionPoint.Y); } catch { }
                    try { row.Color = sym.Attributes.Color.ToString(); } catch { }
                    row.Provenance = "SymbolObject";
                }
                else if (obj is Cloud cld)
                {
                    row.ObjectType = "Cloud";
                    try { row.Color = cld.Attributes.Line.Color.ToString(); } catch { }
                    row.Provenance = "CloudObject";
                }
                else
                {
                    row.ExtraInfo = obj.GetType().FullName;
                }

                rows.Add(row);
            }
        }

        private static int GetObjectId(DrawingObject obj)
        {
            if (obj == null) return 0;
            try
            {
                object ident = obj.GetType().GetProperty("Identifier")?.GetValue(obj, null);
                if (ident != null)
                {
                    object id = ident.GetType().GetProperty("ID")?.GetValue(ident, null);
                    if (id is int n) return n;
                }
            }
            catch { }
            return 0;
        }

        private static string GetDimensionColor(StraightDimensionSet set)
        {
            try { return set.Attributes.Color.ToString(); } catch { return "Unknown"; }
        }

        private static string GetDimensionSegments(StraightDimensionSet set)
        {
            try
            {
                List<string> segs = new List<string>();
                DrawingObjectEnumerator subDims = set.GetObjects();
                while (subDims != null && subDims.MoveNext())
                {
                    if (subDims.Current is StraightDimension sd)
                    {
                        segs.Add(sd.Distance.ToString("0.#", CultureInfo.InvariantCulture));
                    }
                }
                return string.Join(" | ", segs);
            }
            catch { return ""; }
        }

        private static string GetDimensionPoints(StraightDimensionSet set)
        {
            if (set == null) return "";
            try
            {
                var pts = new List<string>();
                DrawingObjectEnumerator subDims = set.GetObjects();
                while (subDims != null && subDims.MoveNext())
                {
                    if (subDims.Current is StraightDimension sd)
                    {
                        if (sd.StartPoint != null)
                            pts.Add(string.Format(CultureInfo.InvariantCulture, "({0:F1},{1:F1})", sd.StartPoint.X, sd.StartPoint.Y));
                        if (sd.EndPoint != null)
                            pts.Add(string.Format(CultureInfo.InvariantCulture, "({0:F1},{1:F1})", sd.EndPoint.X, sd.EndPoint.Y));
                    }
                }
                // Distinct ordered points
                var distinctPts = pts.Distinct().ToList();
                return string.Join(" | ", distinctPts);
            }
            catch (Exception ex)
            {
                return "Err: " + ex.Message;
            }
        }

        private static string GetDimensionTagDetails(StraightDimensionSet set)
        {
            if (set == null || set.Attributes == null) return "";
            try
            {
                var tagParts = new List<string>();
                InspectTagContainer("RU", set.Attributes.RightUpperTag, tagParts);
                InspectTagContainer("RM", set.Attributes.RightMiddleTag, tagParts);
                InspectTagContainer("RL", set.Attributes.RightLowerTag, tagParts);
                InspectTagContainer("LU", set.Attributes.LeftUpperTag, tagParts);
                InspectTagContainer("LM", set.Attributes.LeftMiddleTag, tagParts);
                InspectTagContainer("LL", set.Attributes.LeftLowerTag, tagParts);
                return string.Join(" ; ", tagParts);
            }
            catch (Exception ex)
            {
                return "TagErr: " + ex.Message;
            }
        }

        private static void InspectTagContainer(string slotName, ContainerElement container, List<string> output)
        {
            if (container == null || container.Count == 0) return;
            var elemDescs = new List<string>();
            string frameType = "";
            try { frameType = container.Frame != null ? container.Frame.Type.ToString() : "None"; } catch { }

            foreach (object el in container)
            {
                if (el == null) continue;
                string elType = el.GetType().Name;
                if (el is TextElement te)
                {
                    elemDescs.Add(string.Format(CultureInfo.InvariantCulture, "Text:'{0}'", te.Value));
                }
                else if (el is PropertyElement pe)
                {
                    elemDescs.Add(string.Format(CultureInfo.InvariantCulture, "Prop:{0}='{1}'", pe.Name, pe.Value));
                }
                else
                {
                    elemDescs.Add(elType);
                }
            }
            output.Add(string.Format(CultureInfo.InvariantCulture, "{0}[Frame={1}]: {2}", slotName, frameType, string.Join("+", elemDescs)));
        }

        private static string GetMarkText(MarkBase mark)
        {
            try
            {
                var prop = mark.GetType().GetProperty("Text");
                if (prop != null)
                {
                    object val = prop.GetValue(mark, null);
                    if (val != null) return val.ToString();
                }
            }
            catch { }
            return "";
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\"", "\"\"").Replace("\r", "").Replace("\n", " ");
        }

        private static void PrintSummary(List<AuditRow> rows)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("             DRAWING OBJECT AUDIT SUMMARY");
            Console.WriteLine("================================================================================");
            var byType = rows.GroupBy(r => r.ObjectType).Select(g => new { Type = g.Key, Count = g.Count() });
            foreach (var t in byType)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-30} : {1,5}", t.Type, t.Count));
            }
            Console.WriteLine("--------------------------------------------------------------------------------");

            var dims = rows.Where(r => r.ObjectType == "StraightDimensionSet").ToList();
            Console.WriteLine("StraightDimensionSets total: " + dims.Count);
            foreach (var d in dims)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  [ID={0,6}] Rel={1,2} | Prov={2,-35} | Segs={3}",
                    d.ObjectId, d.RelatedCount, d.Provenance, d.TextContent));
            }
            Console.WriteLine("================================================================================");
        }
    }
}

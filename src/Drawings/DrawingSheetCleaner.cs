// DrawingSheetCleaner.cs
// Mark-scoped drawing delete via Tekla.Structures.Drawing.Drawing.Delete().
// Safety: a non-empty mark filter is mandatory. GA sheets are never deleted.
// Returns how many matched sheets are still present after close + delete + one retry.

using System;
using System.Collections.Generic;
using Tekla.Structures.Drawing;

namespace TeklaExtractor.Services
{
    public static class DrawingSheetCleaner
    {
        /// <summary>
        /// Deletes every non-GA sheet whose drawing mark matches <paramref name="markFilter"/>.
        /// Returns the number of matched sheets still present afterwards. Zero means the mark is clear.
        /// Every attempt is logged, including a successful delete.
        /// </summary>
        public static int DeleteByMark(string markFilter, bool dryRun = false, int onlySheet = 0)
        {
            if (string.IsNullOrWhiteSpace(markFilter))
            {
                Console.WriteLine("[Delete] refused: a --mark value is required (never bulk-delete drawings).");
                return 0;
            }

            var handler = new DrawingHandler();
            if (!handler.GetConnectionStatus())
            {
                Console.WriteLine("[Delete] DrawingHandler not connected.");
                return 0;
            }

            var targets = new List<Drawing>();
            int scanned = 0;
            try
            {
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var drawing = en.Current as Drawing;
                    if (drawing == null) continue;
                    scanned++;
                    bool isGa = false;
                    try { isGa = drawing is GADrawing; } catch { }
                    if (isGa) continue;
                    if (!CivilDrawingSupport.MarksMatch(Mark(drawing), markFilter)) continue;
                    if (onlySheet > 0 && !SheetRoleMap.MatchesOnlySheet(Mark(drawing), Name(drawing), onlySheet)) continue;
                    targets.Add(drawing);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Delete] GetDrawings: " + ex.Message);
            }

            Console.WriteLine("[Clean] scanned=" + scanned + " matching mark=" + markFilter + " → " + targets.Count
                + (onlySheet > 0 ? " only-sheet=" + onlySheet.ToString() : ""));
            Console.WriteLine("[Delete] scanned=" + scanned + " matching=" + targets.Count + " mark=" + markFilter
                + (onlySheet > 0 ? " only-sheet=" + onlySheet.ToString() : ""));
            if (onlySheet > 0 && targets.Count > 1)
            {
                Console.WriteLine("[OnlySheet] ERROR " + targets.Count + " sheets match sheet=" + onlySheet + "; not deleted");
                foreach (Drawing drawing in targets)
                    LogOnlySheet("match", drawing);
                return targets.Count;
            }
            if (onlySheet > 0 && targets.Count == 1)
                LogOnlySheet("picked", targets[0]);
            if (targets.Count == 0 || dryRun)
                return 0;

            foreach (var drawing in targets)
            {
                Attempt(handler, drawing, 1);
                if (!StillThere(handler, drawing)) continue;
                Attempt(handler, drawing, 2);
            }

            var survivors = new List<string>();
            try
            {
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var drawing = en.Current as Drawing;
                    if (drawing == null) continue;
                    bool isGa = false;
                    try { isGa = drawing is GADrawing; } catch { }
                    if (isGa) continue;
                    string mark = Mark(drawing);
                    if (!CivilDrawingSupport.MarksMatch(mark, markFilter)) continue;
                    string name = Name(drawing);
                    if (onlySheet > 0 && !SheetRoleMap.MatchesOnlySheet(mark, name, onlySheet)) continue;
                    survivors.Add(mark + (name.Length > 0 ? " '" + name + "'" : ""));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Delete] rescan: " + ex.Message);
                survivors.Add("(rescan failed: " + ex.Message + ")");
            }

            if (survivors.Count > 0)
                Console.WriteLine("[Delete] ERROR survivors=" + string.Join(", ", survivors));
            else
                Console.WriteLine("[Delete] survivors=0 mark=" + markFilter);
            return survivors.Count;
        }

        /// <summary>How many non-GA sheets for the mark match suffix n or that suffix's role. Does not delete.</summary>
        public static int CountOnlySheet(string markFilter, int onlySheet, out List<string> lines)
        {
            lines = new List<string>();
            if (string.IsNullOrWhiteSpace(markFilter) || onlySheet < 1) return 0;
            var handler = new DrawingHandler();
            if (!handler.GetConnectionStatus()) return 0;
            int n = 0;
            try
            {
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var drawing = en.Current as Drawing;
                    if (drawing == null) continue;
                    bool isGa = false;
                    try { isGa = drawing is GADrawing; } catch { }
                    if (isGa) continue;
                    string mark = Mark(drawing);
                    string name = Name(drawing);
                    if (!CivilDrawingSupport.MarksMatch(mark, markFilter)) continue;
                    if (!SheetRoleMap.MatchesOnlySheet(mark, name, onlySheet)) continue;
                    n++;
                    int? suffix = SheetRoleMap.SuffixNumber(mark);
                    SheetRole role = SheetRoleMap.Resolve(mark, name);
                    lines.Add("[OnlySheet] match mark='" + mark + "' name='" + name
                        + "' suffix=" + (suffix.HasValue ? suffix.Value.ToString() : "?")
                        + " role=" + role);
                }
            }
            catch (Exception ex)
            {
                lines.Add("[OnlySheet] list failed: " + ex.Message);
                n++;
            }
            return n;
        }

        private static void LogOnlySheet(string word, Drawing drawing)
        {
            string mark = Mark(drawing);
            string name = Name(drawing);
            int? suffix = SheetRoleMap.SuffixNumber(mark);
            SheetRole role = SheetRoleMap.Resolve(mark, name);
            Console.WriteLine("[OnlySheet] " + word + " mark='" + mark + "' name='" + name
                + "' suffix=" + (suffix.HasValue ? suffix.Value.ToString() : "?")
                + " role=" + role);
        }

        private static void Attempt(DrawingHandler handler, Drawing drawing, int attempt)
        {
            string mark = Mark(drawing);
            string name = Name(drawing);
            bool active = IsActive(handler, drawing);
            bool closeFirst = active;
            string closeOk = "not-needed";
            string error = "none";
            string result = "false";
            if (closeFirst)
            {
                try
                {
                    closeOk = handler.CloseActiveDrawing(true) ? "yes" : "false";
                }
                catch (Exception ex)
                {
                    closeOk = "false";
                    error = ex.Message;
                }
                active = IsActive(handler, drawing);
            }
            try
            {
                result = drawing.Delete() ? "true" : "false";
            }
            catch (Exception ex)
            {
                result = "false";
                error = ex.Message;
            }
            Console.WriteLine("[Delete] name='" + name + "' mark='" + mark
                + "' active=" + (active ? "yes" : "no")
                + " close-first=" + (closeFirst ? "yes" : "no")
                + " close-ok=" + closeOk
                + " delete=" + result
                + " error=" + error
                + " attempt=" + attempt.ToString());
        }

        private static bool StillThere(DrawingHandler handler, Drawing drawing)
        {
            string mark = Mark(drawing);
            string name = Name(drawing);
            try
            {
                var en = handler.GetDrawings();
                while (en != null && en.MoveNext())
                {
                    var d = en.Current as Drawing;
                    if (d == null) continue;
                    if (!string.Equals(Mark(d), mark, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(Name(d), name, StringComparison.OrdinalIgnoreCase)) continue;
                    return true;
                }
            }
            catch { return true; }
            return false;
        }

        private static bool IsActive(DrawingHandler handler, Drawing drawing)
        {
            try
            {
                Drawing active = handler.GetActiveDrawing();
                if (active == null) return false;
                return string.Equals(Mark(active), Mark(drawing), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Name(active), Name(drawing), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Mark(Drawing d)
        {
            try { return CivilDrawingSupport.StripMark(d.Mark ?? d.Name ?? "?"); }
            catch { return "?"; }
        }

        private static string Name(Drawing d)
        {
            try { return d.Name ?? ""; }
            catch { return ""; }
        }
    }
}

// DrawingSheetCleaner.cs
// ─────────────────────────────────────────────────────────────────
// Mark-scoped drawing delete via Tekla.Structures.Drawing.Drawing.Delete().
// Used before re-running macros so a piece does not end up with duplicate
// Cast Unit sheets in Document Manager.
//
// Safety: a non-empty mark filter is mandatory. GA sheets are never deleted.

using System;
using System.Collections.Generic;
using Tekla.Structures.Drawing;

namespace TeklaExtractor.Services
{
    public static class DrawingSheetCleaner
    {
        /// <summary>
        /// Deletes every non-GA sheet whose drawing mark matches <paramref name="markFilter"/>.
        /// Returns the number of deleted sheets. Set <paramref name="dryRun"/> to only list them.
        /// </summary>
        public static int DeleteByMark(string markFilter, bool dryRun = false)
        {
            if (string.IsNullOrWhiteSpace(markFilter))
            {
                Console.WriteLine("[Clean] refused: a --mark value is required (never bulk-delete drawings).");
                return 0;
            }

            var handler = new DrawingHandler();
            if (!handler.GetConnectionStatus())
            {
                Console.WriteLine("[Clean] DrawingHandler not connected.");
                return 0;
            }

            try { handler.CloseActiveDrawing(false); } catch { }

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

                    string mark = Mark(drawing);
                    if (!CivilDrawingSupport.MarksMatch(mark, markFilter)) continue;
                    targets.Add(drawing);
                    Console.WriteLine("[Clean] match '" + mark + "' (" + drawing.GetType().Name + ")");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Clean] GetDrawings: " + ex.Message);
            }

            Console.WriteLine("[Clean] scanned=" + scanned + " matching mark=" + markFilter + " → " + targets.Count);
            if (targets.Count == 0 || dryRun)
                return 0;

            int deleted = 0;
            foreach (var drawing in targets)
            {
                string mark = Mark(drawing);
                try
                {
                    if (drawing.Delete())
                    {
                        deleted++;
                        Console.WriteLine("[Clean] deleted '" + mark + "'");
                    }
                    else
                        Console.WriteLine("[Clean] Delete() returned false for '" + mark + "'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Clean] delete '" + mark + "': " + ex.Message);
                }
            }

            Console.WriteLine("[Clean] deleted=" + deleted + " of " + targets.Count);
            return deleted;
        }

        private static string Mark(Drawing d)
        {
            try { return CivilDrawingSupport.StripMark(d.Mark ?? d.Name ?? "?"); }
            catch { return "?"; }
        }
    }
}

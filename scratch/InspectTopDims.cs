using System;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

class Program
{
    static void Main()
    {
        var dh = new DrawingHandler();
        var dwg = dh.GetActiveDrawing();
        if (dwg == null) { Console.WriteLine("No active drawing"); return; }
        var sheet = dwg.GetSheet();
        var ve = sheet.GetViews();
        View front = null;
        while (ve.MoveNext())
        {
            if (ve.Current is View v && (v.Name ?? "").Contains("TOP IN FORM") && v.Attributes.Scale > 35)
                front = v;
        }
        if (front == null) { Console.WriteLine("No front view"); return; }

        var de = front.GetAllObjects(typeof(StraightDimensionSet));
        while (de.MoveNext())
        {
            if (de.Current is StraightDimensionSet sds)
            {
                var tag = "";
                if (sds.Attributes.RightUpperTag != null)
                    foreach (var el in sds.Attributes.RightUpperTag) if (el is TextElement te) tag += te.Value + " ";
                if (sds.Attributes.RightLowerTag != null)
                    foreach (var el in sds.Attributes.RightLowerTag) if (el is TextElement te) tag += te.Value + " ";

                var subs = sds.GetObjects();
                Point sp = null, ep = null;
                Vector up = null;
                int count = 0;
                while (subs.MoveNext())
                {
                    if (subs.Current is StraightDimension sd)
                    {
                        count++;
                        if (sp == null) { sp = sd.StartPoint; ep = sd.EndPoint; up = sd.UpDirection; }
                    }
                }
                if (tag.Contains("OVERALL") || tag.Contains("LEDGE") || tag.Contains("LIFTERS") || sds.Distance > 6000)
                {
                    Console.WriteLine("Tag='{0}' Dist={1:F1} UpDir=({2:F1},{3:F1}) Segs={4} SP=({5:F1},{6:F1}) EP=({7:F1},{8:F1})",
                        tag.Trim(), sds.Distance, up?.X ?? 0, up?.Y ?? 0, count,
                        sp?.X ?? 0, sp?.Y ?? 0, ep?.X ?? 0, ep?.Y ?? 0);
                }
            }
        }
    }
}

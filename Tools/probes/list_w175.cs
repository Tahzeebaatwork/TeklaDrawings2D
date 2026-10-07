using System;
using Tekla.Structures.Drawing;

class P
{
    static void Main()
    {
        var h = new DrawingHandler();
        if (!h.GetConnectionStatus()) { Console.WriteLine("no conn"); return; }
        try { h.CloseActiveDrawing(); } catch { }
        var en = h.GetDrawings();
        int n = 0;
        while (en != null && en.MoveNext())
        {
            var d = en.Current as Drawing;
            if (d == null) continue;
            n++;
            string m = "";
            try { m = d.Mark ?? ""; } catch { }
            string name = "";
            try { name = d.Name ?? ""; } catch { }
            if (m.IndexOf("W10-175", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("W10-175", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("MARK=[" + m + "] NAME=[" + name + "] TYPE=" + d.GetType().Name);
            }
        }
        Console.WriteLine("total=" + n);
    }
}

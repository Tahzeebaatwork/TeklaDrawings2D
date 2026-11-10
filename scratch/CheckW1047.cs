using System;
using Tekla.Structures.Drawing;

class Program
{
    static void Main()
    {
        var dh = new DrawingHandler();
        var de = dh.GetDrawings();
        Console.WriteLine("Drawings in model:");
        int count = 0;
        while (de.MoveNext())
        {
            if (de.Current is Drawing d)
            {
                count++;
                if ((d.Mark ?? "").Contains("47") || (d.Name ?? "").Contains("47") || (d.Mark ?? "").Contains("78"))
                {
                    Console.WriteLine("  Drawing Name='{0}' Mark='{1}' Type={2}", d.Name, d.Mark, d.GetType().Name);
                }
            }
        }
        Console.WriteLine("Total drawings: " + count);
    }
}

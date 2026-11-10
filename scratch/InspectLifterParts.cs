using System;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

class Program
{
    static void Main()
    {
        var model = new Model();
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

        var wall = model.SelectModelObject(new Identifier(599897)) as Beam;
        var solid = wall.GetSolid();

        Matrix toVcs = MatrixFactory.ToCoordinateSystem(front.ViewCoordinateSystem);
        Matrix toDcs = MatrixFactory.ToCoordinateSystem(front.DisplayCoordinateSystem);

        double minX_vcs = double.MaxValue, maxX_vcs = double.MinValue, topY_vcs = double.MinValue;
        double minX_dcs = double.MaxValue, maxX_dcs = double.MinValue, topY_dcs = double.MinValue;

        var ee = solid.GetEdgeEnumerator();
        while (ee.MoveNext())
        {
            if (ee.Current is Edge e)
            {
                if (e.StartPoint != null)
                {
                    var pV = toVcs.Transform(e.StartPoint);
                    if (pV.X < minX_vcs) minX_vcs = pV.X;
                    if (pV.X > maxX_vcs) maxX_vcs = pV.X;
                    if (pV.Y > topY_vcs) topY_vcs = pV.Y;

                    var pD = toDcs.Transform(e.StartPoint);
                    if (pD.X < minX_dcs) minX_dcs = pD.X;
                    if (pD.X > maxX_dcs) maxX_dcs = pD.X;
                    if (pD.Y > topY_dcs) topY_dcs = pD.Y;
                }
            }
        }

        Console.WriteLine("Via ToCoordinateSystem(ViewCoordinateSystem): X=[{0:F1}..{1:F1}] Span={2:F1}, TopY={3:F1}",
            minX_vcs, maxX_vcs, maxX_vcs - minX_vcs, topY_vcs);
        Console.WriteLine("Via ToCoordinateSystem(DisplayCoordinateSystem): X=[{0:F1}..{1:F1}] Span={2:F1}, TopY={3:F1}",
            minX_dcs, maxX_dcs, maxX_dcs - minX_dcs, topY_dcs);
    }
}

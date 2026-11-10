using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TSPoint = Tekla.Structures.Geometry3d.Point;
using TSVector = Tekla.Structures.Geometry3d.Vector;

namespace TeklaExtractor
{
    class ClaudeSpikeAudit
    {
        static int GetPropId(object obj, string propName)
        {
            if (obj == null) return 0;
            var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p?.GetValue(obj) is Identifier id) return id.ID;
            return 0;
        }

        static TSVector GetSetUpDirection(StraightDimensionSet sds)
        {
            if (sds == null) return new TSVector(0, 0, 0);
            var subs = sds.GetObjects();
            while (subs != null && subs.MoveNext())
            {
                if (subs.Current is StraightDimension sd && sd.UpDirection != null)
                    return sd.UpDirection;
            }
            return new TSVector(0, 0, 0);
        }

        static void Main(string[] args)
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                Console.WriteLine("FAIL: Model not connected.");
                return;
            }

            var dh = new DrawingHandler();
            Drawing drawing = null;
            var de = dh.GetDrawings();
            while (de != null && de.MoveNext())
            {
                if (de.Current is Drawing d && (d.Mark ?? "").Contains("W10-78 - 1"))
                {
                    drawing = d;
                    break;
                }
            }

            if (drawing == null)
            {
                Console.WriteLine("Drawing [W10-78 - 1] not found.");
                return;
            }

            dh.SetActiveDrawing(drawing, true);
            var sheet = dh.GetActiveDrawing();
            Console.WriteLine("Active Drawing: " + sheet.Mark + " Name=" + sheet.Name);

            // Find front view
            View front = null;
            var ve = sheet.GetSheet().GetViews();
            while (ve != null && ve.MoveNext())
            {
                if (ve.Current is View v && (v.Name ?? "").IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    front = v;
                    break;
                }
            }

            if (front == null)
            {
                Console.WriteLine("Front view (TOP IN FORM) not found.");
                return;
            }

            Console.WriteLine("\n=== STRAIGHT DIMENSION SETS IN FRONT VIEW ===");
            var sdsList = new List<StraightDimensionSet>();
            var sde = front.GetObjects(new Type[] { typeof(StraightDimensionSet) });
            while (sde != null && sde.MoveNext())
            {
                if (sde.Current is StraightDimensionSet sds)
                    sdsList.Add(sds);
            }

            Console.WriteLine("Total StraightDimensionSets found: " + sdsList.Count);
            int idx = 0;
            foreach (var sds in sdsList)
            {
                idx++;
                int id = GetPropId(sds, "Identifier");
                double dist = sds.Distance;
                var up = GetSetUpDirection(sds);

                // Collect segments
                var segs = new List<double>();
                var pts = new List<TSPoint>();
                var subs = sds.GetObjects();
                while (subs != null && subs.MoveNext())
                {
                    if (subs.Current is StraightDimension sd)
                    {
                        pts.Add(sd.StartPoint);
                        pts.Add(sd.EndPoint);
                        if (sd.StartPoint != null && sd.EndPoint != null)
                            segs.Add(Math.Round(Distance.PointToPoint(sd.StartPoint, sd.EndPoint), 1));
                    }
                }

                // Related objects
                var relObjList = new List<string>();
                var re = sds.GetRelatedObjects();
                while (re != null && re.MoveNext())
                {
                    if (re.Current is DrawingObject dobj)
                    {
                        int mId = GetPropId(dobj, "ModelIdentifier");
                        string typeName = dobj.GetType().Name;
                        relObjList.Add(typeName + "(mid=" + mId + ")");
                    }
                }

                // Tag texts
                string tagL = "";
                string tagR = "";
                if (sds.Attributes.LeftUpperTag != null) foreach (var el in sds.Attributes.LeftUpperTag) if (el is TextElement te) tagL += te.Value + " ";
                if (sds.Attributes.LeftLowerTag != null) foreach (var el in sds.Attributes.LeftLowerTag) if (el is TextElement te) tagL += te.Value + " ";
                if (sds.Attributes.RightUpperTag != null) foreach (var el in sds.Attributes.RightUpperTag) if (el is TextElement te) tagR += te.Value + " ";
                if (sds.Attributes.RightLowerTag != null) foreach (var el in sds.Attributes.RightLowerTag) if (el is TextElement te) tagR += te.Value + " ";

                Console.WriteLine(string.Format("#{0} ID={1} Dist={2:F1} Up=({3:F1},{4:F1}) Segs=[{5}] TagL='{6}' TagR='{7}' RelCount={8} RelObjs=[{9}]",
                    idx, id, dist, up.X, up.Y, string.Join("|", segs), tagL.Trim(), tagR.Trim(), relObjList.Count, string.Join(", ", relObjList)));
            }

            Console.WriteLine("\n=== DIMENSION LINKS IN FRONT VIEW ===");
            var links = new List<DimensionLink>();
            var le = front.GetObjects(new Type[] { typeof(DimensionLink) });
            while (le != null && le.MoveNext())
            {
                if (le.Current is DimensionLink dl)
                    links.Add(dl);
            }
            Console.WriteLine("Total DimensionLinks found: " + links.Count);
            foreach (var link in links)
            {
                int lid = GetPropId(link, "Identifier");
                var d1 = link.GetDimension1();
                var d2 = link.GetDimension2();
                int id1 = GetPropId(d1, "Identifier");
                int id2 = GetPropId(d2, "Identifier");
                Console.WriteLine(string.Format("Link ID={0}: Dim1={1} <-> Dim2={2}", lid, id1, id2));
            }

            dh.CloseActiveDrawing(false);
        }
    }
}

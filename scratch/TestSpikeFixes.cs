using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Drawing.UI;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TSPoint = Tekla.Structures.Geometry3d.Point;
using TSVector = Tekla.Structures.Geometry3d.Vector;

namespace TeklaExtractor
{
    class TestSpikeFixes
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

        static ContainerElement CreateNativeStyleTag(string text, DrawingColors color = DrawingColors.Red)
        {
            var ce = new ContainerElement();
            var te = new TextElement(text);
            te.Font.Name = "Arial Narrow";
            te.Font.Height = 1.8;
            te.Font.Color = color;
            ce.Add(te);
            return ce;
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
            Console.WriteLine("Active Drawing: " + sheet.Mark);

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
                Console.WriteLine("Front view not found.");
                return;
            }

            // Enumerate sets
            var sdsList = new List<StraightDimensionSet>();
            var sde = front.GetObjects(new Type[] { typeof(StraightDimensionSet) });
            while (sde != null && sde.MoveNext())
            {
                if (sde.Current is StraightDimensionSet sds)
                    sdsList.Add(sds);
            }

            Console.WriteLine("Loaded " + sdsList.Count + " sets.");

            // 1. Identify lifters:
            // Find set with anchors (RelCount >= 2 or related parts with ANCHOR / 45407807)
            StraightDimensionSet linkedLifters = null;
            StraightDimensionSet wallOnlyLifters = null;
            foreach (var sds in sdsList)
            {
                var re = sds.GetRelatedObjects();
                int relCount = 0;
                bool hasAnchor = false;
                while (re != null && re.MoveNext())
                {
                    relCount++;
                    if (re.Current is DrawingObject dobj)
                    {
                        int mid = GetPropId(dobj, "ModelIdentifier");
                        if (mid == 45407807 || mid == 45408536) hasAnchor = true;
                    }
                }
                var up = GetSetUpDirection(sds);
                if (Math.Abs(up.Y - 1.0) < 0.1) // Top horizontal row
                {
                    if (hasAnchor || (relCount >= 2 && sds.Distance < 0))
                        linkedLifters = sds;
                    else if (relCount == 1 && sds.Distance > 0 && sds.Distance < 1500)
                    {
                        // Check if it's lifter by segments or tag
                        string tagR = "";
                        if (sds.Attributes.RightUpperTag != null) foreach (var el in sds.Attributes.RightUpperTag) if (el is TextElement te) tagR += te.Value;
                        if (tagR.Contains("LIFTER")) wallOnlyLifters = sds;
                    }
                }
            }

            Console.WriteLine("Linked Lifters ID: " + (linkedLifters != null ? GetPropId(linkedLifters, "Identifier").ToString() : "NULL"));
            Console.WriteLine("Wall-only Lifters ID: " + (wallOnlyLifters != null ? GetPropId(wallOnlyLifters, "Identifier").ToString() : "NULL"));

            if (wallOnlyLifters != null)
            {
                Console.WriteLine("Deleting wall-only Lifters duplicate (ID=" + GetPropId(wallOnlyLifters, "Identifier") + ")");
                wallOnlyLifters.Delete();
                sdsList.Remove(wallOnlyLifters);
            }

            if (linkedLifters != null)
            {
                Console.WriteLine("Promoting linked Lifters to top base position (Distance = 900.0) with [P-205] / LIFTERS tag");
                linkedLifters.Distance = 900.0;
                linkedLifters.Attributes.LeftUpperTag = new ContainerElement();
                linkedLifters.Attributes.LeftLowerTag = new ContainerElement();
                linkedLifters.Attributes.RightUpperTag = CreateNativeStyleTag("[P-205]");
                linkedLifters.Attributes.RightLowerTag = CreateNativeStyleTag("LIFTERS");
                linkedLifters.Modify();
            }

            // 2. Fix P-300 duplicate:
            // Find P-300 at distance 0 or redundant
            StraightDimensionSet p300Main = null;
            StraightDimensionSet p300Redundant = null;
            foreach (var sds in sdsList)
            {
                var up = GetSetUpDirection(sds);
                if (Math.Abs(up.Y - 1.0) < 0.1)
                {
                    var re = sds.GetRelatedObjects();
                    while (re != null && re.MoveNext())
                    {
                        if (re.Current is DrawingObject dobj)
                        {
                            int mid = GetPropId(dobj, "ModelIdentifier");
                            if (mid == 45407097) p300Main = sds;
                            if (mid == 45406868) p300Redundant = sds;
                        }
                    }
                }
            }

            if (p300Redundant != null)
            {
                Console.WriteLine("Deleting redundant P-300 set (ID=" + GetPropId(p300Redundant, "Identifier") + ")");
                p300Redundant.Delete();
                sdsList.Remove(p300Redundant);
            }

            if (p300Main != null)
            {
                p300Main.Distance = 3600.0;
                p300Main.Attributes.RightUpperTag = CreateNativeStyleTag("[P-300]");
                p300Main.Attributes.RightLowerTag = CreateNativeStyleTag("BRACING INSERT");
                p300Main.Modify();
            }

            // 3. Fix Bottom order: GT100-1219 at bottomBase (1500), GT100-686 at bottomBase + pitch (2550), Opening at (3600)
            StraightDimensionSet botGT100 = null;
            StraightDimensionSet botGT686 = null;
            StraightDimensionSet botOpening = null;
            foreach (var sds in sdsList)
            {
                var up = GetSetUpDirection(sds);
                if (Math.Abs(up.Y - (-1.0)) < 0.1) // Bottom row
                {
                    var re = sds.GetRelatedObjects();
                    while (re != null && re.MoveNext())
                    {
                        if (re.Current is DrawingObject dobj)
                        {
                            int mid = GetPropId(dobj, "ModelIdentifier");
                            if (mid == 700345) botGT686 = sds;
                            if (mid == 698903 || mid == 36637012) botGT100 = sds;
                        }
                    }
                    string tagR = "";
                    if (sds.Attributes.RightUpperTag != null) foreach (var el in sds.Attributes.RightUpperTag) if (el is TextElement te) tagR += te.Value;
                    if (tagR.Contains("OPENING")) botOpening = sds;
                }
            }

            if (botGT100 != null)
            {
                botGT100.Distance = 1500.0;
                botGT100.Attributes.RightUpperTag = CreateNativeStyleTag("[GT100-1219]");
                botGT100.Attributes.RightLowerTag = CreateNativeStyleTag("HARDWARE");
                botGT100.Modify();
                Console.WriteLine("Set botGT100 Distance = 1500.0");
            }
            if (botGT686 != null)
            {
                botGT686.Distance = 2550.0;
                botGT686.Attributes.RightUpperTag = CreateNativeStyleTag("[GT100-686]");
                botGT686.Attributes.RightLowerTag = CreateNativeStyleTag("HARDWARE");
                botGT686.Modify();
                Console.WriteLine("Set botGT686 Distance = 2550.0");
            }
            if (botOpening != null)
            {
                botOpening.Distance = 3600.0;
                botOpening.Attributes.RightUpperTag = CreateNativeStyleTag("PROFILE");
                botOpening.Attributes.RightLowerTag = CreateNativeStyleTag("OPENING");
                botOpening.Modify();
                Console.WriteLine("Set botOpening Distance = 3600.0");
            }

            // 4. LEDGE Left L-link:
            // Find LEDGE row and left LEDGE vertical column
            StraightDimensionSet ledgeRow = null;
            StraightDimensionSet ledgeCol = null;
            foreach (var sds in sdsList)
            {
                var up = GetSetUpDirection(sds);
                if (Math.Abs(up.Y - 1.0) < 0.1)
                {
                    string tagR = "";
                    if (sds.Attributes.RightUpperTag != null) foreach (var el in sds.Attributes.RightUpperTag) if (el is TextElement te) tagR += te.Value;
                    if (tagR.Contains("LEDGE")) ledgeRow = sds;
                }
                else if (Math.Abs(up.X - (-1.0)) < 0.1) // Left column
                {
                    // Check segments
                    var subs = sds.GetObjects();
                    var segs = new List<double>();
                    while (subs != null && subs.MoveNext())
                    {
                        if (subs.Current is StraightDimension sd)
                            segs.Add(Math.Round(Distance.PointToPoint(sd.StartPoint, sd.EndPoint), 1));
                    }
                    if (segs.Count == 3 && segs.Any(s => Math.Abs(s - 57.1) < 2.0 || Math.Abs(s - 257.0) < 5.0 || Math.Abs(s - 350.0) < 5.0))
                    {
                        ledgeCol = sds;
                    }
                }
            }

            if (ledgeRow != null)
            {
                ledgeRow.Attributes.RightUpperTag = CreateNativeStyleTag("PROFILE", DrawingColors.Red);
                ledgeRow.Attributes.RightLowerTag = CreateNativeStyleTag("LEDGE", DrawingColors.Red);
                ledgeRow.Modify();
                Console.WriteLine("Updated LEDGE tag to Red 'PROFILE' / 'LEDGE'");
            }

            if (ledgeRow != null && ledgeCol != null)
            {
                Console.WriteLine("Creating native DimensionLink for LEDGE: Row=" + GetPropId(ledgeRow, "Identifier") + " <-> Col=" + GetPropId(ledgeCol, "Identifier"));
                try
                {
                    var link = new DimensionLink(ledgeRow, ledgeCol);
                    bool ok = link.Insert();
                    Console.WriteLine("DimensionLink.Insert() result: " + ok);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("DimensionLink failed: " + ex.Message);
                }
            }

            // 5. SP25 tag format:
            foreach (var sds in sdsList)
            {
                var up = GetSetUpDirection(sds);
                if (Math.Abs(up.Y - 1.0) < 0.1)
                {
                    var re = sds.GetRelatedObjects();
                    bool isSP25 = false;
                    while (re != null && re.MoveNext())
                    {
                        if (re.Current is DrawingObject dobj)
                        {
                            int mid = GetPropId(dobj, "ModelIdentifier");
                            if (mid == 33155566 || mid == 3988641 || mid == 3974983) isSP25 = true;
                        }
                    }
                    if (isSP25)
                    {
                        sds.Distance = 1500.0;
                        sds.Attributes.RightUpperTag = CreateNativeStyleTag("[SP25-1067]");
                        sds.Attributes.RightLowerTag = CreateNativeStyleTag("HARDWARE");
                        sds.Modify();
                        Console.WriteLine("Updated SP25 tag to boxed '[SP25-1067]' / 'HARDWARE'");
                    }
                }
            }

            // Save drawing, close active drawing, then export PDF with DPMPrinterAttributes
            sheet.CommitChanges();
            Console.WriteLine("Changes committed to drawing [W10-78 - 1].");
            dh.CloseActiveDrawing(true);

            // Print to PDF with full color
            string outPdf = @"C:\Users\ASUS\.gemini\antigravity\brain\ea6a5253-20fd-4357-b09a-6ae838074fd6\SpikeV5_W10-78_Sheet1.pdf";
            var dpm = new DPMPrinterAttributes
            {
                OutputType = DotPrintOutputType.PDF,
                PaperSize = DotPrintPaperSize.Auto,
                ScalingMethod = DotPrintScalingType.Auto,
                Orientation = DotPrintOrientationType.Landscape,
                ColorMode = DotPrintColor.Color,
                OutputFileName = outPdf,
                OpenFileWhenFinished = false,
                NumberOfCopies = 1,
                PrintToMultipleSheet = DotPrintToMultipleSheet.Off,
                PrinterName = "TS PDF Writer",
            };
            bool printed = dh.PrintDrawing(sheet, dpm, outPdf);
            Console.WriteLine("PrintDrawing with DPM to " + outPdf + " result: " + printed);
        }
    }
}

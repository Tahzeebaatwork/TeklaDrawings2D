// AutoCompressAndEdit11x17.cs
// ─────────────────────────────────────────────────────────────────
// Tekla Structures 2026 UI Automation Macro:
// 1. Locks Sheet Size to 11x17 (431.8 x 279.4 mm)
// 2. Compresses View Scale & Activates Part Shortening for long panels
// 3. Clamps Views inside Safe Viewport (Zero BOM Overlap)
// 4. Hides Elevation Rebar Mesh & Purges Temporary Tags (AF1/AF2)
// 5. AABB Anti-Collision Relaxation for tight dimensions ("288 288")
// 6. Injects Title Block UDAs (Date, Drafter, Approval, Status)
// 7. Exports single-page vector 11x17 PDF via TS PDF Writer (>100 KB)

#pragma warning disable 1633
#pragma reference "Tekla.Macros.Wpf.Runtime"
#pragma reference "Tekla.Macros.Akit"
#pragma reference "Tekla.Macros.Runtime"
#pragma reference "Tekla.Structures"
#pragma reference "Tekla.Structures.Model"
#pragma reference "Tekla.Structures.Drawing"
#pragma warning restore 1633

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using TSDrawing = Tekla.Structures.Drawing;

namespace UserMacros
{
    public sealed class Macro
    {
        [Tekla.Macros.Runtime.MacroEntryPointAttribute()]
        public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime)
        {
            var handler = new DrawingHandler();
            var drawing = handler.GetActiveDrawing();
            if (drawing == null)
            {
                Console.WriteLine("[Auto11x17] No active drawing found in Tekla UI.");
                return;
            }

            var sheet = drawing.GetSheet();
            if (sheet == null) return;

            Console.WriteLine($"[Auto11x17] Automating 11x17 Compression & Clean Editing for: {drawing.Mark}");

            // =================================================================
            // 1. BOUNDING BOX: Lock Sheet Size to ANSI 11x17 (431.8 x 279.4 mm)
            // =================================================================
            drawing.Layout.SizeDefinitionMode = SizeDefinitionMode.SpecifiedSize;
            drawing.Layout.SheetSize = new Size(431.8, 279.4);

            // Reserved Keepouts on 11x17 Sheet:
            // BOM Area: X in [280, 425], Y in [90, 270]
            // Safe Viewport Area: X in [15, 270], Y in [15, 260]
            const double SafeViewportMaxX = 270.0;
            const double SafeViewportMaxY = 250.0;

            // =================================================================
            // 2. COMPRESSION & CLUTTER CLEANING ON EACH VIEW
            // =================================================================
            var views = sheet.GetViews();
            while (views != null && views.MoveNext())
            {
                if (views.Current is View view)
                {
                    // A. Hide Rebar Mesh from Front Elevation
                    HideElevationRebars(view);

                    // B. Purge Temporary AF1 / AF2 / TEMP_ Tags
                    PurgeTemporaryTags(view);

                    // C. Auto-Compress Scale if View exceeds Safe Viewport
                    var aabb = view.GetAxisAlignedBoundingBox();
                    if (aabb != null)
                    {
                        double viewWidth = Math.Abs(aabb.MaxPoint.X - aabb.MinPoint.X);
                        double viewHeight = Math.Abs(aabb.MaxPoint.Y - aabb.MinPoint.Y);

                        if (viewWidth > SafeViewportMaxX || viewHeight > SafeViewportMaxY)
                        {
                            // Scale down to 1:30 or 1:40
                            if (view.Attributes.Scale < 30.0)
                                view.Attributes.Scale = 30.0;
                            else if (view.Attributes.Scale < 40.0)
                                view.Attributes.Scale = 40.0;
                        }

                        // D. Part Shortening: Compress long members (>8000mm)
                        if (viewWidth > 280.0)
                        {
                            view.Attributes.Shortening.CutParts = true;
                            view.Attributes.Shortening.MinimumLength = 1200.0;
                            view.Attributes.Shortening.CutPartsSymmetry = true;
                        }
                    }

                    // E. Re-center View inside Safe Viewport (Away from BOM)
                    ClampViewAwayFromBom(view, SafeViewportMaxX);

                    view.Modify();
                }
            }

            // =================================================================
            // 3. ZERO BOM OVERLAPPING & AABB TEXT ANTI-COLLISION
            // =================================================================
            DeflectTextOutOfBomKeepout(sheet);
            ResolveOverlappingMarks(sheet);

            // =================================================================
            // 4. TITLE BLOCK METADATA (Drafter, Approval, Release Date)
            // =================================================================
            drawing.SetUserProperty("DR_DRAWN_BY", "VSB");
            drawing.SetUserProperty("DR_APPROVED_BY", "HR");
            drawing.SetUserProperty("DR_DRAWN_DATE", DateTime.Now.ToString("dd.MM.yyyy"));
            drawing.SetUserProperty("DR_STATUS", "ISSUED FOR FABRICATION");

            // Commit and save inside Tekla UI
            drawing.Modify();
            drawing.CommitChanges();
            handler.SaveActiveDrawing();

            // =================================================================
            // 5. EXPORT SINGLE-PAGE 11x17 VECTOR PDF (>100 KB)
            // =================================================================
            string exportDir = Path.Combine(handler.GetActiveDrawing().GetSheet().GetViews() != null ? "Export\\CivilDrawings\\PDF" : "Export\\PDF");
            Directory.CreateDirectory(exportDir);
            string pdfPath = Path.Combine(exportDir, $"{drawing.Mark}_11x17_ENG.pdf");

            var printAttrs = new DPMPrinterAttributes
            {
                PrinterName = "TS PDF Writer",
                OutputType = DotPrintOutputType.PDF,
                PaperSize = DotPrintPaperSize.Letter_11x17,
                Orientation = DotPrintOrientationType.Landscape,
                ScalingMethod = DotPrintScalingType.Auto,      // Compresses all graphics to 11x17
                PrintToMultipleSheet = DotPrintToMultipleSheet.Off // Strictly single page
            };

            bool printed = handler.PrintDrawing(drawing, printAttrs, pdfPath);
            Console.WriteLine(printed ? $"[Auto11x17] SUCCESS: 11x17 PDF Exported → {pdfPath}" : "[Auto11x17] PDF export failed.");
        }

        private static void HideElevationRebars(View view)
        {
            var rebars = view.GetAllObjects(typeof(ReinforcementBase));
            while (rebars != null && rebars.MoveNext())
            {
                if (rebars.Current is ReinforcementBase rebar)
                {
                    rebar.Attributes.Visibility = ReinforcementBase.VisibilityTypes.Hidden;
                    rebar.Modify();
                }
            }
        }

        private static void PurgeTemporaryTags(View view)
        {
            var texts = view.GetAllObjects(typeof(TSDrawing.Text));
            while (texts != null && texts.MoveNext())
            {
                if (texts.Current is TSDrawing.Text txt && Regex.IsMatch(txt.TextString ?? "", @"^(AF\d+|TEMP_)"))
                {
                    var hide = txt.GetType().GetProperty("Hideable")?.GetValue(txt, null) as Hideable;
                    hide?.HideFromDrawingView();
                    txt.Modify();
                }
            }
        }

        private static void ClampViewAwayFromBom(View view, double safeMaxX)
        {
            var aabb = view.GetAxisAlignedBoundingBox();
            if (aabb == null) return;

            // If right edge of view intrudes into BOM zone (X >= 275mm)
            if (aabb.MaxPoint.X > safeMaxX)
            {
                double shiftX = -(aabb.MaxPoint.X - safeMaxX + 10.0);
                view.Origin = new Point(view.Origin.X + shiftX, view.Origin.Y, view.Origin.Z);
            }
        }

        private static void DeflectTextOutOfBomKeepout(ContainerView sheet)
        {
            // BOM Keepout: X >= 280mm, Y >= 90mm
            var objects = sheet.GetAllObjects();
            while (objects != null && objects.MoveNext())
            {
                if (objects.Current is TSDrawing.Text txt)
                {
                    if (txt.InsertionPoint.X >= 280.0 && txt.InsertionPoint.Y >= 90.0)
                    {
                        // Deflect leftwards into safe viewport + leader line
                        txt.InsertionPoint = new Point(265.0, txt.InsertionPoint.Y, txt.InsertionPoint.Z);
                        txt.Modify();
                    }
                }
                else if (objects.Current is MarkBase mark)
                {
                    if (mark.InsertionPoint.X >= 280.0 && mark.InsertionPoint.Y >= 90.0)
                    {
                        mark.Attributes.Placing = PlacingTypes.LeaderLine();
                        mark.InsertionPoint = new Point(265.0, mark.InsertionPoint.Y, mark.InsertionPoint.Z);
                        mark.Modify();
                    }
                }
            }
        }

        private static void ResolveOverlappingMarks(ContainerView sheet)
        {
            var boxes = new List<Tuple<DrawingObject, RectangleBoundingBox>>();
            var objects = sheet.GetAllObjects();

            while (objects != null && objects.MoveNext())
            {
                if (objects.Current is TSDrawing.Text t)
                    boxes.Add(Tuple.Create((DrawingObject)t, t.GetAxisAlignedBoundingBox()));
                else if (objects.Current is MarkBase m)
                    boxes.Add(Tuple.Create((DrawingObject)m, m.GetAxisAlignedBoundingBox()));
            }

            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = 0; i < boxes.Count; i++)
                {
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        var a = boxes[i].Item2;
                        var b = boxes[j].Item2;
                        if (a == null || b == null) continue;

                        if (a.MinPoint.X < b.MaxPoint.X && a.MaxPoint.X > b.MinPoint.X &&
                            a.MinPoint.Y < b.MaxPoint.Y && a.MaxPoint.Y > b.MinPoint.Y)
                        {
                            double dy = (a.MaxPoint.Y - b.MinPoint.Y) + 4.5;
                            var obj = boxes[j].Item1;
                            if (obj is TSDrawing.Text text)
                            {
                                text.InsertionPoint = new Point(text.InsertionPoint.X, text.InsertionPoint.Y + dy, 0);
                                text.Modify();
                            }
                            else if (obj is MarkBase mark)
                            {
                                mark.Attributes.Placing = PlacingTypes.LeaderLine();
                                mark.InsertionPoint = new Point(mark.InsertionPoint.X, mark.InsertionPoint.Y + dy, 0);
                                mark.Modify();
                            }
                        }
                    }
                }
            }
        }
    }
}

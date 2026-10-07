using System;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Optimizes sheet dimensions when dynamic tables (BOM / Rebar Schedule) and views exceed
    /// standard sheet boundaries (such as 11x17).
    /// Works with DrawingGenerator.PrintToFile(..., DotPrintScalingType.Auto)
    /// to compress the expanded drawing onto a single PDF page.
    /// </summary>
    public static class DrawingLayoutOptimizer
    {
        public const double DefaultPadding = 25.0;
        public const double Standard11x17Aspect = 17.0 / 11.0; // ~1.54545

        /// <summary>
        /// Inspects the drawing sheet, calculates bounding extent of all tables and views,
        /// and expands Layout.SheetSize if boundary overflow is detected.
        /// </summary>
        public static bool OptimizeLayoutForTables(Drawing drawing, double padding = DefaultPadding)
        {
            if (drawing == null) return false;

            try
            {
                var sheet = drawing.GetSheet();
                if (sheet == null) return false;

                var layout = drawing.Layout;
                if (layout == null) return false;

                var currentSheetSize = layout.SheetSize;
                if (currentSheetSize == null) return false;

                double currentWidth = currentSheetSize.Width;
                double currentHeight = currentSheetSize.Height;
                if (currentWidth <= 1.0 || currentHeight <= 1.0)
                {
                    currentWidth = sheet.Width > 1.0 ? sheet.Width : 431.8; // default 17"
                    currentHeight = sheet.Height > 1.0 ? sheet.Height : 279.4; // default 11"
                }

                double minX = 0;
                double minY = 0;
                double maxX = currentWidth;
                double maxY = currentHeight;
                bool overflowDetected = false;

                // 1. Check all views on the sheet (model views, detail views, rebar views)
                try
                {
                    var views = sheet.GetViews();
                    while (views != null && views.MoveNext())
                    {
                        if (views.Current is View view)
                        {
                            try
                            {
                                var aabb = view.GetAxisAlignedBoundingBox();
                                if (aabb != null)
                                {
                                    if (CheckAndExpand(aabb, ref minX, ref minY, ref maxX, ref maxY, currentWidth, currentHeight))
                                        overflowDetected = true;
                                }
                            }
                            catch { /* view box optional */ }
                        }
                    }
                }
                catch { /* view enumeration best effort */ }

                // 2. Check all sheet-level objects (tables, plugins, texts, dwgs)
                try
                {
                    var objects = sheet.GetAllObjects();
                    while (objects != null && objects.MoveNext())
                    {
                        if (objects.Current is IAxisAlignedBoundingBox aabbObj)
                        {
                            try
                            {
                                var aabb = aabbObj.GetAxisAlignedBoundingBox();
                                if (aabb != null)
                                {
                                    if (CheckAndExpand(aabb, ref minX, ref minY, ref maxX, ref maxY, currentWidth, currentHeight))
                                        overflowDetected = true;
                                }
                            }
                            catch { /* object box optional */ }
                        }
                    }
                }
                catch { /* sheet objects best effort */ }

                if (!overflowDetected)
                {
                    return false;
                }

                // Compute required dimensions including padding and origin offset
                double requiredWidth = (maxX - Math.Min(0, minX)) + padding;
                double requiredHeight = (maxY - Math.Min(0, minY)) + padding;

                double targetWidth = Math.Max(currentWidth, requiredWidth);
                double targetHeight = Math.Max(currentHeight, requiredHeight);

                // Preserve 11x17 aspect ratio to avoid distortion during printer auto-scaling
                double currentAspect = currentWidth / currentHeight;
                if (currentAspect < 0.1 || double.IsNaN(currentAspect)) currentAspect = Standard11x17Aspect;

                double targetAspect = targetWidth / targetHeight;
                if (targetAspect < currentAspect)
                {
                    targetWidth = targetHeight * currentAspect;
                }
                else
                {
                    targetHeight = targetWidth / currentAspect;
                }

                Console.WriteLine($"[LayoutOptimizer] Sheet overflow detected on {drawing.Mark}: expanding {currentWidth:F1}x{currentHeight:F1} -> {targetWidth:F1}x{targetHeight:F1}");

                layout.SizeDefinitionMode = SizeDefinitionMode.SpecifiedSize;
                layout.SheetSize = new Size(targetWidth, targetHeight);

                bool modified = drawing.Modify();
                bool committed = drawing.CommitChanges();

                return modified && committed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LayoutOptimizer] Optimization failed: {ex.Message}");
                return false;
            }
        }

        private static bool CheckAndExpand(RectangleBoundingBox aabb, ref double minX, ref double minY, ref double maxX, ref double maxY, double sheetW, double sheetH)
        {
            if (aabb == null) return false;

            double left = Math.Min(aabb.MinPoint.X, aabb.MaxPoint.X);
            double right = Math.Max(aabb.MinPoint.X, aabb.MaxPoint.X);
            double bottom = Math.Min(aabb.MinPoint.Y, aabb.MaxPoint.Y);
            double top = Math.Max(aabb.MinPoint.Y, aabb.MaxPoint.Y);

            // Filter out unreasonable infinity or extreme coordinates
            if (double.IsNaN(left) || double.IsNaN(right) || double.IsNaN(bottom) || double.IsNaN(top)) return false;
            if (Math.Abs(left) > 1e6 || Math.Abs(right) > 1e6 || Math.Abs(bottom) > 1e6 || Math.Abs(top) > 1e6) return false;

            bool overflow = false;
            if (right > sheetW)
            {
                if (right > maxX) maxX = right;
                overflow = true;
            }
            if (top > sheetH)
            {
                if (top > maxY) maxY = top;
                overflow = true;
            }
            if (left < 0)
            {
                if (left < minX) minX = left;
                overflow = true;
            }
            if (bottom < 0)
            {
                if (bottom < minY) minY = bottom;
                overflow = true;
            }

            return overflow;
        }
    }
}

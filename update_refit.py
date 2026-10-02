import re

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'r', encoding='utf-8') as f:
    text = f.read()

new_refit = '''        private static void RefitFrontFrame(View view, TSModel.Part main, int scale)
        {
            if (view == null || main == null || scale < 1) return;
            try
            {
                var attrs = view.Attributes;
                Console.WriteLine($"[Fit] Step 1: W={view.Width:0.##}, H={view.Height:0.##}, Origin=({view.Origin.X:0.##}, {view.Origin.Y:0.##}), scale={scale}, Fixed={attrs.FixedViewPlacing}");

                attrs.FixedViewPlacing = false;
                view.Attributes = attrs;
                view.Modify();
                try { view.Select(); } catch { }
                Console.WriteLine($"[Fit] Step 2: W={view.Width:0.##}, H={view.Height:0.##} (after FixedViewPlacing=false)");

                if (!GetSolidBoundsInView(view, main, out double minX, out double maxX, out double minY, out double maxY))
                    return;

                double baseWidth = (maxX - minX) / scale;
                double baseHeight = (maxY - minY) / scale;
                
                double targetWidth = baseWidth + 50.0 + 30.0;
                double targetHeight = baseHeight + 55.0 + 54.0;

                view.Width = targetWidth;
                view.Height = targetHeight;
                view.Modify();
                try { view.Select(); } catch { }

                Console.WriteLine($"[Fit] Step 3: W={view.Width:0.##}, H={view.Height:0.##} (after size set only)");

                double minXSheet = view.Origin.X;
                double maxXSheet = view.Origin.X + view.Width;
                double minYSheet = view.Origin.Y;
                double maxYSheet = view.Origin.Y + view.Height;
                
                Console.WriteLine($"[Fit] Step 4: Sheet box X {minXSheet:0.##}-{maxXSheet:0.##}, Y {minYSheet:0.##}-{maxYSheet:0.##}");

                if (view.Width == 0 || view.Height == 0 || Math.Abs(view.Width - targetWidth) > 15 || Math.Abs(view.Height - targetHeight) > 15)
                {
                    attrs = view.Attributes;
                    attrs.FixedViewPlacing = true;
                    view.Attributes = attrs;
                    view.Modify();
                    Console.WriteLine($"[Fit] front frame FAILED. W={view.Width:0.##}, H={view.Height:0.##}");
                    return;
                }

                if (minXSheet < 15 || maxXSheet > 270 || minYSheet < 115 || maxYSheet > 279)
                {
                    Console.WriteLine($"[Fit] Step 6: Outside bounds. Modifying Origin. Before: ({view.Origin.X:0.##}, {view.Origin.Y:0.##})");
                    view.Origin = new Point(35.0, 130.0, 0);
                    view.Modify();
                    try { view.Select(); } catch { }
                    Console.WriteLine($"[Fit] Step 6: After Origin modify: ({view.Origin.X:0.##}, {view.Origin.Y:0.##})");
                }
                
                attrs = view.Attributes;
                attrs.FixedViewPlacing = true;
                view.Attributes = attrs;
                view.Modify();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Fit] front frame exception: " + ex.Message);
            }
        }'''

text = re.sub(
    r'private static void RefitFrontFrame\(View view, TSModel\.Part main, int scale\)\s*\{.*?(?=\s*private static void LogDimGeometry)',
    new_refit,
    text,
    flags=re.DOTALL
)

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'w', encoding='utf-8') as f:
    f.write(text)

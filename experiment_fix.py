import re

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'r', encoding='utf-8') as f:
    text = f.read()

# 1. Update RefitFrontFrame to be LOG-ONLY
new_refit = '''        private static void RefitFrontFrame(View view, TSModel.Part main, int scale)
        {
            if (view == null || main == null || scale < 1) return;
            try
            {
                var rBox = view.RestrictionBox;
                Console.WriteLine($"[Fit] RefitFrontFrame LOG-ONLY: RestrictionBox = ({rBox.MinPoint.X:0.##}, {rBox.MinPoint.Y:0.##}, {rBox.MinPoint.Z:0.##}) to ({rBox.MaxPoint.X:0.##}, {rBox.MaxPoint.Y:0.##}, {rBox.MaxPoint.Z:0.##})");
                
                try
                {
                    var aabb = view.GetAxisAlignedBoundingBox();
                    if (aabb != null)
                        Console.WriteLine($"[Fit] RefitFrontFrame LOG-ONLY: GetAxisAlignedBoundingBox = ({aabb.MinPoint.X:0.##}, {aabb.MinPoint.Y:0.##}, {aabb.MinPoint.Z:0.##}) to ({aabb.MaxPoint.X:0.##}, {aabb.MaxPoint.Y:0.##}, {aabb.MaxPoint.Z:0.##})");
                }
                catch { }
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

# 2. Update SealView to set RestrictionBox idempotently
new_seal = '''        private static void SealView(View view, TSModel.Part main, int scale)
        {
            if (view == null || main == null) return;
            try
            {
                if (!GetSolidBoundsInView(view, main, out double minX, out double maxX, out double minY, out double maxY))
                    return;
                
                // Z range unchanged from whatever was before or just keep it wide
                var oldBox = view.RestrictionBox;
                double minZ = oldBox != null && oldBox.MinPoint != null ? oldBox.MinPoint.Z : -5000.0;
                double maxZ = oldBox != null && oldBox.MaxPoint != null ? oldBox.MaxPoint.Z : 5000.0;
                
                // Pads in model mm
                double padLeft = 3750.0;
                double padRight = 2250.0;
                double padBelow = 4125.0;
                double padAbove = 4050.0;
                
                view.RestrictionBox = new AABB(
                    new Point(minX - padLeft, minY - padBelow, minZ),
                    new Point(maxX + padRight, maxY + padAbove, maxZ));
                view.Modify();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Fit] SealView: " + ex.Message);
            }
        }'''

# Replace SealView signature and body
text = re.sub(
    r'private static void SealView\(View view, double modelLength, int scale, double sheetHeight, double bomLeft\)\s*\{.*?(?=\s*private static void RefitFrontFrame)',
    new_seal,
    text,
    flags=re.DOTALL
)

# Update the call to SealView in Run()
text = text.replace('SealView(front, length, scale, sheetHeight, bomLeft);', 'SealView(front, main, scale);')

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'w', encoding='utf-8') as f:
    f.write(text)

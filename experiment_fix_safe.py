import sys

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'r', encoding='utf-8') as f:
    text = f.read()

# Replace RefitFrontFrame
start_refit = text.find('private static void RefitFrontFrame(')
if start_refit == -1: sys.exit(1)
end_refit = text.find('private static void LogDimGeometry', start_refit)
text = text[:start_refit] + '''private static void RefitFrontFrame(View view, Tekla.Structures.Model.Part main, int scale)
	{
		if (view == null || main == null || scale < 1) return;
		try
		{
			var rBox = view.RestrictionBox;
			if (rBox != null && rBox.MinPoint != null && rBox.MaxPoint != null)
				Console.WriteLine($"[Fit] RefitFrontFrame LOG-ONLY: RestrictionBox = ({rBox.MinPoint.X:0.##}, {rBox.MinPoint.Y:0.##}, {rBox.MinPoint.Z:0.##}) to ({rBox.MaxPoint.X:0.##}, {rBox.MaxPoint.Y:0.##}, {rBox.MaxPoint.Z:0.##})");
			try
			{
				var aabb = view.GetAxisAlignedBoundingBox();
				if (aabb != null && aabb.MinPoint != null && aabb.MaxPoint != null)
					Console.WriteLine($"[Fit] RefitFrontFrame LOG-ONLY: GetAxisAlignedBoundingBox = ({aabb.MinPoint.X:0.##}, {aabb.MinPoint.Y:0.##}, {aabb.MinPoint.Z:0.##}) to ({aabb.MaxPoint.X:0.##}, {aabb.MaxPoint.Y:0.##}, {aabb.MaxPoint.Z:0.##})");
			}
			catch { }
		}
		catch (Exception ex)
		{
			Console.WriteLine("[Fit] front frame exception: " + ex.Message);
		}
	}

	''' + text[end_refit:]

# Replace SealView
start_seal = text.find('private static void SealView(')
if start_seal == -1: sys.exit(2)
# Find the next method after SealView
end_seal = text.find('private static void TightenRestriction', start_seal)
text = text[:start_seal] + '''private static void SealView(View view, Tekla.Structures.Model.Part main, int scale, double sheetHeight, double bomLeft)
	{
		if (view == null || main == null) return;
		try
		{
			if (!GetSolidBoundsInView(view, main, out double minX, out double maxX, out double minY, out double maxY))
				return;
			
			var oldBox = view.RestrictionBox;
			double minZ = oldBox != null && oldBox.MinPoint != null ? oldBox.MinPoint.Z : -5000.0;
			double maxZ = oldBox != null && oldBox.MaxPoint != null ? oldBox.MaxPoint.Z : 5000.0;
			
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
	}

	''' + text[end_seal:]

text = text.replace('SealView(front, num, scale, sheetHeight, num2);', 'SealView(front, part, scale, sheetHeight, num2);')

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'w', encoding='utf-8') as f:
    f.write(text)

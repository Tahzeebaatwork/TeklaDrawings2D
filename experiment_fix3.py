with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'r', encoding='utf-8') as f:
    text = f.read()

# Fix the remaining SealView call
text = text.replace('SealView(front, modelLength, scale, sheet.Height, bomLeft);', 'SealView(front, main, scale, sheet.Height, bomLeft);')

# Add ViewBox definition if it doesn't exist
if 'private static AABB ViewBox(View view)' not in text:
    viewbox_def = '''	private static AABB ViewBox(View view)
	{
		try { return view.GetAxisAlignedBoundingBox(); } catch { return null; }
	}
'''
    # Insert it right before SealView
    text = text.replace('private static void SealView', viewbox_def + '\n\tprivate static void SealView')

with open(r'C:\Users\ASUS\Desktop\2d_tekla\Services\PrecastDimensionPostProcessor.cs', 'w', encoding='utf-8') as f:
    f.write(text)

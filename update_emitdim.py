import re

with open(r'Services\PrecastDimensionPostProcessor.cs', 'r', encoding='utf-8') as f:
    text = f.read()

new_emit = '''	private void EmitDim(View view, PanelAxis axis, List<double> stations, bool isBottom, double distance, string tagLabel = null, int scale = 20)
	{
		if (stations == null || stations.Count < 2)
		{
			return;
		}
		PointList pointList = new PointList();
		foreach (double station in stations)
		{
			Point point = (isBottom ? axis.PaperBottomPoint(station) : axis.PaperPoint(station));
			pointList.Add(new Point(point.X, point.Y, 0.0));
		}
		Vector upDirection = (isBottom ? new Vector(0.0, -1.0, 0.0) : new Vector(0.0, 1.0, 0.0));
		double paper = distance / (double)((scale < 1) ? 1 : scale);
		try
		{
			var attrs = new StraightDimensionSet.StraightDimensionSetAttributes();
			try { attrs.LoadAttributes(\"standard\"); } catch { }
			attrs.ExtensionLine = DimensionSetBaseAttributes.ExtensionLineTypes.Yes;
			attrs.Color = DrawingColors.Green;
			try { attrs.CombinedDimension.MinimumNumberToCombine = 99; } catch { }
			try { attrs.Text.Font.Color = DrawingColors.Yellow; } catch { }

			StraightDimensionSet set = null;
			try { set = _dims.CreateDimensionSet(view, pointList, upDirection, paper, attrs); }
			catch (Exception ex) { Console.WriteLine(\"[DimError] \" + ex.Message); }
			if (set == null)
				set = _dims.CreateDimensionSet(view, pointList, upDirection, paper);

			if (set == null)
				Console.WriteLine(\"[DimDiagnostic] FAILED '\" + tagLabel + \"' CreateDimensionSet returned NULL\");
			else
			{
                double readBack = 0;
                try { readBack = set.Distance; } catch {}
				Console.WriteLine($\"[DimDiagnostic] Type: '{tagLabel}' | Side: {(isBottom ? \"BOTTOM\" : \"TOP\")} | Intended (Model): {distance:F2} | Intended (Paper): {paper:F2} | Read-Back: {readBack:F2} | Vector: {upDirection.X}, {upDirection.Y}, {upDirection.Z} | Pts: {pointList.Count}\");
            }
			if (set != null)
			{
				try
				{
					set.Distance = paper;
					set.Attributes = attrs;
					set.Modify();
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}'''

# find the method boundary
start_idx = text.find('private void EmitDim(View view, PanelAxis axis')
if start_idx == -1:
    print('EmitDim not found')
else:
    # find the next method
    end_idx = text.find('private void TrySectionView', start_idx)
    # wait, the next method in the file was actually: InsertText or TrySectionView?
    # looking at the previous log, EmitDim ends with a catch. The code after it was inside a different method, but wait:
    # "EmitDim(view, axis, new List<double> { micro[0], micro[1] }, isBottom: false, 18.0);"
    # Wait, my previous output showed EmitDim calling EmitDim!?
    # Let me just replace the exact method body.

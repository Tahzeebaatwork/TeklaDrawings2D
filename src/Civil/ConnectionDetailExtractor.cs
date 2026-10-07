using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>Connection detail: joint type, hardware list, detail-view dimensions.</summary>
    public class ConnectionDetailExtractor
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public ConnectionDetailExtractor(TSModel.Model model, DrawingHandler handler)
        {
            _model = model;
            _handler = handler;
        }

        public ConnectionDetailDocument Extract(Drawing drawing, IList<TSModel.Part> parts, string outputRoot, CivilSheetCapture capture = null)
        {
            var main = CivilDrawingSupport.MainPart(parts);
            string piece = CivilDrawingSupport.PieceMark(main, drawing);
            var doc = new ConnectionDetailDocument
            {
                Header = CivilDrawingSupport.Header(_model, drawing, CivilDrawingTypes.Connection, piece, drawing != null),
            };

            try
            {
                doc.Hardware = CivilDrawingSupport.ReadHardware(parts, piece)
                    .Where(h => h.Kind != "PART").ToList();
                doc.Bom = CivilDrawingSupport.BomFromHardware(doc.Hardware);
                doc.JointType = CivilDrawingSupport.JointType(doc.Hardware, main);
                var other = parts.FirstOrDefault(p => main != null && p.Identifier.ID != main.Identifier.ID);
                if (other != null)
                    doc.ConnectedTo = CivilDrawingSupport.FirstNonEmpty(
                        CivilDrawingSupport.Report(other, "PART_POS"),
                        CivilDrawingSupport.Safe(() => other.Name));
            }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " connection", ex); }

            if (capture != null)
            {
                doc.DetailViews = capture.Views.Count > 0
                    ? capture.Views
                    : new List<CivilViewRef> { new CivilViewRef { Name = "Detail", ViewType = "FrontView" } };
                doc.Dimensions = capture.Dimensions;
            }
            else
            {
                doc.DetailViews = new List<CivilViewRef>
                {
                    new CivilViewRef { Name = "Detail", ViewType = "FrontView" },
                };
            }
            return doc;
        }
    }
}

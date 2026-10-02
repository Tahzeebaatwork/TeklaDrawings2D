using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>Erection drawing: site position/orientation, anchors/embeds, plan/elevation refs, notes.</summary>
    public class ErectionCivilExtractor
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public ErectionCivilExtractor(TSModel.Model model, DrawingHandler handler)
        {
            _model = model;
            _handler = handler;
        }

        public ErectionCivilDocument Extract(Drawing drawing, IList<TSModel.Part> parts, string outputRoot, CivilSheetCapture capture = null)
        {
            var main = CivilDrawingSupport.MainPart(parts);
            string piece = CivilDrawingSupport.PieceMark(main, drawing);
            var doc = new ErectionCivilDocument
            {
                Header = CivilDrawingSupport.Header(_model, drawing, CivilDrawingTypes.Erection, piece, drawing != null),
            };
            try
            {
                if (main != null) doc.Placement = CivilDrawingSupport.Placement(main, piece);
            }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " erection placement", ex); }

            try
            {
                var hardware = CivilDrawingSupport.ReadHardware(parts, piece);
                doc.AnchorsAndEmbeds = hardware.Where(h =>
                    h.Kind == "ANCHOR" || h.Kind == "EMBED" || h.Kind == "INSERT" ||
                    h.Kind == "SLEEVE" || h.Kind == "GROUT_TUBE" || h.Kind == "BOLT" ||
                    h.Kind == "CONDUIT" || h.Kind == "PLATE").ToList();
            }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " erection hardware", ex); }

            if (capture != null && capture.Views.Count > 0)
            {
                doc.ElevationAndPlanRefs = capture.Views;
                doc.Dimensions = capture.Dimensions;
                foreach (var t in capture.Texts)
                {
                    if (!string.IsNullOrWhiteSpace(t) && t.IndexOf("erect", StringComparison.OrdinalIgnoreCase) >= 0)
                        doc.ErectionSequenceNotes.Add(t);
                }
            }
            else
            {
                doc.ElevationAndPlanRefs = new List<CivilViewRef>
                {
                    new CivilViewRef { Name = "Plan", ViewType = "TopView" },
                    new CivilViewRef { Name = "Elevation", ViewType = "FrontView" },
                };
            }

            try
            {
                if (drawing != null)
                {
                    foreach (var t in new[] { drawing.Title1, drawing.Title2, drawing.Title3, drawing.Name })
                    {
                        if (!string.IsNullOrWhiteSpace(t)) doc.ErectionSequenceNotes.Add(t);
                    }
                }
            }
            catch { }

            return doc;
        }
    }
}

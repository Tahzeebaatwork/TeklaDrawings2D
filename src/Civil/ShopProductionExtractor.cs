using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Shop / production piece drawing (Cast Unit / Single Part).
    /// Fields aligned to civil piece tickets (notes, hardware BOM, End1/End2/Side A/B views).
    /// </summary>
    public class ShopProductionExtractor
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public ShopProductionExtractor(TSModel.Model model, DrawingHandler handler)
        {
            _model = model;
            _handler = handler;
        }

        public ShopProductionDocument Extract(Drawing drawing, IList<TSModel.Part> parts, string outputRoot, CivilSheetCapture capture = null)
        {
            var main = CivilDrawingSupport.MainPart(parts);
            string piece = CivilDrawingSupport.PieceMark(main, drawing);
            var doc = new ShopProductionDocument
            {
                Header = CivilDrawingSupport.Header(_model, drawing, CivilDrawingTypes.Shop, piece, drawing != null),
            };

            try { doc.GeneralNotes = CivilDrawingSupport.ReadNotes(main); }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " shop notes", ex); }

            try
            {
                doc.Hardware = CivilDrawingSupport.ReadHardware(parts, piece);
                doc.Bom = CivilDrawingSupport.BomFromHardware(doc.Hardware);
            }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " shop BOM", ex); }

            try
            {
                doc.Rebar = CivilDrawingSupport.ReadRebarAllParts(_model, parts, piece);
            }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " shop rebar", ex); }

            try
            {
                CivilDrawingSupport.EnrichNotesFromSheetAndRebar(doc.GeneralNotes, capture, parts);
            }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " shop notes enrich", ex); }

            try { doc.ProfileFeatures = CivilDrawingSupport.ReadCuts(parts); }
            catch (Exception ex) { CivilDrawingSupport.LogError(outputRoot, piece + " shop cuts", ex); }

            if (capture != null)
            {
                try { CivilDrawingSupport.AddOverallDimensions(capture, main, piece); } catch { }
                doc.Views = capture.Views.Count > 0
                    ? capture.Views
                    : CivilDrawingSupport.EnsureShopViewSlots(new List<CivilViewRef>());
                doc.Dimensions = capture.Dimensions;
                if (capture.Features.Count > 0)
                    doc.ProfileFeatures.AddRange(capture.Features);
                if (capture.Scale > 0)
                    doc.Header.Scale = capture.Scale.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(capture.DrawnBy))
                    doc.Header.DrawnBy = capture.DrawnBy;
                if (!string.IsNullOrWhiteSpace(capture.Error))
                    doc.Header.Error = capture.Error;
            }
            else
            {
                doc.Views = CivilDrawingSupport.EnsureShopViewSlots(new List<CivilViewRef>());
            }

            if (doc.Views.Count == 0)
                doc.Views = CivilDrawingSupport.EnsureShopViewSlots(new List<CivilViewRef>());

            doc.Identification["projectCode"] = doc.Header.ProjectCode;
            doc.Identification["pieceMark"] = piece;
            doc.Identification["drawnBy"] = doc.Header.DrawnBy;
            doc.Identification["date"] = doc.Header.Date;
            doc.Identification["scale"] = doc.Header.Scale;
            doc.Identification["drawingName"] = doc.Header.DrawingName;
            doc.Identification["drawingMark"] = doc.Header.DrawingMark;
            return doc;
        }
    }
}

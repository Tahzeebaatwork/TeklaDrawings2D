using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Rebar / BBS extractor. Reads RebarGroup, SingleRebar, RebarMesh, and TS2026 RebarSet
    /// (including FrozenState: NOT_FROZEN / PARTIALLY_FROZEN / FULLY_FROZEN).
    /// Bend legs DIM_A..O preserved in millimetres. Scans every part in the assembly.
    /// </summary>
    public class RebarBbsExtractor
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public RebarBbsExtractor(TSModel.Model model, DrawingHandler handler)
        {
            _model = model;
            _handler = handler;
        }

        public RebarBbsDocument Extract(Drawing drawing, IList<TSModel.Part> parts, string outputRoot, CivilSheetCapture capture = null)
        {
            var main = CivilDrawingSupport.MainPart(parts);
            string piece = CivilDrawingSupport.PieceMark(main, drawing);
            var doc = new RebarBbsDocument
            {
                Header = CivilDrawingSupport.Header(_model, drawing, CivilDrawingTypes.RebarBbs, piece, drawing != null),
            };

            try
            {
                doc.Rebar = CivilDrawingSupport.ReadRebarAllParts(_model, parts, piece);
            }
            catch (Exception ex)
            {
                CivilDrawingSupport.LogError(outputRoot, piece + " BBS", ex);
            }

            if (doc.Rebar.Count == 0)
                CivilDrawingSupport.LogSkip(outputRoot, "Reinforcement", piece + " had 0 rebar after scanning " + (parts != null ? parts.Count : 0) + " parts");

            doc.BarLineCount = doc.Rebar.Count;
            doc.TotalLengthMm = doc.Rebar.Sum(b => (b.TotalLengthMm ?? 0) * Math.Max(1, b.Quantity));
            doc.TotalWeightKg = doc.Rebar.Sum(b => (b.WeightKg ?? 0) * Math.Max(1, b.Quantity));
            if (doc.TotalLengthMm <= 0) doc.TotalLengthMm = null;
            if (doc.TotalWeightKg <= 0) doc.TotalWeightKg = null;
            if (capture != null && !string.IsNullOrWhiteSpace(capture.Error))
                doc.Header.Error = capture.Error;
            return doc;
        }
    }
}

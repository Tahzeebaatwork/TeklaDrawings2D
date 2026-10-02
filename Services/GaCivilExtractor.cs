using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    /// <summary>GA (General Arrangement): grids, piece XYZ, overall dims, piece list per floor.</summary>
    public class GaCivilExtractor
    {
        private readonly TSModel.Model _model;
        private readonly DrawingHandler _handler;

        public GaCivilExtractor(TSModel.Model model, DrawingHandler handler)
        {
            _model = model;
            _handler = handler;
        }

        public GaCivilDocument Extract(Drawing drawing, string outputRoot)
        {
            var parts = drawing != null
                ? new List<TSModel.Part>()
                : new List<TSModel.Part>();
            var main = CivilDrawingSupport.MainPart(parts);
            string piece = drawing != null
                ? CivilDrawingSupport.FirstNonEmpty(
                    CivilDrawingSupport.StripBrackets(SafeMark(drawing)),
                    CivilDrawingSupport.PieceMark(main, drawing),
                    "GA")
                : "MODEL_GRIDS";
            var doc = new GaCivilDocument
            {
                Header = CivilDrawingSupport.Header(_model, drawing, CivilDrawingTypes.GA, piece, drawing != null),
            };
            doc.GridLines = CivilDrawingSupport.ReadGrids(_model);
            doc.Views = new List<CivilViewRef>
            {
                new CivilViewRef { Name = "Plan", ViewType = "TopView" },
            };

            foreach (var part in parts)
            {
                try
                {
                    if (!CivilDrawingSupport.IsWallColumnBeam(part)) continue;
                    try
                    {
                        if (!CivilDrawingSupport.IsPrecastAssembly(part.GetAssembly())) continue;
                    }
                    catch { continue; }
                    string mark = CivilDrawingSupport.PieceMark(part, drawing);
                    var place = CivilDrawingSupport.Placement(part, mark);
                    doc.Pieces.Add(place);
                    string floor = string.IsNullOrWhiteSpace(place.Floor) ? "Unknown" : place.Floor;
                    if (!doc.PieceListByFloor.TryGetValue(floor, out var list))
                    {
                        list = new List<CivilPiecePlacement>();
                        doc.PieceListByFloor[floor] = list;
                    }
                    list.Add(place);
                }
                catch (Exception ex)
                {
                    CivilDrawingSupport.LogError(outputRoot, "GA part", ex);
                }
            }

            FillOverall(doc);
            return doc;
        }

        public GaCivilDocument ExtractWallColumnBeams(string outputRoot)
        {
            var parts = CivilDrawingSupport.CollectWallColumnBeamMains(_model);
            Console.WriteLine("[GA] WALL/COLUMN/BEAM mains=" + parts.Count);
            var doc = new GaCivilDocument
            {
                Header = CivilDrawingSupport.Header(_model, null, CivilDrawingTypes.GA, "WALL_COLUMN_BEAM", false),
            };
            doc.GridLines = CivilDrawingSupport.ReadGrids(_model);
            doc.Views = new List<CivilViewRef>
            {
                new CivilViewRef { Name = "Plan", ViewType = "TopView" },
            };

            foreach (var part in parts)
            {
                try
                {
                    string mark = CivilDrawingSupport.PieceMark(part, null);
                    var place = CivilDrawingSupport.Placement(part, mark);
                    doc.Pieces.Add(place);
                    string floor = string.IsNullOrWhiteSpace(place.Floor) ? "Unknown" : place.Floor;
                    if (!doc.PieceListByFloor.TryGetValue(floor, out var list))
                    {
                        list = new List<CivilPiecePlacement>();
                        doc.PieceListByFloor[floor] = list;
                    }
                    list.Add(place);
                }
                catch (Exception ex)
                {
                    CivilDrawingSupport.LogError(outputRoot, "GA W/C/B part", ex);
                }
            }

            FillOverall(doc);
            return doc;
        }

        private static void FillOverall(GaCivilDocument doc)
        {
            if (doc == null || doc.Pieces == null || doc.Pieces.Count == 0) return;
            doc.OverallMin = new CivilXyz
            {
                X = doc.Pieces.Min(p => Math.Min(p.StartPoint.X, p.EndPoint.X)),
                Y = doc.Pieces.Min(p => Math.Min(p.StartPoint.Y, p.EndPoint.Y)),
                Z = doc.Pieces.Min(p => Math.Min(p.StartPoint.Z, p.EndPoint.Z)),
            };
            doc.OverallMax = new CivilXyz
            {
                X = doc.Pieces.Max(p => Math.Max(p.StartPoint.X, p.EndPoint.X)),
                Y = doc.Pieces.Max(p => Math.Max(p.StartPoint.Y, p.EndPoint.Y)),
                Z = doc.Pieces.Max(p => Math.Max(p.StartPoint.Z, p.EndPoint.Z)),
            };
            doc.OverallLengthMm = CivilDrawingSupport.Round(doc.OverallMax.X - doc.OverallMin.X, 1);
            doc.OverallWidthMm = CivilDrawingSupport.Round(doc.OverallMax.Y - doc.OverallMin.Y, 1);
        }

        private static string SafeMark(Drawing d)
        {
            try { return d.Mark ?? d.Name ?? ""; } catch { return ""; }
        }
    }
}

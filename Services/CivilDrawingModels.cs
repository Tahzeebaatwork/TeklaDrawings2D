using System;
using System.Collections.Generic;

namespace TeklaExtractor.Services
{
    public static class CivilDrawingTypes
    {
        public const string GA = "GA";
        public const string Erection = "ERECTION";
        public const string Shop = "SHOP";
        public const string Connection = "CONNECTION";
        public const string RebarBbs = "REBAR_BBS";
        public const string Hardware = "HARDWARE";
        public const string RebarPlace = "REBAR_PLACE";
        public const string RebarTable = "REBAR_TABLE";
        public const string Fabricator = "FABRICATOR";
        public const string Single = "SINGLE";
        public const string Assembly = "ASSEMBLY";
        public const string UnitsMm = "mm";

        /// <summary>Macro-driven export folder under Export/CivilDrawings/.</summary>
        public const string MacrosFolder = "new with macros";
        /// <summary>Open API CastUnitDrawing.Insert export folder (no RunMacro).</summary>
        public const string OpenApiFolder = "new without macros";
    }

    public class CivilXyz
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class CivilDrawingHeader
    {
        public string Schema { get; set; } = "tekla-civil-drawing/1.0";
        public string DrawingType { get; set; } = "";
        public string ProjectCode { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string PieceMark { get; set; } = "";
        public string DrawingName { get; set; } = "";
        public string DrawingMark { get; set; } = "";
        public string Title1 { get; set; } = "";
        public string Title2 { get; set; } = "";
        public string Title3 { get; set; } = "";
        public string DrawnBy { get; set; } = "";
        public string Date { get; set; } = "";
        public string Scale { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Units { get; set; } = CivilDrawingTypes.UnitsMm;
        public string TeklaDrawingType { get; set; } = "";
        public bool HasDrawingSheet { get; set; }
        public string Error { get; set; }
        public string GeneratedUtc { get; set; } = "";
    }

    public class CivilPieceNotes
    {
        public string ConcreteGrade { get; set; }
        public string Strength28DayMPa { get; set; }
        public string StrippingStrength { get; set; }
        public double? WeightKg { get; set; }
        public double? WeightLbs { get; set; }
        public string AirEntrainment { get; set; }
        public double? MinCoverMm { get; set; }
        public double? CoverTopMm { get; set; }
        public double? CoverBottomMm { get; set; }
        public double? CoverSideMm { get; set; }
        public string Class { get; set; }
        public double? VolumeMm3 { get; set; }
        public double? VolumeM3 { get; set; }
        public string Material { get; set; }
        public string Profile { get; set; }
        public string Finish { get; set; }
        public string Name { get; set; }
    }

    public class CivilBomRow
    {
        public string PieceMark { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Description { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public int Quantity { get; set; }
        public double? LengthMm { get; set; }
        public double? WeightKg { get; set; }
        public string Guid { get; set; } = "";
        public int Id { get; set; }
    }

    public class CivilBbsBar
    {
        public string PieceMark { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Size { get; set; } = "";
        public string Grade { get; set; } = "";
        public string ShapeType { get; set; } = "";
        public string ShapeCode { get; set; } = "";
        public string SourceType { get; set; } = "";
        public string FrozenState { get; set; }
        public int Quantity { get; set; }
        public double? DiameterMm { get; set; }
        public double? TotalLengthMm { get; set; }
        public double? TotalAreaMm2 { get; set; }
        public double? WeightKg { get; set; }
        public double? PinRadiusMm { get; set; }
        public string Spacing { get; set; } = "";
        public double? A { get; set; }
        public double? B { get; set; }
        public double? C { get; set; }
        public double? D { get; set; }
        public double? E { get; set; }
        public double? F { get; set; }
        public double? G { get; set; }
        public double? H { get; set; }
        public double? H2 { get; set; }
        public double? J { get; set; }
        public double? K { get; set; }
        public double? K2 { get; set; }
        public double? O { get; set; }
        public List<CivilXyz> Geometry { get; set; } = new List<CivilXyz>();
        public string Guid { get; set; } = "";
    }

    public class CivilDimensionRow
    {
        public string PieceMark { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public double? Value { get; set; }
        public CivilXyz StartPoint { get; set; }
        public CivilXyz EndPoint { get; set; }
        public string ViewName { get; set; } = "";
    }

    public class CivilHardwareRow
    {
        public string PieceMark { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Description { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public CivilXyz Position { get; set; }
        public string Guid { get; set; } = "";
        public int Id { get; set; }
    }

    public class CivilGridLine
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public string Axis { get; set; } = "";
        public CivilXyz StartPoint { get; set; } = new CivilXyz();
        public CivilXyz EndPoint { get; set; } = new CivilXyz();
        public double? SpacingMm { get; set; }
    }

    public class CivilPiecePlacement
    {
        public string PieceMark { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Floor { get; set; } = "";
        public string Grid { get; set; } = "";
        public CivilXyz Position { get; set; } = new CivilXyz();
        public CivilXyz StartPoint { get; set; } = new CivilXyz();
        public CivilXyz EndPoint { get; set; } = new CivilXyz();
        public CivilXyz AxisX { get; set; }
        public CivilXyz AxisY { get; set; }
        public CivilXyz AxisZ { get; set; }
        public double LengthMm { get; set; }
        public int Id { get; set; }
        public string Guid { get; set; } = "";
    }

    public class CivilViewRef
    {
        public string Name { get; set; } = "";
        public string ViewType { get; set; } = "";
        public double Scale { get; set; }
        public string Notes { get; set; } = "";
        public List<string> PluginInputs { get; set; } = new List<string>();
    }

    public class GaCivilDocument
    {
        public CivilDrawingHeader Header { get; set; } = new CivilDrawingHeader();
        public List<CivilGridLine> GridLines { get; set; } = new List<CivilGridLine>();
        public CivilXyz OverallMin { get; set; }
        public CivilXyz OverallMax { get; set; }
        public double? OverallLengthMm { get; set; }
        public double? OverallWidthMm { get; set; }
        public Dictionary<string, List<CivilPiecePlacement>> PieceListByFloor { get; set; }
            = new Dictionary<string, List<CivilPiecePlacement>>(StringComparer.OrdinalIgnoreCase);
        public List<CivilPiecePlacement> Pieces { get; set; } = new List<CivilPiecePlacement>();
        public List<CivilDimensionRow> Dimensions { get; set; } = new List<CivilDimensionRow>();
        public List<CivilViewRef> Views { get; set; } = new List<CivilViewRef>();
    }

    public class ErectionCivilDocument
    {
        public CivilDrawingHeader Header { get; set; } = new CivilDrawingHeader();
        public CivilPiecePlacement Placement { get; set; } = new CivilPiecePlacement();
        public List<CivilHardwareRow> AnchorsAndEmbeds { get; set; } = new List<CivilHardwareRow>();
        public List<CivilViewRef> ElevationAndPlanRefs { get; set; } = new List<CivilViewRef>();
        public List<string> ErectionSequenceNotes { get; set; } = new List<string>();
        public List<CivilDimensionRow> Dimensions { get; set; } = new List<CivilDimensionRow>();
    }

    public class ShopProductionDocument
    {
        public CivilDrawingHeader Header { get; set; } = new CivilDrawingHeader();
        public CivilPieceNotes GeneralNotes { get; set; } = new CivilPieceNotes();
        public List<CivilBomRow> Bom { get; set; } = new List<CivilBomRow>();
        public List<CivilHardwareRow> Hardware { get; set; } = new List<CivilHardwareRow>();
        public List<CivilBbsBar> Rebar { get; set; } = new List<CivilBbsBar>();
        public List<CivilDimensionRow> Dimensions { get; set; } = new List<CivilDimensionRow>();
        public List<CivilViewRef> Views { get; set; } = new List<CivilViewRef>();
        public List<CivilProfileFeature> ProfileFeatures { get; set; } = new List<CivilProfileFeature>();
        public Dictionary<string, string> Identification { get; set; } = new Dictionary<string, string>();
    }

    public class CivilProfileFeature
    {
        public string Kind { get; set; } = "";
        public string Source { get; set; } = "";
        public string ViewName { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public class CivilSheetCapture
    {
        public bool Opened { get; set; }
        public double Scale { get; set; }
        public string DrawnBy { get; set; } = "";
        public List<CivilViewRef> Views { get; set; } = new List<CivilViewRef>();
        public List<CivilDimensionRow> Dimensions { get; set; } = new List<CivilDimensionRow>();
        public List<string> Texts { get; set; } = new List<string>();
        public List<CivilProfileFeature> Features { get; set; } = new List<CivilProfileFeature>();
        public List<IdentifierRef> SheetModelObjects { get; set; } = new List<IdentifierRef>();
        public string Error { get; set; }
    }

    public class IdentifierRef
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
    }

    public class ConnectionDetailDocument
    {
        public CivilDrawingHeader Header { get; set; } = new CivilDrawingHeader();
        public string JointType { get; set; } = "";
        public string ConnectedTo { get; set; } = "";
        public List<CivilHardwareRow> Hardware { get; set; } = new List<CivilHardwareRow>();
        public List<CivilBomRow> Bom { get; set; } = new List<CivilBomRow>();
        public List<CivilDimensionRow> Dimensions { get; set; } = new List<CivilDimensionRow>();
        public List<CivilViewRef> DetailViews { get; set; } = new List<CivilViewRef>();
    }

    public class RebarBbsDocument
    {
        public CivilDrawingHeader Header { get; set; } = new CivilDrawingHeader();
        public List<CivilBbsBar> Rebar { get; set; } = new List<CivilBbsBar>();
        public double? TotalLengthMm { get; set; }
        public double? TotalWeightKg { get; set; }
        public int BarLineCount { get; set; }
    }
}

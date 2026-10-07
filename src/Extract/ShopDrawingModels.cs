using System;
using System.Collections.Generic;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Precast piece-ticket JSON:
    /// Drawing → Sheet → TitleBlock / Tables / Views[]
    ///   View → CoordinateSystems, Parts, Openings, Reinforcement,
    ///          Hardware, Bolts, Welds, Dimensions, Marks, Texts,
    ///          SectionSymbols, GraphicObjects
    /// Contours are stored in model, view and sheet space.
    /// </summary>
    public class ShopDrawing
    {
        public string Schema { get; set; } = "tekla-cast-unit-shop-drawing/1.0";
        public string Kind { get; set; } = "PrecastPieceTicket";
        public string ModelName { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public string DrawingType { get; set; } = "CastUnit";
        public string TeklaDrawingType { get; set; } = "";
        public string DrawingMark { get; set; } = "";
        public string CastUnitMark { get; set; } = "";
        public string DrawingName { get; set; } = "";
        public string Title1 { get; set; } = "";
        public string Title2 { get; set; } = "";
        public string Title3 { get; set; } = "";
        public string Revision { get; set; } = "";
        public string FloorLevel { get; set; } = "";
        public string DrawingUnits { get; set; } = "mm";
        public double DrawingScale { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime ModificationDate { get; set; }
        public DateTime IssuingDate { get; set; }
        public bool IsIssued { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsLocked { get; set; }
        public string UpToDateStatus { get; set; } = "";
        public int LinkedCastUnitId { get; set; }
        public string LinkedCastUnitGuid { get; set; } = "";
        public Dictionary<string, string> UDAs { get; set; } = new Dictionary<string, string>();
        public ShopSheet Sheet { get; set; } = new ShopSheet();
        public ShopOutputs Outputs { get; set; } = new ShopOutputs();
        public ShopValidation Validation { get; set; } = new ShopValidation();
    }

    public class ShopOutputs
    {
        public string JsonPath { get; set; } = "";
        public string SvgPath { get; set; } = "";
        public string DxfPath { get; set; } = "";
        public string PdfPath { get; set; } = "";
        public string TxtPath { get; set; } = "";
        public string HtmlPath { get; set; } = "";
        public string ValidationPath { get; set; } = "";
    }

    public class ShopSheet
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public string Units { get; set; } = "mm";
        public ShopTitleBlock TitleBlock { get; set; } = new ShopTitleBlock();
        public List<ShopTable> Tables { get; set; } = new List<ShopTable>();
        public List<ShopView> Views { get; set; } = new List<ShopView>();
    }

    public class ShopTitleBlock
    {
        public string PieceMark { get; set; } = "";
        public string Title { get; set; } = "";
        public string Title1 { get; set; } = "";
        public string Title2 { get; set; } = "";
        public string Title3 { get; set; } = "";
        public string Revision { get; set; } = "";
        public string FloorLevel { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string DrawingStatus { get; set; } = "";
        public List<ShopText> Cells { get; set; } = new List<ShopText>();
    }

    public class ShopTable
    {
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public List<ShopBomRow> Rows { get; set; } = new List<ShopBomRow>();
        public List<ShopText> Notes { get; set; } = new List<ShopText>();
    }

    public class ShopBomRow
    {
        public string PieceMark { get; set; } = "";
        public string PartMark { get; set; } = "";
        public string Description { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public int Quantity { get; set; }
        public double Length { get; set; }
        public double Weight { get; set; }
        public string HardwareCode { get; set; } = "";
        public List<string> Guids { get; set; } = new List<string>();
    }

    public class ShopView
    {
        public string Name { get; set; } = "";
        public string ViewType { get; set; } = "";
        public string SectionIdentifier { get; set; } = "";
        public double Scale { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double RotationDeg { get; set; }
        public double[] Origin { get; set; }
        public double[] FrameOrigin { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public double[] RestrictionBoxMin { get; set; }
        public double[] RestrictionBoxMax { get; set; }
        public double[] SectionCutStart { get; set; }
        public double[] SectionCutEnd { get; set; }
        public double[] SectionDirection { get; set; }
        public double SectionDepth { get; set; }
        public ShopCoordinateSystems CoordinateSystems { get; set; } = new ShopCoordinateSystems();
        public List<ShopPart> Parts { get; set; } = new List<ShopPart>();
        public List<ShopOpening> Openings { get; set; } = new List<ShopOpening>();
        public List<ShopRebar> Reinforcement { get; set; } = new List<ShopRebar>();
        public List<ShopHardware> Hardware { get; set; } = new List<ShopHardware>();
        public List<ShopBolt> Bolts { get; set; } = new List<ShopBolt>();
        public List<ShopWeld> Welds { get; set; } = new List<ShopWeld>();
        public List<ShopDimension> Dimensions { get; set; } = new List<ShopDimension>();
        public List<ShopMark> Marks { get; set; } = new List<ShopMark>();
        public List<ShopText> Texts { get; set; } = new List<ShopText>();
        public List<ShopSectionSymbol> SectionSymbols { get; set; } = new List<ShopSectionSymbol>();
        public List<ShopGraphic> GraphicObjects { get; set; } = new List<ShopGraphic>();
        public int DrawableCount { get; set; }
    }

    public class ShopCoordinateSystems
    {
        public double[] ModelOrigin { get; set; }
        public double[] ModelAxisX { get; set; }
        public double[] ModelAxisY { get; set; }
        public double[] ModelAxisZ { get; set; }
        public double[] DisplayOrigin { get; set; }
        public double[] DisplayAxisX { get; set; }
        public double[] DisplayAxisY { get; set; }
        public double[] LocalX { get; set; }
        public double[] LocalY { get; set; }
        public double[] LocalZ { get; set; }
        /// <summary>4x4 row-major: model → view.</summary>
        public double[] ModelToView { get; set; }
        /// <summary>4x4 row-major: view → sheet (scale + origin).</summary>
        public double[] ViewToSheet { get; set; }
    }

    public class ShopPart
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string PartMark { get; set; } = "";
        public string AssemblyMark { get; set; } = "";
        public string CastUnitMark { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Class { get; set; } = "";
        public string PositionNumber { get; set; } = "";
        public string Finish { get; set; } = "";
        public string TeklaType { get; set; } = "";
        public string Role { get; set; } = "Concrete";
        public double Length { get; set; }
        public double Weight { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public ShopContour OuterContour { get; set; }
        public List<ShopContour> InnerContours { get; set; } = new List<ShopContour>();
        public List<ShopContour> HiddenEdges { get; set; } = new List<ShopContour>();
        public List<ShopContour> CenterLines { get; set; } = new List<ShopContour>();
        public List<ShopArc> Arcs { get; set; } = new List<ShopArc>();
        public string HatchPattern { get; set; } = "";
    }

    public class ShopOpening
    {
        public int OpeningId { get; set; }
        public string Guid { get; set; } = "";
        public string OpeningType { get; set; } = "";
        public string ParentCastUnitGuid { get; set; } = "";
        public bool ThroughOpening { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double OffsetLeft { get; set; }
        public double OffsetRight { get; set; }
        public double OffsetBottom { get; set; }
        public double OffsetTop { get; set; }
        public ShopContour OuterContour { get; set; }
        public double[] CenterModel { get; set; }
        public double[] CenterView { get; set; }
        public double[] CenterSheet { get; set; }
    }

    public class ShopRebar
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string RebarMark { get; set; } = "";
        public string Name { get; set; } = "";
        public string Size { get; set; } = "";
        public double Diameter { get; set; }
        public string Grade { get; set; } = "";
        public string Shape { get; set; } = "";
        public int NumberOfBars { get; set; }
        public double Length { get; set; }
        public string Spacing { get; set; } = "";
        public double[] DistributionDirection { get; set; }
        public double Cover { get; set; }
        public string Layer { get; set; } = "";
        public string Face { get; set; } = "";
        public List<double[]> Polyline3d { get; set; } = new List<double[]>();
        public List<double[]> Polyline2dView { get; set; } = new List<double[]>();
        public List<double[]> Polyline2dSheet { get; set; } = new List<double[]>();
        public List<double[]> BendingPoints { get; set; } = new List<double[]>();
        public List<List<double[]>> IndividualBars { get; set; } = new List<List<double[]>>();
        public ShopHook StartHook { get; set; }
        public ShopHook EndHook { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
    }

    public class ShopHook
    {
        public double Angle { get; set; }
        public double Radius { get; set; }
        public double Length { get; set; }
    }

    public class ShopHardware
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string HardwareCode { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Description { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Material { get; set; } = "";
        public string Class { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public double[] CenterModel { get; set; }
        public double[] CenterView { get; set; }
        public double[] CenterSheet { get; set; }
        public double[] AxisX { get; set; }
        public double[] AxisY { get; set; }
        public ShopContour VisibleContour { get; set; }
    }

    public class ShopBolt
    {
        public int Id { get; set; }
        public string Guid { get; set; } = "";
        public string Standard { get; set; } = "";
        public double Size { get; set; }
        public double[] CenterView { get; set; }
        public double[] CenterSheet { get; set; }
    }

    public class ShopWeld
    {
        public int Id { get; set; }
        public string Type { get; set; } = "";
        public double Size { get; set; }
        public bool IsShopWeld { get; set; }
        public double[] PositionView { get; set; }
        public double[] PositionSheet { get; set; }
    }

    public class ShopDimension
    {
        public string DimensionType { get; set; } = "";
        public string DisplayedValue { get; set; } = "";
        public double MeasuredValue { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public List<double[]> IntermediatePoints { get; set; } = new List<double[]>();
        public double[] DimensionLinePosition { get; set; }
        public double[] ExtensionStart { get; set; }
        public double[] ExtensionEnd { get; set; }
        public double[] TextPosition { get; set; }
        public double[] Direction { get; set; }
        public double Offset { get; set; }
        public string Prefix { get; set; } = "";
        public string Postfix { get; set; } = "";
        public string Tolerance { get; set; } = "";
        public string Units { get; set; } = "";
        public string Precision { get; set; } = "";
        public string Format { get; set; } = "";
        public string ViewName { get; set; } = "";
        public List<int> AssociatedModelIds { get; set; } = new List<int>();
        public double[] StartSheet { get; set; }
        public double[] EndSheet { get; set; }
        public double[] TextSheet { get; set; }
    }

    public class ShopMark
    {
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public double[] InsertionPoint { get; set; }
        public double[] InsertionSheet { get; set; }
        public double Rotation { get; set; }
        public double FontSize { get; set; }
        public string Alignment { get; set; } = "";
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public int AssociatedId { get; set; }
        public string AssociatedGuid { get; set; } = "";
        public List<double[]> LeaderVertices { get; set; } = new List<double[]>();
        public string Arrowhead { get; set; } = "";
    }

    public class ShopText
    {
        public string Text { get; set; } = "";
        public double[] InsertionPoint { get; set; }
        public double[] InsertionSheet { get; set; }
        public double Rotation { get; set; }
        public double FontSize { get; set; }
        public string Alignment { get; set; } = "";
    }

    public class ShopSectionSymbol
    {
        public string Identifier { get; set; } = "";
        public double[] LeftPoint { get; set; }
        public double[] RightPoint { get; set; }
        public double[] LabelPoint { get; set; }
    }

    public class ShopGraphic
    {
        public string Kind { get; set; } = "";
        public string Layer { get; set; } = "drawing";
        public List<double[]> PointsView { get; set; } = new List<double[]>();
        public List<double[]> PointsSheet { get; set; } = new List<double[]>();
        public double Radius { get; set; }
        public bool Hidden { get; set; }
        public bool Closed { get; set; }
    }

    public class ShopContour
    {
        public string Kind { get; set; } = "Outer";
        public bool Closed { get; set; } = true;
        public List<double[]> Model { get; set; } = new List<double[]>();
        public List<double[]> View { get; set; } = new List<double[]>();
        public List<double[]> Sheet { get; set; } = new List<double[]>();
    }

    public class ShopArc
    {
        public double[] CenterView { get; set; }
        public double[] CenterSheet { get; set; }
        public double Radius { get; set; }
        public double StartAngle { get; set; }
        public double EndAngle { get; set; }
    }

    public class ShopValidation
    {
        public bool Passed { get; set; }
        public string Status { get; set; } = "";
        public int ViewCount { get; set; }
        public int ViewsWithGeometry { get; set; }
        public int ViewsFailedEmpty { get; set; }
        public int PartContourCount { get; set; }
        public int GraphicObjectCount { get; set; }
        public int DimensionCount { get; set; }
        public int MarkCount { get; set; }
        public int OpeningCount { get; set; }
        public int RebarCount { get; set; }
        public int HardwareCount { get; set; }
        public int PdfPageCount { get; set; }
        public int PdfWordCount { get; set; }
        public int ExtractedTextCount { get; set; }
        public List<string> Missing { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    public class ShopDrawingIndex
    {
        public string ModelName { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public string OutputRoot { get; set; } = "";
        public int DrawingCount { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public List<ShopDrawingIndexItem> Drawings { get; set; } = new List<ShopDrawingIndexItem>();
    }

    public class ShopDrawingIndexItem
    {
        public string Mark { get; set; } = "";
        public string CastUnitMark { get; set; } = "";
        public bool Passed { get; set; }
        public string Status { get; set; } = "";
        public string JsonPath { get; set; } = "";
        public string SvgPath { get; set; } = "";
        public string HtmlPath { get; set; } = "";
        public string PdfPath { get; set; } = "";
        public int Views { get; set; }
        public int PartContours { get; set; }
        public int Graphics { get; set; }
    }
}

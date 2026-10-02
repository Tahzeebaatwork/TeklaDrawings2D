using System;
using System.Collections.Generic;

namespace TeklaExtractor.Services
{
    public class DrawingDumpIndex
    {
        public string ModelName { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public Dictionary<string, int> CountsByType { get; set; } = new Dictionary<string, int>();
        public int PdfCount { get; set; }
        public List<DrawingDumpSummary> Drawings { get; set; } = new List<DrawingDumpSummary>();
    }

    public class DrawingDumpSummary
    {
        public string Type { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Name { get; set; } = "";
        public string JsonPath { get; set; } = "";
        public string ReadablePath { get; set; } = "";
        public string PdfPath { get; set; } = "";
        public int ViewCount { get; set; }
        public int ObjectCount { get; set; }
        public int DimensionCount { get; set; }
        public int PdfTextCount { get; set; }
    }

    public class DrawingSheetDump
    {
        public string Type { get; set; } = "";
        public string TeklaDrawingType { get; set; } = "";
        public string Mark { get; set; } = "";
        public string Name { get; set; } = "";
        public string Title1 { get; set; } = "";
        public string Title2 { get; set; } = "";
        public string Title3 { get; set; } = "";
        public string UpToDateStatus { get; set; } = "";
        public DateTime CreationDate { get; set; }
        public DateTime ModificationDate { get; set; }
        public string LinkedModelObject { get; set; } = "";
        public int LinkedModelId { get; set; }
        public string LinkedModelGuid { get; set; } = "";
        public double SheetWidth { get; set; }
        public double SheetHeight { get; set; }
        public double[] SheetOrigin { get; set; } = new double[3];
        public double[] SheetSize { get; set; } = new double[2];
        public List<DrawingViewDump> Views { get; set; } = new List<DrawingViewDump>();
        public List<DrawingObjectDump> SheetObjects { get; set; } = new List<DrawingObjectDump>();
        public PdfReadableDump Pdf { get; set; }
        public string HumanReadable { get; set; } = "";
    }

    public class DrawingViewDump
    {
        public string Name { get; set; } = "";
        public string ViewType { get; set; } = "";
        public double Scale { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double[] Origin { get; set; } = new double[3];
        public double[] FrameOrigin { get; set; } = new double[3];
        public double[] ExtremaCenter { get; set; } = new double[3];
        public double[] ViewCsOrigin { get; set; } = new double[3];
        public double[] ViewCsX { get; set; } = new double[3];
        public double[] ViewCsY { get; set; } = new double[3];
        public double[] DisplayCsOrigin { get; set; } = new double[3];
        public double[] DisplayCsX { get; set; } = new double[3];
        public double[] DisplayCsY { get; set; } = new double[3];
        public double[] RestrictionBoxMin { get; set; } = new double[3];
        public double[] RestrictionBoxMax { get; set; } = new double[3];
        public bool Unfolded { get; set; }
        public bool Undeformed { get; set; }
        public List<DrawingObjectDump> Objects { get; set; } = new List<DrawingObjectDump>();
    }

    public class DrawingObjectDump
    {
        public string Kind { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Text { get; set; } = "";
        public double[] InsertionPoint { get; set; }
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] CenterPoint { get; set; }
        public double[] Point1 { get; set; }
        public double[] Point2 { get; set; }
        public double[] Point3 { get; set; }
        public List<double[]> Points { get; set; }
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
        public double Distance { get; set; }
        public double Radius { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Angle { get; set; }
        public int ModelObjectId { get; set; }
        public string ModelObjectGuid { get; set; } = "";
        public bool Hidden { get; set; }
        public Dictionary<string, string> Extra { get; set; }
    }

    public class PdfReadableDump
    {
        public string PdfPath { get; set; } = "";
        public int PageCount { get; set; }
        public string FullText { get; set; } = "";
        public List<PdfPageDump> Pages { get; set; } = new List<PdfPageDump>();
    }

    public class PdfPageDump
    {
        public int PageNumber { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Text { get; set; } = "";
        public List<PdfTextItem> Words { get; set; } = new List<PdfTextItem>();
        public List<PdfTextLine> Lines { get; set; } = new List<PdfTextLine>();
    }

    public class PdfTextItem
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double FontSize { get; set; }
    }

    public class PdfTextLine
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}

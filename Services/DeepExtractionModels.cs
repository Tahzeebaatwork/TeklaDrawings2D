using System.Collections.Generic;

namespace TeklaExtractor.Services
{
    public class DeepExtractionResult
    {
        public string ModelName { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public int SteelCount { get; set; }
        public int PrecastCount { get; set; }
        public int RccCount { get; set; }
        public List<FabricationAssembly> Steel { get; set; } = new List<FabricationAssembly>();
        public List<CastUnitRecord> Precast { get; set; } = new List<CastUnitRecord>();
        public List<RccElement> Rcc { get; set; } = new List<RccElement>();
    }

    public class LocalGeometry
    {
        public double[] LocalDatumPoint_0_0_0 { get; set; } = new double[3];
        public double[] LocalVector_X { get; set; } = new double[] { 1, 0, 0 };
        public double[] LocalVector_Y { get; set; } = new double[] { 0, 1, 0 };
        public double[] COG_Local { get; set; } = new double[3];
        public BoundingBoxRecord BoundingBox { get; set; } = new BoundingBoxRecord();
        public int SolidEdgeCount { get; set; }
        public bool SolidEdgesTruncated { get; set; }
        public List<SolidEdgeRecord> SolidEdges { get; set; } = new List<SolidEdgeRecord>();
    }

    public class BoundingBoxRecord
    {
        public double[] Min { get; set; } = new double[3];
        public double[] Max { get; set; } = new double[3];
    }

    public class SolidEdgeRecord
    {
        public double[] StartLocal { get; set; } = new double[3];
        public double[] EndLocal { get; set; } = new double[3];
        public double Length { get; set; }
    }

    public class FabricationAssembly
    {
        public string AssemblyMark { get; set; } = "";
        public string AssemblyType { get; set; } = "STEEL_ASSEMBLY";
        public int AssemblyId { get; set; }
        public MainPartRecord MainPart { get; set; } = new MainPartRecord();
        public LocalGeometry Geometry { get; set; } = new LocalGeometry();
        public FabricationFeatures Features { get; set; } = new FabricationFeatures();
        public List<SecondaryPartRecord> SecondaryParts { get; set; } = new List<SecondaryPartRecord>();
        public bool NeedsDetailView { get; set; }
    }

    public class MainPartRecord
    {
        public string GUID { get; set; } = "";
        public int Id { get; set; }
        public string Mark { get; set; } = "";
        public string Profile { get; set; } = "";
        public string MaterialGrade { get; set; } = "";
        public double Length { get; set; }
        public double WeightKg { get; set; }
    }

    public class FabricationFeatures
    {
        public List<BoltHoleRecord> BoltHoles { get; set; } = new List<BoltHoleRecord>();
        public List<WeldRecord> Welds { get; set; } = new List<WeldRecord>();
        public List<CutFittingRecord> CutsAndFittings { get; set; } = new List<CutFittingRecord>();
    }

    public class BoltHoleRecord
    {
        public string Type { get; set; } = "Round";
        public double Diameter { get; set; }
        public double BoltSize { get; set; }
        public double Tolerance { get; set; }
        public double LocalCenterX { get; set; }
        public double LocalCenterY { get; set; }
        public double LocalCenterZ { get; set; }
        public double EdgeDistanceX { get; set; }
        public double EdgeDistanceY { get; set; }
        public double SlottedHoleX { get; set; }
        public double SlottedHoleY { get; set; }
    }

    public class CutFittingRecord
    {
        public string Type { get; set; } = "";
        public string Profile { get; set; } = "";
        public double Length { get; set; }
        public double Depth { get; set; }
        public double Width { get; set; }
        public double LocalCenterX { get; set; }
        public double LocalCenterY { get; set; }
        public double LocalCenterZ { get; set; }
    }

    public class SecondaryPartRecord
    {
        public string Mark { get; set; } = "";
        public string GUID { get; set; } = "";
        public string Profile { get; set; } = "";
        public string MaterialGrade { get; set; } = "";
        public double[] LocalInsertPoint { get; set; } = new double[3];
        public List<WeldRecord> Welds { get; set; } = new List<WeldRecord>();
    }

    public class WeldRecord
    {
        public string Type { get; set; } = "";
        public double Size { get; set; }
        public double SizeAbove { get; set; }
        public double SizeBelow { get; set; }
        public bool IsSiteWeld { get; set; }
        public bool ShopWeld { get; set; }
    }

    public class CastUnitRecord
    {
        public string CUMark { get; set; } = "";
        public int AssemblyId { get; set; }
        public string MainPartProfile { get; set; } = "";
        public string ConcreteGrade { get; set; } = "";
        public double Volume_m3 { get; set; }
        public double WeightKg { get; set; }
        public CastUnitGeometry Geometry { get; set; } = new CastUnitGeometry();
        public List<EmbedRecord> Embeds { get; set; } = new List<EmbedRecord>();
        public List<CutFittingRecord> CutsAndFittings { get; set; } = new List<CutFittingRecord>();
        public List<SolidEdgeRecord> SolidEdges { get; set; } = new List<SolidEdgeRecord>();
        public Dictionary<string, string> UDAs { get; set; } = new Dictionary<string, string>();
        public bool NeedsDetailView { get; set; }
    }

    public class CastUnitGeometry
    {
        public double[] LocalDatumPoint_0_0_0 { get; set; } = new double[3];
        public double[] LocalVector_X { get; set; } = new double[] { 1, 0, 0 };
        public double[] LocalVector_Y { get; set; } = new double[] { 0, 1, 0 };
        public double[] COG_Local { get; set; } = new double[3];
        public BoundingBoxRecord BoundingBox { get; set; } = new BoundingBoxRecord();
        public bool HasFormlinerFinish { get; set; }
        public int SolidEdgeCount { get; set; }
    }

    public class EmbedRecord
    {
        public string GUID { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public double[] LocalInsertPoint { get; set; } = new double[3];
        public bool IsSkewed { get; set; }
    }

    public class RccElement
    {
        public string ElementMark { get; set; } = "";
        public string ElementType { get; set; } = "";
        public int PourPhase { get; set; }
        public int AssemblyId { get; set; }
        public string Profile { get; set; } = "";
        public string ConcreteGrade { get; set; } = "";
        public CoverRecord ConcreteCover { get; set; } = new CoverRecord();
        public LocalGeometry Geometry { get; set; } = new LocalGeometry();
        public List<RebarRecord> Reinforcement { get; set; } = new List<RebarRecord>();
        public bool NeedsDetailView { get; set; }
    }

    public class CoverRecord
    {
        public double Top { get; set; }
        public double Bottom { get; set; }
        public double Sides { get; set; }
    }

    public class RebarRecord
    {
        public string RebarGroupGUID { get; set; } = "";
        public string Name { get; set; } = "";
        public string Size { get; set; } = "";
        public string Grade { get; set; } = "";
        public string ShapeCode { get; set; } = "";
        public double TotalLength { get; set; }
        public List<double> LegDimensions { get; set; } = new List<double>();
        public string SpacingList { get; set; } = "";
        public string SpacingZone { get; set; } = "";
        public int BarCount { get; set; }
        public RebarHooks Hooks { get; set; } = new RebarHooks();
    }

    public class RebarHooks
    {
        public HookRecord StartHook { get; set; } = new HookRecord();
        public HookRecord EndHook { get; set; } = new HookRecord();
    }

    public class HookRecord
    {
        public double Angle { get; set; }
        public double Radius { get; set; }
        public double Length { get; set; }
    }
}

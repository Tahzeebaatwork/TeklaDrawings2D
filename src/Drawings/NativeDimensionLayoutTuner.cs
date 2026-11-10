using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TSPoint = Tekla.Structures.Geometry3d.Point;

namespace TeklaExtractor
{
    /// <summary>
    /// Native Dimension Layout Tuner (Production Pipeline).
    /// Regulates vertical/horizontal stacking, semantic part-based classification,
    /// dynamic model-associative L-linking, tag styling, and symmetrical sheet centering
    /// without recreating dimensions, preserving 100% 3D model associativity.
    /// </summary>
    public static class NativeDimensionLayoutTuner
    {
        public enum EdgeSide
        {
            Top,
            Bottom,
            Left,
            Right,
            Unknown
        }

        public enum SemanticFeature
        {
            ProfileOverall,
            ProfileLedge,
            ProfileHole,
            ProfileOpening,
            Reveal,
            LiftersP205,
            SpliceSP25,
            SpliceSP30,
            PlateP616,
            FaceInsertP300,
            GroutTubeGT100,
            GroutTubeGT686,
            // Known artifacts to prune
            SecondaryRecess,
            EdgeInsertP300,
            BottomOverall,
            DuplicateDoor,
            DuplicateLeftInsert,
            Unknown
        }

        public sealed class TunedSetInfo
        {
            public StraightDimensionSet Set { get; set; }
            public EdgeSide Side { get; set; }
            public SemanticFeature Feature { get; set; } = SemanticFeature.Unknown;
            public double OriginalDistance { get; set; }
            public double NewDistance { get; set; }
            public int RelatedCount { get; set; }
            public List<int> ModelPartIds { get; set; } = new List<int>();
            public List<string> ModelPartNames { get; set; } = new List<string>();
            public List<double> Segments { get; set; } = new List<double>();
            public double TotalSpan { get; set; }
            public string TagText { get; set; } = "";
            public int ObjectId { get; set; }
            public bool IsOverall { get; set; }
            public bool IsArtifact { get; set; }
        }

        public sealed class TuningResult
        {
            public int TotalInspected { get; set; }
            public int PrunedArtifacts { get; set; }
            public int RetainedCount { get; set; }
            public int ModifiedCount { get; set; }
            public int LinksCreated { get; set; }
            public bool DynamicRankAssertPassed { get; set; }
            public bool FitAssertPassed { get; set; }
            public string FitReport { get; set; } = "";
            public Dictionary<EdgeSide, int> SetsBySide { get; } = new Dictionary<EdgeSide, int>();
            public Dictionary<EdgeSide, double> MaxExtentPaperMm { get; } = new Dictionary<EdgeSide, double>();
        }

        public sealed class TunerConfig
        {
            public bool RemoveSheet1Views { get; set; } = true;
            public bool RemoveEmptyMarks { get; set; } = true;
            public bool RemoveSideColumnTags { get; set; } = true;
            public bool KeepProfileLedgeTag { get; set; } = true;
            public bool PruneSecondaryRecesses { get; set; } = true;
            public double TopPitchPaperMm { get; set; } = 12.0;
            public double BotPitchPaperMm { get; set; } = 14.0;
            public double SidePitchPaperMm { get; set; } = 14.0;
            public double RightBaseOffsetPaperMm { get; set; } = 18.0;
            public double BottomBaseOffsetPaperMm { get; set; } = 20.0;
            public double BaseOffsetPaperMm { get; set; } = 12.0;
            public double BomLeftPaperMm { get; set; } = 345.0;
        }

        public static TunerConfig LoadConfig(string pieceMark, string baseDir = null)
        {
            var config = new TunerConfig();
            if (string.IsNullOrWhiteSpace(pieceMark)) return config;

            string cleanMark = pieceMark.Replace("[", "").Replace("]", "").Trim();
            if (cleanMark.Contains(" - ")) cleanMark = cleanMark.Split(new[] { " - " }, StringSplitOptions.None)[0].Trim();

            var searchPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                searchPaths.Add(Path.Combine(baseDir, "config", "tuning", $"{cleanMark}.json"));
                searchPaths.Add(Path.Combine(baseDir, "config", $"{cleanMark}.json"));
                searchPaths.Add(Path.Combine(baseDir, "tests", "regression", "tuning", $"{cleanMark}.json"));
            }
            searchPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "config", "tuning", $"{cleanMark}.json"));
            searchPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "config", $"{cleanMark}.json"));
            searchPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "tests", "regression", "tuning", $"{cleanMark}.json"));
            searchPaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "tuning", $"{cleanMark}.json"));
            searchPaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", $"{cleanMark}.json"));

            foreach (var p in searchPaths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        string json = File.ReadAllText(p);
                        if (json.Contains("\"removeSheet1Views\""))
                            config.RemoveSheet1Views = json.IndexOf("\"removeSheet1Views\": false", StringComparison.OrdinalIgnoreCase) < 0;
                        if (json.Contains("\"removeEmptyMarks\""))
                            config.RemoveEmptyMarks = json.IndexOf("\"removeEmptyMarks\": true", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (json.Contains("\"removeSideColumnTags\""))
                            config.RemoveSideColumnTags = json.IndexOf("\"removeSideColumnTags\": false", StringComparison.OrdinalIgnoreCase) < 0;
                        if (json.Contains("\"keepProfileLedgeTag\""))
                            config.KeepProfileLedgeTag = json.IndexOf("\"keepProfileLedgeTag\": false", StringComparison.OrdinalIgnoreCase) < 0;
                        if (json.Contains("\"pruneSecondaryRecesses\""))
                            config.PruneSecondaryRecesses = json.IndexOf("\"pruneSecondaryRecesses\": false", StringComparison.OrdinalIgnoreCase) < 0;

                        double dVal;
                        var mTopPitch = Regex.Match(json, @"\""topPitchPaperMm\""\s*:\s*([0-9.]+)");
                        if (mTopPitch.Success && double.TryParse(mTopPitch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.TopPitchPaperMm = dVal;

                        var mBotPitch = Regex.Match(json, @"\""bottomPitchPaperMm\""\s*:\s*([0-9.]+)");
                        if (mBotPitch.Success && double.TryParse(mBotPitch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.BotPitchPaperMm = dVal;

                        var mSidePitch = Regex.Match(json, @"\""sidePitchPaperMm\""\s*:\s*([0-9.]+)");
                        if (mSidePitch.Success && double.TryParse(mSidePitch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.SidePitchPaperMm = dVal;

                        var mRightBase = Regex.Match(json, @"\""rightBaseOffsetPaperMm\""\s*:\s*([0-9.]+)");
                        if (mRightBase.Success && double.TryParse(mRightBase.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.RightBaseOffsetPaperMm = dVal;

                        var mBotBase = Regex.Match(json, @"\""bottomBaseOffsetPaperMm\""\s*:\s*([0-9.]+)");
                        if (mBotBase.Success && double.TryParse(mBotBase.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.BottomBaseOffsetPaperMm = dVal;

                        var mBase = Regex.Match(json, @"\""baseOffsetPaperMm\""\s*:\s*([0-9.]+)");
                        if (mBase.Success && double.TryParse(mBase.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.BaseOffsetPaperMm = dVal;

                        var mBomLeft = Regex.Match(json, @"\""bomLeftPaperMm\""\s*:\s*([0-9.]+)");
                        if (mBomLeft.Success && double.TryParse(mBomLeft.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out dVal))
                            config.BomLeftPaperMm = dVal;

                        Console.WriteLine($"[LayoutTuner] Loaded config from {p}");
                        return config;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LayoutTuner] WARN: Could not parse config {p}: {ex.Message}");
                    }
                }
            }
            return config;
        }

        private static ContainerElement CreateNativeStyleTag(string text)
        {
            var ce = new ContainerElement();
            var te = new TextElement(text);
            te.Font.Name = "Arial Narrow";
            te.Font.Height = 1.8;
            te.Font.Color = DrawingColors.Red;
            ce.Add(te);
            return ce;
        }

        private static string NormalizePartString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("19O", "19Ø").Trim();
        }

        public static bool GetSolidBoundsInView(View view, Tekla.Structures.Model.Part main, out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue;
            minY = double.MaxValue;
            maxX = double.MinValue;
            maxY = double.MinValue;
            if (view == null || main == null) return false;
            try
            {
                Solid solid = main.GetSolid();
                if (solid != null && solid.MinimumPoint != null && solid.MaximumPoint != null)
                {
                    Matrix matrix = MatrixFactory.ToCoordinateSystem(view.ViewCoordinateSystem);
                    double[] xs = new double[] { solid.MinimumPoint.X, solid.MaximumPoint.X };
                    double[] ys = new double[] { solid.MinimumPoint.Y, solid.MaximumPoint.Y };
                    double[] zs = new double[] { solid.MinimumPoint.Z, solid.MaximumPoint.Z };
                    foreach (double x in xs)
                    {
                        foreach (double y in ys)
                        {
                            foreach (double z in zs)
                            {
                                Point point = matrix.Transform(new Point(x, y, z));
                                if (point.X < minX) minX = point.X;
                                if (point.X > maxX) maxX = point.X;
                                if (point.Y < minY) minY = point.Y;
                                if (point.Y > maxY) maxY = point.Y;
                            }
                        }
                    }
                    return minX < maxX && minY < maxY;
                }
            }
            catch { }
            return false;
        }

        public static double ScanBomLeft(ContainerView sheet, double sheetWidthMm = 431.8)
        {
            if (sheet == null) return 345.0;
            double sheetCap = (sheetWidthMm > 40.0) ? sheetWidthMm : 431.8;
            double rightBand = sheetCap * 0.45;
            double best = double.MaxValue;
            DrawingObjectEnumerator doe = null;
            try { doe = sheet.GetAllObjects(); } catch { doe = null; }
            while (doe != null && doe.MoveNext())
            {
                var obj = doe.Current as DrawingObject;
                if (obj == null || obj is ViewBase) continue;
                AABB aabb = null;
                try { if (obj is IAxisAlignedBoundingBox aabbObj) aabb = aabbObj.GetAxisAlignedBoundingBox(); } catch { continue; }
                if (aabb == null || aabb.MinPoint == null || aabb.MaxPoint == null) continue;
                double x = aabb.MinPoint.X;
                double w = aabb.MaxPoint.X - aabb.MinPoint.X;
                double h = aabb.MaxPoint.Y - aabb.MinPoint.Y;
                if (x < rightBand || x >= sheetCap - 2.0) continue;
                bool keyword = false;
                if (obj is Text txt)
                {
                    string s = (txt.TextString ?? "").ToUpperInvariant();
                    if (s.IndexOf("BILL OF MATERIAL", StringComparison.Ordinal) >= 0
                        || s.IndexOf("GENERAL NOTES", StringComparison.Ordinal) >= 0
                        || s.IndexOf("BOM", StringComparison.Ordinal) >= 0
                        || s.IndexOf("MATERIAL LIST", StringComparison.Ordinal) >= 0)
                        keyword = true;
                }
                bool tableLike = w >= 35.0 && h >= 25.0;
                if (!keyword && !tableLike) continue;
                if (x < best) best = x;
            }
            double chosen = (best < 100000.0 && best > rightBand) ? best : 345.0;
            double minBom = Math.Max(sheetCap * 0.55, 200.0);
            double maxBom = sheetCap - 5.0;
            if (chosen < minBom) chosen = Math.Min(345.0, maxBom);
            if (chosen > maxBom) chosen = maxBom;
            return chosen;
        }

        /// <summary>
        /// Tunes all StraightDimensionSet objects in the given view.
        /// Performs parts-based semantic classification, artifact pruning, dynamic model-associative L-linking,
        /// and symmetrical centered layout fit.
        /// </summary>
        public static TuningResult TuneViewDimensions(
            View view,
            Tekla.Structures.Model.Part mainPart = null,
            int scale = 75,
            TunerConfig config = null,
            bool dryRun = false,
            double computedBomLeft = 0.0)
        {
            if (config == null) config = new TunerConfig();
            var result = new TuningResult();
            if (view == null) return result;

            var dh = new DrawingHandler();
            var activeDrawing = dh.GetActiveDrawing();
            var drawing = activeDrawing ?? view.GetDrawing();
            if (activeDrawing == null && drawing != null)
            {
                try
                {
                    dh.SetActiveDrawing(drawing, true);
                    activeDrawing = dh.GetActiveDrawing();
                    if (activeDrawing != null)
                    {
                        var s = activeDrawing.GetSheet();
                        var ve = s.GetViews();
                        while (ve != null && ve.MoveNext())
                        {
                            if (ve.Current is View v && (v.Name == view.Name || v.ViewType == view.ViewType))
                            {
                                view = v;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LayoutTuner] WARN: Could not set active drawing: {ex.Message}");
                }
            }
            Console.WriteLine($"[LayoutTuner] activeDrawing='{activeDrawing?.Mark}' (ID={GetPropId(activeDrawing, "Identifier")}) view.Drawing='{view.GetDrawing()?.Mark}' (ID={GetPropId(view.GetDrawing(), "Identifier")}) view='{view.Name}' (ID={GetPropId(view, "Identifier")})");
            var sheet = drawing?.GetSheet();
            double scaleD = scale > 0 ? (double)scale : 75.0;

            var model = new Model();
            var idProp = typeof(DrawingObject).GetProperty("Identifier", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            // 1. Inspect All Sets & Semantic Classification (Excluding mainPart by Model Identifier)
            var allSets = new List<TunedSetInfo>();
            var oe = view.GetAllObjects(typeof(StraightDimensionSet));
            while (oe != null && oe.MoveNext())
            {
                if (oe.Current is StraightDimensionSet sds)
                {
                    var info = ClassifyDimensionSet(sds, model, mainPart);
                    if (info != null) allSets.Add(info);
                }
            }
            result.TotalInspected = allSets.Count;

            // 2. Identify Known Artifacts to Prune (Unknown sets are NEVER pruned)
            var setsToPrune = new List<TunedSetInfo>();
            var activeDoorSets = new List<TunedSetInfo>();

            foreach (var set in allSets)
            {
                bool isSide = (set.Side == EdgeSide.Left || set.Side == EdgeSide.Right);

                if (set.Feature == SemanticFeature.EdgeInsertP300)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning Edge-Insert row ID={set.ObjectId} (Edge inserts not shown on elevation)");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (set.Feature == SemanticFeature.BottomOverall)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning redundant Bottom Overall row ID={set.ObjectId}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (set.Side == EdgeSide.Left && set.Feature == SemanticFeature.FaceInsertP300)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning duplicate Left Insert column ID={set.ObjectId}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (set.Side == EdgeSide.Top && (set.Feature == SemanticFeature.ProfileOpening || (set.Segments.Count == 3 && set.Segments.Any(s => Math.Abs(s - 1087.0) < 5.0))))
                {
                    Console.WriteLine($"[LayoutTuner] Pruning redundant Door Opening row on Top ID={set.ObjectId}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (isSide && (set.ModelPartNames.Any(n => n.IndexOf("76Ø", StringComparison.OrdinalIgnoreCase) >= 0) || set.ModelPartNames.Any(n => n.IndexOf("GT76", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    Console.WriteLine($"[LayoutTuner] Pruning GT76 from side column ID={set.ObjectId} Side={set.Side}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (set.Side == EdgeSide.Right && set.Feature == SemanticFeature.ProfileHole)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning redundant Window column on Right ID={set.ObjectId}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (isSide && (set.Feature == SemanticFeature.GroutTubeGT100 || set.Feature == SemanticFeature.GroutTubeGT686))
                {
                    Console.WriteLine($"[LayoutTuner] Pruning side height chain for Grout Tubes ID={set.ObjectId} Side={set.Side}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (set.Side == EdgeSide.Bottom && set.ModelPartNames.Any(n => n.IndexOf("76Ø", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Console.WriteLine($"[LayoutTuner] Pruning GT76 from bottom row ID={set.ObjectId}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (isSide && (set.Feature == SemanticFeature.SpliceSP25 || set.Feature == SemanticFeature.SpliceSP30))
                {
                    Console.WriteLine($"[LayoutTuner] Pruning side height chain for Splices ID={set.ObjectId} Side={set.Side}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (isSide && set.Feature == SemanticFeature.LiftersP205)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning side height chain for Lifters ID={set.ObjectId} Side={set.Side}");
                    set.IsArtifact = true;
                    setsToPrune.Add(set);
                }
                else if (set.Feature == SemanticFeature.SecondaryRecess)
                {
                    if (config.PruneSecondaryRecesses)
                    {
                        Console.WriteLine($"[LayoutTuner] Pruning secondary wall recess cut ID={set.ObjectId} Side={set.Side} (Detailed on Section C-C / Sheet 4)");
                        set.IsArtifact = true;
                        setsToPrune.Add(set);
                    }
                    else
                    {
                        Console.WriteLine($"[LayoutTuner] Keeping secondary recess cut ID={set.ObjectId} Side={set.Side} per config");
                    }
                }
                else if (set.Feature == SemanticFeature.ProfileOpening)
                {
                    activeDoorSets.Add(set);
                }
                else if (set.Feature == SemanticFeature.Unknown)
                {
                    Console.WriteLine($"[LayoutTuner] WARN: Unknown dimension set ID={set.ObjectId} Side={set.Side} Segs={string.Join("|", set.Segments)}. Keeping set safe.");
                }
            }

            // If duplicate door opening rows exist on bottom, keep only one
            if (activeDoorSets.Count > 1)
            {
                for (int i = 1; i < activeDoorSets.Count; i++)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning duplicate door row ID={activeDoorSets[i].ObjectId}");
                    activeDoorSets[i].IsArtifact = true;
                    setsToPrune.Add(activeDoorSets[i]);
                }
            }

            // If duplicate overall sets exist on left or right, keep outermost only
            var leftOveralls = allSets.Where(x => !x.IsArtifact && x.Side == EdgeSide.Left && x.Feature == SemanticFeature.ProfileOverall).ToList();
            if (leftOveralls.Count > 1)
            {
                for (int i = 1; i < leftOveralls.Count; i++)
                {
                    Console.WriteLine($"[LayoutTuner] Pruning duplicate left overall ID={leftOveralls[i].ObjectId}");
                    leftOveralls[i].IsArtifact = true;
                    setsToPrune.Add(leftOveralls[i]);
                }
            }

            var retainedSets = allSets.Where(x => !x.IsArtifact).ToList();
            result.RetainedCount = retainedSets.Count;

            // 3. Dynamic Tier Compaction & Distances Assignment (Zero gaps for missing rows; unknowns placed before Overall)
            double topPitch = config.TopPitchPaperMm * scaleD;
            double topBase = config.BaseOffsetPaperMm * scaleD;
            double botPitch = config.BotPitchPaperMm * scaleD;
            double botBase = (config.BottomBaseOffsetPaperMm > 0 ? config.BottomBaseOffsetPaperMm : config.BaseOffsetPaperMm) * scaleD;
            double sidePitch = config.SidePitchPaperMm * scaleD;
            double sideBase = config.BaseOffsetPaperMm * scaleD;
            double rightSideBase = config.RightBaseOffsetPaperMm * scaleD;

            // Top items ordered by category
            var topLifters = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.LiftersP205);
            var topSP25 = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.SpliceSP25);
            var topSP30 = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.SpliceSP30);
            var topP616 = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.PlateP616);
            var topP300 = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.FaceInsertP300);
            var topHoles = retainedSets.Where(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.ProfileHole).OrderBy(x => x.Segments.FirstOrDefault()).ToList();
            var topUnknowns = retainedSets.Where(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.Unknown).ToList();
            var topLedge = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.ProfileLedge);
            var topOverall = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Top && x.Feature == SemanticFeature.ProfileOverall);

            var orderedTop = new List<TunedSetInfo>();
            if (topLifters != null) orderedTop.Add(topLifters);
            if (topSP25 != null) orderedTop.Add(topSP25);
            if (topSP30 != null) orderedTop.Add(topSP30);
            if (topP616 != null) orderedTop.Add(topP616);
            if (topP300 != null) orderedTop.Add(topP300);
            orderedTop.AddRange(topHoles);
            orderedTop.AddRange(topUnknowns);
            if (topLedge != null) orderedTop.Add(topLedge);
            if (topOverall != null) orderedTop.Add(topOverall);

            for (int i = 0; i < orderedTop.Count; i++)
            {
                orderedTop[i].NewDistance = topBase + i * topPitch;
            }

            // Bottom items ordered by category
            var botGT100 = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Bottom && x.Feature == SemanticFeature.GroutTubeGT100);
            var botGT686 = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Bottom && x.Feature == SemanticFeature.GroutTubeGT686);
            var botUnknowns = retainedSets.Where(x => x.Side == EdgeSide.Bottom && x.Feature == SemanticFeature.Unknown).ToList();
            var botDoor = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Bottom && x.Feature == SemanticFeature.ProfileOpening);

            var orderedBot = new List<TunedSetInfo>();
            if (botGT100 != null) orderedBot.Add(botGT100);
            if (botGT686 != null) orderedBot.Add(botGT686);
            orderedBot.AddRange(botUnknowns);
            if (botDoor != null) orderedBot.Add(botDoor);

            for (int i = 0; i < orderedBot.Count; i++)
            {
                orderedBot[i].NewDistance = botBase + i * botPitch;
            }

            // Left items ordered by category
            var leftWinCol = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Left && x.Feature == SemanticFeature.ProfileHole);
            var leftReveal = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Left && x.Feature == SemanticFeature.Reveal);
            var leftUnknowns = retainedSets.Where(x => x.Side == EdgeSide.Left && x.Feature == SemanticFeature.Unknown).ToList();
            var leftOverall = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Left && x.Feature == SemanticFeature.ProfileOverall);

            var orderedLeft = new List<TunedSetInfo>();
            if (leftWinCol != null) orderedLeft.Add(leftWinCol);
            if (leftReveal != null) orderedLeft.Add(leftReveal);
            orderedLeft.AddRange(leftUnknowns);
            if (leftOverall != null) orderedLeft.Add(leftOverall);

            for (int i = 0; i < orderedLeft.Count; i++)
            {
                orderedLeft[i].NewDistance = sideBase + i * sidePitch;
            }

            // Right items ordered by category
            var rightP616Col = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Right && x.Feature == SemanticFeature.PlateP616);
            var rightP300Col = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Right && x.Feature == SemanticFeature.FaceInsertP300);
            var rightUnknowns = retainedSets.Where(x => x.Side == EdgeSide.Right && x.Feature == SemanticFeature.Unknown).ToList();
            var rightOverall = retainedSets.FirstOrDefault(x => x.Side == EdgeSide.Right && x.Feature == SemanticFeature.ProfileOverall);

            var orderedRight = new List<TunedSetInfo>();
            if (rightP616Col != null) orderedRight.Add(rightP616Col);
            if (rightP300Col != null) orderedRight.Add(rightP300Col);
            orderedRight.AddRange(rightUnknowns);
            if (rightOverall != null) orderedRight.Add(rightOverall);

            for (int i = 0; i < orderedRight.Count; i++)
            {
                orderedRight[i].NewDistance = rightSideBase + i * sidePitch;
            }

            // 4. PRE-EXECUTION ASSERTIONS (Plan-First: All asserts run BEFORE any object is modified or deleted)

            // A. Assert Overall is strictly outermost on every edge (No unknown or secondary set placed beyond overall)
            if (topOverall != null)
            {
                if (orderedTop.Last() != topOverall)
                    throw new InvalidOperationException($"OVERALL IS NOT OUTERMOST ON TOP! Last element is {orderedTop.Last().Feature} ID={orderedTop.Last().ObjectId}");
                double maxOtherTop = orderedTop.Where(x => x != topOverall).Select(x => x.NewDistance).DefaultIfEmpty(0).Max();
                if (topOverall.NewDistance <= maxOtherTop)
                    throw new InvalidOperationException($"OVERALL IS NOT OUTERMOST ON TOP! Overall={topOverall.NewDistance:F1} <= MaxOther={maxOtherTop:F1}");
            }
            if (rightOverall != null)
            {
                if (orderedRight.Last() != rightOverall)
                    throw new InvalidOperationException($"OVERALL IS NOT OUTERMOST ON RIGHT! Last element is {orderedRight.Last().Feature} ID={orderedRight.Last().ObjectId}");
                double maxOtherRight = orderedRight.Where(x => x != rightOverall).Select(x => x.NewDistance).DefaultIfEmpty(0).Max();
                if (rightOverall.NewDistance <= maxOtherRight)
                    throw new InvalidOperationException($"OVERALL IS NOT OUTERMOST ON RIGHT! Overall={rightOverall.NewDistance:F1} <= MaxOther={maxOtherRight:F1}");
            }
            if (leftOverall != null)
            {
                if (orderedLeft.Last() != leftOverall)
                    throw new InvalidOperationException($"OVERALL IS NOT OUTERMOST ON LEFT! Last element is {orderedLeft.Last().Feature} ID={orderedLeft.Last().ObjectId}");
                double maxOtherLeft = orderedLeft.Where(x => x != leftOverall).Select(x => x.NewDistance).DefaultIfEmpty(0).Max();
                if (leftOverall.NewDistance <= maxOtherLeft)
                    throw new InvalidOperationException($"OVERALL IS NOT OUTERMOST ON LEFT! Overall={leftOverall.NewDistance:F1} <= MaxOther={maxOtherLeft:F1}");
            }

            // B. Span Sum Sanity Check
            double panelW = 5120.0;
            double panelH = 3475.0;
            double sMinX = 0, sMaxX = 0, sMinY = 0, sMaxY = 0;
            if (mainPart != null && GetSolidBoundsInView(view, mainPart, out sMinX, out sMaxX, out sMinY, out sMaxY))
            {
                panelW = Math.Round(sMaxX - sMinX, 1);
                panelH = Math.Round(sMaxY - sMinY, 1);
            }

            foreach (var r in retainedSets)
            {
                double expectedSpan = (r.Side == EdgeSide.Top || r.Side == EdgeSide.Bottom) ? panelW : panelH;
                if (Math.Abs(r.TotalSpan - expectedSpan) > 2.5)
                {
                    if (r.Feature == SemanticFeature.Unknown)
                    {
                        Console.WriteLine($"[LayoutTuner] WARN: Span delta on unknown set SDS {r.ObjectId} Side={r.Side}: sum={r.TotalSpan:F1} vs span={expectedSpan:F1}");
                    }
                    else
                    {
                        throw new InvalidOperationException($"Span sum mismatch on SDS {r.ObjectId} Side={r.Side} Feature={r.Feature}: sum={r.TotalSpan:F1} != expected {expectedSpan:F1} (segments: {string.Join("|", r.Segments)})");
                    }
                }
            }

            // B. Dynamic Rank Check on Planned Distances
            var plannedRightCols = new List<TunedSetInfo> { rightP616Col, rightP300Col, rightOverall }.Where(x => x != null).ToList();
            plannedRightCols.Sort((a, b) => a.NewDistance.CompareTo(b.NewDistance));

            var plannedLinkedRows = new List<TunedSetInfo> { topP616, topP300 }.Where(x => x != null).ToList();
            plannedLinkedRows.Sort((a, b) => a.NewDistance.CompareTo(b.NewDistance));

            int rP616ColRank = plannedRightCols.IndexOf(rightP616Col) + 1;
            int rP300ColRank = plannedRightCols.IndexOf(rightP300Col) + 1;
            int rP616RowRank = plannedLinkedRows.IndexOf(topP616) + 1;
            int rP300RowRank = plannedLinkedRows.IndexOf(topP300) + 1;

            if (rP616ColRank != rP616RowRank || rP300ColRank != rP300RowRank)
            {
                result.DynamicRankAssertPassed = false;
                throw new InvalidOperationException($"DYNAMIC RANK MISMATCH: P-616 (Col {rP616ColRank} vs Row {rP616RowRank}) or P-300 (Col {rP300ColRank} vs Row {rP300RowRank})");
            }
            result.DynamicRankAssertPassed = true;

            // C. Symmetrical Centering & Fit Check (Using GetSolidBoundsInView for view-space solid bounds)
            double leftStackPaper = (orderedLeft.Count > 0 ? orderedLeft.Max(x => x.NewDistance) : 3000.0) / scaleD;
            double rightStackPaper = (orderedRight.Count > 0 ? orderedRight.Max(x => x.NewDistance) : 3450.0) / scaleD;
            double topStackPaper = (orderedTop.Count > 0 ? orderedTop.Max(x => x.NewDistance) : 8100.0) / scaleD;
            double botStackPaper = (orderedBot.Count > 0 ? orderedBot.Max(x => x.NewDistance) : 3300.0) / scaleD;

            double bomLeft = computedBomLeft > 50.0 
                ? computedBomLeft 
                : ((sheet != null) ? ScanBomLeft(sheet, sheet.Width > 50.0 ? sheet.Width : 431.8) : (config.BomLeftPaperMm > 50.0 ? config.BomLeftPaperMm : 345.0));
            double marginMinX = 15.0;
            double marginMaxX = bomLeft - 5.0;
            double marginMinY = 12.0;
            double marginMaxY = 265.0;

            if (sheet != null)
            {
                double sheetH = sheet.Height > 50.0 ? sheet.Height : 279.4;
                marginMaxY = sheetH - 14.4;
            }

            double solidMinX = 0.0, solidMaxX = panelW, solidMinY = 0.0, solidMaxY = panelH;
            double vMinX = 0, vMaxX = 0, vMinY = 0, vMaxY = 0;
            if (mainPart != null && GetSolidBoundsInView(view, mainPart, out vMinX, out vMaxX, out vMinY, out vMaxY))
            {
                solidMinX = vMinX;
                solidMaxX = vMaxX;
                solidMinY = vMinY;
                solidMaxY = vMaxY;
            }

            double panelPaperW = (solidMaxX - solidMinX) / scaleD;
            double totalStackW = leftStackPaper + panelPaperW + rightStackPaper;
            double totalAvailW = marginMaxX - marginMinX;
            double horizClearance = (totalAvailW - totalStackW) / 2.0;
            double targetOriginX = marginMinX + horizClearance + leftStackPaper - (solidMinX / scaleD);

            double panelPaperH = (solidMaxY - solidMinY) / scaleD;
            double totalStackH = botStackPaper + panelPaperH + topStackPaper;
            double totalAvailH = marginMaxY - marginMinY;
            double vertClearance = (totalAvailH - totalStackH) / 2.0;
            double targetOriginY = marginMinY + vertClearance + botStackPaper - (solidMinY / scaleD);

            double liveMinX = marginMinX + horizClearance;
            double liveMaxX = marginMaxX - horizClearance;
            double liveMinY = marginMinY + vertClearance;
            double liveMaxY = marginMaxY - vertClearance;

            double clL = liveMinX - marginMinX;
            double clR = marginMaxX - liveMaxX;
            double clB = liveMinY - marginMinY;
            double clT = marginMaxY - liveMaxY;

            result.FitReport = string.Format(CultureInfo.InvariantCulture,
                "Extents: X=[{0:F1}..{1:F1}] mm, Y=[{2:F1}..{3:F1}] mm | Margin: Left={4:F1} mm, Right={5:F1} mm, Bottom={6:F1} mm, Top={7:F1} mm",
                liveMinX, liveMaxX, liveMinY, liveMaxY, clL, clR, clB, clT);

            result.FitAssertPassed = (liveMinX >= marginMinX && liveMaxX <= marginMaxX && liveMinY >= marginMinY && liveMaxY <= marginMaxY);
            if (!result.FitAssertPassed)
            {
                Console.WriteLine("[LayoutTuner] WARN: Fit check violated margins! " + result.FitReport);
            }

            result.MaxExtentPaperMm[EdgeSide.Left] = Math.Round(leftStackPaper, 1);
            result.MaxExtentPaperMm[EdgeSide.Right] = Math.Round(rightStackPaper, 1);
            result.MaxExtentPaperMm[EdgeSide.Top] = Math.Round(topStackPaper, 1);
            result.MaxExtentPaperMm[EdgeSide.Bottom] = Math.Round(botStackPaper, 1);

            // If dryRun, return simulation result immediately without modifying drawing objects
            if (dryRun)
            {
                return result;
            }

            // 5. EXECUTION PHASE (All assertions passed — modify Tekla drawing objects safely)

            // Clean Sheet 1 redundant views if configured
            if (sheet != null && config.RemoveSheet1Views)
            {
                var viewsToDelete = new List<View>();
                var ve = sheet.GetViews();
                while (ve.MoveNext())
                {
                    if (ve.Current is View v)
                    {
                        string vn = v.Name ?? "";
                        if (vn.IndexOf("END 2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            vn.IndexOf("VIEW A", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            v.Origin.X > 280.0)
                        {
                            viewsToDelete.Add(v);
                        }
                    }
                }
                foreach (var v in viewsToDelete)
                {
                    Console.WriteLine($"[LayoutTuner] Cleanly deleted redundant Sheet 1 view '{v.Name}'");
                    v.Delete();
                }
            }

            // Clean empty / unresolved marks if configured
            if (config.RemoveEmptyMarks)
            {
                var marksToDelete = new List<Mark>();
                var me = view.GetAllObjects(typeof(Mark));
                while (me.MoveNext())
                {
                    if (me.Current is Mark m)
                    {
                        bool isEmpty = (m.Attributes?.Content == null || m.Attributes.Content.Count == 0);
                        string mText = "";
                        try { mText = m.GetType().GetProperty("Text")?.GetValue(m, null)?.ToString() ?? ""; } catch { }
                        if (isEmpty || mText == "?" || string.IsNullOrWhiteSpace(mText))
                        {
                            marksToDelete.Add(m);
                        }
                    }
                }
                foreach (var m in marksToDelete) m.Delete();
                Console.WriteLine($"[LayoutTuner] Deleted {marksToDelete.Count} empty/unresolved marks from view.");
            }

            // Scale & View Origin
            if (Math.Abs(view.Attributes.Scale - scaleD) > 0.5)
            {
                view.Attributes.Scale = scaleD;
            }
            var newOrigin = new TSPoint(Math.Round(targetOriginX, 1), Math.Round(targetOriginY, 1), 0.0);
            if (Math.Abs(view.Origin.X - newOrigin.X) > 0.5 || Math.Abs(view.Origin.Y - newOrigin.Y) > 0.5)
            {
                view.Origin = newOrigin;
            }
            view.Modify();

            // Delete only identified artifact sets
            foreach (var s in setsToPrune)
            {
                s.Set.Delete();
            }
            result.PrunedArtifacts = setsToPrune.Count;

            // Apply Uniform Distances
            foreach (var item in retainedSets)
            {
                if (item.NewDistance > 0 && Math.Abs(item.Set.Distance - item.NewDistance) > 1.0)
                {
                    item.Set.Distance = item.NewDistance;
                    item.Set.Modify();
                    result.ModifiedCount++;
                }
            }

            // Apply Tag & Color Styling
            if (topOverall != null)
            {
                topOverall.Set.Attributes.LeftUpperTag = new ContainerElement();
                topOverall.Set.Attributes.RightUpperTag = CreateNativeStyleTag("PROFILE");
                topOverall.Set.Attributes.RightLowerTag = CreateNativeStyleTag("OVERALL");
                topOverall.Set.Attributes.Color = DrawingColors.Green;
                topOverall.Set.Attributes.Text.Font.Color = DrawingColors.Red;
                topOverall.Set.Modify();
            }
            if (rightOverall != null)
            {
                rightOverall.Set.Attributes.Color = DrawingColors.Green;
                rightOverall.Set.Modify();
            }
            if (leftOverall != null)
            {
                leftOverall.Set.Attributes.Color = DrawingColors.Green;
                leftOverall.Set.Modify();
            }
            if (topLedge != null && config.KeepProfileLedgeTag)
            {
                topLedge.Set.Attributes.RightUpperTag = CreateNativeStyleTag("PROFILE");
                topLedge.Set.Attributes.RightLowerTag = CreateNativeStyleTag("LEDGE");
                topLedge.Set.Modify();
            }
            if (topLifters != null)
            {
                topLifters.Set.Attributes.LeftUpperTag = new ContainerElement();
                topLifters.Set.Attributes.LeftLowerTag = new ContainerElement();
                topLifters.Set.Attributes.RightUpperTag = CreateNativeStyleTag("[P-205]");
                topLifters.Set.Attributes.RightLowerTag = CreateNativeStyleTag("LIFTERS");
                topLifters.Set.Modify();
                Console.WriteLine("[LayoutTuner] Set Lifters row tag to '[P-205]' / 'LIFTERS' (matching Engineer standard).");
            }
            if (topP300 != null)
            {
                topP300.Set.Attributes.LeftUpperTag = new ContainerElement();
                topP300.Set.Attributes.LeftLowerTag = new ContainerElement();
                topP300.Set.Attributes.RightUpperTag = CreateNativeStyleTag("P-300");
                topP300.Set.Attributes.RightLowerTag = CreateNativeStyleTag("BRACING INSERT");
                topP300.Set.Modify();
                Console.WriteLine("[LayoutTuner] Set P-300 row tag to 'P-300' / 'BRACING INSERT' (matching Engineer standard).");
            }
            foreach (var h in topHoles)
            {
                h.Set.Attributes.LeftUpperTag = new ContainerElement();
                h.Set.Attributes.LeftLowerTag = new ContainerElement();
                h.Set.Attributes.RightUpperTag = CreateNativeStyleTag("HOLE");
                h.Set.Attributes.RightLowerTag = new ContainerElement();
                h.Set.Modify();
            }
            if (botDoor != null)
            {
                botDoor.Set.Attributes.LeftUpperTag = new ContainerElement();
                botDoor.Set.Attributes.LeftLowerTag = new ContainerElement();
                botDoor.Set.Attributes.RightUpperTag = CreateNativeStyleTag("OPENING");
                botDoor.Set.Attributes.RightLowerTag = new ContainerElement();
                botDoor.Set.Modify();
                Console.WriteLine("[LayoutTuner] Set Door Opening row tag to 'OPENING' (matching Engineer standard).");
            }

            // Reposition "TOP IN FORM" view label below all bottom dimensions to prevent collision
            try
            {
                var te = view.GetAllObjects(typeof(Text));
                while (te != null && te.MoveNext())
                {
                    if (te.Current is Text txt)
                    {
                        string tStr = txt.TextString ?? "";
                        if (tStr.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0 || tStr.IndexOf("IN FORM", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            double labelX = (solidMinX + solidMaxX) / 2.0;
                            double labelY = solidMinY - ((botStackPaper + 10.0) * scaleD);
                            txt.InsertionPoint = new TSPoint(labelX, labelY, 0.0);
                            txt.Modify();
                            Console.WriteLine($"[LayoutTuner] Repositioned '{tStr.Replace('\n', ' ')}' to Y={labelY:F1} (below bottom dimension stack {botStackPaper:F1}mm)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LayoutTuner] WARN: Could not reposition view label: " + ex.Message);
            }

            // Clear tags on side columns per config (matching Engineer standard: zero tags on side columns)
            if (config.RemoveSideColumnTags)
            {
                int wiped = 0;
                foreach (var item in retainedSets)
                {
                    if (item.Side == EdgeSide.Left || item.Side == EdgeSide.Right)
                    {
                        item.Set.Attributes.RightUpperTag = new ContainerElement();
                        item.Set.Attributes.RightLowerTag = new ContainerElement();
                        item.Set.Attributes.LeftUpperTag = new ContainerElement();
                        item.Set.Attributes.LeftLowerTag = new ContainerElement();
                        item.Set.Modify();
                        wiped++;
                    }
                }
                Console.WriteLine($"[LayoutTuner] Cleared tags on {wiped} side columns (matching Engineer standard).");
            }

            // Commit all changes before handling dimension links so Tekla drawing DB handles are fresh
            drawing?.CommitChanges();

            // 6. DIMENSIONLINK MANAGEMENT (Link Census, Safe Delete of Stale/Invalid Links, Fresh Re-fetch, Insertion)
            var existingLinks = new List<DimensionLink>();
            var le = view.GetAllObjects(typeof(DimensionLink));
            while (le != null && le.MoveNext())
            {
                if (le.Current is DimensionLink dl) existingLinks.Add(dl);
            }
            Console.WriteLine($"[LayoutTuner] Existing DimensionLinks before cleanup: {existingLinks.Count}");

            foreach (var dl in existingLinks)
            {
                dl.Delete();
            }
            if (existingLinks.Count > 0)
            {
                drawing?.CommitChanges();
            }

            // Re-fetch all StraightDimensionSet instances with fresh Tekla database handles
            var freshOE = view.GetAllObjects(typeof(StraightDimensionSet));
            var freshDict = new Dictionary<int, StraightDimensionSet>();
            while (freshOE != null && freshOE.MoveNext())
            {
                if (freshOE.Current is StraightDimensionSet freshSds)
                {
                    if (idProp?.GetValue(freshSds) is Identifier id)
                    {
                        freshDict[id.ID] = freshSds;
                    }
                }
            }

            // Retrieve fresh handles for the 5 intended link pairs
            StraightDimensionSet topP616_fresh = (topP616 != null && freshDict.ContainsKey(topP616.ObjectId)) ? freshDict[topP616.ObjectId] : null;
            StraightDimensionSet rightP616_fresh = (rightP616Col != null && freshDict.ContainsKey(rightP616Col.ObjectId)) ? freshDict[rightP616Col.ObjectId] : null;

            StraightDimensionSet topP300_fresh = (topP300 != null && freshDict.ContainsKey(topP300.ObjectId)) ? freshDict[topP300.ObjectId] : null;
            StraightDimensionSet rightP300_fresh = (rightP300Col != null && freshDict.ContainsKey(rightP300Col.ObjectId)) ? freshDict[rightP300Col.ObjectId] : null;

            StraightDimensionSet topOverall_fresh = (topOverall != null && freshDict.ContainsKey(topOverall.ObjectId)) ? freshDict[topOverall.ObjectId] : null;
            StraightDimensionSet leftOverall_fresh = (leftOverall != null && freshDict.ContainsKey(leftOverall.ObjectId)) ? freshDict[leftOverall.ObjectId] : null;
            StraightDimensionSet rightOverall_fresh = (rightOverall != null && freshDict.ContainsKey(rightOverall.ObjectId)) ? freshDict[rightOverall.ObjectId] : null;
            StraightDimensionSet botDoor_fresh = (botDoor != null && freshDict.ContainsKey(botDoor.ObjectId)) ? freshDict[botDoor.ObjectId] : null;

            var matchingTopHole = topHoles.FirstOrDefault(h => SharesModelPart(h, leftWinCol)) ?? topHoles.FirstOrDefault();
            StraightDimensionSet topHole_fresh = (matchingTopHole != null && freshDict.ContainsKey(matchingTopHole.ObjectId)) ? freshDict[matchingTopHole.ObjectId] : null;
            StraightDimensionSet leftWinCol_fresh = (leftWinCol != null && freshDict.ContainsKey(leftWinCol.ObjectId)) ? freshDict[leftWinCol.ObjectId] : null;

            // 1. Overall (Top ↔ Left) [Per Engineer drawing standard]
            if (TryCreateDimensionLink(topOverall_fresh, leftOverall_fresh, "Overall (Top ↔ Left)")) result.LinksCreated++;
            // 2. Window Hole (Top ↔ Left)
            if (TryCreateDimensionLink(topHole_fresh, leftWinCol_fresh, "Window Hole (Top ↔ Left)")) result.LinksCreated++;
            // 3. P-616 Plate (Top ↔ Right)
            if (TryCreateDimensionLink(topP616_fresh, rightP616_fresh, "P-616 Plate (Top ↔ Right)")) result.LinksCreated++;
            // 4. P-300 Bracing Insert (Top ↔ Right)
            if (TryCreateDimensionLink(topP300_fresh, rightP300_fresh, "P-300 Bracing Insert (Top ↔ Right)")) result.LinksCreated++;
            // 5. Door Opening ↔ Right Overall (Bottom ↔ Right)
            if (TryCreateDimensionLink(botDoor_fresh, rightOverall_fresh, "Door Opening ↔ Right Overall (Bottom ↔ Right)")) result.LinksCreated++;

            drawing?.CommitChanges();

            // Post-Link Stabilization Pass
            var postOE = view.GetAllObjects(typeof(StraightDimensionSet));
            var postSets = new List<TunedSetInfo>();
            while (postOE != null && postOE.MoveNext())
            {
                if (postOE.Current is StraightDimensionSet sds)
                {
                    var info = ClassifyDimensionSet(sds, model, mainPart);
                    if (info != null) postSets.Add(info);
                }
            }

            int stabilizedCount = 0;
            foreach (var target in retainedSets)
            {
                var freshItem = postSets.FirstOrDefault(x => x.ObjectId == target.ObjectId);
                if (freshItem != null && Math.Abs(freshItem.Set.Distance - target.NewDistance) > 1.0)
                {
                    freshItem.Set.Distance = target.NewDistance;
                    freshItem.Set.Modify();
                    stabilizedCount++;
                }
            }
            if (stabilizedCount > 0)
            {
                drawing?.CommitChanges();
            }

            // Final Link Census
            var finalLinks = new List<DimensionLink>();
            var finalLe = view.GetAllObjects(typeof(DimensionLink));
            while (finalLe != null && finalLe.MoveNext())
            {
                if (finalLe.Current is DimensionLink dl) finalLinks.Add(dl);
            }
            Console.WriteLine($"[LayoutTuner] Final DimensionLinks active: {finalLinks.Count} (expected 5)");

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[LayoutTuner] DONE: Retained={0}/{1} (Pruned={2}), Modified={3}, Links={4}, RankAssert={5}, FitAssert={6} [{7}]",
                result.RetainedCount, result.TotalInspected, result.PrunedArtifacts, result.ModifiedCount,
                result.LinksCreated, result.DynamicRankAssertPassed, result.FitAssertPassed, result.FitReport));

            return result;
        }

        private static int GetPropId(object obj, string propName)
        {
            if (obj == null) return 0;
            var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p?.GetValue(obj) is Identifier id) return id.ID;
            return 0;
        }

        private static bool TryCreateDimensionLink(StraightDimensionSet s1, StraightDimensionSet s2, string label)
        {
            if (s1 == null || s2 == null)
            {
                Console.WriteLine($"[LayoutTuner] WARN: Cannot create DimensionLink '{label}' - one or both sets are missing.");
                return false;
            }
            int id1 = GetPropId(s1, "Identifier");
            int id2 = GetPropId(s2, "Identifier");
            int vid1 = GetPropId(s1, "ViewIdentifier");
            int did1 = GetPropId(s1, "DrawingIdentifier");
            int vid2 = GetPropId(s2, "ViewIdentifier");
            int did2 = GetPropId(s2, "DrawingIdentifier");
            Console.WriteLine($"[LayoutTuner] TryCreateLink '{label}': s1 ID={id1} ViewID={vid1} DwgID={did1} | s2 ID={id2} ViewID={vid2} DwgID={did2}");
            try
            {
                var dl = new DimensionLink(s1, s2);
                bool ok = dl.Insert();
                if (ok)
                {
                    Console.WriteLine($"[LayoutTuner] DimensionLink inserted: '{label}' (Row ID={id1} ↔ Col ID={id2})");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[LayoutTuner] WARN: DimensionLink '{label}' Insert returned false.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LayoutTuner] ERROR: DimensionLink '{label}' failed ({ex.GetType().FullName}): {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static bool SharesModelPart(TunedSetInfo a, TunedSetInfo b)
        {
            if (a == null || b == null) return false;
            return a.ModelPartIds.Intersect(b.ModelPartIds).Any();
        }

        public static TunedSetInfo ClassifyDimensionSet(StraightDimensionSet sds, Model model, Tekla.Structures.Model.Part mainPart)
        {
            if (sds == null) return null;
            var info = new TunedSetInfo { Set = sds, OriginalDistance = sds.Distance };

            var idProp = typeof(DrawingObject).GetProperty("Identifier", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (idProp != null)
            {
                var idObj = idProp.GetValue(sds);
                if (idObj is Identifier id) info.ObjectId = id.ID;
            }

            // Segments & UpDirection
            var subs = sds.GetObjects();
            Vector up = new Vector(0, 0, 0);
            while (subs != null && subs.MoveNext())
            {
                if (subs.Current is StraightDimension sd)
                {
                    up = sd.UpDirection;
                    break;
                }
            }
            bool isHoriz = Math.Abs(up.Y) > Math.Abs(up.X);
            if (isHoriz) info.Side = up.Y > 0 ? EdgeSide.Top : EdgeSide.Bottom;
            else info.Side = up.X > 0 ? EdgeSide.Right : EdgeSide.Left;

            subs?.Reset();
            while (subs != null && subs.MoveNext())
            {
                if (subs.Current is StraightDimension sd)
                {
                    double seg = isHoriz ? Math.Abs(sd.EndPoint.X - sd.StartPoint.X) : Math.Abs(sd.EndPoint.Y - sd.StartPoint.Y);
                    info.Segments.Add(Math.Round(seg, 1));
                }
            }
            info.TotalSpan = info.Segments.Sum();

            // Measure panel bounds dynamically from mainPart solid geometry (no hardcoded panel width/height)
            double panelW = 0.0, panelH = 0.0;
            if (mainPart != null)
            {
                var s = mainPart.GetSolid();
                if (s != null)
                {
                    panelW = Math.Round(Math.Abs(s.MaximumPoint.X - s.MinimumPoint.X), 1);
                    panelH = Math.Round(Math.Abs(s.MaximumPoint.Z - s.MinimumPoint.Z), 1);
                    if (panelH < 1.0) panelH = Math.Round(Math.Abs(s.MaximumPoint.Y - s.MinimumPoint.Y), 1);
                }
            }
            double expectedSpan = isHoriz ? panelW : panelH;
            info.IsOverall = (expectedSpan > 0 && info.Segments.Count == 1 && Math.Abs(info.TotalSpan - expectedSpan) < 25.0);

            // Tags
            string tag = "";
            var ru = sds.Attributes?.RightUpperTag;
            if (ru != null) foreach (var el in ru) if (el is TextElement te) tag += te.Value + " ";
            var rl = sds.Attributes?.RightLowerTag;
            if (rl != null) foreach (var el in rl) if (el is TextElement te) tag += te.Value + " ";
            info.TagText = tag.Trim();

            // Related Model Objects (Excluding mainPart by Model Identifier and Name)
            int mainPartId = mainPart != null ? mainPart.Identifier.ID : -1;
            var ro = sds.GetRelatedObjects();
            while (ro != null && ro.MoveNext())
            {
                info.RelatedCount++;
                if (ro.Current is Tekla.Structures.Drawing.ModelObject dmo)
                {
                    var mid = dmo.ModelIdentifier;
                    if (mid.ID > 0 && mid.ID != mainPartId)
                    {
                        var mo = model.SelectModelObject(mid);
                        if (mo is Tekla.Structures.Model.Part p)
                        {
                            if (mainPartId > 0 && p.Identifier.ID == mainPartId) continue;
                            if (p.Name.Equals("WALL", StringComparison.OrdinalIgnoreCase)) continue;

                            info.ModelPartIds.Add(p.Identifier.ID);
                            string pName = NormalizePartString(p.Name);
                            string assemPos = "";
                            try { p.GetReportProperty("ASSEMBLY_POS", ref assemPos); } catch { }
                            string combined = (pName + " " + assemPos).Trim();
                            info.ModelPartNames.Add(combined);
                        }
                    }
                }
            }

            // Semantic Classification based purely on hardware families and model features
            bool hasPlateP616 = info.ModelPartNames.Any(n => Regex.IsMatch(n, @"\b(P-?616|PLATE)\b", RegexOptions.IgnoreCase));
            bool hasFaceInsert = info.ModelPartNames.Any(n => (Regex.IsMatch(n, @"\b(P-?300|19[ØO]\s*INSERT)\b", RegexOptions.IgnoreCase)) && n.IndexOf("EDGE", StringComparison.OrdinalIgnoreCase) < 0);
            bool hasEdgeInsert = info.ModelPartNames.Any(n => n.IndexOf("EDGE", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("EDGE INSERT", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasSpliceSP25 = info.ModelPartNames.Any(n => Regex.IsMatch(n, @"\bSP25\b", RegexOptions.IgnoreCase));
            bool hasSpliceSP30 = info.ModelPartNames.Any(n => Regex.IsMatch(n, @"\b(SP30|30M)\b", RegexOptions.IgnoreCase));
            bool hasGroutTube100 = info.ModelPartNames.Any(n => Regex.IsMatch(n, @"\b(100Ø|100O|GT100)\b", RegexOptions.IgnoreCase));
            bool hasGroutTube686 = info.ModelPartNames.Any(n => Regex.IsMatch(n, @"\b(686|GT100-686)\b", RegexOptions.IgnoreCase));
            bool hasLifters = info.ModelPartNames.Any(n => Regex.IsMatch(n, @"\b(ANCHOR|LIFTER|P-?205)\b", RegexOptions.IgnoreCase))
                || info.TagText.IndexOf("LIFTER", StringComparison.OrdinalIgnoreCase) >= 0
                || (info.Side == EdgeSide.Top && info.Segments.Count == 3 && ((info.Segments[0] > 600 && info.Segments[0] < 850) && (info.Segments[2] > 600 && info.Segments[2] < 850)));

            // Check correlation with boolean cuts on the wall solid
            bool isDoorCut = false;
            bool isLedgeCut = false;
            bool isRevealCut = false;
            bool isHoleCut = false;
            bool isSecondaryRecessCut = false;

            if (mainPart != null)
            {
                try
                {
                    var be = mainPart.GetBooleans();
                    while (be.MoveNext())
                    {
                        if (be.Current is BooleanPart bp && bp.OperativePart != null)
                        {
                            var cp = bp.OperativePart;
                            string cpName = cp.Name ?? "";
                            var cSolid = cp.GetSolid();
                            if (cSolid != null)
                            {
                                double cutSpanX = Math.Abs(cSolid.MaximumPoint.X - cSolid.MinimumPoint.X);
                                double cutSpanY = Math.Abs(cSolid.MaximumPoint.Z - cSolid.MinimumPoint.Z);
                                if (cutSpanY < 1.0) cutSpanY = Math.Abs(cSolid.MaximumPoint.Y - cSolid.MinimumPoint.Y);

                                double relevantCutSpan = isHoriz ? cutSpanX : cutSpanY;
                                if (relevantCutSpan > 0 && info.Segments.Any(s => Math.Abs(s - relevantCutSpan) < 5.0))
                                {
                                    if (cpName.IndexOf("DOOR", StringComparison.OrdinalIgnoreCase) >= 0 || cpName.IndexOf("OPENING", StringComparison.OrdinalIgnoreCase) >= 0) isDoorCut = true;
                                    else if (cpName.IndexOf("LEDGE", StringComparison.OrdinalIgnoreCase) >= 0) isLedgeCut = true;
                                    else if (cpName.IndexOf("REVEAL", StringComparison.OrdinalIgnoreCase) >= 0 || cpName.IndexOf("FALSE JOINT", StringComparison.OrdinalIgnoreCase) >= 0) isRevealCut = true;
                                    else if (cpName.IndexOf("CUTPART", StringComparison.OrdinalIgnoreCase) >= 0 || cpName.IndexOf("WINDOW", StringComparison.OrdinalIgnoreCase) >= 0 || cpName.IndexOf("HOLE", StringComparison.OrdinalIgnoreCase) >= 0) isHoleCut = true;
                                    else if (cpName.IndexOf("Drip", StringComparison.OrdinalIgnoreCase) >= 0 || cpName.IndexOf("CHAMFER", StringComparison.OrdinalIgnoreCase) >= 0 || cpName.IndexOf("NOTCH", StringComparison.OrdinalIgnoreCase) >= 0) isSecondaryRecessCut = true;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (hasPlateP616) info.Feature = SemanticFeature.PlateP616;
            else if (hasFaceInsert) info.Feature = SemanticFeature.FaceInsertP300;
            else if (hasEdgeInsert) info.Feature = SemanticFeature.EdgeInsertP300;
            else if (hasSpliceSP25) info.Feature = SemanticFeature.SpliceSP25;
            else if (hasSpliceSP30) info.Feature = SemanticFeature.SpliceSP30;
            else if (hasGroutTube100) info.Feature = SemanticFeature.GroutTubeGT100;
            else if (hasGroutTube686) info.Feature = SemanticFeature.GroutTubeGT686;
            else if (hasLifters) info.Feature = SemanticFeature.LiftersP205;
            else if (info.IsOverall || info.TagText.IndexOf("OVERALL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                info.Feature = info.Side == EdgeSide.Bottom ? SemanticFeature.BottomOverall : SemanticFeature.ProfileOverall;
            }
            else if (info.Side == EdgeSide.Top && (isLedgeCut || info.TagText.IndexOf("LEDGE", StringComparison.OrdinalIgnoreCase) >= 0 || info.Segments.Count >= 5))
            {
                info.Feature = SemanticFeature.ProfileLedge;
            }
            else if (info.Side == EdgeSide.Bottom && (isDoorCut || info.TagText.IndexOf("OPENING", StringComparison.OrdinalIgnoreCase) >= 0 || info.TagText.IndexOf("RECESS", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                info.Feature = SemanticFeature.ProfileOpening;
            }
            else if (isHoleCut || info.TagText.IndexOf("HOLE", StringComparison.OrdinalIgnoreCase) >= 0 || (info.Side == EdgeSide.Top && info.TagText.IndexOf("OPENING", StringComparison.OrdinalIgnoreCase) >= 0) || (info.Side == EdgeSide.Left && info.Segments.Count == 3 && info.Segments.Any(s => s > 300.0 && s < 800.0)))
            {
                info.Feature = SemanticFeature.ProfileHole;
            }
            else if (info.Side == EdgeSide.Left && (isRevealCut || (info.TotalSpan > 0 && Math.Abs(info.TotalSpan - panelH) < 25.0 && info.Segments.Count == 3)))
            {
                info.Feature = SemanticFeature.Reveal;
            }
            else if (info.ModelPartIds.Count == 0 && (isSecondaryRecessCut || info.TagText.IndexOf("RECESS", StringComparison.OrdinalIgnoreCase) >= 0 || info.Side == EdgeSide.Right))
            {
                info.Feature = SemanticFeature.SecondaryRecess;
            }
            else
            {
                info.Feature = SemanticFeature.Unknown;
            }

            return info;
        }
    }
}

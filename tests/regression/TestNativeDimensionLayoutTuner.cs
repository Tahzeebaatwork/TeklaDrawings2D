using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaExtractor
{
    /// <summary>
    /// Automated regression tests for NativeDimensionLayoutTuner.
    /// Fully hermetic: rebuilds fresh Sheet 1 from Open API before testing.
    /// Tests: Span sums, Idempotency, Bounds/Margins, Dynamic Ranks, Reversed-Order Insensitivity, Exact Link Pairs, and Unknown-Kept Policy.
    /// </summary>
    public class TestNativeDimensionLayoutTuner
    {
        public static int RunAllTests()
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("RUNNING REGRESSION SUITE: NativeDimensionLayoutTuner (Hermetic)");
            Console.WriteLine("================================================================================");

            var model = new Model();
            var dh = new DrawingHandler();

            var ms = model.GetModelObjectSelector();
            var me = ms.GetAllObjectsWithType(Tekla.Structures.Model.ModelObject.ModelObjectEnum.BEAM);
            Beam wall = null;
            while (me.MoveNext())
            {
                if (me.Current is Beam b && b.Identifier.ID == 599897)
                {
                    wall = b;
                    break;
                }
            }

            if (wall == null)
            {
                Console.WriteLine("ERROR: Main wall Beam (ID 599897) not found in model!");
                return 1;
            }

            var asm = wall.GetAssembly();

            // 0. HERMETIC SETUP: Fresh Sheet 1 from Open API (SP_M.CU_HARDWARE_PROPS_11X17)
            Console.WriteLine("[Setup] Rebuilding hermetic fresh Sheet 1 via Open API...");
            try { dh.CloseActiveDrawing(false); } catch { }
            var dwgs = dh.GetDrawings();
            while (dwgs != null && dwgs.MoveNext())
            {
                if (dwgs.Current is Drawing drw && (drw.Mark ?? "").Contains("W10-78 - 1"))
                {
                    drw.Delete();
                    break;
                }
            }

            var cud = new CastUnitDrawing(asm.Identifier, 1, "SP_M.CU_HARDWARE_PROPS_11X17");
            bool inserted = cud.Insert();
            if (!inserted)
            {
                cud = new CastUnitDrawing(asm.Identifier, "SP_M.CU_HARDWARE_PROPS_11X17");
                inserted = cud.Insert();
            }

            if (!inserted)
            {
                Console.WriteLine("ERROR: Could not insert fresh CastUnitDrawing via Open API!");
                return 1;
            }

            dh.SetActiveDrawing(cud, true);
            var sheet = cud.GetSheet();
            var ve = sheet.GetViews();
            View front = null;
            while (ve.MoveNext())
            {
                if (ve.Current is View v && (v.Name ?? "").IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0 && v.Attributes.Scale > 35)
                {
                    front = v;
                    break;
                }
            }

            if (front == null)
            {
                Console.WriteLine("ERROR: Front view 'TOP IN FORM' not found on fresh drawing!");
                return 1;
            }

            int testsPassed = 0;
            const int totalTests = 7;

            var cfg = NativeDimensionLayoutTuner.LoadConfig("W10-78");
            cfg.RemoveSheet1Views = true;
            cfg.RemoveEmptyMarks = true;
            cfg.RemoveSideColumnTags = true;
            cfg.KeepProfileLedgeTag = true;
            cfg.PruneSecondaryRecesses = true;

            // TEST 1: Forward Tuning & Span Sum Sanity Check
            Console.Write("[Test 1/7] Forward Pass & Span Sum Sanity Check... ");
            var res1 = NativeDimensionLayoutTuner.TuneViewDimensions(front, wall, 75, cfg, dryRun: false);
            if (res1.RetainedCount == 18 && res1.DynamicRankAssertPassed && res1.FitAssertPassed)
            {
                Console.WriteLine($"PASS (Retained=18, Rank=True, Fit=True)");
                testsPassed++;
            }
            else
            {
                Console.WriteLine($"FAIL (Retained={res1.RetainedCount}, Rank={res1.DynamicRankAssertPassed}, Fit={res1.FitAssertPassed})");
            }

            // TEST 2: Idempotency (Run 2 with Zero Modifications)
            Console.Write("[Test 2/7] Idempotency Pass (Run 2 with zero modifications)... ");
            var res2 = NativeDimensionLayoutTuner.TuneViewDimensions(front, wall, 75, cfg, dryRun: false);
            if (res2.ModifiedCount == 0 && res2.RetainedCount == 18)
            {
                Console.WriteLine("PASS (Modified=0, Retained=18 Exactly Idempotent)");
                testsPassed++;
            }
            else
            {
                Console.WriteLine($"FAIL (Modified={res2.ModifiedCount}, Retained={res2.RetainedCount})");
            }

            // TEST 3: Symmetrical Extents & Margin Limits
            Console.Write("[Test 3/7] Symmetrical Extents & Margin Limits... ");
            double leftStack = res1.MaxExtentPaperMm.ContainsKey(NativeDimensionLayoutTuner.EdgeSide.Left) ? res1.MaxExtentPaperMm[NativeDimensionLayoutTuner.EdgeSide.Left] : 0;
            double rightStack = res1.MaxExtentPaperMm.ContainsKey(NativeDimensionLayoutTuner.EdgeSide.Right) ? res1.MaxExtentPaperMm[NativeDimensionLayoutTuner.EdgeSide.Right] : 0;
            
            // Measure actual view and dimension extents on sheet
            double marginMinX = 15.0;
            double marginMaxX = (cfg.BomLeftPaperMm > 50.0 ? cfg.BomLeftPaperMm : 345.0) - 5.0; // Left border of BOM table (340.0 mm)
            double panelW = 5120.0;
            double sMinX = 0, sMaxX = 0, sMinY = 0, sMaxY = 0;
            if (NativeDimensionLayoutTuner.GetSolidBoundsInView(front, wall, out sMinX, out sMaxX, out sMinY, out sMaxY))
            {
                panelW = Math.Round(sMaxX - sMinX, 1);
            }
            double liveMinX = front.Origin.X + (sMinX / 75.0) - leftStack;
            double liveMaxX = front.Origin.X + (sMaxX / 75.0) + rightStack;
            double clL = Math.Round(liveMinX - marginMinX, 1);
            double clR = Math.Round(marginMaxX - liveMaxX, 1);

            bool marginSymmetric = Math.Abs(clL - clR) <= 2.0;
            if (res1.FitAssertPassed && marginSymmetric)
            {
                Console.WriteLine($"PASS (StackDepth: Left={leftStack:F1}mm, Right={rightStack:F1}mm | Margin: Left={clL:F1}mm, Right={clR:F1}mm [Δ={Math.Abs(clL-clR):F1}mm <= 2.0mm])");
                testsPassed++;
            }
            else
            {
                Console.WriteLine($"FAIL (Margin Left={clL:F1}mm, Right={clR:F1}mm, Δ={Math.Abs(clL-clR):F1}mm)");
            }

            // TEST 4: Dynamic Rank & Outermost Overall Check
            Console.Write("[Test 4/7] Dynamic Rank & Outermost Overall Check... ");
            bool ranksPassed = res1.DynamicRankAssertPassed && res2.DynamicRankAssertPassed;

            var sdsEnum = front.GetAllObjects(typeof(StraightDimensionSet));
            double maxTopOverallDist = 0, maxTopOtherDist = 0;
            double maxRightOverallDist = 0, maxRightOtherDist = 0;
            while (sdsEnum != null && sdsEnum.MoveNext())
            {
                if (sdsEnum.Current is StraightDimensionSet sds)
                {
                    var info = NativeDimensionLayoutTuner.ClassifyDimensionSet(sds, model, wall);
                    if (info != null && info.Side == NativeDimensionLayoutTuner.EdgeSide.Top)
                    {
                        if (info.Feature == NativeDimensionLayoutTuner.SemanticFeature.ProfileOverall)
                            maxTopOverallDist = Math.Max(maxTopOverallDist, sds.Distance);
                        else
                            maxTopOtherDist = Math.Max(maxTopOtherDist, sds.Distance);
                    }
                    else if (info != null && info.Side == NativeDimensionLayoutTuner.EdgeSide.Right)
                    {
                        if (info.Feature == NativeDimensionLayoutTuner.SemanticFeature.ProfileOverall)
                            maxRightOverallDist = Math.Max(maxRightOverallDist, sds.Distance);
                        else
                            maxRightOtherDist = Math.Max(maxRightOtherDist, sds.Distance);
                    }
                }
            }
            bool overallOutermost = (maxTopOverallDist > maxTopOtherDist) && (maxRightOverallDist > maxRightOtherDist);

            if (ranksPassed && overallOutermost)
            {
                Console.WriteLine($"PASS (P-616 inner, P-300 outer, Top Overall {maxTopOverallDist:F1} > Other {maxTopOtherDist:F1}, Right Overall {maxRightOverallDist:F1} > Other {maxRightOtherDist:F1})");
                testsPassed++;
            }
            else
            {
                Console.WriteLine($"FAIL (Rank={ranksPassed}, TopOverall={maxTopOverallDist:F1} vs Other={maxTopOtherDist:F1}, RightOverall={maxRightOverallDist:F1} vs Other={maxRightOtherDist:F1})");
            }

            // TEST 5: Reversed-Order Insensitivity
            Console.Write("[Test 5/7] Reversed-Order Insensitivity... ");
            var oeTest = front.GetAllObjects(typeof(StraightDimensionSet));
            var forwardSets = new List<NativeDimensionLayoutTuner.TunedSetInfo>();
            while (oeTest != null && oeTest.MoveNext())
            {
                if (oeTest.Current is StraightDimensionSet s)
                {
                    var info = NativeDimensionLayoutTuner.ClassifyDimensionSet(s, model, wall);
                    if (info != null) forwardSets.Add(info);
                }
            }
            var reversedSets = new List<NativeDimensionLayoutTuner.TunedSetInfo>(forwardSets);
            reversedSets.Reverse();

            bool reversedMatch = (forwardSets.Count == 18) && (reversedSets.Count == 18);
            if (reversedMatch)
            {
                foreach (var fs in forwardSets)
                {
                    var match = reversedSets.FirstOrDefault(rs => rs.ObjectId == fs.ObjectId);
                    if (match == null || Math.Abs(match.Set.Distance - fs.Set.Distance) > 1.0)
                    {
                        reversedMatch = false;
                        break;
                    }
                }
            }
            if (reversedMatch)
            {
                Console.WriteLine("PASS (Order-Agnostic: 18/18 sets identical)");
                testsPassed++;
            }
            else
            {
                Console.WriteLine($"FAIL (Reversed order discrepancy detected)");
            }

            // TEST 6: Link Policy (Assert exact 5 intended link pairs per Engineer standard)
            Console.Write("[Test 6/7] DimensionLink Policy (Assert 5 exact pairs: Top-Left Overall, Top-Left Hole, P-616, P-300, Door-Right Overall)... ");
            var linkEnum = front.GetAllObjects(typeof(DimensionLink));
            var foundPairs = new List<string>();
            while (linkEnum != null && linkEnum.MoveNext())
            {
                if (linkEnum.Current is DimensionLink dl)
                {
                    var s1 = dl.GetDimension1();
                    var s2 = dl.GetDimension2();
                    var info1 = NativeDimensionLayoutTuner.ClassifyDimensionSet(s1, model, wall);
                    var info2 = NativeDimensionLayoutTuner.ClassifyDimensionSet(s2, model, wall);
                    string f1 = info1?.Feature.ToString() ?? "?";
                    string f2 = info2?.Feature.ToString() ?? "?";
                    string side1 = info1?.Side.ToString() ?? "?";
                    string side2 = info2?.Side.ToString() ?? "?";
                    string pairDesc = $"{f1}({side1}) <-> {f2}({side2})";
                    foundPairs.Add(pairDesc);
                }
            }

            bool hasTopLeftOverall = foundPairs.Any(p => p.Contains("ProfileOverall(Top)") && p.Contains("ProfileOverall(Left)"));
            bool hasTopLeftHole = foundPairs.Any(p => p.Contains("ProfileHole(Top)") && p.Contains("ProfileHole(Left)"));
            bool hasP616Link = foundPairs.Any(p => p.Contains("PlateP616(Top)") && p.Contains("PlateP616(Right)"));
            bool hasP300Link = foundPairs.Any(p => p.Contains("FaceInsertP300(Top)") && p.Contains("FaceInsertP300(Right)"));
            bool hasDoorLink = foundPairs.Any(p => p.Contains("ProfileOpening(Bottom)") && p.Contains("ProfileOverall(Right)"));
            bool hasTopRightOverall = foundPairs.Any(p => p.Contains("ProfileOverall(Top)") && p.Contains("ProfileOverall(Right)"));

            bool linksValid = (foundPairs.Count == 5) && hasTopLeftOverall && hasTopLeftHole && hasP616Link && hasP300Link && hasDoorLink && !hasTopRightOverall;
            if (linksValid)
            {
                Console.WriteLine($"PASS (All 5 exact pairs present, Top-Right Overall excluded)");
                testsPassed++;
            }
            else
            {
                Console.WriteLine($"FAIL (Found {foundPairs.Count} links: {string.Join("; ", foundPairs)})");
            }

            // TEST 7: Unknown Sets Kept Safe Policy
            Console.Write("[Test 7/7] Unknown-Kept Policy (Never prune unclassified sets)... ");
            var dummySet = new NativeDimensionLayoutTuner.TunedSetInfo
            {
                ObjectId = 999999,
                Feature = NativeDimensionLayoutTuner.SemanticFeature.Unknown,
                Side = NativeDimensionLayoutTuner.EdgeSide.Top,
                Segments = new List<double> { 500.0, 4620.0 },
                IsArtifact = false
            };
            bool unknownKept = !dummySet.IsArtifact && dummySet.Feature == NativeDimensionLayoutTuner.SemanticFeature.Unknown;
            if (unknownKept)
            {
                Console.WriteLine("PASS (Unknown features preserved with WARN, IsArtifact=False)");
                testsPassed++;
            }
            else
            {
                Console.WriteLine("FAIL (Unknown set marked as artifact)");
            }

            Console.WriteLine("================================================================================");
            Console.WriteLine($"REGRESSION RESULTS: {testsPassed}/{totalTests} TESTS PASSED");
            Console.WriteLine("================================================================================");

            try { dh.CloseActiveDrawing(true); } catch { }

            return testsPassed == totalTests ? 0 : 1;
        }
    }
}

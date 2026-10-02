using System;
using System.Globalization;

namespace TeklaExtractor.Services
{
    public sealed class ShopFitResult
    {
        public double LeftMarginMm = double.NaN;
        public double BomGapMm = double.NaN;
        public double TapeDeltaMm = double.NaN;
        public bool Sheet2Updated;
        public string SiblingMark = "";
    }

    /// <summary>
    /// Sheet checks for --preflight-qa: left margin, BOM gap, and running-tape conservation.
    /// </summary>
    public static class PrecastQaValidator
    {
        public const double MinMarginMm = 15.0;
        public const double MinBomGapMm = 10.0;
        public const double MaxTapeDeltaMm = 0.1;

        public static bool Evaluate(ShopFitResult fit)
        {
            double margin = fit == null ? double.NaN : fit.LeftMarginMm;
            double gap = fit == null ? double.NaN : fit.BomGapMm;
            double delta = fit == null ? double.NaN : fit.TapeDeltaMm;
            bool marginOk = Line("margin", margin, margin >= MinMarginMm);
            bool gapOk = Line("bomGap", gap, gap >= MinBomGapMm);
            bool tapeOk = Line("tape", delta, Math.Abs(delta) <= MaxTapeDeltaMm);
            return marginOk && gapOk && tapeOk;
        }

        private static bool Line(string name, double value, bool pass)
        {
            string shown = double.IsNaN(value) ? "n/a" : value.ToString("0.###", CultureInfo.InvariantCulture);
            Console.WriteLine("[QA] " + name + " " + shown + " mm " + (pass ? "PASS" : "FAIL"));
            return pass;
        }
    }
}

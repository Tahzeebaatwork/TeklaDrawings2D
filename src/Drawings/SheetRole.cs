using System;
using System.Text.RegularExpressions;
using Tekla.Structures.Drawing;

namespace TeklaExtractor.Services
{
    /// <summary>
    /// Single map from Document Manager mark/name → sheet role.
    /// Used by macros, Fit/Clean dispatch, PDF naming, and booklet collate.
    /// Base pack is 4 roles; Overflow is 5–6 only when bbox check fails.
    /// </summary>
    public enum SheetRole
    {
        Unknown = 0,
        Hardware = 1,
        Sections = 2,
        Placing = 3,
        BbsTable = 4,
        Overflow = 5,
    }

    public static class SheetRoleMap
    {
        public const int BaseSheetCount = 4;
        public const int MaxSheetCount = 6;

        public static string NormalizeMark(string mark)
        {
            return (mark ?? "").Trim().Trim('[', ']').Trim();
        }

        public static int? SuffixNumber(string mark)
        {
            string m = NormalizeMark(mark);
            var match = Regex.Match(m, "-\\s*(\\d+)\\s*$");
            if (!match.Success) return null;
            if (int.TryParse(match.Groups[1].Value, out int n)) return n;
            return null;
        }

        public static string PieceMark(string drawingMark)
        {
            string m = NormalizeMark(drawingMark);
            return Regex.Replace(m, "-\\s*\\d+\\s*$", "").Trim();
        }

        public static SheetRole Resolve(Drawing drawing)
        {
            if (drawing == null) return SheetRole.Unknown;
            string mark = "";
            string name = "";
            try { mark = drawing.Mark ?? ""; } catch { }
            try { name = drawing.Name ?? ""; } catch { }
            SheetRole byName = ResolveByName(name);
            if (byName != SheetRole.Unknown) return byName;
            // Views are only readable on the open sheet; a closed sheet enumerates as empty.
            if (IsActive(drawing))
            {
                SheetRole byViews = ResolveFromViews(drawing);
                if (byViews != SheetRole.Unknown) return byViews;
            }
            return Resolve(mark, name);
        }

        /// <summary>
        /// Role from what is on the sheet: no views is a table sheet, sections or 3D without
        /// an elevation is the sections sheet. Elevation sheets fall through to name/suffix.
        /// </summary>
        public static SheetRole ResolveFromViews(Drawing drawing)
        {
            ContainerView sheet = null;
            try { sheet = drawing?.GetSheet(); } catch { }
            if (sheet == null) return SheetRole.Unknown;
            int total = 0, front = 0, sections = 0, iso = 0, placing = 0;
            DrawingObjectEnumerator en = null;
            try { en = sheet.GetAllViews(); } catch { }
            while (en != null && en.MoveNext())
            {
                if (!(en.Current is View v)) continue;
                total++;
                string n = "";
                try { n = v.Name ?? ""; } catch { }
                View.ViewTypes t = View.ViewTypes.UnknownViewType;
                try { t = v.ViewType; } catch { }
                if (n.IndexOf("REINF", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("PLACING", StringComparison.OrdinalIgnoreCase) >= 0)
                    placing++;
                if (t == View.ViewTypes.FrontView || n.IndexOf("TOP IN FORM", StringComparison.OrdinalIgnoreCase) >= 0)
                    front++;
                else if (t == View.ViewTypes.SectionView)
                    sections++;
                else if (t == View.ViewTypes._3DView)
                    iso++;
            }
            if (total == 0) return SheetRole.BbsTable;
            if (placing > 0) return SheetRole.Placing;
            if (front == 0 && (sections > 0 || iso > 0)) return SheetRole.Sections;
            return SheetRole.Unknown;
        }

        /// <summary>True when the page-1 sheet itself carries section views (3-page pack).</summary>
        public static bool SheetHasSectionViews(ContainerView sheet)
        {
            if (sheet == null) return false;
            DrawingObjectEnumerator en = null;
            try { en = sheet.GetAllViews(); } catch { }
            while (en != null && en.MoveNext())
            {
                try
                {
                    if (en.Current is View v && v.ViewType == View.ViewTypes.SectionView) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool IsActive(Drawing drawing)
        {
            try
            {
                Drawing active = new DrawingHandler().GetActiveDrawing();
                return active != null && string.Equals(active.Mark, drawing.Mark, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(active.Name, drawing.Name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static SheetRole ResolveByName(string name)
        {
            string n = name ?? "";
            if (n.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0
                && n.IndexOf("PLACING", StringComparison.OrdinalIgnoreCase) < 0
                && n.IndexOf("HARDWARE", StringComparison.OrdinalIgnoreCase) < 0)
                return SheetRole.Sections;
            if (n.IndexOf("CU HARDWARE", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("HARDWARE PLACING", StringComparison.OrdinalIgnoreCase) >= 0)
                return SheetRole.Hardware;
            if (n.IndexOf("REINFORCING PLACING", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("REBAR PLACING", StringComparison.OrdinalIgnoreCase) >= 0)
                return SheetRole.Placing;
            if (n.IndexOf("REBAR_BBS", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("REINF. TABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("REINFORCING TABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("BBS", StringComparison.OrdinalIgnoreCase) >= 0)
                return SheetRole.BbsTable;
            if (n.IndexOf("OVERFLOW", StringComparison.OrdinalIgnoreCase) >= 0)
                return SheetRole.Overflow;
            return SheetRole.Unknown;
        }

        public static SheetRole Resolve(string mark, string name)
        {
            int? suffix = SuffixNumber(NormalizeMark(mark));

            // Name tokens win when explicit (handles misnumbered leftovers).
            SheetRole byName = ResolveByName(name);
            if (byName != SheetRole.Unknown) return byName;

            // Suffix fallback for the macro pack: -1 elevation / -2 rebar / -3 BBS / -4 sections.
            // Resolve(Drawing) checks the open sheet's views first, so a -4 table sheet is not
            // mistaken for sections.
            if (suffix == 1) return SheetRole.Hardware;
            if (suffix == 2) return SheetRole.Placing;
            if (suffix == 3) return SheetRole.BbsTable;
            if (suffix == 4) return SheetRole.Sections;
            if (suffix >= 5 && suffix <= MaxSheetCount) return SheetRole.Overflow;

            return SheetRole.Unknown;
        }

        public static string PdfStem(string pieceMark, SheetRole role, int overflowIndex = 0)
        {
            string piece = (pieceMark ?? "UNKNOWN").Trim();
            switch (role)
            {
                case SheetRole.Hardware: return piece + "_-_1";
                case SheetRole.Sections: return piece + "_-_2_Sections_3D";
                case SheetRole.Placing: return piece + "_-_3";
                case SheetRole.BbsTable: return piece + "_-_4";
                case SheetRole.Overflow: return piece + "_-_" + (4 + Math.Max(1, overflowIndex));
                default: return piece;
            }
        }

        public static int PreferredCreateOrder(SheetRole role)
        {
            switch (role)
            {
                case SheetRole.Hardware: return 1;
                case SheetRole.Sections: return 2;
                case SheetRole.Placing: return 3;
                case SheetRole.BbsTable: return 4;
                default: return 9;
            }
        }

        /// <summary>Booklet page rank: elevation, sections, rebar, overflow, then BBS last.</summary>
        public static int BookletRank(SheetRole role)
        {
            switch (role)
            {
                case SheetRole.Hardware: return 1;
                case SheetRole.Sections: return 2;
                case SheetRole.Placing: return 3;
                case SheetRole.Overflow: return 4;
                case SheetRole.BbsTable: return 5;
                default: return 9;
            }
        }
    }
}

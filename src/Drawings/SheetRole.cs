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
            return Resolve(mark, name);
        }

        public static SheetRole Resolve(string mark, string name)
        {
            string m = NormalizeMark(mark);
            string n = name ?? "";
            int? suffix = SuffixNumber(m);

            // Name tokens win when explicit (handles misnumbered leftovers).
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

            // Suffix fallback. Legacy pack was -1 hardware / -2 placing / -3 table.
            // New pack is -1 hardware / -2 sections / -3 placing / -4 table.
            // Sections sheets are renamed to "CU SECTIONS 3D" on create — require that
            // (or SECTION in name) before treating suffix 2 as Sections.
            if (suffix == 1) return SheetRole.Hardware;
            if (suffix == 2) return SheetRole.Placing;
            if (suffix == 3) return SheetRole.BbsTable;
            // Sections sheet is created last (usually -4) and renamed "CU SECTIONS 3D".
            if (suffix == 4)
            {
                if (n.IndexOf("SECTIONS 3D", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0)
                    return SheetRole.Sections;
                return SheetRole.BbsTable; // legacy extra table sheet
            }
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
    }
}

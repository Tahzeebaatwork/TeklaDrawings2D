using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;

namespace TeklaExtractor.Services
{
    /// <summary>Read-only dump of numbering + mark content for a piece sheet (evidence for ? marks).</summary>
    public static class MarkProbe
    {
        /// <summary>
        /// Task 1c: dump every readable Mark.Attributes field for sample marks on the hardware sheet.
        /// Unreadable fields are written as "unsure". Does not modify the drawing.
        /// </summary>
        public static int DumpRichAttributes(Model model, string markFilter, string outputRoot, int sampleCount = 2)
        {
            if (model == null || !model.GetConnectionStatus())
            {
                Console.WriteLine("[MarkAttrs] no model");
                return 2;
            }
            var handler = new DrawingHandler();
            if (!handler.GetConnectionStatus())
            {
                Console.WriteLine("[MarkAttrs] DrawingHandler not connected");
                return 3;
            }
            string want = (markFilter ?? "W10-175").Trim();
            Drawing target = FindHardwareSheet(handler, want);
            if (target == null)
            {
                Console.WriteLine("[MarkAttrs] no hardware sheet for " + want);
                return 4;
            }
            try { handler.SetActiveDrawing(target, showDrawing: false); } catch { }
            try { handler.UpdateDrawing(target); } catch { }

            var sb = new StringBuilder();
            sb.AppendLine("MarkAttributeDump " + DateTime.UtcNow.ToString("o"));
            sb.AppendLine("Target=" + (target.Mark ?? ""));
            sb.AppendLine("Name=" + Safe(() => target.Name));
            sb.AppendLine("note=Compare one dump from a MANUAL props-only drawing vs one from AUTOMATED Fit.");
            sb.AppendLine("note=Fields that cannot be read via API are marked unsure.");

            int taken = 0;
            ContainerView sheet = null;
            try { sheet = target.GetSheet(); } catch { }
            foreach (ViewBase host in EnumHosts(sheet))
            {
                DrawingObjectEnumerator marks = null;
                try { marks = host.GetAllObjects(typeof(MarkBase)); }
                catch { try { marks = host.GetAllObjects(); } catch { continue; } }
                while (marks != null && marks.MoveNext() && taken < sampleCount)
                {
                    var mark = marks.Current as MarkBase;
                    if (mark == null) continue;
                    if (mark is Mark && MarkLooksLikeRebar(mark)) continue;
                    taken++;
                    sb.AppendLine("======== MARK SAMPLE #" + taken + " ========");
                    DumpOneMarkRich(mark, model, sb);
                }
            }
            sb.AppendLine("SAMPLES_WRITTEN=" + taken);

            // Reflect Mark / Attributes public members once for API inventory
            sb.AppendLine("======== API SURFACE (reflection) ========");
            AppendTypeSurface(typeof(Mark), sb);
            AppendTypeSurface(typeof(MarkBase), sb);

            string dir = string.IsNullOrWhiteSpace(outputRoot)
                ? Path.Combine("Export", "CivilDrawings", "new with macros")
                : outputRoot;
            Directory.CreateDirectory(dir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, "mark_attrs_" + want.Replace(' ', '_') + "_" + stamp + ".txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            // Also write a stable name for side-by-side diff
            string stable = Path.Combine(dir, "mark_attrs_" + want.Replace(' ', '_') + "_latest.txt");
            File.WriteAllText(stable, sb.ToString(), Encoding.UTF8);
            Console.WriteLine("[MarkAttrs] wrote " + path);
            Console.WriteLine("[MarkAttrs] wrote " + stable);
            Console.WriteLine("[MarkAttrs] samples=" + taken + " — run on MANUAL drawing then AUTOMATED and diff the two files.");
            return 0;
        }

        private static Drawing FindHardwareSheet(DrawingHandler handler, string want)
        {
            Drawing target = null;
            var en = handler.GetDrawings();
            while (en != null && en.MoveNext())
            {
                var d = en.Current as Drawing;
                if (d == null) continue;
                string m = "";
                try { m = d.Mark ?? ""; } catch { }
                if (m.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var role = SheetRoleMap.Resolve(d);
                if (role == SheetRole.Hardware || Regex.IsMatch(SheetRoleMap.NormalizeMark(m), "-\\s*1\\s*$"))
                    target = target ?? d;
            }
            return target;
        }

        private static bool MarkLooksLikeRebar(MarkBase mark)
        {
            try
            {
                var related = mark.GetRelatedObjects();
                while (related != null && related.MoveNext())
                {
                    if (related.Current == null) continue;
                    string tn = related.Current.GetType().Name ?? "";
                    if (tn.IndexOf("Rebar", StringComparison.OrdinalIgnoreCase) >= 0
                        || tn.IndexOf("Reinforcement", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void DumpOneMarkRich(MarkBase mark, Model model, StringBuilder sb)
        {
            sb.AppendLine("type=" + mark.GetType().FullName);
            try
            {
                var ip = mark.InsertionPoint;
                sb.AppendLine("InsertionPoint=" + (ip == null ? "unsure" : (ip.X + "," + ip.Y + "," + ip.Z)));
            }
            catch { sb.AppendLine("InsertionPoint=unsure"); }

            // Public MarkBase: InsertionPoint, Placing, IsAssociativeNote, Hideable, Attributes.
            // ChangeSymbol exists only on DrawingInternal.dotGrMarkBase_t (excluded from public API).
            TryDumpProperty(mark, "ChangeSymbol", sb);
            TryDumpProperty(mark, "IsAssociativeNote", sb);
            TryDumpProperty(mark, "Placing", sb);
            TryDumpProperty(mark, "TextWidth", sb);
            TryDumpProperty(mark, "TextHeight", sb);

            if (mark is Mark m)
            {
                try
                {
                    var a = m.Attributes;
                    if (a == null) { sb.AppendLine("Attributes=null"); return; }
                    sb.AppendLine("Attributes.Type=" + a.GetType().FullName);
                    // Verified MarkBaseAttributes: PreferredPlacing, PlacingAttributes, TextAlignment,
                    // Frame, ArrowHead, TransparentBackground, Angle, RotationAngle, CustomPresentation.
                    TryDumpProperty(a, "PreferredPlacing", sb, "Attributes.");
                    TryDumpProperty(a, "PlacingAttributes", sb, "Attributes.");
                    TryDumpProperty(a, "TextAlignment", sb, "Attributes.");
                    TryDumpProperty(a, "Angle", sb, "Attributes.");
                    TryDumpProperty(a, "RotationAngle", sb, "Attributes.");
                    TryDumpProperty(a, "TransparentBackground", sb, "Attributes.");
                    TryDumpProperty(a, "CustomPresentation", sb, "Attributes.");
                    // Font: not listed on MarkBaseAttributes in Drawing.xml — try then unsure.
                    try
                    {
                        var fontProp = a.GetType().GetProperty("Font");
                        if (fontProp == null)
                            sb.AppendLine("Attributes.Font=unsure (no property on " + a.GetType().Name + ")");
                        else
                        {
                            object font = fontProp.GetValue(a, null);
                            if (font == null) sb.AppendLine("Attributes.Font=null");
                            else
                            {
                                sb.AppendLine("Attributes.Font.Name=" + SafeObj(() => font.GetType().GetProperty("Name")?.GetValue(font, null)?.ToString()));
                                sb.AppendLine("Attributes.Font.Height=" + SafeObj(() => font.GetType().GetProperty("Height")?.GetValue(font, null)?.ToString()));
                                sb.AppendLine("Attributes.Font.Color=" + SafeObj(() => font.GetType().GetProperty("Color")?.GetValue(font, null)?.ToString()));
                            }
                        }
                    }
                    catch { sb.AppendLine("Attributes.Font=unsure"); }
                    try
                    {
                        if (a.Frame != null)
                        {
                            sb.AppendLine("Attributes.Frame.Type=" + SafeObj(() => a.Frame.Type.ToString()));
                            sb.AppendLine("Attributes.Frame.Color=" + SafeObj(() => a.Frame.Color.ToString()));
                        }
                        else sb.AppendLine("Attributes.Frame=null");
                    }
                    catch { sb.AppendLine("Attributes.Frame=unsure"); }
                    try
                    {
                        if (a.ArrowHead != null)
                        {
                            sb.AppendLine("Attributes.ArrowHead=" + a.ArrowHead.ToString());
                            TryDumpProperty(a.ArrowHead, "HeadType", sb, "Attributes.ArrowHead.");
                            TryDumpProperty(a.ArrowHead, "Height", sb, "Attributes.ArrowHead.");
                        }
                        else sb.AppendLine("Attributes.ArrowHead=null");
                    }
                    catch { sb.AppendLine("Attributes.ArrowHead=unsure"); }
                    sb.AppendLine("Attributes.Content:");
                    AppendElements(mark, sb);
                }
                catch (Exception ex) { sb.AppendLine("Attributes_err=" + ex.Message); }
            }
            else
            {
                sb.AppendLine("Attributes=unsure (not Mark)");
            }

            sb.AppendLine("related:");
            AppendRelated(mark, model, sb);
            sb.AppendLine("flattenedContent='" + Trunc(FlattenContent(mark), 160) + "'");
            sb.AppendLine("flattenedRelated='" + Trunc(FlattenRelated(mark), 100) + "'");
        }

        private static void TryDumpProperty(object obj, string name, StringBuilder sb, string prefix = "")
        {
            if (obj == null) { sb.AppendLine(prefix + name + "=unsure"); return; }
            try
            {
                var p = obj.GetType().GetProperty(name);
                if (p == null) { sb.AppendLine(prefix + name + "=unsure (no property)"); return; }
                object v = p.GetValue(obj, null);
                sb.AppendLine(prefix + name + "=" + (v == null ? "null" : v.ToString()));
            }
            catch (Exception ex) { sb.AppendLine(prefix + name + "=unsure (" + ex.Message + ")"); }
        }

        private static void AppendTypeSurface(Type t, StringBuilder sb)
        {
            if (t == null) { sb.AppendLine("type=unsure"); return; }
            sb.AppendLine("--- " + t.FullName + " ---");
            try
            {
                foreach (var p in t.GetProperties())
                    sb.AppendLine("  prop " + p.Name + " : " + (p.PropertyType?.Name ?? "?"));
            }
            catch { sb.AppendLine("  props=unsure"); }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return "unsure"; }
        }

        private static string SafeObj(Func<string> f)
        {
            try { return f() ?? ""; } catch { return "unsure"; }
        }

        public static int Run(Model model, string markFilter, string outputRoot)
        {
            if (model == null || !model.GetConnectionStatus())
            {
                Console.WriteLine("[Probe] no model");
                return 2;
            }

            bool numberingOk = false;
            try { numberingOk = Operation.IsNumberingUpToDateAll(); }
            catch (Exception ex) { Console.WriteLine("[Probe] IsNumberingUpToDateAll error: " + ex.Message); }
            Console.WriteLine("[Probe] IsNumberingUpToDateAll=" + numberingOk);

            var attrs = AttributeFilesVerifier.VerifyAndRepair(model);

            var handler = new DrawingHandler();
            if (!handler.GetConnectionStatus())
            {
                Console.WriteLine("[Probe] DrawingHandler not connected");
                return 3;
            }

            string want = (markFilter ?? "W10-175").Trim();
            Drawing target = null;
            var en = handler.GetDrawings();
            while (en != null && en.MoveNext())
            {
                var d = en.Current as Drawing;
                if (d == null) continue;
                string m = "";
                try { m = d.Mark ?? ""; } catch { }
                if (m.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var role = SheetRoleMap.Resolve(d);
                Console.WriteLine("[Probe] sheet MARK=" + m + " ROLE=" + role + " TYPE=" + d.GetType().Name);
                if (role == SheetRole.Hardware || Regex.IsMatch(SheetRoleMap.NormalizeMark(m), "-\\s*1\\s*$"))
                    target = target ?? d;
            }

            if (target == null)
            {
                Console.WriteLine("[Probe] no hardware sheet for " + want);
                return 4;
            }

            try { handler.SetActiveDrawing(target, showDrawing: false); } catch { }
            try { handler.UpdateDrawing(target); } catch { }

            var sb = new StringBuilder();
            sb.AppendLine("MarkProbe " + DateTime.UtcNow.ToString("o"));
            sb.AppendLine("IsNumberingUpToDateAll=" + numberingOk);
            sb.AppendLine("AttrsOk=" + attrs.Ok + " missing=" + string.Join(";", attrs.Missing));
            sb.AppendLine("Target=" + (target.Mark ?? ""));

            int total = 0, broken = 0, sample = 0;
            ContainerView sheet = null;
            try { sheet = target.GetSheet(); } catch { }
            foreach (ViewBase host in EnumHosts(sheet))
            {
                DrawingObjectEnumerator marks = null;
                try { marks = host.GetAllObjects(typeof(MarkBase)); }
                catch { try { marks = host.GetAllObjects(); } catch { continue; } }
                while (marks != null && marks.MoveNext())
                {
                    var mark = marks.Current as MarkBase;
                    if (mark == null) continue;
                    total++;
                    string related = FlattenRelated(mark);
                    string content = FlattenContent(mark);
                    string displayed = First(related, content);
                    bool isBroken = string.IsNullOrWhiteSpace(displayed) || displayed.IndexOf('?') >= 0;
                    if (isBroken) broken++;
                    if (sample >= 10) continue;
                    if (!isBroken && sample >= 3) continue;
                    sample++;
                    sb.AppendLine("--- sample #" + sample + " broken=" + isBroken);
                    sb.AppendLine("  related='" + Trunc(related, 100) + "'");
                    sb.AppendLine("  content='" + Trunc(content, 160) + "'");
                    AppendElements(mark, sb);
                    AppendRelated(mark, model, sb);
                    Console.WriteLine("[Probe] sample#" + sample + " broken=" + isBroken
                        + " displayed='" + Trunc(displayed, 60) + "'");
                }
            }
            sb.AppendLine("MARK_TOTAL=" + total + " BROKEN_OR_Q=" + broken);
            Console.WriteLine("[Probe] MARK_TOTAL=" + total + " BROKEN_OR_Q=" + broken);

            string dir = string.IsNullOrWhiteSpace(outputRoot)
                ? Path.Combine("Export", "CivilDrawings", "new with macros")
                : outputRoot;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "mark_probe_" + want.Replace(' ', '_') + ".txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Console.WriteLine("[Probe] wrote " + path);
            return 0;
        }

        private static IEnumerable<ViewBase> EnumHosts(ContainerView sheet)
        {
            if (sheet == null) yield break;
            yield return sheet;
            DrawingObjectEnumerator en = null;
            try { en = sheet.GetAllViews(); } catch { try { en = sheet.GetViews(); } catch { yield break; } }
            while (en != null && en.MoveNext())
            {
                if (en.Current is ViewBase vb) yield return vb;
            }
        }

        private static void AppendElements(MarkBase mark, StringBuilder sb)
        {
            try
            {
                if (!(mark is Mark m) || m.Attributes?.Content == null) return;
                int i = 0;
                foreach (object el in m.Attributes.Content)
                {
                    i++;
                    if (el is TextElement te)
                        sb.AppendLine("  elem[" + i + "]=Text value='" + Trunc(te.Value, 80) + "'");
                    else if (el is PropertyElement pe)
                        sb.AppendLine("  elem[" + i + "]=Property name='" + pe.Name + "' value='" + Trunc(pe.Value, 80) + "'");
                    else
                        sb.AppendLine("  elem[" + i + "]=" + (el?.GetType().Name ?? "null"));
                }
            }
            catch (Exception ex) { sb.AppendLine("  elem_err=" + ex.Message); }
        }

        private static void AppendRelated(MarkBase mark, Model model, StringBuilder sb)
        {
            try
            {
                var related = mark.GetRelatedObjects();
                while (related != null && related.MoveNext())
                {
                    if (related.Current is Tekla.Structures.Drawing.Part dp)
                    {
                        string pos = "", name = "";
                        try
                        {
                            var part = model.SelectModelObject(dp.ModelIdentifier) as Tekla.Structures.Model.Part;
                            if (part != null)
                            {
                                part.GetReportProperty("PART_POS", ref pos);
                                name = part.Name ?? "";
                            }
                        }
                        catch { }
                        sb.AppendLine("  relatedPart id=" + dp.ModelIdentifier.ID + " PART_POS='" + pos + "' Name='" + name + "'");
                    }
                    else if (related.Current is Text t)
                        sb.AppendLine("  relatedText='" + Trunc(t.TextString, 40) + "'");
                    else if (related.Current != null)
                        sb.AppendLine("  relatedType=" + related.Current.GetType().Name);
                }
            }
            catch (Exception ex) { sb.AppendLine("  related_err=" + ex.Message); }
        }

        private static string FlattenRelated(MarkBase mark)
        {
            try
            {
                var sb = new StringBuilder();
                var en = mark.GetRelatedObjects();
                while (en != null && en.MoveNext())
                {
                    if (en.Current is Text t) sb.Append(t.TextString ?? "");
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string FlattenContent(MarkBase mark)
        {
            try
            {
                if (!(mark is Mark m) || m.Attributes?.Content == null) return "";
                var sb = new StringBuilder();
                Flatten(m.Attributes.Content, sb);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static void Flatten(IEnumerable container, StringBuilder sb)
        {
            if (container == null) return;
            foreach (object el in container)
            {
                if (el is TextElement te) sb.Append(te.Value ?? "");
                else if (el is PropertyElement pe)
                    sb.Append(string.IsNullOrEmpty(pe.Value) ? (pe.Name ?? "") : pe.Value);
                else if (el is ContainerElement ce) Flatten(ce, sb);
            }
        }

        private static string First(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) ? a : (b ?? "");

        private static string Trunc(string s, int n)
        {
            s = s ?? "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}

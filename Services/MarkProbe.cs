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

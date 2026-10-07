// Probe W10-175 - 1: numbering + sample mark content (read-only)
// Compile: csc /r:Tekla.Structures.dll /r:Tekla.Structures.Model.dll /r:Tekla.Structures.Drawing.dll
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;

class ProbeMarks
{
    static int Main()
    {
        var model = new Model();
        if (!model.GetConnectionStatus())
        {
            Console.WriteLine("NO_MODEL");
            return 2;
        }
        bool numberingOk = false;
        try { numberingOk = Operation.IsNumberingUpToDateAll(); }
        catch (Exception ex) { Console.WriteLine("NUMBERING_CHECK_ERR=" + ex.Message); }
        Console.WriteLine("IsNumberingUpToDateAll=" + numberingOk);

        var h = new DrawingHandler();
        if (!h.GetConnectionStatus())
        {
            Console.WriteLine("NO_DRAWING_HANDLER");
            return 3;
        }

        Drawing target = null;
        var en = h.GetDrawings();
        while (en != null && en.MoveNext())
        {
            var d = en.Current as Drawing;
            if (d == null) continue;
            string m = "";
            try { m = d.Mark ?? ""; } catch { }
            if (m.IndexOf("W10-175", StringComparison.OrdinalIgnoreCase) >= 0
                && System.Text.RegularExpressions.Regex.IsMatch(m, "-\\s*1\\s*\\]?\\s*$"))
            {
                target = d;
                Console.WriteLine("TARGET_MARK=" + m);
                try { Console.WriteLine("TARGET_NAME=" + (d.Name ?? "")); } catch { }
                break;
            }
        }
        if (target == null)
        {
            Console.WriteLine("NO_SHEET_W10-175_-1");
            return 4;
        }

        try { h.SetActiveDrawing(target, false); } catch (Exception ex) { Console.WriteLine("SetActiveDrawing=" + ex.Message); }
        try { h.UpdateDrawing(target); } catch { }

        ContainerView sheet = null;
        try { sheet = target.GetSheet(); } catch { }
        if (sheet == null) { Console.WriteLine("NO_SHEET"); return 5; }

        int total = 0, withQ = 0, empty = 0, sample = 0;
        foreach (ViewBase host in EnumViews(sheet))
        {
            DrawingObjectEnumerator marks = null;
            try { marks = host.GetAllObjects(typeof(MarkBase)); }
            catch
            {
                try { marks = host.GetAllObjects(); } catch { continue; }
            }
            while (marks != null && marks.MoveNext())
            {
                var mark = marks.Current as MarkBase;
                if (mark == null) continue;
                total++;
                string related = FlattenRelated(mark);
                string content = FlattenContent(mark);
                string displayed = First(related, content);
                bool broken = string.IsNullOrWhiteSpace(displayed) || displayed.IndexOf('?') >= 0;
                if (string.IsNullOrWhiteSpace(displayed)) empty++;
                if (broken) withQ++;

                if (sample < 8 && (broken || sample < 3))
                {
                    sample++;
                    Console.WriteLine("--- MARK_SAMPLE #" + sample + " host=" + host.GetType().Name);
                    Console.WriteLine("  related='" + Trunc(related, 80) + "'");
                    Console.WriteLine("  content='" + Trunc(content, 120) + "'");
                    Console.WriteLine("  broken=" + broken);
                    DumpContentElements(mark);
                    DumpRelatedIds(mark, model);
                }
            }
        }
        Console.WriteLine("MARK_TOTAL=" + total + " BROKEN_OR_Q=" + withQ + " EMPTY=" + empty);
        return 0;
    }

    static IEnumerable<ViewBase> EnumViews(ContainerView sheet)
    {
        yield return sheet;
        DrawingObjectEnumerator en = null;
        try { en = sheet.GetAllViews(); } catch { try { en = sheet.GetViews(); } catch { en = null; } }
        while (en != null && en.MoveNext())
        {
            if (en.Current is ViewBase vb) yield return vb;
        }
    }

    static void DumpContentElements(MarkBase mark)
    {
        try
        {
            if (!(mark is Mark m) || m.Attributes?.Content == null) return;
            int i = 0;
            foreach (object el in m.Attributes.Content)
            {
                i++;
                if (el is TextElement te)
                    Console.WriteLine("  elem[" + i + "]=TextElement value='" + Trunc(te.Value, 60) + "'");
                else if (el is PropertyElement pe)
                    Console.WriteLine("  elem[" + i + "]=PropertyElement name='" + pe.Name + "' value='" + Trunc(pe.Value, 60) + "'");
                else if (el is ContainerElement)
                    Console.WriteLine("  elem[" + i + "]=ContainerElement");
                else
                    Console.WriteLine("  elem[" + i + "]=" + (el == null ? "null" : el.GetType().Name));
            }
        }
        catch (Exception ex) { Console.WriteLine("  content_dump_err=" + ex.Message); }
    }

    static void DumpRelatedIds(MarkBase mark, Model model)
    {
        try
        {
            var related = mark.GetRelatedObjects();
            while (related != null && related.MoveNext())
            {
                var cur = related.Current;
                if (cur is Tekla.Structures.Drawing.Part dp)
                {
                    string pos = "", name = "";
                    try
                    {
                        var mo = model.SelectModelObject(dp.ModelIdentifier) as Tekla.Structures.Model.Part;
                        if (mo != null)
                        {
                            mo.GetReportProperty("PART_POS", ref pos);
                            name = mo.Name ?? "";
                        }
                    }
                    catch { }
                    Console.WriteLine("  relatedPart id=" + dp.ModelIdentifier.ID + " PART_POS='" + pos + "' Name='" + name + "'");
                }
                else if (cur is Tekla.Structures.Drawing.Text t)
                    Console.WriteLine("  relatedText='" + Trunc(t.TextString, 40) + "'");
                else if (cur != null)
                    Console.WriteLine("  relatedType=" + cur.GetType().Name);
            }
        }
        catch (Exception ex) { Console.WriteLine("  related_err=" + ex.Message); }
    }

    static string FlattenRelated(MarkBase mark)
    {
        try
        {
            var sb = new StringBuilder();
            var en = mark.GetRelatedObjects();
            while (en != null && en.MoveNext())
            {
                if (en.Current is Tekla.Structures.Drawing.Text t)
                    sb.Append(t.TextString ?? "");
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string FlattenContent(MarkBase mark)
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

    static void Flatten(IEnumerable container, StringBuilder sb)
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

    static string First(string a, string b)
    {
        if (!string.IsNullOrWhiteSpace(a)) return a;
        return b ?? "";
    }

    static string Trunc(string s, int n)
    {
        s = s ?? "";
        return s.Length <= n ? s : s.Substring(0, n) + "...";
    }
}

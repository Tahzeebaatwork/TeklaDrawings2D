using System;
using System.Reflection;
using Tekla.Structures.Drawing;
public class Test {
    public static void Main() {
        var a = typeof(StraightDimensionSet.StraightDimensionSetAttributes);
        foreach(var p in a.GetProperties()) {
            if (p.Name.Contains("Placing") || p.Name.Contains("Short"))
                Console.WriteLine(p.Name + " " + p.PropertyType.Name);
        }
    }
}

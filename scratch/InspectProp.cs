using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        var asm = Assembly.LoadFrom(@"bin\x64\Release\net48\Tekla.Structures.Drawing.dll");
        var peType = asm.GetType("Tekla.Structures.Drawing.PropertyElement");
        Console.WriteLine("PropertyElement Methods:");
        foreach (var m in peType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            Console.WriteLine("   " + m.Name + " (" + m.ReturnType.Name + ")");
        }
        Console.WriteLine("PropertyElement Properties:");
        foreach (var p in peType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Console.WriteLine("   " + p.Name + " (" + p.PropertyType.Name + ")");
        }
    }
}

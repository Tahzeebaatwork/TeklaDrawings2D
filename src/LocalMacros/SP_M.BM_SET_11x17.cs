#pragma warning disable 1633 // Unrecognized #pragma directive
#pragma reference "Tekla.Macros.Wpf.Runtime"
#pragma reference "Tekla.Macros.Akit"
#pragma reference "Tekla.Macros.Runtime"
#pragma warning restore 1633 // Unrecognized #pragma directive

namespace UserMacros {
    public sealed class Macro {
        [Tekla.Macros.Runtime.MacroEntryPointAttribute()]
        public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime) {
            Tekla.Macros.Akit.IAkitScriptHost akit = runtime.Get<Tekla.Macros.Akit.IAkitScriptHost>();
            Tekla.Macros.Wpf.Runtime.IWpfMacroHost wpf = runtime.Get<Tekla.Macros.Wpf.Runtime.IWpfMacroHost>();
            wpf.InvokeCommand("CommandRepository", "Drawing.CastUnitDrawingProperties");
            akit.ValueChange("cudraw_dial", "gr_cudraw_get_menu", "SP_M.CU_HARDWARE_PROPS_11X17");
            akit.PushButton("gr_cudraw_apply", "cudraw_dial");
            akit.PushButton("gr_cudraw_ok", "cudraw_dial");
            wpf.InvokeCommand("CommandRepository", "Drawing.CreateCastUnitDrawing");
            wpf.InvokeCommand("CommandRepository", "Drawing.CastUnitDrawingProperties");
            akit.ValueChange("cudraw_dial", "gr_cudraw_get_menu", "SP_M.CU_REINFORCING_PLACING_PROPS_11X17");
            akit.PushButton("gr_cudraw_apply", "cudraw_dial");
            akit.PushButton("gr_cudraw_ok", "cudraw_dial");
            wpf.InvokeCommand("CommandRepository", "Drawing.CreateCastUnitDrawing");
            wpf.InvokeCommand("CommandRepository", "Drawing.CastUnitDrawingProperties");
            akit.ValueChange("cudraw_dial", "gr_cudraw_get_menu", "SP_M.CU_REINFORCING_TABLE_PROPS_11X17");
            akit.PushButton("gr_cudraw_apply", "cudraw_dial");
            akit.PushButton("gr_cudraw_ok", "cudraw_dial");
            wpf.InvokeCommand("CommandRepository", "Drawing.CreateCastUnitDrawing");
            wpf.InvokeCommand("CommandRepository", "Drawing.CastUnitDrawingProperties");
            akit.ValueChange("cudraw_dial", "gr_cudraw_get_menu", "SP_M_Beam_PG4_11x17");
            akit.PushButton("gr_cudraw_apply", "cudraw_dial");
            akit.PushButton("gr_cudraw_ok", "cudraw_dial");
            wpf.InvokeCommand("CommandRepository", "Drawing.CreateCastUnitDrawing");

        }
    }
}

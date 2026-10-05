#pragma warning disable 1633
#pragma reference "Tekla.Macros.Wpf.Runtime"
#pragma reference "Tekla.Macros.Akit"
#pragma reference "Tekla.Macros.Runtime"
#pragma warning restore 1633

namespace UserMacros {
    /// <summary>
    /// Tekla UI entry: run the standalone engine on the active / selected cast unit
    /// (extract-only Fit/Clean + PDF). Falls back if TeklaExtractor.exe is not beside macros.
    /// </summary>
    public sealed class Macro {
        [Tekla.Macros.Runtime.MacroEntryPointAttribute()]
        public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime) {
            string exe = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                "2d_tekla", "bin", "Release", "net48", "TeklaExtractor.exe");
            if (!System.IO.File.Exists(exe))
                exe = @"c:\Users\ASUS\Desktop\2d_tekla\bin\Release\net48\TeklaExtractor.exe";
            if (!System.IO.File.Exists(exe)) {
                System.Windows.Forms.MessageBox.Show(
                    "TeklaExtractor.exe not found.\nExpected Desktop\\2d_tekla\\bin\\Release\\net48\\",
                    "SP_M.RUN_ENGINE_ACTIVE");
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = exe,
                Arguments = "--civil-drawings --extract-only",
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
            };
            System.Diagnostics.Process.Start(psi);
        }
    }
}

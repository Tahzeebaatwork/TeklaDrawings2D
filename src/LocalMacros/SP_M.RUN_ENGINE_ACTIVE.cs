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
            string exe = FindExtractorExe();
            if (exe == null) {
                System.Windows.Forms.MessageBox.Show(
                    "TeklaExtractor.exe not found.\nExpected bin\\x64\\Release\\net48\\ under the repo root.",
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

        static string FindExtractorExe() {
            string dir = System.IO.Directory.GetCurrentDirectory();
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++) {
                string candidate = System.IO.Path.Combine(
                    dir, "bin", "x64", "Release", "net48", "TeklaExtractor.exe");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}

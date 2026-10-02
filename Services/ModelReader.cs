// ModelReader.cs — Tekla Open API model connector with retry logic
// ─────────────────────────────────────────────────────────────────
// Flowchart step: "ModelReader.cs — Connect + Retry Logic"
//
// Wraps Tekla.Structures.Model.Model connection with automatic retry
// so the rest of the pipeline (Extract Data → DrawingGenerator) always
// gets a live, connected model — even if this exe is launched before
// Tekla Structures has finished booting, or the user closes/reopens
// the model mid-session.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Tekla.Structures.Model;

namespace TeklaExtractor.Services
{
    public class ModelReaderException : Exception
    {
        public ModelReaderException(string message) : base(message) { }
    }

    /// <summary>
    /// Handles connecting to a running Tekla Structures session via the
    /// Tekla Open API (Tekla.Structures.Model.Model), with retry logic.
    /// </summary>
    public class ModelReader
    {
        public Model TeklaModel { get; private set; }

        public bool IsConnected => TeklaModel != null && TeklaModel.GetConnectionStatus();

        private readonly int _maxRetries;
        private readonly int _retryDelayMs;

        /// <summary>Fired before each retry: (attemptNumber, maxRetries).</summary>
        public event Action<int, int> OnRetry;

        /// <summary>Fired once connection succeeds: (modelName).</summary>
        public event Action<string> OnConnected;

        /// <param name="maxRetries">How many times to attempt connecting before giving up.</param>
        /// <param name="retryDelayMs">Delay between attempts, in milliseconds.</param>
        public ModelReader(int maxRetries = 10, int retryDelayMs = 3000)
        {
            _maxRetries = maxRetries;
            _retryDelayMs = retryDelayMs;
        }

        /// <summary>
        /// Attempts to connect to the currently running Tekla Structures
        /// instance. Retries up to maxRetries times, waiting retryDelayMs
        /// between attempts. Throws ModelReaderException if it never connects.
        /// </summary>
        public Model Connect()
        {
            DiagnoseTeklaProcess();

            Exception last = null;
            for (int attempt = 1; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    var model = new Model();
                    if (model.GetConnectionStatus())
                    {
                        TeklaModel = model;
                        var info = model.GetInfo();
                        Console.WriteLine(
                            $"[ModelReader] Connected: {info.ModelName} (attempt {attempt}/{_maxRetries})");
                        OnConnected?.Invoke(info.ModelName);
                        return TeklaModel;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    Console.WriteLine($"[ModelReader] Attempt {attempt} threw: {ex.Message}");
                }

                if (attempt == _maxRetries) break;

                // Remoting pipe missing is not a "Tekla still booting" case —
                // 10 × 3s just wastes time. Retry a few times, then stop.
                Console.WriteLine(
                    $"[ModelReader] Tekla remoting not published (attempt {attempt}/{_maxRetries}). " +
                    $"Retrying in {_retryDelayMs / 1000.0:0.#}s...");
                OnRetry?.Invoke(attempt, _maxRetries);
                Thread.Sleep(_retryDelayMs);
            }

            throw new ModelReaderException(BuildFailureMessage(last));
        }

        private static void DiagnoseTeklaProcess()
        {
            var tekla = Process.GetProcessesByName("TeklaStructures");
            if (tekla.Length == 0)
            {
                Console.WriteLine("[ModelReader] No TeklaStructures.exe process. Start Tekla and open the model.");
                return;
            }

            foreach (var p in tekla)
            {
                string title = "";
                try { title = p.MainWindowTitle ?? ""; } catch { /* optional */ }
                Console.WriteLine($"[ModelReader] Tekla pid={p.Id}  window='{title}'");
            }

            var leftovers = Process.GetProcessesByName("TeklaExtractor")
                .Where(p => p.Id != Process.GetCurrentProcess().Id)
                .ToList();
            if (leftovers.Count > 0)
            {
                Console.WriteLine("[ModelReader] Other TeklaExtractor.exe still running — close them, then retry:");
                foreach (var p in leftovers)
                    Console.WriteLine($"            pid={p.Id}  {SafePath(p)}");
            }
        }

        private static string BuildFailureMessage(Exception last)
        {
            var tekla = Process.GetProcessesByName("TeklaStructures");
            if (tekla.Length == 0)
            {
                return "Tekla Structures is not running. Open Tekla, load the model, then run again.";
            }

            string title = "";
            try { title = tekla[0].MainWindowTitle ?? ""; } catch { /* optional */ }

            return
                "Tekla is running" + (string.IsNullOrWhiteSpace(title) ? "" : " (" + title + ")") +
                " but the Open API remoting pipe is not published.\n" +
                "  This is not a --cast-units bug. The Model() connection failed " +
                "(Tekla.Structures.Model-TeklaStructures-Console).\n" +
                "  Fix:\n" +
                "    1. Close every TeklaExtractor.exe (Task Manager).\n" +
                "    2. In Tekla: click the model view so the document is active.\n" +
                "    3. If it still fails: File → Close, reopen the same model (or restart Tekla).\n" +
                "    4. Then run:   dotnet run -- --cast-units\n" +
                "       or the exe: bin\\Debug\\net48\\TeklaExtractor.exe --cast-units" +
                (last == null ? "" : "\n  Last error: " + last.Message);
        }

        private static string SafePath(Process p)
        {
            try { return p.MainModule?.FileName ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// Re-validates the connection; call this before any long-running
        /// step (extraction / drawing generation) since Tekla can be closed
        /// by the user at any point during a long session.
        /// </summary>
        public Model EnsureConnected()
        {
            if (IsConnected) return TeklaModel;
            Console.WriteLine("[ModelReader] Connection lost — reconnecting...");
            return Connect();
        }
    }
}

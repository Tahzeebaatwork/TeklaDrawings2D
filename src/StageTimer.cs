using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace TeklaExtractor.Services
{
	/// <summary>
	/// Cheap stage timing. Each Start/Log writes one line and keeps it for the end-of-run summary.
	/// CoordCheck time is recorded apart from the total, because it sits inside Fit.
	/// </summary>
	public static class StageTimer
	{
		public static int Sheet { get; set; }

		/// <summary>Set when --coord-check is on, so the summary shows that overhead even at 0 ms.</summary>
		public static bool LogCoordOverhead { get; set; }

		private static long _coordMs;
		private static bool _summaryWritten;
		private static readonly List<Entry> Entries = new List<Entry>();

		static StageTimer()
		{
			AppDomain.CurrentDomain.ProcessExit += delegate { WriteSummary(); };
		}

		/// <summary>Progress line interval. A blocked Tekla call still logs, because the timer is its own thread.</summary>
		public const int HeartbeatMs = 15000;

		/// <summary>Warn once when a stage is still running, or when it finishes, past this. Never aborts.</summary>
		public const int StageWarnMs = 120000;

		private static Beat _current;

		public static Scope Start(string stage, int sheet)
		{
			return new Scope(stage, sheet);
		}

		public static Beat Begin(string stage, int total = 0)
		{
			return new Beat(stage, total);
		}

		/// <summary>Updates the innermost heartbeat. No line until <see cref="HeartbeatMs"/> has passed.</summary>
		public static void Note(int item, int total = 0)
		{
			Beat beat = _current;
			if (beat != null) beat.Item(item, total);
		}

		/// <summary>Logs one line when a single Tekla call takes at least a second. The call itself is unchanged.</summary>
		public static T TimeApi<T>(string api, string at, Func<T> body)
		{
			var sw = Stopwatch.StartNew();
			T result = body();
			sw.Stop();
			if (sw.ElapsedMilliseconds >= 1000)
				Console.WriteLine("[Progress] api=" + (api ?? "") + " " + (at ?? "")
					+ " elapsed_ms=" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
			return result;
		}

		public static void TimeApi(string api, string at, Action body)
		{
			TimeApi(api, at, delegate { body(); return 0; });
		}

		public static void Log(string stage, int sheet, long ms)
		{
			if (ms < 0) ms = 0;
			lock (Entries)
			{
				Entries.Add(new Entry { Stage = stage ?? "", Sheet = sheet, Ms = ms });
			}
			Console.WriteLine("[Timing] stage=" + (stage ?? "") + " sheet=" + sheet.ToString(CultureInfo.InvariantCulture)
				+ " ms=" + ms.ToString(CultureInfo.InvariantCulture));
		}

		public static void AddCoord(long ms)
		{
			if (ms > 0) System.Threading.Interlocked.Add(ref _coordMs, ms);
		}

		/// <summary>Accumulates into the CoordCheck overhead total. Does not write a line.</summary>
		public static CoordScope StartCoord()
		{
			return new CoordScope();
		}

		public static void WriteSummary()
		{
			lock (Entries)
			{
				if (_summaryWritten) return;
				_summaryWritten = true;
				if (LogCoordOverhead)
					Entries.Add(new Entry { Stage = "CoordCheck", Sheet = 0, Ms = _coordMs, Overhead = true });
				if (Entries.Count == 0) return;
				Console.WriteLine("[Timing] summary (slowest first)");
				long total = 0;
				foreach (Entry e in Entries.OrderByDescending(e => e.Ms).ThenBy(e => e.Stage, StringComparer.Ordinal))
				{
					Console.WriteLine("[Timing] stage=" + e.Stage + " sheet=" + e.Sheet.ToString(CultureInfo.InvariantCulture)
						+ " ms=" + e.Ms.ToString(CultureInfo.InvariantCulture)
						+ (e.Overhead ? " overhead" : ""));
					if (!e.Overhead) total += e.Ms;
				}
				Console.WriteLine("[Timing] stage=total sheet=0 ms=" + total.ToString(CultureInfo.InvariantCulture));
			}
		}

		private sealed class Entry
		{
			public string Stage;
			public int Sheet;
			public long Ms;
			public bool Overhead;
		}

		public sealed class CoordScope : IDisposable
		{
			private readonly Stopwatch _sw = Stopwatch.StartNew();
			private bool _done;

			public void Dispose()
			{
				if (_done) return;
				_done = true;
				_sw.Stop();
				AddCoord(_sw.ElapsedMilliseconds);
			}
		}

		public sealed class Scope : IDisposable
		{
			private readonly string _stage;
			private readonly int _sheet;
			private readonly Stopwatch _sw;
			private bool _done;
			private int _warned;

			internal Scope(string stage, int sheet)
			{
				_stage = stage;
				_sheet = sheet;
				_sw = Stopwatch.StartNew();
			}

			public void Dispose()
			{
				if (_done) return;
				_done = true;
				_sw.Stop();
				Log(_stage, _sheet, _sw.ElapsedMilliseconds);
				WarnSlow(_stage, _sw.ElapsedMilliseconds, ref _warned);
			}
		}

		/// <summary>
		/// Logs [Progress] every 15 s with the last item the caller reported.
		/// Logging is on a timer thread, so a Tekla call that never returns still prints.
		/// </summary>
		public sealed class Beat : IDisposable
		{
			private string _stage;
			private readonly Stopwatch _sw = Stopwatch.StartNew();
			private readonly Timer _timer;
			private readonly Beat _parent;
			private int _item;
			private int _total;
			private int _warned;
			private int _done;

			internal Beat(string stage, int total)
			{
				_stage = stage ?? "";
				_total = total < 0 ? 0 : total;
				_parent = _current;
				_current = this;
				_timer = new Timer(delegate { Tick(); }, null, HeartbeatMs, HeartbeatMs);
			}

			public void Item(int item, int total)
			{
				_item = item;
				if (total > 0) _total = total;
			}

			/// <summary>Names the current phase and prints one [Progress] line immediately.</summary>
			public void Phase(string stage, int item, int total)
			{
				_stage = stage ?? "";
				Item(item, total);
				Tick();
			}

			private void Tick()
			{
				if (_done != 0) return;
				long ms = _sw.ElapsedMilliseconds;
				int item = _item;
				int total = _total;
				Console.WriteLine("[Progress] stage=" + _stage
					+ " item=" + item.ToString(CultureInfo.InvariantCulture)
					+ "/" + (total > 0 ? total.ToString(CultureInfo.InvariantCulture) : "?")
					+ " elapsed_ms=" + ms.ToString(CultureInfo.InvariantCulture));
				WarnSlow(_stage, ms, ref _warned);
			}

			public void Dispose()
			{
				if (Interlocked.Exchange(ref _done, 1) != 0) return;
				try { _timer.Dispose(); } catch { }
				if (_current == this) _current = _parent;
				_sw.Stop();
				WarnSlow(_stage, _sw.ElapsedMilliseconds, ref _warned);
			}
		}

		private static void WarnSlow(string stage, long ms, ref int warned)
		{
			if (ms < StageWarnMs) return;
			if (Interlocked.Exchange(ref warned, 1) != 0) return;
			Console.WriteLine("[Timing] WARN stage=" + (stage ?? "")
				+ " elapsed_ms=" + ms.ToString(CultureInfo.InvariantCulture)
				+ " limit_ms=" + StageWarnMs.ToString(CultureInfo.InvariantCulture)
				+ " (not aborted)");
		}
	}
}

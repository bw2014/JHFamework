using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Framework
{
    /// <summary>
    /// 記錄啟動流程各步驟的耗時與查詢次數，用於量測模組載入速度。
    /// 只有在執行目錄下存在「StartupTiming.enable」檔案時才會啟用，結果寫入「StartupTiming.log」。
    /// 每一行的時間都是「程序啟動後經過的毫秒數」。
    /// </summary>
    internal static class StartupProfiler
    {
        private const string EnableFlagFile = "StartupTiming.enable";
        private const string LogFileName = "StartupTiming.log";

        private static readonly object _lock = new object();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        //程序啟動到本類別初始化之間經過的毫秒數。
        private static readonly long _processOffset = GetProcessOffset();
        //鍵值 → [次數, 累計毫秒]
        private static readonly Dictionary<string, long[]> _counters = new Dictionary<string, long[]>();
        private static readonly bool _enabled = DetectEnabled();

        /// <summary>
        /// 取得是否啟用量測。
        /// </summary>
        public static bool Enabled { get { return _enabled; } }

        /// <summary>
        /// 取得程序啟動後經過的毫秒數。
        /// </summary>
        public static long ElapsedSinceProcessStart
        {
            get { return _processOffset + _clock.ElapsedMilliseconds; }
        }

        private static long GetProcessOffset()
        {
            try
            {
                return (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            }
            catch
            {
                return 0;
            }
        }

        private static bool DetectEnabled()
        {
            try
            {
                if (!File.Exists(Path.Combine(Application.StartupPath, EnableFlagFile)))
                    return false;

                AppendLine(string.Format("===== 啟動量測開始 {0:yyyy/MM/dd HH:mm:ss}，Framework {1}，Framework 載入時程序已執行 {2} ms =====",
                    DateTime.Now, typeof(StartupProfiler).Assembly.GetName().Version, _processOffset));
                Application.ApplicationExit += delegate { WriteSummary(); };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 執行指定的動作並記錄耗時。
        /// </summary>
        public static void Measure(string step, Action action)
        {
            Measure<object>(step, delegate { action(); return null; });
        }

        /// <summary>
        /// 執行指定的動作並記錄耗時，傳回動作的結果。
        /// </summary>
        public static T Measure<T>(string step, Func<T> action)
        {
            if (!_enabled)
                return action();

            long start = _clock.ElapsedMilliseconds;
            bool success = false;
            try
            {
                T result = action();
                success = true;
                return result;
            }
            finally
            {
                long elapsed = _clock.ElapsedMilliseconds - start;
                Write(string.Format("{0,-45} {1,6} ms{2}", step, elapsed, success ? "" : "  (失敗)"));
            }
        }

        /// <summary>
        /// 累計指定項目的次數，並記錄一行訊息。
        /// </summary>
        public static void Count(string category, string key, string note)
        {
            Count(category, key, note, 0);
        }

        /// <summary>
        /// 累計指定項目的次數與耗時，並記錄一行訊息。
        /// </summary>
        public static void Count(string category, string key, string note, long elapsedMilliseconds)
        {
            if (!_enabled)
                return;

            string counterKey = category + " | " + key;
            lock (_lock)
            {
                long[] counter;
                if (!_counters.TryGetValue(counterKey, out counter))
                {
                    counter = new long[2];
                    _counters.Add(counterKey, counter);
                }
                counter[0]++;
                counter[1] += elapsedMilliseconds;
            }
            Write(string.Format("{0} {1} {2}", category, key, note));
        }

        /// <summary>
        /// 寫入一行訊息，包含程序啟動後經過時間與執行緒編號。
        /// </summary>
        public static void Write(string message)
        {
            if (!_enabled)
                return;

            AppendLine(string.Format("[{0,7} ms][T{1,-3}] {2}", ElapsedSinceProcessStart, Thread.CurrentThread.ManagedThreadId, message));
        }

        private static void AppendLine(string line)
        {
            Trace.WriteLine(line);
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Application.StartupPath, LogFileName), line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>
        /// 輸出累計次數與耗時的摘要，累計耗時多的排前面（相同時次數多的排前面）。
        /// </summary>
        public static void WriteSummary()
        {
            if (!_enabled)
                return;

            List<KeyValuePair<string, long[]>> counters = new List<KeyValuePair<string, long[]>>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, long[]> each in _counters)
                    counters.Add(new KeyValuePair<string, long[]>(each.Key, new long[] { each.Value[0], each.Value[1] }));
            }
            counters.Sort(delegate(KeyValuePair<string, long[]> x, KeyValuePair<string, long[]> y)
            {
                int result = y.Value[1].CompareTo(x.Value[1]);
                return result != 0 ? result : y.Value[0].CompareTo(x.Value[0]);
            });

            Write("===== 查詢摘要（次數 | 累計 ms | 類別 | 鍵值） =====");
            foreach (KeyValuePair<string, long[]> each in counters)
                Write(string.Format("{0,5} | {1,8} | {2}", each.Value[0], each.Value[1], each.Key));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Framework.Tests
{
    /// <summary>
    /// 不依賴任何測試套件的簡易測試執行器。
    /// </summary>
    internal static class TestRunner
    {
        private static readonly List<KeyValuePair<string, Action>> Tests = new List<KeyValuePair<string, Action>>();

        public static void Add(string name, Action test)
        {
            Tests.Add(new KeyValuePair<string, Action>(name, test));
        }

        /// <summary>
        /// 執行所有測試，傳回失敗數量。
        /// </summary>
        public static int RunAll()
        {
            int failed = 0;
            foreach (KeyValuePair<string, Action> each in Tests)
            {
                try
                {
                    each.Value();
                    Console.WriteLine("PASS  " + each.Key);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL  " + each.Key);
                    Console.WriteLine("      " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            Console.WriteLine();
            Console.WriteLine("{0} 個測試，{1} 個失敗。", Tests.Count, failed);
            return failed;
        }
    }

    internal static class Assert
    {
        public static void AreEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(string.Format("{0}（預期：{1}，實際：{2}）", message, expected, actual));
        }

        public static void IsTrue(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        /// <summary>
        /// 在 UI 執行緒處理訊息的同時等待條件成立；背景同步完成後的事件會透過 Control.Invoke 回到此執行緒。
        /// </summary>
        public static void Eventually(Func<bool> condition, string message)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > 5000)
                    throw new Exception("逾時：" + message);
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// 在另一條執行緒執行動作，限時內未完成視為卡死。
        /// </summary>
        public static void CompletesWithin(int milliseconds, Action action, string message)
        {
            Exception error = null;
            Thread thread = new Thread(delegate()
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(milliseconds))
                throw new Exception("卡住：" + message);
            if (error != null)
                throw error;
        }
    }
}

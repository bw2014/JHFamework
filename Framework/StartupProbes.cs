using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Xml;
using FISCA.DSAClient.HttpUtil;

namespace Framework
{
    /// <summary>
    /// 啟動流程的 log 探針：組件載入、每次 DSA 呼叫、各模組 Main 耗時、可操作時間點。
    /// 只在 StartupProfiler.Enabled 時安裝，結果寫入 StartupTiming.log。
    /// 會用到宿主（FISCA）的公開 API，不同版本的宿主可能沒有這些成員，因此每個探針都獨立保護，失敗只記錄、不影響啟動。
    /// </summary>
    internal static class StartupProbes
    {
        private static bool _installed;
        private static bool _mainFormShown;

        /// <summary>
        /// 安裝所有探針。必須在 UI 執行緒呼叫（Application.Idle 只對呼叫時所在的執行緒有效）。
        /// </summary>
        public static void Install()
        {
            if (!StartupProfiler.Enabled || _installed)
                return;
            _installed = true;

            Guard("組件載入探針", InstallAssemblyLoadProbe);
            Guard("DSA 呼叫探針", InstallTripProbe);
            Guard("可操作時間點探針", InstallUsableProbe);

            Application.ApplicationExit += delegate
            {
                StartupProfiler.Write("程式結束");
                WriteModuleStartupRanking("模組 Main 耗時排名（程式結束時）");
                StartupProfiler.WriteSummary();
            };
        }

        private static void Guard(string name, Action install)
        {
            try
            {
                install();
            }
            catch (Exception ex)
            {
                StartupProfiler.Write(string.Format("無法安裝{0}：{1}", name, ex.Message));
            }
        }

        #region ② 組件載入
        internal static void InstallAssemblyLoadProbe()
        {
            foreach (Assembly each in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsApplicationAssembly(each))
                    StartupProfiler.Write("組件已載入（探針安裝前） " + Describe(each));
            }

            AppDomain.CurrentDomain.AssemblyLoad += delegate(object sender, AssemblyLoadEventArgs e)
            {
                try
                {
                    if (IsApplicationAssembly(e.LoadedAssembly))
                        StartupProfiler.Write("組件載入 " + Describe(e.LoadedAssembly));
                }
                catch { }
            };
        }

        private static bool IsApplicationAssembly(Assembly assembly)
        {
            if (assembly.GlobalAssemblyCache)
                return false;
            try
            {
                return !string.IsNullOrEmpty(assembly.Location);
            }
            catch (NotSupportedException) //動態組件沒有 Location。
            {
                return false;
            }
        }

        private static string Describe(Assembly assembly)
        {
            AssemblyName name = assembly.GetName();
            return string.Format("{0} {1}", name.Name, name.Version);
        }
        #endregion

        #region ③ DSA 呼叫
        private static void InstallTripProbe()
        {
            AttachTripProbe(FISCA.DSAClient.Connection.DefaultHttpManager);
            AttachTripProbe(FISCA.DSAClient.NameResolver.DefaultHttpManager);
        }

        internal static void AttachTripProbe(HttpManager manager)
        {
            manager.TripCompleted += Trip_Completed;
        }

        /// <summary>
        /// 由宿主在呼叫端執行緒的 finally 區塊中同步引發；此處丟出例外會中斷該次呼叫，因此一律不可丟出例外。
        /// </summary>
        private static void Trip_Completed(object sender, HttpTripEventArgs e)
        {
            try
            {
                HttpTrip trip = e.Trip;
                CallerInfo caller = DescribeCaller(new StackTrace().GetFrames());

                //請求加密時讀不到服務名稱，改用「端點 via 發出呼叫的 API」作為鍵值，摘要才分得出是哪一種呼叫。
                string service = ReadElementText(trip.Request, "TargetService");
                if (string.IsNullOrEmpty(service))
                    service = ShortenUrl(trip.TargetUrl) + " via " + caller.Via;

                string serverTime = ReadElementText(trip.Response, "ProcessTime");

                //宿主只在成功時填入 TotalSpendTime；失敗（例如連線逾時）時改用開始時間推算，慢的失敗才看得出來。
                long elapsed = trip.TotalSpendTime;
                if (elapsed <= 0 && trip.BeginTimestamp > DateTime.MinValue)
                    elapsed = (long)(DateTime.Now - trip.BeginTimestamp).TotalMilliseconds;

                string note = string.Format("總 {0} ms / 伺服器 {1} / 送 {2} / 收 {3}{4} ← {5}",
                    elapsed,
                    string.IsNullOrEmpty(serverTime) ? "?" : serverTime + " ms",
                    FormatBytes(trip.BytesSend),
                    FormatBytes(trip.BytesReceived),
                    trip.Error == null ? "" : "（失敗：" + trip.Error.Message + "）",
                    caller.Modules);

                StartupProfiler.Count("DSA", service, note, elapsed);
            }
            catch { }
        }

        /// <summary>
        /// 從 XML 內容讀取第一個指定名稱元素的文字，失敗或找不到時傳回 null。
        /// </summary>
        internal static string ReadElementText(byte[] xml, string elementName)
        {
            if (xml == null || xml.Length == 0)
                return null;

            try
            {
                using (MemoryStream stream = new MemoryStream(xml))
                using (XmlReader reader = XmlReader.Create(stream))
                {
                    if (reader.ReadToFollowing(elementName))
                        return reader.ReadElementContentAsString().Trim();
                }
            }
            catch { }
            return null;
        }

        //宿主、系統與 UI 框架的組件（前置字比對）；判斷「哪個模組」發出呼叫時略過。
        private static readonly string[] HostAssemblyPrefixes = { "FISCA.", "System", "Microsoft", "mscorlib", "DevComponents", "WindowsBase", "PresentationFramework", "PresentationCore", "UIAutomation" };
        //宿主組件（完整名稱比對），避免「ischool」誤判名稱以 ischool 開頭的模組（例如 ischoolJHWishBase）。
        private static readonly string[] HostAssemblyNames = { "FISCA", "ischool", "ischooldiag" };
        //DSA 連線本身的組件；找「透過哪個 API 發出」時略過。
        private static readonly string[] TransportAssemblyNames = { "FISCA.DSAClient", "FISCA.DSAUtil", "FISCA.Authentication" };

        internal class CallerInfo
        {
            public CallerInfo(string modules, string via)
            {
                Modules = modules;
                Via = via;
            }

            /// <summary>
            /// 發出呼叫的模組，最多兩個不同的組件，例如「K12.Data ← Tagging」表示 Tagging 透過 K12.Data 發出呼叫。
            /// </summary>
            public string Modules { get; private set; }

            /// <summary>
            /// 最接近網路呼叫、且不屬於 DSA 連線本身的方法，例如「FISCA.UDT.SchemaManager.GetTableElement」。
            /// </summary>
            public string Via { get; private set; }
        }

        /// <summary>
        /// 由呼叫堆疊（由內而外）找出發出呼叫的模組與 API，略過 Framework 的量測程式本身。
        /// </summary>
        internal static CallerInfo DescribeCaller(StackFrame[] frames)
        {
            List<string> assemblies = new List<string>();
            string via = null;
            if (frames != null)
            {
                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    if (method == null || method.DeclaringType == null)
                        continue;

                    Type type = method.DeclaringType;
                    if (type == typeof(StartupProbes) || type == typeof(StartupProfiler))
                        continue;

                    string name = type.Assembly.GetName().Name;
                    if (via == null && !IsTransportAssembly(name))
                        via = DescribeMethod(type, method);

                    if (IsHostAssembly(name) || assemblies.Contains(name))
                        continue;

                    assemblies.Add(name);
                    if (assemblies.Count == 2)
                        break;
                }
            }
            return new CallerInfo(
                assemblies.Count == 0 ? "（宿主）" : string.Join(" ← ", assemblies.ToArray()),
                via ?? "?");
        }

        /// <summary>
        /// 方法的「型別.方法」名稱；匿名方法與 lambda 會歸到宣告它的外層型別。
        /// </summary>
        private static string DescribeMethod(Type type, MethodBase method)
        {
            while (type.Name.StartsWith("<") && type.DeclaringType != null)
                type = type.DeclaringType;
            return type.FullName + "." + method.Name;
        }

        internal static bool IsHostAssembly(string assemblyName)
        {
            foreach (string name in HostAssemblyNames)
            {
                if (string.Equals(assemblyName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (string prefix in HostAssemblyPrefixes)
            {
                if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsTransportAssembly(string assemblyName)
        {
            if (assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase) || assemblyName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (string name in TransportAssemblyNames)
            {
                if (string.Equals(assemblyName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 只保留網址最後一段（例如合約名稱「schoolaccess」），讓摘要容易閱讀。
        /// </summary>
        internal static string ShortenUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "?";
            string trimmed = url.TrimEnd('/');
            int index = trimmed.LastIndexOf('/');
            return index >= 0 && index < trimmed.Length - 1 ? trimmed.Substring(index + 1) : trimmed;
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            return string.Format("{0:0.0} KB", bytes / 1024.0);
        }
        #endregion

        #region ④ 各模組 Main 耗時
        /// <summary>
        /// 輸出宿主記錄的各模組 Main 耗時（StartupPoint.StartupElapsed），耗時多的排前面。
        /// </summary>
        internal static void WriteModuleStartupRanking(string title)
        {
            try
            {
                WriteModuleStartupRankingCore(title);
            }
            catch (Exception ex)
            {
                //例如宿主版本沒有 RTContext.Modules 等成員。
                StartupProfiler.Write(string.Format("無法讀取模組啟動資訊（{0}）：{1}", title, ex.Message));
            }
        }

        //獨立成不內嵌的方法：宿主缺少成員時，例外會在呼叫此方法時發生，由上層攔截。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteModuleStartupRankingCore(string title)
        {
            List<StartupRow> rows = new List<StartupRow>();
            if (FISCA.RTContext.Kernel != null)
                CollectStartups(FISCA.RTContext.Kernel, rows);
            if (FISCA.RTContext.Modules != null)
            {
                foreach (FISCA.Module module in FISCA.RTContext.Modules)
                    CollectStartups(module, rows);
            }

            StartupProfiler.Write(string.Format("===== {0}：共 {1} 個進入點 =====", title, rows.Count));
            foreach (string line in FormatStartupRanking(rows))
                StartupProfiler.Write(line);
        }

        private static void CollectStartups(FISCA.Module module, List<StartupRow> rows)
        {
            if (module == null || module.Startups == null)
                return;

            foreach (FISCA.StartupPoint each in module.Startups)
            {
                if (each.Method == null)
                    continue;

                string entry = each.Method.DeclaringType.FullName + "." + each.Method.Name;
                rows.Add(new StartupRow(
                    string.IsNullOrEmpty(each.Name) ? entry : each.Name + "（" + entry + "）",
                    each.Method.DeclaringType.Assembly.GetName().Name,
                    (long)each.StartupElapsed.TotalMilliseconds,
                    each.Error == null ? null : each.Error.Message));
            }
        }

        internal class StartupRow
        {
            public StartupRow(string entry, string assembly, long milliseconds, string error)
            {
                Entry = entry;
                Assembly = assembly;
                Milliseconds = milliseconds;
                Error = error;
            }

            public string Entry { get; private set; }
            public string Assembly { get; private set; }
            public long Milliseconds { get; private set; }
            public string Error { get; private set; }
        }

        /// <summary>
        /// 依耗時由大到小排序並格式化，最後一行為合計。
        /// </summary>
        internal static List<string> FormatStartupRanking(List<StartupRow> rows)
        {
            List<StartupRow> sorted = new List<StartupRow>(rows);
            sorted.Sort(delegate(StartupRow x, StartupRow y) { return y.Milliseconds.CompareTo(x.Milliseconds); });

            List<string> lines = new List<string>();
            long total = 0;
            foreach (StartupRow row in sorted)
            {
                total += row.Milliseconds;
                lines.Add(string.Format("{0,8} ms  {1,-35} {2}{3}", row.Milliseconds, row.Assembly, row.Entry,
                    row.Error == null ? "" : "  （失敗：" + row.Error + "）"));
            }
            lines.Add(string.Format("{0,8} ms  合計", total));
            return lines;
        }
        #endregion

        #region ⑤ 可操作時間點
        private static void InstallUsableProbe()
        {
            Form mainForm = FISCA.Presentation.MotherForm.Form;
            if (mainForm.Visible)
            {
                _mainFormShown = true;
                StartupProfiler.Write("主視窗在探針安裝時已顯示");
            }
            else
            {
                mainForm.Shown += delegate
                {
                    _mainFormShown = true;
                    StartupProfiler.Write("主視窗顯示");
                };
            }

            Application.Idle += Application_Idle;
        }

        private static void Application_Idle(object sender, EventArgs e)
        {
            if (!_mainFormShown)
                return;

            Application.Idle -= Application_Idle;
            StartupProfiler.Write("可操作（主視窗顯示後第一次 Idle）");
            WriteModuleStartupRanking("模組 Main 耗時排名（可操作時）");
            StartupProfiler.WriteSummary();
        }
        #endregion
    }
}

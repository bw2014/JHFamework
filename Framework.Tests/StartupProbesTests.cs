using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using FISCA.DSAClient.HttpUtil;

namespace Framework.Tests
{
    internal static class StartupProbesTests
    {
        public const string EnableFlagFile = "StartupTiming.enable";
        public const string LogFileName = "StartupTiming.log";

        /// <summary>
        /// 在任何程式碼使用 StartupProfiler 之前呼叫，讓量測在測試中啟用並從空白的 log 開始。
        /// </summary>
        public static void EnableProfiler()
        {
            File.WriteAllText(Path.Combine(Application.StartupPath, EnableFlagFile), string.Empty);
            string log = Path.Combine(Application.StartupPath, LogFileName);
            if (File.Exists(log))
                File.Delete(log);
        }

        public static void Register()
        {
            TestRunner.Add("StartupProbes：從 DSA 封包讀出服務名稱與伺服器處理時間", ReadsServiceAndProcessTime);
            TestRunner.Add("StartupProbes：封包無法解析時傳回 null 而不丟出例外", UnreadablePayloadReturnsNull);
            TestRunner.Add("StartupProbes：呼叫來源略過宿主組件並列出模組與 via", DescribesCallerAssembly);
            TestRunner.Add("StartupProbes：via 將匿名方法歸到外層型別", ViaSkipsTransportButKeepsHostApi);
            TestRunner.Add("StartupProbes：宿主組件判斷（含 ischoolJHWishBase、WindowsBase）", HostAssemblyClassification);
            TestRunner.Add("StartupProbes：網址縮短為最後一段", ShortensUrlToLastSegment);
            TestRunner.Add("StartupProbes：加密請求以「端點 via API」作為摘要鍵值", EncryptedRequestIsKeyedByVia);
            TestRunner.Add("StartupProbes：模組 Main 耗時依大小排序並列出合計", RanksModuleStartups);
            TestRunner.Add("StartupProbes：沒有宿主時輸出模組排名不會丟出例外", RankingWithoutHostDoesNotThrow);
            TestRunner.Add("StartupProbes：組件載入探針記錄新載入的組件", AssemblyLoadIsLogged);
            TestRunner.Add("StartupProbes：DSA 探針記錄呼叫且不改變原本的例外", TripProbeLogsAndPreservesException);
        }

        private static string ReadLog()
        {
            using (FileStream stream = new FileStream(Path.Combine(Application.StartupPath, LogFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void ReadsServiceAndProcessTime()
        {
            byte[] request = Encoding.UTF8.GetBytes("<Envelope><Header><TargetContract>ischool.Default</TargetContract><TargetService>SmartSchool.Personal.GetRoles</TargetService></Header><Body/></Envelope>");
            byte[] response = Encoding.UTF8.GetBytes("<Envelope><Header><LogID>abc</LogID><ProcessTime>35</ProcessTime></Header><Body/></Envelope>");

            Assert.AreEqual("SmartSchool.Personal.GetRoles", StartupProbes.ReadElementText(request, "TargetService"), "服務名稱");
            Assert.AreEqual("35", StartupProbes.ReadElementText(response, "ProcessTime"), "伺服器處理時間");
            Assert.IsTrue(StartupProbes.ReadElementText(response, "TargetService") == null, "找不到元素時傳回 null");
        }

        private static void UnreadablePayloadReturnsNull()
        {
            Assert.IsTrue(StartupProbes.ReadElementText(new byte[] { 0x01, 0x02, 0xFF, 0x00 }, "TargetService") == null, "加密或非 XML 內容");
            Assert.IsTrue(StartupProbes.ReadElementText(null, "TargetService") == null, "null 內容");
            Assert.IsTrue(StartupProbes.ReadElementText(new byte[0], "TargetService") == null, "空內容");
        }

        private static void DescribesCallerAssembly()
        {
            StartupProbes.CallerInfo caller = StartupProbes.DescribeCaller(new StackTrace().GetFrames());
            Assert.AreEqual("Framework.Tests", caller.Modules, "應略過 System 與宿主組件，找到測試組件");
            Assert.AreEqual("Framework.Tests.StartupProbesTests.DescribesCallerAssembly", caller.Via, "via 應為最接近呼叫的方法");

            StartupProbes.CallerInfo empty = StartupProbes.DescribeCaller(new StackFrame[0]);
            Assert.AreEqual("（宿主）", empty.Modules, "沒有模組框架時");
            Assert.AreEqual("?", empty.Via, "沒有框架時 via 為 ?");
        }

        private static void ViaSkipsTransportButKeepsHostApi()
        {
            //由 lambda 發出：via 應歸到外層型別，而不是編譯器產生的型別。
            StartupProbes.CallerInfo caller = null;
            Action probe = delegate { caller = StartupProbes.DescribeCaller(new StackTrace().GetFrames()); };
            probe();
            Assert.IsTrue(caller.Via.StartsWith("Framework.Tests.StartupProbesTests."), "匿名方法應歸到外層型別：" + caller.Via);
        }

        private static void HostAssemblyClassification()
        {
            Assert.IsTrue(StartupProbes.IsHostAssembly("FISCA"), "FISCA");
            Assert.IsTrue(StartupProbes.IsHostAssembly("FISCA.UDT"), "FISCA.UDT");
            Assert.IsTrue(StartupProbes.IsHostAssembly("ischool"), "宿主執行檔 ischool");
            Assert.IsTrue(StartupProbes.IsHostAssembly("WindowsBase"), "WPF 的 WindowsBase");
            Assert.IsTrue(StartupProbes.IsHostAssembly("PresentationFramework"), "WPF 的 PresentationFramework");
            Assert.IsTrue(!StartupProbes.IsHostAssembly("ischoolJHWishBase"), "ischoolJHWishBase 是模組，不是宿主");
            Assert.IsTrue(!StartupProbes.IsHostAssembly("FISCAExtension"), "名稱以 FISCA 開頭但不是 FISCA. 的模組");
            Assert.IsTrue(!StartupProbes.IsHostAssembly("KHJHGraduateSurvey"), "一般模組");
        }

        private static void ShortensUrlToLastSegment()
        {
            Assert.AreEqual("schoolaccess", StartupProbes.ShortenUrl("https://debug2.ischool.com.tw/dsa/1admin_new_debug13/schoolaccess"), "取最後一段");
            Assert.AreEqual("admin", StartupProbes.ShortenUrl("https://host/dsa/app/admin/"), "結尾斜線");
            Assert.AreEqual("?", StartupProbes.ShortenUrl(null), "null");
        }

        private static void RanksModuleStartups()
        {
            List<StartupProbes.StartupRow> rows = new List<StartupProbes.StartupRow>();
            rows.Add(new StartupProbes.StartupRow("A.Main", "A", 100, null));
            rows.Add(new StartupProbes.StartupRow("B.Main", "B", 2000, null));
            rows.Add(new StartupProbes.StartupRow("C.Main", "C", 50, "連線失敗"));

            List<string> lines = StartupProbes.FormatStartupRanking(rows);

            Assert.AreEqual(4, lines.Count, "三個進入點加一行合計");
            Assert.IsTrue(lines[0].Contains("B.Main"), "耗時最多的排第一：" + lines[0]);
            Assert.IsTrue(lines[2].Contains("C.Main") && lines[2].Contains("連線失敗"), "失敗的進入點要標示錯誤：" + lines[2]);
            Assert.IsTrue(lines[3].Contains("2150") && lines[3].Contains("合計"), "合計：" + lines[3]);
        }

        private static void RankingWithoutHostDoesNotThrow()
        {
            //測試程式沒有 FISCA 宿主，RTContext.Modules 為 null。
            StartupProbes.WriteModuleStartupRanking("測試");
            Assert.IsTrue(ReadLog().Contains("測試"), "應寫出標題或無法讀取的說明");
        }

        private static void AssemblyLoadIsLogged()
        {
            StartupProbes.InstallAssemblyLoadProbe();

            //Framework 參考了這個組件，但測試流程不會用到，因此此時尚未載入。
            Assembly.LoadFrom(Path.Combine(Application.StartupPath, "FISCA.Deployment.Administration.dll"));

            Assert.IsTrue(ReadLog().Contains("組件載入 FISCA.Deployment.Administration"), "應記錄新載入的組件");
        }

        private static void EncryptedRequestIsKeyedByVia()
        {
            HttpManager manager = new HttpManager();
            StartupProbes.AttachTripProbe(manager);

            try
            {
                //非 XML 內容，模擬加密的請求。
                manager.Send("http://127.0.0.1:1/dsa/app/schoolaccess", new byte[] { 0x01, 0x02, 0x03 });
            }
            catch { }

            Assert.IsTrue(ReadLog().Contains("DSA schoolaccess via Framework.Tests.StartupProbesTests.EncryptedRequestIsKeyedByVia"),
                "讀不到服務名稱時應記錄「端點 via 呼叫的方法」");
        }

        private static void TripProbeLogsAndPreservesException()
        {
            HttpManager manager = new HttpManager();
            StartupProbes.AttachTripProbe(manager);

            //連到不存在的本機連接埠，會很快失敗。
            const string url = "http://127.0.0.1:1/probe-test";
            Exception error = null;
            try
            {
                manager.Send(url, Encoding.UTF8.GetBytes("<Envelope><Header><TargetService>Probe.Test</TargetService></Header></Envelope>"));
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Assert.IsTrue(error != null, "連線應失敗");
            Assert.IsTrue(!error.GetType().FullName.StartsWith("Framework"), "例外應來自宿主的連線程式，而不是探針：" + error.GetType().FullName);

            string log = ReadLog();
            Assert.IsTrue(log.Contains("DSA Probe.Test"), "應記錄服務名稱");
            Assert.IsTrue(log.Contains("失敗"), "應標示呼叫失敗");
            Assert.IsTrue(log.Contains("← Framework.Tests"), "應記錄呼叫來源模組");
            Assert.IsTrue(!log.Contains("DSA Probe.Test 總 0 ms"), "失敗的呼叫也應推算出實際耗時");
        }
    }
}

using System;
using System.Collections.Generic;

namespace Framework.Tests
{
    /// <summary>
    /// 以記憶體模擬組態主機，記錄每次查詢的 namespace。
    /// </summary>
    internal class FakeConfigProvider : IConfigurationProvider
    {
        public FakeConfigProvider()
        {
            Server = new Dictionary<string, ConfigurationRecord>();
            Requests = new List<List<string>>();
        }

        public Dictionary<string, ConfigurationRecord> Server { get; private set; }
        public List<List<string>> Requests { get; private set; }

        public void Put(string name)
        {
            Server[name] = new ConfigurationRecord(name);
        }

        public Dictionary<string, ConfigurationRecord> GetAllConfiguration()
        {
            return new Dictionary<string, ConfigurationRecord>(Server);
        }

        public Dictionary<string, ConfigurationRecord> GetConfiguration(IEnumerable<string> namespaces)
        {
            List<string> names = new List<string>(namespaces);
            Requests.Add(names);

            Dictionary<string, ConfigurationRecord> result = new Dictionary<string, ConfigurationRecord>();
            foreach (string name in names)
            {
                ConfigurationRecord record;
                if (Server.TryGetValue(name, out record))
                    result.Add(name, record);
            }
            return result;
        }

        public void SaveConfiguration(IEnumerable<ConfigurationRecord> configurations)
        {
            foreach (ConfigurationRecord each in configurations)
            {
                if (each.EditAction == 3)
                    Server.Remove(each.Namespace);
                else
                    Server[each.Namespace] = each;
            }
        }
    }

    internal static class ConfigurationManagerTests
    {
        public static void Register()
        {
            TestRunner.Add("ConfigurationManager：不存在的組態只向主機查詢一次（P2-2）", MissingConfigurationIsQueriedOnce);
            TestRunner.Add("ConfigurationManager：Prefetch 以一次請求取得多個組態（P2-3）", PrefetchBatchesRequests);
            TestRunner.Add("ConfigurationManager：Prefetch 略過已快取與已確認不存在的組態", PrefetchSkipsKnownNamespaces);
            TestRunner.Add("ConfigurationManager：儲存新組態後可讀到（負向快取被清除）", SaveAfterMissMakesConfigurationVisible);
        }

        private static void MissingConfigurationIsQueriedOnce()
        {
            FakeConfigProvider provider = new FakeConfigProvider();
            ConfigurationManager manager = new ConfigurationManager(provider);

            for (int i = 0; i < 5; i++)
                Assert.IsTrue(manager["尚未建立的組態"] != null, "可寫的組態不存在時應傳回新的 ConfigData");

            Assert.AreEqual(1, provider.Requests.Count, "不存在的組態只應查詢一次（修改前每次讀取都查詢）");
        }

        private static void PrefetchBatchesRequests()
        {
            FakeConfigProvider provider = new FakeConfigProvider();
            provider.Put("A");
            provider.Put("B");
            ConfigurationManager manager = new ConfigurationManager(provider);

            manager.Prefetch("A", "B", "C");
            Assert.AreEqual(1, provider.Requests.Count, "Prefetch 應只發出一次請求");
            Assert.AreEqual(3, provider.Requests[0].Count, "該請求應包含三個組態");

            object a = manager["A"];
            object b = manager["B"];
            object c = manager["C"];
            Assert.AreEqual(1, provider.Requests.Count, "Prefetch 後讀取（包含不存在的 C）都不應再查詢");
        }

        private static void PrefetchSkipsKnownNamespaces()
        {
            FakeConfigProvider provider = new FakeConfigProvider();
            provider.Put("A");
            ConfigurationManager manager = new ConfigurationManager(provider);

            object a = manager["A"];         //已快取
            object x = manager["不存在的X"]; //已確認不存在
            provider.Requests.Clear();

            manager.Prefetch("A", "不存在的X");
            Assert.AreEqual(0, provider.Requests.Count, "全部已知時 Prefetch 不應發出請求");

            manager.Prefetch("A", "B");
            Assert.AreEqual(1, provider.Requests.Count, "只應為未知的 B 發出請求");
            Assert.AreEqual("B", string.Join(",", provider.Requests[0].ToArray()), "請求只應包含 B");
        }

        private static void SaveAfterMissMakesConfigurationVisible()
        {
            FakeConfigProvider provider = new FakeConfigProvider();
            ConfigurationManager manager = new ConfigurationManager(provider);

            ConfigData data = manager["新組態"];
            data["欄位"] = "值";
            data.Save();

            Assert.AreEqual("值", manager["新組態"]["欄位"], "儲存後應讀到新組態的內容");
        }
    }
}

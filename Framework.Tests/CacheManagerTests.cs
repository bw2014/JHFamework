using System;
using System.Collections.Generic;
using System.Threading;

namespace Framework.Tests
{
    internal class Item : IComparable<Item>
    {
        public Item(string id, int rank)
        {
            Id = id;
            Rank = rank;
        }

        public string Id { get; private set; }
        public int Rank { get; private set; }

        public int CompareTo(Item other)
        {
            int result = Rank.CompareTo(other.Rank);
            return result != 0 ? result : string.CompareOrdinal(Id, other.Id);
        }
    }

    /// <summary>
    /// 以記憶體中的「主機資料」模擬 GetData，並記錄被呼叫的次數與內容。
    /// </summary>
    internal class FakeCache : CacheManager<Item>
    {
        private int _getDataCalls;

        public FakeCache(bool rememberMissing)
        {
            RememberMissing = rememberMissing;
            Server = new Dictionary<string, Item>();
            Requests = new List<List<string>>();
        }

        public bool RememberMissing { get; private set; }
        public Dictionary<string, Item> Server { get; private set; }
        public List<List<string>> Requests { get; private set; }
        public int GetDataCalls { get { return _getDataCalls; } }

        /// <summary>
        /// 設定後，GetData 進入時會設定 Entered，並等待 Gate 才繼續，用來讓多個執行緒在查詢中交錯。
        /// </summary>
        public ManualResetEvent Gate { get; set; }
        public ManualResetEvent Entered { get; set; }

        public void Put(string id, int rank)
        {
            lock (Server)
                Server[id] = new Item(id, rank);
        }

        protected override Dictionary<string, Item> GetAllData()
        {
            lock (Server)
                return new Dictionary<string, Item>(Server);
        }

        protected override Dictionary<string, Item> GetData(IEnumerable<string> primaryKeys)
        {
            Interlocked.Increment(ref _getDataCalls);
            if (Gate != null)
            {
                Entered.Set();
                Gate.WaitOne();
            }
            List<string> keys = new List<string>(primaryKeys);
            lock (Requests)
                Requests.Add(keys);

            Dictionary<string, Item> result = new Dictionary<string, Item>();
            lock (Server)
            {
                foreach (string key in keys)
                {
                    Item item;
                    if (Server.TryGetValue(key, out item))
                        result.Add(key, item);
                }
            }
            return result;
        }

        protected override bool CacheMissingKeys
        {
            get { return RememberMissing; }
        }
    }

    internal static class CacheManagerTests
    {
        public static void Register()
        {
            TestRunner.Add("CacheManager：快取命中後不再查詢主機", HitIsServedFromCache);
            TestRunner.Add("CacheManager：預設不記住查無資料的鍵值（維持原行為）", MissIsRequeriedByDefault);
            TestRunner.Add("CacheManager：CacheMissingKeys 時查無資料只查一次", MissIsRememberedWhenEnabled);
            TestRunner.Add("CacheManager：明確 SyncData 會清除查無資料的記錄", SyncDataClearsMissingKey);
            TestRunner.Add("CacheManager：不合法的鍵值不查詢主機", InvalidKeyIsNotQueried);
            TestRunner.Add("CacheManager：主機刪除的資料會被移除並通知", DeletedItemIsRemovedAndNotified);
            TestRunner.Add("CacheManager：可排序型別維持排序與 QuickCompare", SortedOrderIsMaintained);
            TestRunner.Add("CacheManager：SyncDataBackground 全為已刪除鍵值時不會卡死（P2-1）", BackgroundSyncOfRemovedKeysDoesNotHang);
            TestRunner.Add("CacheManager：SyncDataBackground 更新資料並引發事件", BackgroundSyncUpdatesAndNotifies);
            TestRunner.Add("CacheManager：多執行緒同時 SyncData 不會卡死且資料完整", ConcurrentSyncDataCompletes);
            TestRunner.Add("CacheManager：SyncAllBackground 載入全部資料", SyncAllLoadsEverything);
            TestRunner.Add("CacheManager：多執行緒同時讀取同一筆既有資料只查詢一次", ConcurrentReadOfSameKeyQueriesOnce);
            TestRunner.Add("CacheManager：多執行緒同時讀取同一筆不存在的資料只查詢一次", ConcurrentReadOfMissingKeyQueriesOnce);
        }

        private static void HitIsServedFromCache()
        {
            FakeCache cache = new FakeCache(false);
            cache.Put("1", 1);

            Assert.AreEqual("1", cache["1"].Id, "第一次讀取應取得資料");
            Assert.AreEqual("1", cache["1"].Id, "第二次讀取應取得資料");
            Assert.AreEqual(1, cache.GetDataCalls, "只應查詢主機一次");
        }

        private static void MissIsRequeriedByDefault()
        {
            FakeCache cache = new FakeCache(false);

            Assert.IsTrue(cache["99"] == null, "查無資料應傳回 null");
            Assert.IsTrue(cache["99"] == null, "查無資料應傳回 null");
            Assert.AreEqual(2, cache.GetDataCalls, "未啟用 CacheMissingKeys 時每次都應查詢（與修改前相同）");
        }

        private static void MissIsRememberedWhenEnabled()
        {
            FakeCache cache = new FakeCache(true);

            Assert.IsTrue(cache["99"] == null, "查無資料應傳回 null");
            Assert.IsTrue(cache["99"] == null, "查無資料應傳回 null");
            Assert.IsTrue(cache["99"] == null, "查無資料應傳回 null");
            Assert.AreEqual(1, cache.GetDataCalls, "查無資料的鍵值只應查詢一次");
        }

        private static void SyncDataClearsMissingKey()
        {
            FakeCache cache = new FakeCache(true);
            Assert.IsTrue(cache["5"] == null, "一開始查無資料");

            cache.Put("5", 5); //例如 ConfigData.Save 新增後會呼叫 SyncData。
            cache.SyncData("5");

            Assert.IsTrue(cache["5"] != null, "SyncData 後應取得新資料");
            Assert.AreEqual(2, cache.GetDataCalls, "讀取（1 次）+ SyncData（1 次）");
        }

        private static void InvalidKeyIsNotQueried()
        {
            FakeCache cache = new FakeCache(true);

            Assert.IsTrue(cache["abc"] == null, "不合法的鍵值應傳回 null");
            Assert.AreEqual(0, cache.GetDataCalls, "不合法的鍵值不應查詢主機");
        }

        private static void DeletedItemIsRemovedAndNotified()
        {
            FakeCache cache = new FakeCache(false);
            cache.Put("1", 1);
            cache.Put("2", 2);
            cache.SyncData("1", "2");

            List<string> updated = new List<string>();
            cache.ItemUpdated += delegate(object sender, ItemUpdatedEventArgs e) { updated.AddRange(e.PrimaryKeys); };

            lock (cache.Server)
                cache.Server.Remove("1");
            cache.SyncData("1");

            Assert.IsTrue(!cache.Items.ContainsKey("1"), "被刪除的資料應從快取移除");
            Assert.IsTrue(cache.Items.ContainsKey("2"), "其他資料應保留");
            Assert.IsTrue(updated.Contains("1"), "ItemUpdated 應包含被刪除的鍵值");
        }

        private static void SortedOrderIsMaintained()
        {
            FakeCache cache = new FakeCache(false);
            cache.Put("1", 30);
            cache.Put("2", 10);
            cache.Put("3", 20);
            cache.Put("4", 40);
            cache.SyncData("1", "2", "3", "4");

            Assert.AreEqual("2,3,1,4", Order(cache), "初次載入應依 Rank 排序");

            cache.Put("4", 5);  //更新後排到最前面
            cache.Put("5", 25); //新增的資料插在中間
            cache.SyncData("4", "5");

            Assert.AreEqual("4,2,3,5,1", Order(cache), "更新與新增後仍應維持排序");
            Assert.IsTrue(cache.QuickCompare("4", "2") < 0, "QuickCompare：4 應排在 2 之前");
            Assert.IsTrue(cache.QuickCompare("1", "5") > 0, "QuickCompare：1 應排在 5 之後");
        }

        private static string Order(FakeCache cache)
        {
            List<string> ids = new List<string>();
            foreach (Item each in cache.Items)
                ids.Add(each.Id);
            return string.Join(",", ids.ToArray());
        }

        private static void BackgroundSyncOfRemovedKeysDoesNotHang()
        {
            FakeCache cache = new FakeCache(false);
            cache.Put("1", 1);
            cache.SyncData("1");
            lock (cache.Server)
                cache.Server.Remove("1");
            cache.SyncData("1"); //「1」進入已刪除清單。

            cache.SyncDataBackground("1"); //修改前：這裡提早 return 但沒有解除載入狀態。

            Assert.CompletesWithin(2000, delegate { cache.WaitLoadingComplete(); }, "SyncDataBackground 之後讀取不應永久等待");
        }

        private static void BackgroundSyncUpdatesAndNotifies()
        {
            FakeCache cache = new FakeCache(false);
            cache.Put("7", 7);

            List<string> updated = new List<string>();
            cache.ItemUpdated += delegate(object sender, ItemUpdatedEventArgs e) { lock (updated) updated.AddRange(e.PrimaryKeys); };

            cache.SyncDataBackground("7");

            Assert.Eventually(delegate { lock (updated) return updated.Contains("7"); }, "背景同步完成後應引發 ItemUpdated");
            Assert.IsTrue(cache.Items.ContainsKey("7"), "背景同步後資料應在快取中");
        }

        private static void ConcurrentSyncDataCompletes()
        {
            FakeCache cache = new FakeCache(false);
            for (int i = 1; i <= 200; i++)
                cache.Put(i.ToString(), i);

            //在背景執行緒同步會透過 Control.Invoke 回到 UI 執行緒引發事件，因此 UI 執行緒需持續處理訊息。
            int finished = 0;
            for (int t = 0; t < 4; t++)
            {
                int offset = t;
                Thread thread = new Thread(delegate()
                {
                    for (int i = 1 + offset; i <= 200; i += 4)
                        cache.SyncData(i.ToString());
                    Interlocked.Increment(ref finished);
                });
                thread.IsBackground = true;
                thread.Start();
            }

            Assert.Eventually(delegate { return finished == 4; }, "四條執行緒都應完成同步");
            Assert.AreEqual(200, cache.Items.Count, "所有資料都應在快取中");
            Assert.AreEqual(200, cache.GetDataCalls, "每個鍵值查詢一次");
        }

        /// <summary>
        /// 第一個執行緒停在主機查詢中，第二個執行緒讀取同一筆後等待載入權；放行後第二個執行緒應直接使用第一個的結果。
        /// </summary>
        private static int ReadSameKeyConcurrently(FakeCache cache, string key, out Item first, out Item second)
        {
            FakeCache.CacheItemCollection items = cache.Items; //先取得 Items，兩個執行緒都透過它讀取。
            cache.Entered = new ManualResetEvent(false);
            cache.Gate = new ManualResetEvent(false);

            Item[] results = new Item[2];
            int finished = 0;
            Thread t1 = new Thread(delegate() { results[0] = items[key]; Interlocked.Increment(ref finished); });
            t1.IsBackground = true;
            t1.Start();
            Assert.IsTrue(cache.Entered.WaitOne(2000), "第一個執行緒應進入主機查詢");

            Thread t2 = new Thread(delegate() { results[1] = items[key]; Interlocked.Increment(ref finished); });
            t2.IsBackground = true;
            t2.Start();
            Thread.Sleep(300); //讓第二個執行緒走到等待載入權的位置。

            cache.Gate.Set();
            //同步完成後的事件會透過 Control.Invoke 回到此執行緒，需持續處理訊息。
            Assert.Eventually(delegate { return finished == 2; }, "兩個執行緒都應完成讀取");

            cache.Gate = null;
            first = results[0];
            second = results[1];
            return cache.GetDataCalls;
        }

        private static void ConcurrentReadOfSameKeyQueriesOnce()
        {
            FakeCache cache = new FakeCache(false);
            cache.Put("1", 1);

            Item first, second;
            int calls = ReadSameKeyConcurrently(cache, "1", out first, out second);

            Assert.IsTrue(first != null && second != null, "兩個執行緒都應取得資料");
            Assert.AreEqual(1, calls, "同一筆資料只應查詢主機一次");

            cache.SyncData("1");
            Assert.AreEqual(2, cache.GetDataCalls, "明確呼叫 SyncData 仍應重新查詢");
        }

        private static void ConcurrentReadOfMissingKeyQueriesOnce()
        {
            FakeCache cache = new FakeCache(true);

            Item first, second;
            int calls = ReadSameKeyConcurrently(cache, "99", out first, out second);

            Assert.IsTrue(first == null && second == null, "查無資料應傳回 null");
            Assert.AreEqual(1, calls, "啟用 CacheMissingKeys 時，同一筆不存在的資料只應查詢一次");
        }

        private static void SyncAllLoadsEverything()
        {
            FakeCache cache = new FakeCache(true);
            cache.Put("1", 2);
            cache.Put("2", 1);
            Assert.IsTrue(cache["3"] == null, "先讓「3」被記為查無資料");

            bool loaded = false;
            cache.ItemLoaded += delegate { loaded = true; };
            cache.SyncAllBackground();

            Assert.Eventually(delegate { return loaded; }, "SyncAllBackground 完成後應引發 ItemLoaded");
            Assert.AreEqual("2,1", Order(cache), "應載入全部資料並排序");

            cache.Put("3", 3);
            Assert.IsTrue(cache["3"] != null, "SyncAllBackground 應清除查無資料的記錄");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Framework
{
    public class ConfigurationManager
    {
        #region Instance Members
        /// <summary>
        /// Configuration 的快取。
        /// </summary>
        internal ConfigurationCache Cache { get; set; }
        /// <summary>
        /// 負責提供 Configuration 的資料。
        /// </summary>
        internal IConfigurationProvider Provider { get; set; }
        /// <summary>
        /// 提供一個時機，處理當使用者要求指定的 Namespace 組態資料時，可以變更實際傳送到底層的 Namespace 名稱。
        /// </summary>
        /// <param name="configNamespace"></param>
        /// <returns></returns>
        protected virtual string NamespacePreprocess(string configNamespace)
        {
            return configNamespace;
        }
        /// <summary>
        /// 指示此組態是否為可讀寫的。
        /// </summary>
        public virtual bool Readonly { get { return false; } }
        /// <summary>
        /// 取得組態資料，如果指定的名稱存在則回傳資料，不存在則自動建立新的組態。
        /// </summary>
        /// <param name="configName"></param>
        /// <returns></returns>
        public ConfigData this[string configNamespace]
        {
            get
            {
                string ns = NamespacePreprocess(configNamespace);

                ConfigurationRecord record = Cache[ns];

                if (record == null)
                {
                    if (Readonly)
                        throw new ArgumentException("指定的組態不存在。", "configNamespace");
                    else
                        return new ConfigData(this, new ConfigurationRecord(ns));
                }
                else
                    return new ConfigData(this, record);
            }
        }
        /// <summary>
        /// 向主機同步組態資料。
        /// </summary>
        public void Sync(string configNamespace)
        {
            //快取以處理後的名稱為鍵值（例如使用者組態的「帳號::名稱」），同步時必須使用相同的名稱。
            Cache.SyncData(NamespacePreprocess(configNamespace));
        }
        /// <summary>
        /// 一次向主機取得多個組態並放入快取，之後讀取這些組態時就不需要再逐一查詢。
        /// 已在快取中、或已確認不存在的組態不會重複查詢。
        /// </summary>
        /// <param name="configNamespaces">要預先取得的組態名稱。</param>
        public void Prefetch(params string[] configNamespaces)
        {
            List<string> keys = GetPrefetchKeys(configNamespaces);
            if (keys.Count > 0)
                Cache.SyncData(keys);
        }
        /// <summary>
        /// Prefetch 的背景版本：在背景執行緒向主機取得組態，呼叫端不需等待。
        /// 背景取得期間讀取組態，會等待取得完成。
        /// </summary>
        /// <param name="configNamespaces">要預先取得的組態名稱。</param>
        public void PrefetchBackground(params string[] configNamespaces)
        {
            List<string> keys = GetPrefetchKeys(configNamespaces);
            if (keys.Count > 0)
                Cache.SyncDataBackground(keys);
        }
        private List<string> GetPrefetchKeys(IEnumerable<string> configNamespaces)
        {
            List<string> keys = new List<string>();
            foreach (string each in configNamespaces)
                keys.Add(NamespacePreprocess(each));
            return Cache.FilterUnknownKeys(keys);
        }
        /// <summary>
        /// 刪除指定的組態。
        /// </summary>
        /// <param name="conf"></param>
        public void Remove(ConfigData conf)
        {
            if (Readonly)
                throw new ArgumentException("此類型的組態是唯讀的。");

            conf.Record.EditAction = 3; //刪除
            Provider.SaveConfiguration(new ConfigurationRecord[] { conf.Record });
            Cache.SyncData(conf.Namespace);
        }
        /// <summary>
        /// 當組態被更新時引發，參數中的「PrimaryKeys」是被更新的組態名稱清單。
        /// </summary>
        internal event EventHandler<ItemUpdatedEventArgs> ConfigurationUpdated;
        /// <summary>
        /// 建立實體。
        /// </summary>
        /// <param name="provider"></param>
        public ConfigurationManager(IConfigurationProvider provider)
        {
            Provider = provider;
            Cache = new ConfigurationCache(Provider);
            SubscribeCache();
        }
        /// <summary>
        /// 建立與指定實體共用資料來源與快取的實體，避免同一份組態被查詢兩次。
        /// </summary>
        /// <param name="source">要共用快取的實體。</param>
        internal ConfigurationManager(ConfigurationManager source)
        {
            Provider = source.Provider;
            Cache = source.Cache;
            SubscribeCache();
        }

        private void SubscribeCache()
        {
            Cache.ItemUpdated += delegate(object sender, ItemUpdatedEventArgs e)
            {
                if (ConfigurationUpdated != null)
                {
                    BigEvent be = new BigEvent("ConfigurationUpdated", ConfigurationUpdated, this, e);
                    be.UIRaise();
                }
            };
        }

        #endregion
    }
}

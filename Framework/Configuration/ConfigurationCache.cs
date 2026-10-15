using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Framework
{
    #region Configuration Cache
    internal class ConfigurationCache : CacheManager<ConfigurationRecord>
    {
        private IConfigurationProvider Provider { get; set; }

        public ConfigurationCache(IConfigurationProvider provider)
        {
            Provider = provider;
        }

        protected override Dictionary<string, ConfigurationRecord> GetAllData()
        {
            Dictionary<string, ConfigurationRecord> records = Provider.GetAllConfiguration();

            if (StartupProfiler.Enabled)
                StartupProfiler.Count(Provider.GetType().Name, "<GetAll>", "筆數：" + records.Count);

            return records;
        }

        protected override Dictionary<string, ConfigurationRecord> GetData(IEnumerable<string> primaryKeys)
        {
            Dictionary<string, ConfigurationRecord> records = Provider.GetConfiguration(primaryKeys);

            if (StartupProfiler.Enabled)
            {
                foreach (string key in primaryKeys)
                    StartupProfiler.Count(Provider.GetType().Name, key, records.ContainsKey(key) ? "存在" : "不存在");
            }

            return records;
        }

        /// <summary>
        /// 組態不存在是常態（尚未建立的組態），記住不存在的 namespace，避免每次讀取都向主機查詢。
        /// </summary>
        protected override bool CacheMissingKeys
        {
            get { return true; }
        }

        protected override bool ValidateKey(string key)
        {
            return key.IndexOfAny(new char[] { '$', '&' }) < 0;
        }
    }
    #endregion
}

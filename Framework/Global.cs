using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Framework
{
    public static class Global
    {
        /// <summary>
        /// 取得全域的組態資料。
        /// </summary>
        public static ConfigurationManager Configuration { get; internal set; }
        /// <summary>
        /// 可讀寫的全域組態資料。
        /// </summary>
        internal static ConfigurationManager ConfigurationWritable { get; set; }
        #region GlobalConfigManager
        /// <summary>
        /// 負責管理全域的組態資料，並可選擇是否為唯讀管理。
        /// </summary>
        public class GlobalConfigManager : ConfigurationManager
        {
            private bool _readonly;

            internal GlobalConfigManager(IConfigurationProvider provider, bool readOnly)
                : base(provider)
            {
                _readonly = readOnly;
            }

            /// <summary>
            /// 建立與指定實體共用快取的全域組態管理，唯讀與否只在此層判斷。
            /// </summary>
            internal GlobalConfigManager(GlobalConfigManager source, bool readOnly)
                : base(source)
            {
                _readonly = readOnly;
            }

            public override bool Readonly
            {
                get
                {
                    return _readonly;
                }
            }
        }
        #endregion
    }
}

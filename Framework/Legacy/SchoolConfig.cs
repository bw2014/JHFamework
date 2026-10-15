using System;
using System.Collections.Generic;
using System.Text;
using FISCA.DSAUtil;
using System.Xml;
using Framework.Feature;

namespace Framework.Legacy
{
    [Obsolete("程式碼轉移遺，為了相容留下來。")]
    public class SchoolConfig
    {
        //private XmlElement _default = null;

        public SchoolConfig()
        {
            Load();
        }

        private DSXmlHelper _source;
        private DSXmlHelper Source
        {
            get { return _source; }
            set { _source = value; }
        }

        public XmlElement Content
        {
            get { return Source.BaseElement; }
        }

        private const int MaxLoadAttempts = 2;

        public void Load()
        {
            Exception lastError = null;
            for (int i = 0; i < MaxLoadAttempts; i++)
            {
                try
                {
                    _source = new DSXmlHelper(Config.GetSchoolConfig().GetElement("Content"));
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            //重試都失敗才回報一次，避免每次重試都進行一次錯誤回報。
            if (lastError != null)
                BugReporter.ReportException(GlobalOld.SystemName, GlobalOld.SystemVersion, lastError, false, string.Empty);
        }

        public void Save()
        {
            try
            {
                Config.SetSchoolConfig(Source.BaseElement);
            }
            catch (Exception ex)
            {
                //CurrentUser user = CurrentUser.Instance;
                BugReporter.ReportException(GlobalOld.SystemName, GlobalOld.SystemVersion, ex, false, string.Empty);
            }
        }
    }
}

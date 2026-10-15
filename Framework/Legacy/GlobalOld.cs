using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Framework.Security;
using Framework.Feature;
using FISCA.Presentation;

namespace Framework.Legacy
{
    public class GlobalOld
    {
        private GlobalOld()
        {
            //建立主執行緒控制項。
            _main_thread_control = new Control();
            IntPtr point = _main_thread_control.Handle;
        }

        #region Singleton Pattern
        private static GlobalOld _instance;
        internal static GlobalOld Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new GlobalOld();

                return _instance;
            }
        }
        #endregion

        #region Instance Members
        private Control _main_thread_control; //主執行緒控制項。
        private IPreferenceProvider _preference; //個人設定值。
        private FeatureAcl _acl;   //使用者權限清單。
        private SchoolInformation _school_info; //學校資訊(不使用)。
        private SchoolConfig _school_config; //學校組態(不使用)。
        private SystemConfig _system_config;  //系統組態(不使用)。
        private string _runtime_build; //目前正在執行的程式的 Build。
        private ConfigurationCollection _configuration; //目前似乎沒有用到…
        #endregion

        #region Static Members
        /// <summary>
        /// 取得主執行緒的 UI Control，用於在慌亂的世界中找到真理。
        /// </summary>
        public static Control MainThreadControl { get { return Instance._main_thread_control; } internal set { Instance._main_thread_control = value; } }

        /// <summary>
        /// 取得取得個人資訊。
        /// </summary>
        public static IPreferenceProvider Preference { get { return Instance._preference; } internal set { Instance._preference = value; } }

        /// <summary>
        /// 取得使用者權限清單。
        /// </summary>
        public static FeatureAcl Acl { get { return Instance._acl; } internal set { Instance._acl = value; } }

        /// <summary>
        /// 取得目前正在執行的建置名稱(例：Release、Debug、Test)。
        /// </summary>
        public static string RuntimeBuild { get { return Instance._runtime_build; } set { Instance._runtime_build = value; } }

        /// <summary>
        /// 回報錯誤訊息到 ischool 公司。
        /// </summary>
        /// <param name="ex">包含錯誤資訊的物件。</param>
        public static void ReportError(Exception ex)
        {
            BugReporter.ReportException(SystemName, SystemVersion, ex, false, string.Empty);
        }

        #region Obsolete
        /// <summary>
        /// 取得學校相關資訊。
        /// </summary>
        [Obsolete()]
        public static SchoolInformation SchoolInformation { get { return Instance._school_info; } set { Instance._school_info = value; } }

        /// <summary>
        /// 取得學校相關設定。
        /// </summary>
        [Obsolete()]
        public static SchoolConfig SchoolConfig
        {
            get
            {
                //啟動時用不到，第一次存取時才向主機讀取。
                lock (_lazy_lock)
                {
                    if (Instance._school_config == null)
                        Instance._school_config = new SchoolConfig();
                    return Instance._school_config;
                }
            }
            set { Instance._school_config = value; }
        }

        /// <summary>
        /// 取得系統相關設定。
        /// </summary>
        [Obsolete("Refactoring:似乎沒有用到，需要討論如何處理此屬性。")]
        public static SystemConfig SystemConfig { get { return Instance._system_config; } set { Instance._system_config = value; } }

        /// <summary>
        /// 取得系統相關設定。
        /// </summary>
        [Obsolete()]
        public static ConfigurationCollection Configuration
        {
            get
            {
                lock (_lazy_lock)
                {
                    if (Instance._configuration == null)
                        Instance._configuration = new ConfigurationCollection();
                    return Instance._configuration;
                }
            }
            set { Instance._configuration = value; }
        }

        /// <summary>
        /// 取得系統版本。
        /// </summary>
        [Obsolete()]
        public static string SystemVersion { get { return "0.0.0.0"; } }

        /// <summary>
        /// 取得系統名稱。
        /// </summary>
        [Obsolete()]
        public static string SystemName { get { return "Smart School"; } }
        #endregion

        #endregion

        private static readonly object _lazy_lock = new object();

        /// <summary>
        /// 設定啟動時已（並行）取得的資料。SchoolConfig 與 Configuration 改為第一次存取時才載入。
        /// </summary>
        internal static void BeginInitialize(FeatureAcl acl, SchoolInformation schoolInformation, SystemConfig systemConfig)
        {
            Acl = acl;
            SchoolInformation = schoolInformation;
            SystemConfig = systemConfig;
        }

        internal static void EndInitialize()
        {
        }
    }
}

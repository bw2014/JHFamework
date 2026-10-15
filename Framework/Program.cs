using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using DataSynchronization;
using FISCA.Authentication;
using FISCA.Deployment.Administration;
using FISCA.DSAUtil;
using Framework.Feature;
using Framework.Legacy;
using Framework.Security;

namespace Framework
{
    public static class Program
    {
        public static void Initial()
        {
            //Framework 是最早啟動的模組之一，在此安裝探針，之後載入的模組都能量測到。
            StartupProbes.Install();
            StartupProfiler.Measure("Framework.Program.Initial（總計）", InitialCore);
        }

        private static void InitialCore()
        {
            StartupProfiler.Measure("AutoDisplayLoadingMessageOnMotherForm", DSAServices.AutoDisplayLoadingMessageOnMotherForm);

            StartupProfiler.Measure("InitializeGlobalData", InitializeGlobalData);

            StartupProfiler.Measure("建立組態管理與 DBChangeMonitor", delegate
            {
                //負責儲存使用者個人設定。
                User.Configuration = new User.UserConfigManager(new ConfigProvider_User(), DSAServices.UserAccount);
                //負責儲存應用程式設定。
                App.Configuration = new ConfigurationManager(new ConfigProvider_App());
                //可讀寫的全域組態。
                Global.GlobalConfigManager globalWritable = new Global.GlobalConfigManager(new ConfigProvider_Global(), false);
                Global.ConfigurationWritable = globalWritable;
                //負責儲存全域設定（唯讀），與可讀寫的全域組態共用快取，避免同一份組態查詢兩次。
                Global.Configuration = new Global.GlobalConfigManager(globalWritable, true);
                //負責資料庫變動的通知物件。
                App.DBMonitor = new DBChangeMonitor(10, new PTChangeSetProvider());
            });

            //在背景預先建立全域組態主機的連線。
            ThreadPool.QueueUserWorkItem(delegate { ConfigProvider_Global.WarmUp(); });

            try
            {
                StartupProfiler.Measure("Campus.Configuration.Config.Initialize", delegate
                {
                    Campus.Configuration.Config.Initialize(
                        new Campus.Configuration.UserConfigManager(new Campus.Configuration.ConfigProvider_User(), DSAServices.UserAccount),
                        new Campus.Configuration.ConfigurationManager(new Campus.Configuration.ConfigProvider_App()),
                        new Campus.Configuration.ConfigurationManager(new Campus.Configuration.ConfigProvider_Global()));
                });
            }
            catch (Exception ex) { MsgBox.Show("載入組態管理程式庫錯誤：" + ex.Message); }

            FISCA.Presentation.MotherForm.Form.Icon = Properties.Resources.ischoolIcon;
            FISCA.Presentation.MotherForm.StartMenu.Image = Properties.Resources.Icon;
            FISCA.Presentation.MotherForm.Form.Text = StartupProfiler.Measure("GetTitleText", GetTitleText);

            //DSAServices.SetupLoadingDisplay();

            //2011/8/4日 - dylan註解
            //FISCA.Presentation.MotherForm.StartMenu["模組管理"].Enable = User.Acl["StartButton0002"].Executable | DSAServices.IsSysAdmin;
            //FISCA.Presentation.MotherForm.StartMenu["模組管理"].Image = Properties.Resources.spiral_lock_64;
            //FISCA.Presentation.MotherForm.StartMenu["模組管理"].Click += delegate
            //{
            //    new ModuleManager().ShowDialog();
            //};

            StartupProfiler.Measure("註冊安全性選單與權限項目", RegisterSecurityMenu);

            if (Control.ModifierKeys == Keys.Shift)
            {
                if (User.Acl["StartButton0002"].Executable | DSAServices.IsSysAdmin)
                    new FISCA.Deployment.Administration.ModuleManager().ShowDialog();
            }

            //Student.Instance.ReflashAll();
            //Class.Instance.ReflashAll();
            //Teacher.Instance.ReflashAll();

            //Student.Instance.SetupPresentation();
            //Class.Instance.SetupPresentation();
            //Teacher.Instance.SetupPresentation();
            //Course.Instance.SetupPresentation();
        }

        private static void RegisterSecurityMenu()
        {
            FISCA.Presentation.MotherForm.StartMenu["安全性"]["使用者管理"].Click += new EventHandler(UserManage_Click);
            FISCA.Presentation.MotherForm.StartMenu["安全性"]["使用者管理"].Enable = User.Acl["StartButton0000"].Executable | DSAServices.IsSysAdmin;

            FISCA.Presentation.MotherForm.StartMenu["安全性"]["角色權限管理"].Click += new EventHandler(RoleManage_Click);
            FISCA.Presentation.MotherForm.StartMenu["安全性"]["角色權限管理"].Enable = User.Acl["StartButton0001"].Executable | DSAServices.IsSysAdmin;

            //最新消息註解 by Dylan 
            //if (General.Feedback.Program.Extension == null)
            //    General.Feedback.Program.Extension = new General.Feedback.ExtensionData();

            //General.Feedback.Program.Extension.SchoolChineseName = Framework.Legacy.GlobalOld.SchoolInformation.ChineseName;

            //General.Feedback.Program.RegisterStartButton();
            //LogViewfinder.PluginMain.RegisterStartButton();

            //2011/8/4日 - dylan註解
            //RoleAclSource.Instance["系統"].Add(new RibbonFeature("StartButton0002", "模組管理"));
            RoleAclSource.Instance["系統"].Add(new RibbonFeature("StartButton0000", "使用者管理"));
            RoleAclSource.Instance["系統"].Add(new RibbonFeature("StartButton0001", "角色權限管理"));
        }

        private static string GetTitleText()
        {
            string schoolName = Framework.Legacy.GlobalOld.SchoolInformation.ChineseName;
            string schoolYear = Framework.Legacy.GlobalOld.SystemConfig.DefaultSchoolYear.ToString();
            string semester = Framework.Legacy.GlobalOld.SystemConfig.DefaultSemester.ToString();
            string server = DSAServices.AccessPoint;
            string user = DSAServices.UserAccount;

            string version = "0.0.0.0";
            try
            {
                string path = Path.Combine(Application.StartupPath, "release.xml");
                XmlDocument doc = new XmlDocument();
                doc.Load(path);
                version = doc.DocumentElement.GetAttribute("Version");
            }
            catch (Exception) { }

            return string.Format("ischool 〈{0} {1} {2} 〉〈FISCA：{3}〉〈{4}〉〈{5}〉", schoolName, schoolYear, semester, version, server, user);
        }

        #region Start Menu Events

        private static void UserManage_Click(object sender, EventArgs e)
        {
            //new Framework.Security.UI.UserManager().ShowDialog();
            new FISCA.Permission.UI.UserManager().ShowDialog();
        }

        private static void RoleManage_Click(object sender, EventArgs e)
        {
            //new Framework.Security.UI.RoleManager().ShowDialog();
            new FISCA.Permission.UI.RoleManager().ShowDialog();
        }
        #endregion

        private static void InitializeGlobalData()
        {
            //GlobalOld 會建立主執行緒控制項，必須在 UI 執行緒先建立，再開始背景工作。
            Control mainThreadControl = Framework.Legacy.GlobalOld.MainThreadControl;

            //以下四個呼叫彼此無關，同時向主機發出，等待時間由「全部相加」縮短為「最慢的一個」。
            //背景工作只做網路呼叫與 XML 解析，不碰任何 UI 物件。
            Task<FeatureAcl> acl = Task.Factory.StartNew(() => StartupProfiler.Measure("Permission.GetUserAccessControlList", () => Permission.GetUserAccessControlList()));
            Task<SchoolInformation> schoolInformation = Task.Factory.StartNew(() => StartupProfiler.Measure("SchoolInformation", () => new SchoolInformation()));
            Task<SystemConfig> systemConfig = Task.Factory.StartNew(() => StartupProfiler.Measure("SystemConfig", () => new SystemConfig()));
            Task<DSResponse> preferenceData = Task.Factory.StartNew(() => StartupProfiler.Measure("Personal.GetPreference", () => Personal.GetPreference()));

            StartupProfiler.Measure("等待並行呼叫完成", delegate { WaitAll(acl, schoolInformation, systemConfig, preferenceData); });

            Framework.Legacy.GlobalOld.BeginInitialize(acl.Result, schoolInformation.Result, systemConfig.Result);

            //設定  Preference。PreferenceProvider 會掛 Application.Idle，必須在 UI 執行緒建立。
            FISCA.Presentation.IPreferenceProvider preference = new PreferenceProvider(preferenceData.Result);
            FISCA.Presentation.MotherForm.PreferenceProvider = preference;
            Framework.Legacy.GlobalOld.Preference = preference;

            Framework.Legacy.GlobalOld.EndInitialize();
        }

        /// <summary>
        /// 等待所有工作完成。若有工作失敗，依傳入順序丟出第一個失敗工作的原始例外，與改為並行前的行為一致。
        /// </summary>
        private static void WaitAll(params Task[] tasks)
        {
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException)
            {
                foreach (Task each in tasks)
                {
                    if (each.IsFaulted)
                        throw each.Exception.InnerException;
                }
                throw;
            }
        }
    }
}

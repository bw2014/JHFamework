using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.IO;
using System.Xml.Serialization;
using FISCA.DSAUtil;
using System.Diagnostics;
using System.Reflection;
using System.Net.NetworkInformation;
using System.Threading;

namespace Framework
{
    public partial class BugReporter : FISCA.Presentation.Controls.BaseForm
    {
        private static string _System = "", _Version = "";

        public static void SetSystem(string name) { _System = name; }

        public static void SetVersion(string version) { _Version = version; }

        public static void SetRunTimeMessage(string field, string msg)
        {
            lock ( _RunTimeMessages )
            {
                _RunTimeMessages[field] = msg;
            }
        }

        public static void ReportException(string system, string version, Exception exception, bool show)
        {
            ReportException(_System, _Version, exception, show, null, "");
        }

        public static void ReportException(string system, string version, Exception exception, bool show, string tipInfo)
        {
            ReportException(_System, _Version, exception, show, null, tipInfo);
        }

        public static void ReportException(string system, string version, Exception exception, bool show, ExceptionReport customTransform, string tipInfo)
        {
            if ( !FilterThreadAbortException(exception) )
            {
                if ( !show )
                {
                    ReportSilently(system, version, exception, customTransform, tipInfo);
                    return;
                }

                BugReporter exceptionForm = null;
                try
                {
                    exceptionForm = new BugReporter(customTransform, exception, system, version, show, tipInfo);
                }
                catch
                { }
                try
                {
                    if ( show )
                    {
                        if ( exceptionForm != null )
                            exceptionForm.ShowDialog();
                        else
                            MessageBox.Show("系統發生錯誤，嘗試自動回報失敗。");
                    }
                }
                catch
                {
                    MessageBox.Show("系統發生錯誤，嘗試自動回報失敗。");
                }
            }
        }

        public static void ReportException(Exception exception, bool show)
        {
            ReportException(_System, _Version, exception, show, string.Empty);
        }

        public static void ReportException(Exception exception, bool show, ExceptionReport customTransform)
        {
            ReportException(_System, _Version, exception, show, customTransform, string.Empty);
        }

        private static bool FilterThreadAbortException(Exception exception)
        {
            if ( exception == null )
                return false;
            if ( exception is ThreadAbortException )
                return true;
            else
                return FilterThreadAbortException(exception.InnerException);
        }

        private static Dictionary<string, string> _RunTimeMessages = new Dictionary<string, string>();

        private XmlDocument doc;

        private string fileName = NewFileName();

        private static string NewFileName()
        {
            return DateTime.Now.Year + "_" + DateTime.Now.Month + "_" + DateTime.Now.Day + "_" + DateTime.Now.Hour + "_" + DateTime.Now.Month + "_" + DateTime.Now.Second + ".xml";
        }

        private static void SaveExceptionOnLocal(XmlDocument doc, string fileName)
        {
            try
            {
                DirectoryInfo dic = new DirectoryInfo(Application.StartupPath + "Exception");
                if ( !dic.Exists )
                {
                    ( new DirectoryInfo(Application.StartupPath) ).CreateSubdirectory("Exception");
                }
                doc.Save(Application.StartupPath + "\\Exception\\" + fileName);
            }
            catch { }
        }

        private static bool ReportToServerDisabled
        {
            get { return File.Exists(System.IO.Path.Combine(Application.StartupPath, "噓~不要告訴johnny5")); }
        }

        /// <summary>
        /// 傳送錯誤報告到回報主機，傳回是否成功。
        /// </summary>
        private static bool SendToServer(XmlDocument doc, string system, string version)
        {
            try
            {
                DSConnection conn = GetDSConnection();

                if ( conn == null )
                    throw new Exception("無法連線到錯誤回報系統。");

                conn.SendRequest("QualitySystem.ReportBug", new DSRequest("<ReportBugRequest>" +
                    "<SystemName>" + system + "</SystemName>" +
                    "<SystemVersion>" + version + "</SystemVersion>" +
                    "<Content>" + doc.DocumentElement.OuterXml + "</Content>" +
                    "</ReportBugRequest>"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly HashSet<string> _SilentReported = new HashSet<string>();

        /// <summary>
        /// 不顯示畫面的錯誤回報：不建立表單，本機存檔後在背景傳送，避免卡住呼叫端（例如啟動流程）。
        /// 同一種錯誤（型別與訊息相同）在單次執行中只傳送一次。
        /// </summary>
        private static void ReportSilently(string system, string version, Exception exception, ExceptionReport customTransform, string tipInfo)
        {
            XmlDocument report;
            try
            {
                report = BuildReportDocument(customTransform, exception, system, version, false, tipInfo);
            }
            catch
            {
                return;
            }

            SaveExceptionOnLocal(report, NewFileName());

            if ( ReportToServerDisabled )
                return;

            lock ( _SilentReported )
            {
                if ( !_SilentReported.Add(exception.GetType().FullName + ":" + exception.Message) )
                    return;
            }

            ThreadPool.QueueUserWorkItem(delegate { SendToServer(report, system, version); });
        }

        private static XmlDocument BuildReportDocument(ExceptionReport customTransform, Exception e, string system, string version, bool sorry, string tipInfo)
        {
            XmlDocument doc = new XmlDocument();

            //doc.LoadXml("<Exception></Exception>");
            ExceptionReport report;
            if ( customTransform != null )
                report = customTransform;
            else
            {
                report = new ExceptionReport();
                report.AddType(typeof(System.Net.HttpWebRequest), true);
                report.AddType(typeof(System.Net.HttpWebResponse), true);
            }

            doc.LoadXml(report.Transform(e));

            try { doc.DocumentElement.SetAttribute("TipInformation", tipInfo); }
            catch { }
            //儲存系統名稱資訊
            try { doc.DocumentElement.SetAttribute("有沒有道歉", sorry ? "ㄨ" : "哞"); }
            catch { }
            try { doc.DocumentElement.SetAttribute("SystemName", system); }
            catch { }
            try { doc.DocumentElement.SetAttribute("SystemVersion", version); }
            catch { }
            try { doc.DocumentElement.SetAttribute("AssemblyName", e.TargetSite.Module.Assembly.GetName().Name); }
            catch { }
            try { doc.DocumentElement.SetAttribute("AssemblyVersion", e.TargetSite.Module.Assembly.GetName().Version.ToString()); }
            catch { }
            try { doc.DocumentElement.SetAttribute("AssemblyImageRuntimeVersion", e.TargetSite.Module.Assembly.ImageRuntimeVersion); }
            catch { }
            try { doc.DocumentElement.SetAttribute("EnvironmentUserName", Environment.UserName); }
            catch { }
            try { doc.DocumentElement.SetAttribute("EnvironmentMachineName", Environment.MachineName); }
            catch { }
            try { doc.DocumentElement.SetAttribute("EnvironmentOSVersion", Environment.OSVersion.VersionString); }
            catch { }
            try { doc.DocumentElement.SetAttribute("EnvironmentServicePack", Environment.OSVersion.ServicePack); }
            catch { }
            try { doc.DocumentElement.SetAttribute("EnvironmentPlatform", Environment.OSVersion.Platform.ToString()); }
            catch { }

            lock ( _RunTimeMessages )
            {
                foreach ( string field in _RunTimeMessages.Keys )
                {
                    doc.DocumentElement.SetAttribute(field, _RunTimeMessages[field]);
                }
            }

            //BulideExceptionXml(doc.DocumentElement, e);

            return doc;
        }

        private BugReporter(ExceptionReport customTransform, Exception e, string system, string version, bool sorry, string tipInfo)
        {
            InitializeComponent();
            doc = BuildReportDocument(customTransform, e, system, version, sorry, tipInfo);

            SaveExceptionOnLocal(doc, fileName);
            if ( !ReportToServerDisabled )
            {
                if ( !SendToServer(doc, system, version) )
                {
                    this.richTextBox1.Text = "系統發生錯誤，進行中的動作將停止執行。\r\n\r\n嘗試自動回報失敗，錯誤資訊儲存於\"" + Application.StartupPath + "\\Exception\\" + fileName + "\"。\r\n\r\n造成您的不便，本公司深感抱歉。\r\n";
                }
            }
        }

        private static DSConnection _dscon;
        private static DateTime _dsconFailedAt = DateTime.MinValue;
        private static readonly TimeSpan ConnectRetryCooldown = TimeSpan.FromMinutes(5);
        private static readonly object _dsconLock = new object();
        private static DSConnection GetDSConnection()
        {
            lock ( _dsconLock )
            {
                if ( _dscon != null )
                    return _dscon;

                //連線失敗後一段時間內不再重試，避免每次回報都等待連線逾時。
                if ( DateTime.Now - _dsconFailedAt < ConnectRetryCooldown )
                    return null;

                try
                {
                    _dscon = new DSConnection("http://beta.smartschool.com.tw/commonserver/quality", "anonymous", "");
                }
                catch ( Exception )
                {
                    _dsconFailedAt = DateTime.Now;
                    return null;
                }
                if ( !_dscon.IsConnected )
                {
                    try
                    {
                        _dscon.Connect();
                    }
                    catch ( Exception )
                    {
                        _dscon = null;
                        _dsconFailedAt = DateTime.Now;
                    }
                }
                return _dscon;
            }
        }

        private void BulideExceptionXml(XmlElement element, Exception ex)
        {
            //儲存Exception的真正型別
            element.SetAttribute("Type", ex.GetType().Name);
            XmlElement ele;
            #region 用反映把所有可以XML序列化的屬性轉換成XML，不能序列化就算了
            foreach ( PropertyInfo var in ex.GetType().GetProperties() )
            {
                if ( var.Name != "InnerException" && var.CanRead )
                {
                    try
                    {
                        StringWriter sw = new StringWriter();
                        XmlSerializer xs = new XmlSerializer(var.PropertyType);
                        xs.Serialize(sw, var.GetValue(ex, null));
                        XmlDocument doc = new XmlDocument();
                        doc.LoadXml(sw.ToString());

                        ele = element.OwnerDocument.CreateElement(var.Name);

                        if ( var.Name == "StackTrace" )
                            ele.AppendChild(element.OwnerDocument.CreateCDataSection(doc.DocumentElement.InnerText));
                        else if ( var.PropertyType == typeof(DSRequest) )
                        {//發生錯誤就不管...
                            try
                            {
                                ele.InnerXml = ( var.GetValue(ex, null) as DSRequest ).GetRawXml();
                            }
                            catch ( Exception ) { }
                        }
                        else
                            ele.InnerText = doc.DocumentElement.InnerText;

                        element.AppendChild(ele);
                    }
                    catch
                    { }
                }
                //if(var.Name=="Data"
            }
            #endregion
            //如果有InnerException則遞迴呼叫
            ele = element.OwnerDocument.CreateElement("InnerException");
            element.AppendChild(ele);
            if ( ex.InnerException != null )
            {
                BulideExceptionXml(ele, ex.InnerException);
            }
            foreach ( object var in ex.Data.Keys )
            {
                ele = element.OwnerDocument.CreateElement("Data");
                element.AppendChild(ele);
                try
                {
                    StringWriter sw = new StringWriter();
                    XmlSerializer xs = new XmlSerializer(var.GetType());
                    xs.Serialize(sw, var);
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(sw.ToString());
                    XmlElement keyElement = element.OwnerDocument.CreateElement("Key");
                    keyElement.InnerText = doc.DocumentElement.InnerText;
                    ele.AppendChild(keyElement);
                }
                catch
                { }
                try
                {
                    StringWriter sw = new StringWriter();
                    XmlSerializer xs = new XmlSerializer(ex.Data[var].GetType());
                    xs.Serialize(sw, ex.Data[var]);
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(sw.ToString());
                    XmlElement valueElement = element.OwnerDocument.CreateElement("Value");
                    valueElement.InnerText = doc.DocumentElement.InnerText;
                    ele.AppendChild(valueElement);
                }
                catch
                { }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(Application.StartupPath + "\\Exception\\" + fileName);
        }
    }
}
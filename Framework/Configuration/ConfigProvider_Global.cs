using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Framework;
using FISCA.DSAUtil;
using System.Xml;
using System.Net;
using FISCA.Authentication;

namespace Framework
{
    /// <summary>
    /// 實作組態存取介面，核心會透過此類別存取組態資料。
    /// </summary>
    internal class ConfigProvider_Global : IConfigurationProvider
    {
        private static DSConnection Connection { get; set; }
        private static readonly object ConnectionLock = new object();

        private static DSConnection GetConnection()
        {
            lock (ConnectionLock)
            {
                if (Connection != null)
                    return Connection;

                string Server;

                if (Diagnostic.DebugMode && Diagnostic.Options.UseInsideGlobalConfigurationServer)
                    Server = Diagnostic.Options.InsideGlobalConfigurationServer;
                else
                    Server = "quality";

                try
                {
                    FISCA.DSAClient.Connection c = new FISCA.DSAClient.Connection();
                    c.EnableSession = false;
                    c.Connect(Server, "", "anonymous", "");
                    Connection = new DSConnection(c);
                }
                catch (Exception ex)
                {
                    ExceptionReport report = new ExceptionReport();
                    report.AddType(typeof(HttpWebResponse), true);
                    report.AddType(typeof(HttpWebRequest), true);

                    Console.WriteLine("嘗試連線到組態儲存主機失敗({0})。", Server);
                    Console.WriteLine(report.Transform(ex));

                    throw;
                }

                return Connection;
            }
        }

        private static DSXmlHelper CallService(string serviceName, DSXmlHelper request)
        {
            return GetConnection().SendRequest(serviceName, request);
        }

        /// <summary>
        /// 預先建立到全域組態主機的連線，讓第一次讀取全域組態時不必等待連線建立。失敗時不丟出例外，第一次讀取時會再重試。
        /// </summary>
        internal static void WarmUp()
        {
            try
            {
                StartupProfiler.Measure("ConfigProvider_Global.WarmUp", delegate { GetConnection(); });
            }
            catch { }
        }

        public ConfigProvider_Global()
        {
        }

        #region IConfigurationProvider 成員

        public Dictionary<string, ConfigurationRecord> GetAllConfiguration()
        {
            DSXmlHelper request = new DSXmlHelper("Request");
            request.AddElement("Content");

            return SendRequest(request);
        }

        public Dictionary<string, ConfigurationRecord> GetConfiguration(IEnumerable<string> namespaces)
        {
            DSXmlHelper request = new DSXmlHelper("Request");
            bool execute_required = false;
            request.AddElement("Content");
            request.AddElement("Condition");
            foreach (string each in namespaces)
            {
                request.AddElement("Condition", "Name", each);
                execute_required = true;
            }

            if (execute_required)
                return SendRequest(request);
            else
                return new Dictionary<string, ConfigurationRecord>();

        }

        [AutoRetryOnWebException()]
        private static Dictionary<string, ConfigurationRecord> SendRequest(DSXmlHelper request)
        {
            string srvname = "Configuration.GetDetailList";
            Dictionary<string, ConfigurationRecord> records = new Dictionary<string, ConfigurationRecord>();

            DSXmlHelper response = CallService(srvname, request);

            foreach (XmlElement each in response.GetElements("Configuration"))
            {
                XmlHelper helper = new XmlHelper(each);
                string name = helper.GetString("Name");

                XmlElement configdata = null;
                foreach (XmlNode content in helper.GetElement("Content").ChildNodes)
                {
                    if (content.NodeType == XmlNodeType.Element) //內容可能是以「Configurations」為 Root，也可能是舊的格式。
                        configdata = content as XmlElement;
                }

                if (configdata == null)
                    configdata = XmlHelper.LoadXml("<" + ConfigurationRecord.RootName + "/>");

                records.Add(name, new ConfigurationRecord(name, configdata as XmlElement));
            }

            return records;
        }

        public void SaveConfiguration(IEnumerable<ConfigurationRecord> configurations)
        {
            MultiThreadWorker<ConfigurationRecord> worker = new MultiThreadWorker<ConfigurationRecord>();
            worker.MaxThreads = 3;
            worker.PackageSize = 20;
            worker.PackageWorker += delegate(object sender, PackageWorkEventArgs<ConfigurationRecord> e)
            {
                DSXmlHelper insert = new DSXmlHelper("Request");
                DSXmlHelper update = new DSXmlHelper("Request");
                DSXmlHelper delete = new DSXmlHelper("Request");
                bool insert_exec = false, update_exec = false, delete_exec = false;

                foreach (ConfigurationRecord eachConf in e.List)
                {
                    if (eachConf.EditAction == 1) //新增
                    {
                        insert.AddElement("Configuration");
                        insert.AddElement("Configuration", "Name", eachConf.Namespace);
                        insert.AddElement("Configuration", "Content", eachConf.GetXml(), true);
                        insert_exec = true;
                    }
                    else if (eachConf.EditAction == 2) //修改
                    {
                        update.AddElement("Configuration");
                        update.AddElement("Configuration", "Content", eachConf.GetXml(), true);
                        update.AddElement("Configuration", "Condition");
                        update.AddElement("Configuration/Condition", "Name", eachConf.Namespace);
                        update_exec = true;
                    }
                    else if (eachConf.EditAction == 3) //刪除
                    {
                        delete.AddElement("Configuration");
                        delete.AddElement("Configuration", "Name", eachConf.Namespace);
                        delete_exec = true;
                    }
                    else
                        throw new ArgumentException("沒有這一種的啦。");
                }

                if (insert_exec)
                    CallService("Configuration.Insert", (insert));

                if (update_exec)
                    CallService("Configuration.Update", (update));

                if (delete_exec)
                    CallService("Configuration.Delete", (delete));
            };

            List<PackageWorkEventArgs<ConfigurationRecord>> results = worker.Run(configurations);

            foreach (PackageWorkEventArgs<ConfigurationRecord> each in results)
                if (each.HasException) throw each.Exception;
        }

        #endregion
    }
}

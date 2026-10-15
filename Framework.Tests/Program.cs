using System;

namespace Framework.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            //必須在任何程式碼使用 StartupProfiler 之前啟用。
            StartupProbesTests.EnableProfiler();

            CacheManagerTests.Register();
            ConfigurationManagerTests.Register();
            StartupProbesTests.Register();
            return TestRunner.RunAll();
        }
    }
}

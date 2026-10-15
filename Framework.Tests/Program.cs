using System;

namespace Framework.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            CacheManagerTests.Register();
            ConfigurationManagerTests.Register();
            return TestRunner.RunAll();
        }
    }
}

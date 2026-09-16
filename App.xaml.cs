using System.Windows;
using System.Threading;

namespace QuickCopy
{
    public partial class App : Application
    {
        private static Mutex singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "QuickCopy.SingleInstance", out createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (singleInstanceMutex != null)
            {
                singleInstanceMutex.ReleaseMutex();
                singleInstanceMutex.Dispose();
                singleInstanceMutex = null;
            }
            base.OnExit(e);
        }
    }
}

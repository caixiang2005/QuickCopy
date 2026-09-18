using System.Windows;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuickCopy
{
    public partial class App : Application
    {
        private const int SwRestore = 9;
        private static readonly IntPtr InvalidWindow = IntPtr.Zero;
        private static Mutex singleInstanceMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "QuickCopy.SingleInstance", out createdNew);
            if (!createdNew)
            {
                var existingWindow = FindWindow(null, "QuickCopy");
                if (existingWindow != InvalidWindow)
                {
                    ShowWindow(existingWindow, SwRestore);
                    SetForegroundWindow(existingWindow);
                }
                singleInstanceMutex.Dispose();
                singleInstanceMutex = null;
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

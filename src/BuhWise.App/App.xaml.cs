using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BuhWise.Services;

namespace BuhWise
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            TryLogException("[Dispatcher]", e.Exception);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                TryLogException("[AppDomain]", ex);
            }
        }

        private static void TryLogException(string prefix, Exception ex)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {prefix} {ex}\r\n";
                File.AppendAllText(AppPaths.LogFilePath, line);
            }
            catch
            {
                // Logging should never crash the app
            }
        }
    }
}

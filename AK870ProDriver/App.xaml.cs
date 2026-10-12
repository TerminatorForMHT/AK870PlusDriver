using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace Ak870Pro
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ak870Pro", "app-startup.log");

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] App ctor ok{Environment.NewLine}");
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Log("OnLaunched begin");
            MainWindow = new MainWindow();
            Log("MainWindow constructed");
            MainWindow.Activate();
            Log("Activated");
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log($"未处理异常: {e.Exception.GetType().FullName}: {e.Message}\n{e.Exception.StackTrace}");
            Services.AppLog.Log($"未处理异常: {e.Message}");
            e.Handled = true;
        }

        internal static void Log(string msg)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); }
            catch { }
        }
    }
}

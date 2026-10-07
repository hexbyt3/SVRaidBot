using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    internal static class Program
    {
        public static readonly string WorkingDirectory = Application.StartupPath;
        public static string ConfigPath { get; private set; } = Path.Combine(WorkingDirectory, "config.json");

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main()
        {
#if NETCOREAPP
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
#endif
            var cmd = Environment.GetCommandLineArgs();
            var cfg = Array.Find(cmd, z => z.EndsWith(".json"));
            if (cfg != null)
                ConfigPath = cfg;

            Application.EnableVisualStyles();

            // Two copies from one folder share a config and a Switch and fight over both.
            // The wait lets a copy that is restarting after an update finish closing first.
            using var folderLock = new Mutex(false, FolderLockName());
            if (!TakeFolderLock(folderLock))
            {
                MessageBox.Show($"SVRaidBot is already running from this folder:\n{WorkingDirectory}",
                    "SVRaidBot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Application.Run(new Main());
            }
            finally
            {
                folderLock.ReleaseMutex();
            }
        }

        private static string FolderLockName()
        {
            var folder = Path.GetFullPath(WorkingDirectory).TrimEnd('\\').ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
            return $@"Local\SVRaidBot_{hash}";
        }

        private static bool TakeFolderLock(Mutex folderLock)
        {
            try
            {
                return folderLock.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                return true; // the previous copy crashed while holding it
            }
        }
    }
}

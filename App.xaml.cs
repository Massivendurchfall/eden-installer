using System;
using System.IO;
using System.Text;
using System.Windows;

namespace EdenInstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            System.Windows.Forms.Application.EnableVisualStyles();

            var opts = AppOptions.Parse(e.Args);
            Log.Init(opts.LogPath);
            Log.Write("UI language: " + (L.German ? "German" : "English"));

            DispatcherUnhandledException += (s, a) =>
            {
                Log.Write("Unexpected error: " + a.Exception);
                MessageBox.Show(L.T("Unerwarteter Fehler:\n", "Unexpected error:\n") + a.Exception.Message
                    + L.T("\n\nDetails stehen in ", "\n\nDetails are in ") + Log.FilePath,
                    "Eden Installer", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            var window = new MainWindow(opts);
            MainWindow = window;
            window.Show();
        }
    }

    /// <summary>Command line options. Without any, everything is normal, the switches are only for testing.</summary>
    public sealed class AppOptions
    {
        /// <summary>Test mode: all paths go into this folder, Eden is not started, the installation runs by itself.</summary>
        public string SandboxDir;
        /// <summary>Test mode: this program is started and closed instead of eden.exe.</summary>
        public string FakeEdenExe;
        /// <summary>Test mode: these files are sorted in through "Add game" right away (Eden has to be in the test folder).</summary>
        public readonly System.Collections.Generic.List<string> ImportFiles = new System.Collections.Generic.List<string>();

        public bool IsSandbox => SandboxDir != null;

        public string LogPath => IsSandbox
            ? Path.Combine(SandboxDir, "log.txt")
            : Path.Combine(Path.GetTempPath(), "Eden-Installer.log");

        public static AppOptions Parse(string[] args)
        {
            var o = new AppOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--sandbox" && i + 1 < args.Length) o.SandboxDir = Path.GetFullPath(args[++i]);
                else if (a == "--fake-eden" && i + 1 < args.Length) o.FakeEdenExe = args[++i];
                else if (a == "--import" && i + 1 < args.Length) o.ImportFiles.Add(Path.GetFullPath(args[++i]));
                else if (a == "--lang" && i + 1 < args.Length) L.Force(args[++i]);   // de or en, otherwise Windows decides
            }
            return o;
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath { get; private set; }

        public static void Init(string path)
        {
            FilePath = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, $"Eden Installer started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}", new UTF8Encoding(true));
            }
            catch { }
        }

        public static void Write(string line)
        {
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); }
                catch { }
            }
        }
    }
}

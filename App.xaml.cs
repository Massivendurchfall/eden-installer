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

            DispatcherUnhandledException += (s, a) =>
            {
                Log.Write("Unerwarteter Fehler: " + a.Exception);
                MessageBox.Show("Unerwarteter Fehler:\n" + a.Exception.Message + "\n\nDetails stehen in " + Log.FilePath,
                    "Eden Installer", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            var window = new MainWindow(opts);
            MainWindow = window;
            window.Show();
        }
    }

    /// <summary>Startparameter. Ohne Parameter ist alles normal, die beiden Schalter sind nur zum Testen.</summary>
    public sealed class AppOptions
    {
        /// <summary>Testmodus: alle Pfade landen in diesem Ordner, Eden wird nicht gestartet, die Installation läuft von selbst.</summary>
        public string SandboxDir;
        /// <summary>Testmodus: dieses Programm wird statt eden.exe gestartet und wieder geschlossen.</summary>
        public string FakeEdenExe;
        /// <summary>Testmodus: diese Dateien werden gleich über "Spiel hinzufügen" einsortiert (Eden muss im Testordner stehen).</summary>
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
                File.WriteAllText(path, $"Eden Installer gestartet am {DateTime.Now:dd.MM.yyyy HH:mm:ss}{Environment.NewLine}", new UTF8Encoding(true));
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

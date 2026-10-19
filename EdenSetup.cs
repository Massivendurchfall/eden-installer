using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdenInstaller
{
    public enum StepState { Pending, Active, Done, Failed }

    public interface IInstallerUi
    {
        void SetStep(int index, StepState state, string detail);
        /// <summary>fraction null heißt: Fortschritt unbekannt.</summary>
        void SetProgress(int step, string label, double? fraction, string value);
    }

    public sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    public sealed class SetupPaths
    {
        public string InstallDir;
        /// <summary>Edens Datenordner, normalerweise %APPDATA%\eden.</summary>
        public string DataDir;
        public string TempDir;
        /// <summary>Hier kommt nur die Verknüpfung hin.</summary>
        public string DesktopDir;
        public bool Sandbox;
        public string FakeEdenExe;

        public string EdenExe => Path.Combine(InstallDir, "eden.exe");
        public string GamesDir => Path.Combine(InstallDir, "Games");
        /// <summary>Wird in Eden als "Externer Inhalt" eingetragen, Updates und DLCs werden dort ohne NAND-Installation gefunden.</summary>
        public string DlcDir => Path.Combine(InstallDir, "Updates und DLCs");
        /// <summary>Hier packt "Spiel hinzufügen" aus, danach wird nur noch verschoben (gleiches Laufwerk).</summary>
        public string ImportTempDir => Path.Combine(InstallDir, "Import-Temp");
        public string ShortcutPath => Path.Combine(DesktopDir, "Eden.lnk");
        public string ConfigDir => Path.Combine(DataDir, "config");
        public string KeysDir => Path.Combine(DataDir, "keys");
        /// <summary>Hierhin kopiert Edens eigenes "Install Firmware" die NCAs.</summary>
        public string RegisteredDir => Path.Combine(DataDir, "nand", "system", "Contents", "registered");
    }

    /// <summary>
    /// Merkdatei unter %LOCALAPPDATA%\Eden-Installer. Steht Eden dort drin, zeigt der Installer beim nächsten
    /// Start gleich die Knöpfe (Spiel hinzufügen, Ordner öffnen, Eden starten) statt der ganzen Installation.
    /// </summary>
    public static class InstallState
    {
        const string DirKey = "InstallDir=";

        /// <summary>Der eingetragene Eden-Ordner oder null.</summary>
        public static string Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    if (line.StartsWith(DirKey, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(DirKey.Length).Trim();
            }
            catch (Exception ex) { Log.Write("Merkdatei nicht lesbar: " + ex.Message); }
            return null;
        }

        public static void Save(string file, SetupPaths p)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, new[]
                {
                    "[Eden Installer]",
                    DirKey + p.InstallDir,
                    "DataDir=" + p.DataDir,
                    "EdenVersion=" + EdenSetup.EdenVersion,
                    "FirmwareVersion=" + EdenSetup.FirmwareVersion,
                    "Eingetragen=" + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                }, new UTF8Encoding(false));
                Log.Write("Merkdatei: " + file);
            }
            catch (Exception ex) { Log.Write("Merkdatei nicht geschrieben: " + ex.Message); }
        }
    }

    public sealed class EdenSetup
    {
        public const string EdenVersion = "0.2.1";
        public const string FirmwareVersion = "22.5.0";
        public const string KeysVersion = "22.5.0";

        public const string EdenUrl = "https://stable.eden-emu.dev/v0.2.1/Eden-Windows-v0.2.1-amd64-msvc-standard.zip";
        public const string FirmwareUrl = "https://github.com/THZoria/NX_Firmware/releases/download/22.5.0/Firmware.22.5.0.zip";
        public const string KeysUrl = "https://files.prodkeys.net/ProdKeys.NET-v22.5.0.zip";

        public static readonly string[] StepTitles =
        {
            "Eden herunterladen",
            "Eden entpacken und Verknüpfung anlegen",
            "Eden einmal starten und schließen",
            "Keys herunterladen und einfügen",
            "Firmware herunterladen",
            "Firmware installieren",
        };

        // Wenn Eden beim ersten Start einen dieser Ordner findet, fragt es vor allem anderen nach einer Datenübernahme.
        static readonly string[] LegacyEmulators = { "yuzu", "citron", "sudachi", "suyu" };

        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
        static readonly HttpClient Http = CreateClient();

        readonly SetupPaths p;
        readonly IInstallerUi ui;
        readonly Stopwatch uiThrottle = Stopwatch.StartNew();
        int current = -1;

        public EdenSetup(SetupPaths paths, IInstallerUi ui)
        {
            p = paths;
            this.ui = ui;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Log.Write($"Installationsordner: {p.InstallDir}");
            Log.Write($"Datenordner: {p.DataDir}");
            CheckPreconditions();
            Directory.CreateDirectory(p.TempDir);

            Begin(0, "Lade von eden-emu.dev");
            string edenZip = Path.Combine(p.TempDir, "Eden.zip");
            long edenBytes = await DownloadAsync(EdenUrl, edenZip, "Eden wird heruntergeladen", ct);
            Finish(0, $"Eden v{EdenVersion}, {Size(edenBytes)}");

            Begin(1, "Entpacke in den Eden-Ordner");
            int edenFiles = await Task.Run(() => ExtractEden(edenZip, ct));
            DeleteFile(edenZip);
            Directory.CreateDirectory(p.GamesDir);
            Directory.CreateDirectory(p.DlcDir);
            Log.Write($"  Ordner: {p.GamesDir} und {p.DlcDir}");
            // läuft wieder auf dem UI-Thread, das braucht das COM-Objekt für die Verknüpfung
            bool shortcut = TryCreateShortcut();
            Finish(1, shortcut
                ? $"{edenFiles} Dateien entpackt, Spiele- und DLC-Ordner sowie Verknüpfung angelegt"
                : $"{edenFiles} Dateien entpackt, Spiele- und DLC-Ordner angelegt (Verknüpfung ging nicht)");

            // Liegt neben der exe ein Ordner "user", nutzt Eden den statt AppData (portabler Modus).
            string portable = Path.Combine(p.InstallDir, "user");
            if (!p.Sandbox && Directory.Exists(portable))
            {
                p.DataDir = portable;
                Log.Write("Portabler Modus erkannt, Datenordner: " + portable);
            }

            Begin(2, "Eden startet und legt seine Ordner an");
            string launchNote = await LaunchOnceAsync(ct);
            Finish(2, launchNote + ", " + TryRegisterFolders());

            Begin(3, "Lade die Keys");
            string keysZip = Path.Combine(p.TempDir, "Keys.zip");
            await DownloadAsync(KeysUrl, keysZip, "Keys werden heruntergeladen", ct);
            List<string> keyFiles = await Task.Run(() => InstallKeys(keysZip));
            DeleteFile(keysZip);
            Finish(3, $"{string.Join(" und ", keyFiles)} liegen im Ordner keys");

            Begin(4, "Lade von GitHub");
            string firmwareZip = Path.Combine(p.TempDir, "Firmware.zip");
            long firmwareBytes = await DownloadAsync(FirmwareUrl, firmwareZip, "Firmware wird heruntergeladen", ct);
            Finish(4, $"Firmware {FirmwareVersion}, {Size(firmwareBytes)}");

            Begin(5, "Kopiere die Systemdateien in Edens NAND");
            int ncaCount = await Task.Run(() => InstallFirmware(firmwareZip, ct));
            DeleteFile(firmwareZip);
            Finish(5, $"{ncaCount} Systemdateien installiert, Zip gelöscht");

            ui.SetProgress(-1, "Fertig", 1, "");
            Log.Write("Installation abgeschlossen");
        }

        public void FailCurrentStep(string message)
        {
            if (current >= 0) ui.SetStep(current, StepState.Failed, message);
        }

        public void CleanupTemp()
        {
            try
            {
                if (Directory.Exists(p.TempDir)) Directory.Delete(p.TempDir, true);
            }
            catch (Exception ex) { Log.Write("Temp-Ordner bleibt liegen: " + ex.Message); }
        }

        /// <summary>Kurzer, verständlicher Fehlertext für die Oberfläche.</summary>
        public static string Describe(Exception ex)
        {
            switch (ex)
            {
                case InstallException ie: return ie.Message;
                case HttpRequestException he:
                    return "Download fehlgeschlagen. Bitte prüfe die Internetverbindung. (" + Innermost(he).Message + ")";
                case UnauthorizedAccessException ua:
                    return "Kein Zugriff auf einen Ordner. Wähle einen anderen Installationsordner. (" + ua.Message + ")";
                case IOException io:
                    return "Dateifehler: " + io.Message;
                default:
                    return ex.Message;
            }
        }

        static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        // ---------------------------------------------------------------- Schritte

        void Begin(int step, string detail)
        {
            current = step;
            Log.Write($"Schritt {step + 1}: {StepTitles[step]}");
            ui.SetStep(step, StepState.Active, detail);
        }

        void Finish(int step, string detail)
        {
            Log.Write("  " + detail);
            ui.SetStep(step, StepState.Done, detail);
        }

        void CheckPreconditions()
        {
            if (!p.Sandbox && Process.GetProcessesByName("eden").Length > 0)
                throw new InstallException("Eden läuft gerade. Bitte schließe Eden und versuche es dann erneut.");

            // Firmware-Zip (rund 340 MB) und entpackte Firmware liegen kurz gleichzeitig auf der Platte.
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void Need(string path, long mb)
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                need[root] = (need.TryGetValue(root, out long v) ? v : 0) + mb * 1024 * 1024;
            }
            Need(p.TempDir, 400);
            Need(p.InstallDir, 100);
            Need(p.DataDir, 400);

            foreach (var kv in need)
            {
                long free;
                try { free = new DriveInfo(kv.Key).AvailableFreeSpace; }
                catch { continue; }
                if (free < kv.Value)
                    throw new InstallException($"Auf Laufwerk {kv.Key.TrimEnd('\\')} ist zu wenig Platz. Gebraucht werden etwa {Size(kv.Value)}, frei sind {Size(free)}.");
            }
        }

        async Task<long> DownloadAsync(string url, string target, string label, CancellationToken ct)
        {
            int step = current;
            Log.Write("  Download " + url);
            ui.SetProgress(step, label, 0, "Verbinde…");

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            using (ct.Register(() => resp.Dispose()))   // bricht hängende Lesevorgänge beim Abbrechen ab
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InstallException($"Download fehlgeschlagen ({(int)resp.StatusCode} {resp.ReasonPhrase}) bei {new Uri(url).Host}.");

                long? total = resp.Content.Headers.ContentLength;
                long done = 0;
                var sw = Stopwatch.StartNew();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, true))
                {
                    var buffer = new byte[1 << 17];
                    int n;
                    while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        if (Throttled()) ReportDownload(step, label, done, total, sw.Elapsed);
                    }
                }
                ReportDownload(step, label, done, total, sw.Elapsed);

                if (total.HasValue && done != total.Value)
                    throw new InstallException("Der Download wurde unterbrochen. Bitte versuche es erneut.");
                Log.Write($"  {done} Bytes in {sw.Elapsed.TotalSeconds:0.0} s");
                return done;
            }
        }

        void ReportDownload(int step, string label, long done, long? total, TimeSpan elapsed)
        {
            string speed = elapsed.TotalSeconds > 0.3 ? Size((long)(done / elapsed.TotalSeconds)) + "/s" : "";
            if (total.HasValue && total.Value > 0)
                ui.SetProgress(step, label, (double)done / total.Value, $"{Size(done)} von {Size(total.Value)}   {speed}".TrimEnd());
            else
                ui.SetProgress(step, label, null, $"{Size(done)}   {speed}".TrimEnd());
        }

        int ExtractEden(string zipPath, CancellationToken ct)
        {
            Directory.CreateDirectory(p.InstallDir);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                string prefix = CommonFolder(files);
                long total = files.Sum(e => e.Length), done = 0;
                foreach (var entry in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = SafeCombine(p.InstallDir, entry.FullName.Substring(prefix.Length));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    CopyEntry(entry, target, ref done, total, "Eden wird entpackt", ct);
                }
                if (!File.Exists(p.EdenExe))
                    throw new InstallException("Im Eden-Paket wurde keine eden.exe gefunden.");
                return files.Count;
            }
        }

        /// <summary>Startet Eden einmal, damit es seine Ordner anlegt, und schließt es wieder.</summary>
        async Task<string> LaunchOnceAsync(CancellationToken ct)
        {
            bool firstStart = !Directory.Exists(p.ConfigDir);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            bool migrationPrompt = !p.Sandbox && firstStart
                && LegacyEmulators.Any(n => Directory.Exists(Path.Combine(appData, n)));

            string exe = p.Sandbox ? p.FakeEdenExe : p.EdenExe;
            Process proc = null;
            try
            {
                if (exe != null)
                {
                    proc = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = p.InstallDir, UseShellExecute = false });
                    Log.Write($"  {Path.GetFileName(exe)} gestartet, PID {proc.Id}");
                }

                if (p.Sandbox)
                {
                    // Testmodus: Eden selbst wird nicht gestartet, die Ordner legen wir an wie Eden.
                    await Task.Delay(1000, ct);
                    Directory.CreateDirectory(p.ConfigDir);
                    Directory.CreateDirectory(p.KeysDir);
                    Directory.CreateDirectory(Path.Combine(p.DataDir, "nand"));
                }

                if (migrationPrompt)
                {
                    ui.SetStep(2, StepState.Active,
                        "Eden fragt, ob alte Daten (Yuzu, Citron und Co.) übernommen werden sollen. Bitte im Eden-Fenster auswählen, danach geht es von selbst weiter.");
                    Log.Write("  Alte Emulator-Daten gefunden, Eden zeigt den Übernahme-Dialog");
                }
                ui.SetProgress(2, "Warte auf Eden", null, "");

                // Eden legt die Ordner direkt beim Start an, bei der Übernahmefrage erst nach der Antwort.
                var limit = migrationPrompt ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(90);
                var sw = Stopwatch.StartNew();
                while (!Directory.Exists(p.ConfigDir) && proc != null && !proc.HasExited && sw.Elapsed < limit)
                    await Task.Delay(400, ct);

                bool created = Directory.Exists(p.ConfigDir);
                Log.Write(created
                    ? $"  Ordner sind da nach {sw.Elapsed.TotalSeconds:0.0} s"
                    : $"  Eden hat nach {sw.Elapsed.TotalSeconds:0} s keine Ordner angelegt");

                if (proc != null && !proc.HasExited)
                {
                    ui.SetStep(2, StepState.Active, "Eden wird wieder geschlossen");
                    // Eden kurz fertig starten lassen, damit es seine Einstellungen sauber schreibt.
                    await WaitForWindowAsync(proc, TimeSpan.FromSeconds(10), ct);
                    await Task.Delay(1500, ct);
                    await CloseAsync(proc, ct);
                }

                // Zur Sicherheit, falls Eden abgestürzt ist oder zu früh geschlossen wurde.
                Directory.CreateDirectory(p.KeysDir);
                Directory.CreateDirectory(p.RegisteredDir);

                return created
                    ? "Ordner angelegt"
                    : "Eden hat sich nicht gemeldet, Ordner direkt angelegt";
            }
            finally
            {
                if (proc != null)
                {
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(); } catch { }
                    }
                    proc.Dispose();
                }
            }
        }

        static async Task WaitForWindowAsync(Process proc, TimeSpan limit, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (!proc.HasExited && sw.Elapsed < limit && VisibleWindows(proc.Id).Count == 0)
                await Task.Delay(250, ct);
        }

        /// <summary>
        /// Schickt allen sichtbaren Fenstern von Eden ein WM_CLOSE. Beim ersten Start steht oft noch die
        /// Meldung "Decryption keys are missing" davor, die schließt die erste Runde (gilt als "Nein"),
        /// die zweite Runde schließt dann das Hauptfenster. Hilft das nicht, wird Eden beendet.
        /// </summary>
        static async Task CloseAsync(Process proc, CancellationToken ct)
        {
            for (int round = 0; round < 5 && !proc.HasExited; round++)
            {
                var windows = VisibleWindows(proc.Id);
                foreach (var hwnd in windows) Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Log.Write($"  WM_CLOSE an {windows.Count} Fenster (Runde {round + 1})");
                if (await WaitExitAsync(proc, TimeSpan.FromSeconds(2.5), ct)) break;
            }
            if (!proc.HasExited)
            {
                Log.Write("  Eden reagiert nicht aufs Schließen, wird beendet");
                try { proc.Kill(); } catch { }
                proc.WaitForExit(5000);
            }
            else Log.Write("  Eden ist geschlossen");
        }

        static async Task<bool> WaitExitAsync(Process proc, TimeSpan limit, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (!proc.HasExited && sw.Elapsed < limit) await Task.Delay(100, ct);
            return proc.HasExited;
        }

        static List<IntPtr> VisibleWindows(int pid)
        {
            var result = new List<IntPtr>();
            Native.EnumWindows((hwnd, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == pid && Native.IsWindowVisible(hwnd)) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        bool TryCreateShortcut()
        {
            try
            {
                Directory.CreateDirectory(p.DesktopDir);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                try
                {
                    dynamic link = shell.CreateShortcut(p.ShortcutPath);
                    link.TargetPath = p.EdenExe;
                    link.WorkingDirectory = p.InstallDir;
                    link.IconLocation = p.EdenExe + ",0";
                    link.Description = "Eden, Nintendo Switch Emulator";
                    link.Save();
                    Marshal.FinalReleaseComObject(link);
                }
                finally { Marshal.FinalReleaseComObject(shell); }
                Log.Write("  Verknüpfung: " + p.ShortcutPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("  Verknüpfung fehlgeschlagen: " + ex);
                return false;
            }
        }

        /// <summary>Trägt Games als Spieleverzeichnis und "Updates und DLCs" als externen Inhalt in Eden ein.</summary>
        string TryRegisterFolders()
        {
            try
            {
                var config = new EdenConfigFile(Path.Combine(p.ConfigDir, "qt-config.ini"));
                bool games = config.AddGameDir(p.GamesDir);
                bool dlc = config.AddExternalContentDir(p.DlcDir);
                if (games || dlc) config.Save();
                Log.Write($"  Spieleverzeichnis {(games ? "eingetragen" : "stand schon drin")}, externer Inhalt {(dlc ? "eingetragen" : "stand schon drin")}");
                return "Spiele- und DLC-Ordner in Eden eingetragen";
            }
            catch (Exception ex)
            {
                Log.Write("  Ordner nicht eingetragen: " + ex);
                return "Ordner bitte in Eden selbst hinzufügen";
            }
        }

        List<string> InstallKeys(string zipPath)
        {
            Directory.CreateDirectory(p.KeysDir);
            var names = new List<string>();
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".keys", StringComparison.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(p.KeysDir, entry.Name);
                    entry.ExtractToFile(target, true);
                    names.Add(entry.Name);
                    Log.Write("  " + target);
                }
            }
            if (!names.Any(n => n.Equals("prod.keys", StringComparison.OrdinalIgnoreCase)))
                throw new InstallException("Im Key-Paket wurde keine prod.keys gefunden.");
            return names.OrderBy(n => n.Equals("prod.keys", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        }

        /// <summary>Macht dasselbe wie Edens "Install Firmware": registered leeren, alle NCAs hineinkopieren.</summary>
        int InstallFirmware(string zipPath, CancellationToken ct)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var ncas = zip.Entries.Where(e => e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)).ToList();
                if (ncas.Count == 0)
                    throw new InstallException("Im Firmware-Paket wurden keine .nca Dateien gefunden.");

                DeleteDirectory(p.RegisteredDir);
                Directory.CreateDirectory(p.RegisteredDir);

                long total = ncas.Sum(e => e.Length), done = 0;
                foreach (var entry in ncas)
                {
                    ct.ThrowIfCancellationRequested();
                    CopyEntry(entry, Path.Combine(p.RegisteredDir, entry.Name), ref done, total, "Firmware wird installiert", ct);
                }
                Log.Write($"  {ncas.Count} NCAs nach {p.RegisteredDir}");
                return ncas.Count;
            }
        }

        // ---------------------------------------------------------------- Hilfen

        void CopyEntry(ZipArchiveEntry entry, string target, ref long done, long total, string label, CancellationToken ct)
        {
            using (var src = entry.Open())
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17))
            {
                var buffer = new byte[1 << 17];
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, n);
                    done += n;
                    if (Throttled())
                        ui.SetProgress(current, label, total > 0 ? (double)done / total : (double?)null, $"{Size(done)} von {Size(total)}");
                }
            }
        }

        bool Throttled()
        {
            if (uiThrottle.ElapsedMilliseconds < 100) return false;
            uiThrottle.Restart();
            return true;
        }

        /// <summary>Liegt der ganze Zip-Inhalt in einem gemeinsamen Ordner, wird der weggelassen.</summary>
        static string CommonFolder(List<ZipArchiveEntry> files)
        {
            if (files.Count == 0) return "";
            string first = files[0].FullName;
            int slash = first.IndexOf('/');
            if (slash <= 0) return "";
            string prefix = first.Substring(0, slash + 1);
            return files.All(f => f.FullName.StartsWith(prefix, StringComparison.Ordinal)) ? prefix : "";
        }

        static string SafeCombine(string root, string relative)
        {
            string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("Ungültiger Pfad im Archiv: " + relative);
            return full;
        }

        internal static void DeleteFile(string path)
        {
            for (int i = 0; ; i++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }   // Virenscanner hält die Datei kurz fest
            }
        }

        internal static void DeleteDirectory(string path)
        {
            for (int i = 0; ; i++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }
            }
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", De) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0", De) + " MB";
            return (bytes / 1073741824.0).ToString("0.00", De) + " GB";
        }

        static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            return client;
        }
    }

    static class Native
    {
        public const uint WM_CLOSE = 0x0010;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}

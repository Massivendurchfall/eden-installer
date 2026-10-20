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
        /// <summary>A null fraction means: progress unknown.</summary>
        void SetProgress(int step, string label, double? fraction, string value);
    }

    public sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    public sealed class SetupPaths
    {
        public string InstallDir;
        /// <summary>Eden's data folder, usually %APPDATA%\eden.</summary>
        public string DataDir;
        public string TempDir;
        /// <summary>Only the shortcut goes here.</summary>
        public string DesktopDir;
        public bool Sandbox;
        public string FakeEdenExe;

        public string EdenExe => Path.Combine(InstallDir, "eden.exe");
        public string GamesDir => Path.Combine(InstallDir, "Games");

        /// <summary>
        /// Registered in Eden as "external content", so updates and DLCs load without a NAND install.
        /// Named after the UI language ("Updates und DLCs" or "Updates and DLCs"), an existing folder is kept.
        /// </summary>
        public string DlcDir
        {
            get
            {
                string wanted = Path.Combine(InstallDir, L.DlcFolderName);
                if (Directory.Exists(wanted)) return wanted;
                foreach (string name in new[] { "Updates und DLCs", "Updates and DLCs" })
                    if (Directory.Exists(Path.Combine(InstallDir, name))) return Path.Combine(InstallDir, name);
                return wanted;
            }
        }

        /// <summary>"Add game" unpacks here, afterwards files are only moved (same drive).</summary>
        public string ImportTempDir => Path.Combine(InstallDir, "Import-Temp");
        public string ShortcutPath => Path.Combine(DesktopDir, "Eden.lnk");
        public string ConfigDir => Path.Combine(DataDir, "config");
        public string KeysDir => Path.Combine(DataDir, "keys");
        /// <summary>This is where Eden's own "Install Firmware" copies the NCAs.</summary>
        public string RegisteredDir => Path.Combine(DataDir, "nand", "system", "Contents", "registered");
    }

    /// <summary>
    /// State file in %LOCALAPPDATA%\Eden-Installer. If it names an Eden install, the installer starts right on the
    /// home screen (add game, open folders, start Eden) instead of the whole installation.
    /// </summary>
    public static class InstallState
    {
        /// <summary>The recorded Eden folder, or null.</summary>
        public static string Load(string file) => Read(file).TryGetValue("InstallDir", out string dir) ? dir : null;

        /// <summary>The recorded data folder (differs from %APPDATA%\eden in portable mode), or null.</summary>
        public static string LoadDataDir(string file) => Read(file).TryGetValue("DataDir", out string dir) && dir.Length > 0 ? dir : null;

        /// <summary>Installed versions (without links), or null if the state file has none.</summary>
        public static ReleaseSet LoadVersions(string file)
        {
            var values = Read(file);
            values.TryGetValue("EdenVersion", out string eden);
            values.TryGetValue("FirmwareVersion", out string firmware);
            if (eden == null && firmware == null) return null;
            // Older state files have no keys version, back then keys and firmware were always the same
            return new ReleaseSet
            {
                EdenVersion = eden,
                FirmwareVersion = firmware,
                KeysVersion = values.TryGetValue("KeysVersion", out string keys) ? keys : firmware,
            };
        }

        /// <param name="installed">null if the versions are unknown (install from before the state file).</param>
        public static void Save(string file, SetupPaths p, ReleaseSet installed)
        {
            try
            {
                var lines = new List<string> { "[Eden Installer]", "InstallDir=" + p.InstallDir, "DataDir=" + p.DataDir };
                if (installed != null)
                {
                    lines.Add("EdenVersion=" + installed.EdenVersion);
                    lines.Add("FirmwareVersion=" + installed.FirmwareVersion);
                    lines.Add("KeysVersion=" + installed.KeysVersion);
                }
                lines.Add("Saved=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
                Log.Write("State file: " + file);
            }
            catch (Exception ex) { Log.Write("State file not written: " + ex.Message); }
        }

        static Dictionary<string, string> Read(string file)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(file))
                    foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch (Exception ex) { Log.Write("State file not readable: " + ex.Message); }
            return values;
        }
    }

    public sealed class EdenSetup
    {
        public static string[] StepTitles => new[]
        {
            L.T("Eden herunterladen", "Download Eden"),
            L.T("Eden entpacken und Verknüpfung anlegen", "Unpack Eden and create the shortcut"),
            L.T("Eden einmal starten und schließen", "Start Eden once and close it"),
            L.T("Keys herunterladen und einfügen", "Download and add the keys"),
            L.T("Firmware herunterladen", "Download the firmware"),
            L.T("Firmware installieren", "Install the firmware"),
        };

        // If Eden finds one of these folders on its first start, it asks about importing data before anything else.
        static readonly string[] LegacyEmulators = { "yuzu", "citron", "sudachi", "suyu" };

        internal static readonly HttpClient Http = CreateClient();

        readonly SetupPaths p;
        readonly ReleaseSet r;
        readonly IInstallerUi ui;
        readonly Stopwatch uiThrottle = Stopwatch.StartNew();
        int current = -1;

        public EdenSetup(SetupPaths paths, ReleaseSet releases, IInstallerUi ui)
        {
            p = paths;
            r = releases;
            this.ui = ui;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Log.Write($"Install folder: {p.InstallDir}");
            Log.Write($"Data folder: {p.DataDir}");
            // Firmware and keys always need exactly the same version, anything else causes trouble
            if (r.FirmwareVersion != r.KeysVersion)
                throw new InstallException(L.T(
                    $"Firmware {r.FirmwareVersion} und Keys {r.KeysVersion} passen nicht zusammen. Bitte den Installer neu starten.",
                    $"Firmware {r.FirmwareVersion} and keys {r.KeysVersion} don't match. Please restart the installer."));
            CheckPreconditions();
            Directory.CreateDirectory(p.TempDir);

            Begin(0, L.T("Lade von eden-emu.dev", "Downloading from eden-emu.dev"));
            string edenZip = Path.Combine(p.TempDir, "Eden.zip");
            long edenBytes = await DownloadAsync(r.EdenUrl, edenZip, L.T("Eden wird heruntergeladen", "Downloading Eden"), ct);
            Finish(0, $"Eden v{r.EdenVersion}, {Size(edenBytes)}");

            Begin(1, L.T("Entpacke in den Eden-Ordner", "Unpacking into the Eden folder"));
            int edenFiles = await Task.Run(() => ExtractEden(edenZip, ct));
            DeleteFile(edenZip);
            Directory.CreateDirectory(p.GamesDir);
            Directory.CreateDirectory(p.DlcDir);
            Log.Write($"  Folders: {p.GamesDir} and {p.DlcDir}");
            // back on the UI thread, the COM object for the shortcut needs that
            bool shortcut = TryCreateShortcut();
            Finish(1, shortcut
                ? L.T($"{edenFiles} Dateien entpackt, Spiele- und DLC-Ordner sowie Verknüpfung angelegt",
                    $"{edenFiles} files unpacked, game and DLC folders and shortcut created")
                : L.T($"{edenFiles} Dateien entpackt, Spiele- und DLC-Ordner angelegt (Verknüpfung ging nicht)",
                    $"{edenFiles} files unpacked, game and DLC folders created (shortcut failed)"));

            // With a "user" folder next to the exe, Eden uses that instead of AppData (portable mode).
            string portable = Path.Combine(p.InstallDir, "user");
            if (!p.Sandbox && Directory.Exists(portable))
            {
                p.DataDir = portable;
                Log.Write("Portable mode detected, data folder: " + portable);
            }

            Begin(2, L.T("Eden startet und legt seine Ordner an", "Eden starts and creates its folders"));
            string launchNote = await LaunchOnceAsync(ct);
            Finish(2, launchNote + ", " + TryRegisterFolders());

            Begin(3, L.T("Lade die Keys", "Downloading the keys"));
            string keysZip = Path.Combine(p.TempDir, "Keys.zip");
            await DownloadAsync(r.KeysUrl, keysZip, L.T("Keys werden heruntergeladen", "Downloading keys"), ct);
            List<string> keyFiles = await Task.Run(() => InstallKeys(keysZip));
            DeleteFile(keysZip);
            Finish(3, L.T($"Keys {r.KeysVersion}, {L.Join(keyFiles)} liegen im Ordner keys",
                $"Keys {r.KeysVersion}, {L.Join(keyFiles)} are in the keys folder"));

            Begin(4, L.T("Lade von GitHub", "Downloading from GitHub"));
            string firmwareZip = Path.Combine(p.TempDir, "Firmware.zip");
            long firmwareBytes = await DownloadAsync(r.FirmwareUrl, firmwareZip, L.T("Firmware wird heruntergeladen", "Downloading firmware"), ct);
            Finish(4, $"Firmware {r.FirmwareVersion}, {Size(firmwareBytes)}" + (r.NewerFirmwareWithoutKeys != null
                ? L.T($". Die neuere {r.NewerFirmwareWithoutKeys} hat noch keine passenden Keys",
                    $". The newer {r.NewerFirmwareWithoutKeys} has no matching keys yet")
                : ""));

            Begin(5, L.T("Kopiere die Systemdateien in Edens NAND", "Copying the system files into Eden's NAND"));
            int ncaCount = await Task.Run(() => InstallFirmware(firmwareZip, ct));
            DeleteFile(firmwareZip);
            Finish(5, L.T($"{ncaCount} Systemdateien installiert, Zip gelöscht", $"{ncaCount} system files installed, zip deleted"));

            ui.SetProgress(-1, L.T("Fertig", "Done"), 1, "");
            Log.Write("Installation finished");
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
            catch (Exception ex) { Log.Write("Temp folder stays: " + ex.Message); }
        }

        /// <summary>Short, plain error text for the UI.</summary>
        public static string Describe(Exception ex)
        {
            switch (ex)
            {
                case InstallException ie: return ie.Message;
                case HttpRequestException he:
                    return L.T("Download fehlgeschlagen. Bitte prüfe die Internetverbindung. (",
                        "Download failed. Please check your internet connection. (") + Innermost(he).Message + ")";
                case UnauthorizedAccessException ua:
                    return L.T("Kein Zugriff auf einen Ordner. Wähle einen anderen Installationsordner. (",
                        "No access to a folder. Choose a different install folder. (") + ua.Message + ")";
                case IOException io:
                    return L.T("Dateifehler: ", "File error: ") + io.Message;
                default:
                    return ex.Message;
            }
        }

        static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        // ---------------------------------------------------------------- Steps

        void Begin(int step, string detail)
        {
            current = step;
            Log.Write($"Step {step + 1}: {StepTitles[step]}");
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
                throw new InstallException(L.T("Eden läuft gerade. Bitte schließe Eden und versuche es dann erneut.",
                    "Eden is running. Please close Eden and try again."));

            // The firmware zip (about 340 MB) and the unpacked firmware sit on the disk at the same time for a moment.
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
                    throw new InstallException(NotEnoughSpace(kv.Key, kv.Value, free));
            }
        }

        internal static string NotEnoughSpace(string root, long needed, long free) => L.T(
            $"Auf Laufwerk {root.TrimEnd('\\')} ist zu wenig Platz. Gebraucht werden etwa {Size(needed)}, frei sind {Size(free)}.",
            $"Not enough space on drive {root.TrimEnd('\\')}. About {Size(needed)} is needed, {Size(free)} is free.");

        async Task<long> DownloadAsync(string url, string target, string label, CancellationToken ct)
        {
            int step = current;
            Log.Write("  Download " + url);
            ui.SetProgress(step, label, 0, L.T("Verbinde…", "Connecting…"));

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            using (ct.Register(() => resp.Dispose()))   // stops hanging reads on cancel
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InstallException(L.T(
                        $"Download fehlgeschlagen ({(int)resp.StatusCode} {resp.ReasonPhrase}) bei {new Uri(url).Host}.",
                        $"Download failed ({(int)resp.StatusCode} {resp.ReasonPhrase}) at {new Uri(url).Host}."));

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
                    throw new InstallException(L.T("Der Download wurde unterbrochen. Bitte versuche es erneut.",
                        "The download was interrupted. Please try again."));
                Log.Write($"  {done} bytes in {sw.Elapsed.TotalSeconds:0.0} s");
                return done;
            }
        }

        void ReportDownload(int step, string label, long done, long? total, TimeSpan elapsed)
        {
            string speed = elapsed.TotalSeconds > 0.3 ? Size((long)(done / elapsed.TotalSeconds)) + "/s" : "";
            if (total.HasValue && total.Value > 0)
                ui.SetProgress(step, label, (double)done / total.Value, $"{Of(done, total.Value)}   {speed}".TrimEnd());
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
                    CopyEntry(entry, target, ref done, total, L.T("Eden wird entpackt", "Unpacking Eden"), ct);
                }
                if (!File.Exists(p.EdenExe))
                    throw new InstallException(L.T("Im Eden-Paket wurde keine eden.exe gefunden.", "No eden.exe found in the Eden package."));
                return files.Count;
            }
        }

        /// <summary>Starts Eden once so it creates its folders, then closes it again.</summary>
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
                    Log.Write($"  {Path.GetFileName(exe)} started, PID {proc.Id}");
                }

                if (p.Sandbox)
                {
                    // Test mode: Eden itself is not started, we create the folders like Eden would.
                    await Task.Delay(1000, ct);
                    Directory.CreateDirectory(p.ConfigDir);
                    Directory.CreateDirectory(p.KeysDir);
                    Directory.CreateDirectory(Path.Combine(p.DataDir, "nand"));
                }

                if (migrationPrompt)
                {
                    ui.SetStep(2, StepState.Active, L.T(
                        "Eden fragt, ob alte Daten (Yuzu, Citron und Co.) übernommen werden sollen. Bitte im Eden-Fenster auswählen, danach geht es von selbst weiter.",
                        "Eden asks whether to import old data (Yuzu, Citron and others). Please choose in the Eden window, then it continues on its own."));
                    Log.Write("  Old emulator data found, Eden shows the import dialog");
                }
                ui.SetProgress(2, L.T("Warte auf Eden", "Waiting for Eden"), null, "");

                // Eden creates the folders right on start, with the import question only after the answer.
                var limit = migrationPrompt ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(90);
                var sw = Stopwatch.StartNew();
                while (!Directory.Exists(p.ConfigDir) && proc != null && !proc.HasExited && sw.Elapsed < limit)
                    await Task.Delay(400, ct);

                bool created = Directory.Exists(p.ConfigDir);
                Log.Write(created
                    ? $"  Folders exist after {sw.Elapsed.TotalSeconds:0.0} s"
                    : $"  Eden created no folders within {sw.Elapsed.TotalSeconds:0} s");

                if (proc != null && !proc.HasExited)
                {
                    ui.SetStep(2, StepState.Active, L.T("Eden wird wieder geschlossen", "Closing Eden again"));
                    // Let Eden finish starting so it writes its settings cleanly.
                    await WaitForWindowAsync(proc, TimeSpan.FromSeconds(10), ct);
                    await Task.Delay(1500, ct);
                    await CloseAsync(proc, ct);
                }

                // Just in case Eden crashed or was closed too early.
                Directory.CreateDirectory(p.KeysDir);
                Directory.CreateDirectory(p.RegisteredDir);

                return created
                    ? L.T("Ordner angelegt", "Folders created")
                    : L.T("Eden hat sich nicht gemeldet, Ordner direkt angelegt", "Eden did not respond, folders created directly");
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
        /// Sends WM_CLOSE to all visible Eden windows. On the first start the "Decryption keys are missing" message
        /// is often in front, the first round closes it (counts as "No"), the second round closes the main window.
        /// If that does not help, Eden is killed.
        /// </summary>
        static async Task CloseAsync(Process proc, CancellationToken ct)
        {
            for (int round = 0; round < 5 && !proc.HasExited; round++)
            {
                var windows = VisibleWindows(proc.Id);
                foreach (var hwnd in windows) Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Log.Write($"  WM_CLOSE to {windows.Count} windows (round {round + 1})");
                if (await WaitExitAsync(proc, TimeSpan.FromSeconds(2.5), ct)) break;
            }
            if (!proc.HasExited)
            {
                Log.Write("  Eden does not react to closing, killing it");
                try { proc.Kill(); } catch { }
                proc.WaitForExit(5000);
            }
            else Log.Write("  Eden is closed");
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
                Log.Write("  Shortcut: " + p.ShortcutPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("  Shortcut failed: " + ex);
                return false;
            }
        }

        /// <summary>Registers Games as game folder and the updates and DLCs folder as external content in Eden.</summary>
        string TryRegisterFolders()
        {
            try
            {
                var config = new EdenConfigFile(Path.Combine(p.ConfigDir, "qt-config.ini"));
                bool games = config.AddGameDir(p.GamesDir);
                bool dlc = config.AddExternalContentDir(p.DlcDir);
                if (games || dlc) config.Save();
                Log.Write($"  Game folder {(games ? "added" : "was already there")}, external content {(dlc ? "added" : "was already there")}");
                return L.T("Spiele- und DLC-Ordner in Eden eingetragen", "game and DLC folders added to Eden");
            }
            catch (Exception ex)
            {
                Log.Write("  Folders not registered: " + ex);
                return L.T("Ordner bitte in Eden selbst hinzufügen", "please add the folders in Eden yourself");
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
                throw new InstallException(L.T("Im Key-Paket wurde keine prod.keys gefunden.", "No prod.keys found in the keys package."));
            return names.OrderBy(n => n.Equals("prod.keys", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        }

        /// <summary>Does the same as Eden's "Install Firmware": empty registered, copy all NCAs into it.</summary>
        int InstallFirmware(string zipPath, CancellationToken ct)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var ncas = zip.Entries.Where(e => e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)).ToList();
                if (ncas.Count == 0)
                    throw new InstallException(L.T("Im Firmware-Paket wurden keine .nca Dateien gefunden.", "No .nca files found in the firmware package."));

                DeleteDirectory(p.RegisteredDir);
                Directory.CreateDirectory(p.RegisteredDir);

                long total = ncas.Sum(e => e.Length), done = 0;
                foreach (var entry in ncas)
                {
                    ct.ThrowIfCancellationRequested();
                    CopyEntry(entry, Path.Combine(p.RegisteredDir, entry.Name), ref done, total, L.T("Firmware wird installiert", "Installing firmware"), ct);
                }
                Log.Write($"  {ncas.Count} NCAs to {p.RegisteredDir}");
                return ncas.Count;
            }
        }

        // ---------------------------------------------------------------- Helpers

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
                        ui.SetProgress(current, label, total > 0 ? (double)done / total : (double?)null, Of(done, total));
                }
            }
        }

        bool Throttled()
        {
            if (uiThrottle.ElapsedMilliseconds < 100) return false;
            uiThrottle.Restart();
            return true;
        }

        /// <summary>If the whole zip sits in one common folder, that folder is left out.</summary>
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
                throw new InstallException(L.T("Ungültiger Pfad im Archiv: ", "Invalid path in archive: ") + relative);
            return full;
        }

        internal static void DeleteFile(string path)
        {
            for (int i = 0; ; i++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }   // antivirus holds the file for a moment
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
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", L.Culture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0", L.Culture) + " MB";
            return (bytes / 1073741824.0).ToString("0.00", L.Culture) + " GB";
        }

        /// <summary>"1,2 GB von 3,4 GB" or "1.2 GB of 3.4 GB".</summary>
        public static string Of(long done, long total) => Size(done) + L.T(" von ", " of ") + Size(total);

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

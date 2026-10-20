using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace EdenInstaller
{
    public partial class MainWindow : Window, IInstallerUi, IImportUi
    {
        enum Phase { Ready, Running, Done, Failed }

        static string AddGameHintText => L.T(
            "ZIP, RAR, 7z, NSP oder XCI auswählen. Spiele, Updates und DLCs landen von selbst im richtigen Ordner.",
            "Pick ZIP, RAR, 7z, NSP or XCI files. Games, updates and DLCs land in the right folder on their own.");

        readonly AppOptions opts;
        readonly ObservableCollection<StepItem> steps = new ObservableCollection<StepItem>();
        readonly ObservableCollection<ImportItem> imports = new ObservableCollection<ImportItem>();
        readonly string stateFile;
        string installDir;
        /// <summary>Where Eden is already fully set up, otherwise null. Then there is the home screen.</summary>
        string installedDir;
        /// <summary>What the state file knows as installed, otherwise null.</summary>
        ReleaseSet installedVersions;
        /// <summary>Search for the latest versions, runs in the background from the start.</summary>
        Task<ReleaseSet> releases;
        /// <summary>Check for a newer installer on GitHub, null result if there is none.</summary>
        Task<InstallerRelease> installerCheck;
        InstallerRelease installerUpdate;
        bool selfUpdating;
        bool onHome;
        Phase phase;
        CancellationTokenSource cts;
        CancellationTokenSource importCts;
        bool importing;
        bool closeAfterCancel;
        bool progressShotTaken;

        public MainWindow(AppOptions opts)
        {
            this.opts = opts;
            InitializeComponent();

            string[] titles = EdenSetup.StepTitles;
            for (int i = 0; i < titles.Length; i++)
                steps.Add(new StepItem(i + 1, titles[i]));
            StepsList.ItemsSource = steps;
            ResultsList.ItemsSource = imports;
            AddGameHint.Text = AddGameHintText;

            // Eden itself goes to the user's programs (no admin rights needed), only the shortcut goes on the desktop.
            string localAppData = opts.IsSandbox
                ? Path.Combine(opts.SandboxDir, "LocalAppData")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            installDir = Path.Combine(localAppData, "Programs", "Eden");
            stateFile = Path.Combine(localAppData, "Eden-Installer", "installation.ini");
            installedDir = FindInstallation();
            installedVersions = InstallState.LoadVersions(stateFile);
            releases = ReleaseFinder.FindAsync(CancellationToken.None);
            ShowReleasesWhenFound();
            installerCheck = SelfUpdate.FindNewerAsync();
            if (opts.UpdatedFrom != null) ShowUpdatedNote();
            ShowInstallerUpdateWhenFound();
            MaxHeight = SystemParameters.WorkArea.Height;

            if (installedDir != null)
            {
                installDir = installedDir;
                ShowHome(null, null);
            }
            else ShowInstall();

            SourceInitialized += (s, e) => Look.DarkTitleBar(this);
            Closing += OnClosing;
            if (opts.IsSandbox)
                ContentRendered += (s, e) => Dispatcher.BeginInvoke(new Action(RunSandbox), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// First the state file, otherwise the default location (installs from before the state file have none).
        /// Without eden.exe, Eden counts as not installed.
        /// </summary>
        string FindInstallation()
        {
            string dir = InstallState.Load(stateFile);
            if (dir != null && File.Exists(Path.Combine(dir, "eden.exe"))) return dir;

            var standard = BuildPaths();
            if (File.Exists(standard.EdenExe) && Directory.Exists(standard.GamesDir) && Directory.Exists(standard.DlcDir))
            {
                Log.Write("Install without state file found: " + standard.InstallDir);
                InstallState.Save(stateFile, standard, null);
                return standard.InstallDir;
            }
            return null;
        }

        // ---------------------------------------------------------------- Views

        void ShowHome(string message, string brushKey)
        {
            onHome = true;
            UpdateChips();
            UpdateNews();
            InstallPanel.Visibility = Visibility.Collapsed;
            HomePanel.Visibility = Visibility.Visible;
            HomeFolderText.Text = installDir;
            HomeFolderText.ToolTip = installDir;
            RefreshCounts();
            SetHomeStatus(message, brushKey ?? "MutedBrush");
        }

        void ShowInstall()
        {
            onHome = false;
            UpdateChips();
            UpdateNews();
            HomePanel.Visibility = Visibility.Collapsed;
            InstallPanel.Visibility = Visibility.Visible;
            foreach (var s in steps)
            {
                s.State = StepState.Pending;
                s.Detail = null;
            }
            UpdateFolderText();
            SetPhase(Phase.Ready);
        }

        void SetHomeStatus(string text, string brushKey)
        {
            HomeStatus.Text = text ?? "";
            HomeStatus.Foreground = (Brush)FindResource(brushKey);
            HomeStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void RefreshCounts()
        {
            var paths = BuildPaths();
            int games = GameImporter.CountGames(paths.GamesDir), extras = GameImporter.CountGames(paths.DlcDir);
            CountText.Text = games == 0 && extras == 0
                ? L.T("Noch keine Spiele einsortiert.", "No games sorted in yet.")
                : L.T($"Einsortiert: {games} {(games == 1 ? "Spiel" : "Spiele")}, dazu {extras} {(extras == 1 ? "Update oder DLC" : "Updates und DLCs")}.",
                    $"Sorted in: {games} {(games == 1 ? "game" : "games")}, plus {extras} {(extras == 1 ? "update or DLC" : "updates and DLCs")}.");
        }

        // ---------------------------------------------------------------- Versions

        ReleaseSet Found => releases.Status == TaskStatus.RanToCompletion ? releases.Result : null;

        async void ShowReleasesWhenFound()
        {
            var task = releases;
            try { await task; }
            catch (OperationCanceledException) { return; }
            if (task != releases) return;   // a new search is running by now
            UpdateChips();
            UpdateNews();
            if (!onHome && phase == Phase.Ready) ProgressLabel.Text = ReadyLabel();
        }

        /// <summary>Home screen: what is installed. Installation: what is about to be downloaded.</summary>
        void UpdateChips()
        {
            ReleaseSet shown = onHome ? installedVersions : Found;
            bool searching = !onHome && shown == null;
            ChipEden.Text = Chip("Eden v", "Eden", shown?.EdenVersion, searching);
            ChipFirmware.Text = Chip("Firmware ", "Firmware", shown?.FirmwareVersion, searching);
            ChipKeys.Text = Chip("Keys ", "Keys", shown?.KeysVersion, searching);
            string waiting = !onHome ? Found?.NewerFirmwareWithoutKeys : null;
            ChipFirmware.ToolTip = waiting != null
                ? L.T($"Firmware {waiting} gibt es schon, passende Keys aber noch nicht. Firmware und Keys müssen gleich sein.",
                    $"Firmware {waiting} is already out, but matching keys are not yet. Firmware and keys have to match.")
                : null;
        }

        static string Chip(string prefix, string name, string version, bool searching) =>
            version != null ? prefix + version : searching ? name + " …" : name;

        /// <summary>On the home screen a note when newer versions exist. Firmware only counts with matching keys.</summary>
        void UpdateNews()
        {
            ReleaseSet latest = Found;
            var news = new List<string>();
            if (onHome && latest != null && installedVersions != null)
            {
                if (ReleaseFinder.IsNewer(latest.EdenVersion, installedVersions.EdenVersion))
                    news.Add("Eden v" + latest.EdenVersion);
                if (ReleaseFinder.IsNewer(latest.FirmwareVersion, installedVersions.FirmwareVersion)
                    || ReleaseFinder.IsNewer(latest.KeysVersion, installedVersions.KeysVersion))
                    news.Add(L.T("Firmware und Keys ", "firmware and keys ") + latest.FirmwareVersion);
            }
            NewsText.Text = news.Count == 0 ? ""
                : L.T($"Neu verfügbar: {string.Join(" sowie ", news)}. Über „Neu installieren“ holst du es, deine Spiele bleiben drin.",
                    $"New: {string.Join(" and ", news)}. Use “Reinstall” to get it, your games stay.");
            NewsText.Visibility = news.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        string ReadyLabel()
        {
            ReleaseSet found = Found;
            return found?.NewerFirmwareWithoutKeys != null
                ? L.T($"Bereit. Für Firmware {found.NewerFirmwareWithoutKeys} gibt es noch keine Keys, also {found.FirmwareVersion}.",
                    $"Ready. Firmware {found.NewerFirmwareWithoutKeys} has no keys yet, so {found.FirmwareVersion}.")
                : L.T("Bereit", "Ready");
        }

        // ---------------------------------------------------------------- Installer self update

        async void ShowInstallerUpdateWhenFound()
        {
            installerUpdate = await installerCheck;
            if (installerUpdate == null) return;
            SelfUpdateCard.Visibility = Visibility.Visible;
            SelfUpdateTitle.Text = L.T($"Neue Version des Installers: v{installerUpdate.Version}", $"New installer version: v{installerUpdate.Version}");
            SelfUpdateTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));
            SetSelfUpdateDetail(L.T("Holt sich die neue Version und startet neu. Eden und deine Spiele bleiben, wie sie sind.",
                "Downloads the new version and restarts. Eden and your games stay as they are."), "MutedBrush");
            SelfUpdateButton.Content = L.T("Aktualisieren", "Update");
            SelfUpdateButton.IsEnabled = true;
            SelfUpdateButton.Visibility = Visibility.Visible;
        }

        /// <summary>This instance was just swapped in by the self update.</summary>
        void ShowUpdatedNote()
        {
            SelfUpdateCard.Visibility = Visibility.Visible;
            SelfUpdateTitle.Text = L.T($"Der Installer ist jetzt auf v{SelfUpdate.Current}.", $"The installer is now on v{SelfUpdate.Current}.");
            SelfUpdateTitle.Foreground = (Brush)FindResource("SuccessBrush");
            SelfUpdateDetail.Visibility = Visibility.Collapsed;
            SelfUpdateButton.Visibility = Visibility.Collapsed;
        }

        void SetSelfUpdateDetail(string text, string brushKey)
        {
            SelfUpdateDetail.Text = text;
            SelfUpdateDetail.Foreground = (Brush)FindResource(brushKey);
            SelfUpdateDetail.Visibility = Visibility.Visible;
        }

        async void OnSelfUpdate(object sender, RoutedEventArgs e)
        {
            if (installerUpdate == null || selfUpdating) return;
            if (phase == Phase.Running || importing)
            {
                SetSelfUpdateDetail(L.T("Das geht, sobald die Installation oder das Einsortieren fertig ist.",
                    "This works once the installation or the sorting is finished."), "MutedBrush");
                return;
            }
            selfUpdating = true;
            SelfUpdateButton.IsEnabled = false;
            try
            {
                string args = opts.IsSandbox ? $"--sandbox \"{opts.SandboxDir}\"" : "";
                await SelfUpdate.ApplyAsync(installerUpdate, args,
                    text => Dispatcher.BeginInvoke(new Action(() => SetSelfUpdateDetail(text, "MutedBrush"))));
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Write("Installer update failed: " + ex);
                SetSelfUpdateDetail(EdenSetup.Describe(ex), "ErrorBrush");
                SelfUpdateButton.Content = L.T("Erneut versuchen", "Try again");
                SelfUpdateButton.IsEnabled = true;
                selfUpdating = false;
                if (opts.IsSandbox) FinishSandbox("7-update-error.png", 1);
            }
        }

        // ---------------------------------------------------------------- Installation

        async void StartInstall()
        {
            if (phase == Phase.Running || selfUpdating) return;
            foreach (var s in steps)
            {
                s.State = StepState.Pending;
                s.Detail = null;
            }
            SetPhase(Phase.Running);
            cts = new CancellationTokenSource();
            var paths = BuildPaths();
            EdenSetup setup = null;
            try
            {
                // If the search at startup failed (no internet for example), try once more now
                if (releases.IsCompleted && (Found == null || Found.UsedBuiltIn))
                {
                    releases = ReleaseFinder.FindAsync(cts.Token);
                    ShowReleasesWhenFound();
                }
                if (!releases.IsCompleted) SetProgress(-1, L.T("Suche die neuesten Versionen", "Looking for the latest versions"), null, "");
                await Task.WhenAny(releases, Task.Delay(Timeout.Infinite, cts.Token));
                cts.Token.ThrowIfCancellationRequested();
                ReleaseSet found = releases.Result;
                UpdateChips();

                setup = new EdenSetup(paths, found, this);
                await setup.RunAsync(cts.Token);
                InstallState.Save(stateFile, paths, found);   // from now on the installer starts right on the home screen
                installedVersions = found;
                installedDir = installDir;
                SetPhase(Phase.Done);
                ShowHome(L.T("Installation abgeschlossen. Mit „Spiel hinzufügen“ kommen Spiele, Updates und DLCs gleich in den richtigen Ordner.",
                        "Installation finished. Use “Add game” to put games, updates and DLCs straight into the right folder."),
                    "SuccessBrush");
                if (opts.IsSandbox) FinishSandbox("3-done.png", 0);
            }
            catch (Exception ex) when (ex is OperationCanceledException || cts.IsCancellationRequested)
            {
                Log.Write("Cancelled");
                setup?.FailCurrentStep(L.T("Abgebrochen", "Cancelled"));
                SetPhase(Phase.Failed, L.T("Installation abgebrochen.", "Installation cancelled."));
                if (closeAfterCancel) Close();
            }
            catch (Exception ex)
            {
                Log.Write("Error: " + ex);
                string message = EdenSetup.Describe(ex);
                setup?.FailCurrentStep(message);
                SetPhase(Phase.Failed, L.T("Das hat nicht geklappt. Details stehen beim roten Schritt.", "That didn't work. Details are at the red step."));
                if (opts.IsSandbox) FinishSandbox("3-error.png", 1);
            }
            finally
            {
                setup?.CleanupTemp();
            }
        }

        SetupPaths BuildPaths()
        {
            if (opts.IsSandbox)
                return new SetupPaths
                {
                    InstallDir = installDir,
                    DataDir = Path.Combine(opts.SandboxDir, "AppData", "eden"),
                    TempDir = Path.Combine(opts.SandboxDir, "Temp"),
                    DesktopDir = Path.Combine(opts.SandboxDir, "Desktop"),
                    Sandbox = true,
                    FakeEdenExe = opts.FakeEdenExe,
                };
            return new SetupPaths
            {
                InstallDir = installDir,
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eden"),
                TempDir = Path.Combine(Path.GetTempPath(), "Eden-Installer"),
                DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
        }

        void SetPhase(Phase next, string message = null)
        {
            phase = next;
            ChangeFolderButton.IsEnabled = next == Phase.Ready || next == Phase.Failed;
            PrimaryButton.IsEnabled = next != Phase.Running;
            // Coming here through "Reinstall", "Back" leads to the home screen again
            string leave = installedDir != null ? L.T("Zurück", "Back") : L.T("Schließen", "Close");
            switch (next)
            {
                case Phase.Ready:
                    PrimaryButton.Content = L.T("Installieren", "Install");
                    SecondaryButton.Content = leave;
                    SetStatus(L.T("Lädt rund 400 MB, das dauert je nach Internet ein paar Minuten.",
                        "Downloads about 400 MB, which takes a few minutes depending on your internet."), "MutedBrush");
                    ProgressLabel.Text = ReadyLabel();
                    ProgressValue.Text = "";
                    Bar.Value = 0;
                    break;
                case Phase.Running:
                    PrimaryButton.Content = L.T("Installiere…", "Installing…");
                    SecondaryButton.Content = L.T("Abbrechen", "Cancel");
                    SetStatus(L.T("Lass das Fenster einfach offen, der Rest läuft von selbst.",
                        "Just leave the window open, the rest runs on its own."), "MutedBrush");
                    break;
                case Phase.Done:
                    ProgressLabel.Text = L.T("Fertig", "Done");
                    ProgressValue.Text = "";
                    Bar.Value = 1;
                    break;
                case Phase.Failed:
                    PrimaryButton.Content = L.T("Erneut versuchen", "Try again");
                    SecondaryButton.Content = leave;
                    SetStatus(message, "ErrorBrush");
                    break;
            }
        }

        void SetStatus(string text, string brushKey)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource(brushKey);
        }

        void UpdateFolderText()
        {
            FolderText.Text = installDir;
            FolderText.ToolTip = installDir;
            string data = opts.IsSandbox ? Path.Combine(opts.SandboxDir, "AppData", "eden") : "%APPDATA%\\eden";
            DataText.Text = L.T($"Auf den Desktop kommt nur die Verknüpfung „Eden“. Keys und Firmware landen in {data}.",
                $"Only the “Eden” shortcut goes on the desktop. Keys and firmware go to {data}.");
        }

        // ---------------------------------------------------------------- Installation buttons

        void OnPrimaryClick(object sender, RoutedEventArgs e) => StartInstall();

        void OnSecondaryClick(object sender, RoutedEventArgs e)
        {
            if (phase == Phase.Running)
            {
                if (ConfirmCancel()) cts.Cancel();
            }
            else if (installedDir != null)
            {
                installDir = installedDir;
                ShowHome(null, null);
            }
            else Close();
        }

        void OnChangeFolder(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = L.T("Wähle, wo der Eden-Ordner angelegt werden soll.", "Choose where the Eden folder should be created."),
                ShowNewFolderButton = true,
                SelectedPath = Path.GetDirectoryName(installDir),
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string chosen = dialog.SelectedPath.TrimEnd('\\');
                installDir = string.Equals(Path.GetFileName(chosen), "Eden", StringComparison.OrdinalIgnoreCase)
                    ? chosen
                    : Path.Combine(chosen, "Eden");
                UpdateFolderText();
            }
        }

        // ---------------------------------------------------------------- Home screen buttons

        void OnReinstall(object sender, RoutedEventArgs e) => ShowInstall();

        void OnLaunchEden(object sender, RoutedEventArgs e)
        {
            string exe = BuildPaths().EdenExe;
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, L.T("Die eden.exe fehlt. Bitte über „Neu installieren“ wieder einrichten.",
                        "eden.exe is missing. Please set it up again with “Reinstall”."), "Eden Installer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir });
        }

        void OnOpenGames(object sender, RoutedEventArgs e) => OpenFolder(BuildPaths().GamesDir);

        void OnOpenUpdates(object sender, RoutedEventArgs e) => OpenFolder(BuildPaths().DlcDir);

        static void OpenFolder(string dir)
        {
            Directory.CreateDirectory(dir);   // in case someone deleted it
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void OnAddGame(object sender, RoutedEventArgs e)
        {
            if (selfUpdating) return;
            if (importing)
            {
                importCts.Cancel();
                AddGameTitle.Text = L.T("Wird abgebrochen…", "Cancelling…");
                return;
            }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Spiele, Updates oder DLCs auswählen", "Select games, updates or DLCs"),
                Filter = GameImporter.DialogFilter,
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) == true) RunImport(dialog.FileNames);
        }

        // ---------------------------------------------------------------- Add game

        async void RunImport(string[] files)
        {
            if (importing) return;
            importing = true;
            importCts = new CancellationTokenSource();
            imports.Clear();
            SetImporting(true);
            SetHomeStatus(null, "MutedBrush");

            var paths = BuildPaths();
            var importer = new GameImporter(paths.GamesDir, paths.DlcDir, paths.ImportTempDir, this);
            try
            {
                ImportResult result = await Task.Run(() => importer.Run(files, importCts.Token));
                SetHomeStatus(Summarize(result),
                    result.Failed > 0 ? "ErrorBrush" : result.Placed > 0 || result.Already > 0 ? "SuccessBrush" : "MutedBrush");
                OfferRecycle(result.DoneArchives);
            }
            catch (OperationCanceledException)
            {
                Log.Write("Add game cancelled");
                ImportLabel.Text = L.T("Abgebrochen", "Cancelled");
                ImportValue.Text = "";
                SetHomeStatus(L.T("Abgebrochen. Was schon einsortiert war, bleibt in den Ordnern.",
                    "Cancelled. Whatever was already sorted in stays in the folders."), "MutedBrush");
            }
            catch (Exception ex)
            {
                Log.Write("Error while adding: " + ex);
                SetHomeStatus(EdenSetup.Describe(ex), "ErrorBrush");
            }
            finally
            {
                importing = false;
                SetImporting(false);
                RefreshCounts();
                if (closeAfterCancel) Close();
                else if (opts.IsSandbox) FinishSandbox("5-added.png", 0);
            }
        }

        void SetImporting(bool on)
        {
            AddGameTitle.Text = on ? L.T("Abbrechen", "Cancel") : L.T("Spiel hinzufügen", "Add game");
            AddGameHint.Text = on ? L.T("Was schon einsortiert ist, bleibt drin.", "Whatever is already sorted in stays.") : AddGameHintText;
            ReinstallButton.IsEnabled = !on;
            ImportPanel.Visibility = Visibility.Visible;
            if (on)
            {
                ImportLabel.Text = L.T("Wird vorbereitet", "Preparing");
                ImportValue.Text = "";
                ImportBar.Value = 0;
            }
        }

        static string Summarize(ImportResult r)
        {
            var parts = new List<string>();
            if (r.Games > 0) parts.Add(r.Games == 1 ? L.T("1 Spiel", "1 game") : r.Games + L.T(" Spiele", " games"));
            if (r.Updates > 0) parts.Add(r.Updates == 1 ? L.T("1 Update", "1 update") : r.Updates + L.T(" Updates", " updates"));
            if (r.Dlcs > 0) parts.Add(r.Dlcs == 1 ? "1 DLC" : r.Dlcs + " DLCs");

            string text = parts.Count > 0 ? L.T("Fertig, " + L.Join(parts) + " einsortiert.", "Done, " + L.Join(parts) + " sorted in.")
                : r.Already > 0 ? L.T("Fertig, alles lag schon im richtigen Ordner.", "Done, everything was already in the right folder.")
                : L.T("Nichts einsortiert.", "Nothing sorted in.");
            if (r.Failed > 0)
                text += r.Failed == 1
                    ? L.T(" Eine Datei ging nicht, siehe rote Zeile.", " One file didn't work, see the red row.")
                    : L.T($" {r.Failed} Dateien gingen nicht, siehe rote Zeilen.", $" {r.Failed} files didn't work, see the red rows.");
            if (r.Placed > 0 && Process.GetProcessesByName("eden").Length > 0)
                text += L.T(" Eden ist gerade offen, starte es einmal neu, damit es die neuen Dateien findet.",
                    " Eden is open right now, restart it so it finds the new files.");
            return text;
        }

        /// <summary>Nobody needs unpacked archives anymore. On request to the recycle bin, never deleted for good.</summary>
        void OfferRecycle(List<string> archives)
        {
            var existing = archives.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (existing.Count == 0) return;
            if (opts.IsSandbox)
            {
                Log.Write("Test mode: no recycle bin question for " + string.Join(", ", existing.Select(Path.GetFileName)));
                return;
            }

            long size = existing.Sum(f => new FileInfo(f).Length);
            string list = string.Join("\n", existing.Take(8).Select(f => "• " + Path.GetFileName(f)))
                + (existing.Count > 8 ? L.T($"\n… und {existing.Count - 8} weitere", $"\n… and {existing.Count - 8} more") : "");
            var answer = MessageBox.Show(this,
                L.T($"Alles ist ausgepackt und einsortiert. Diese Archive brauchst du nicht mehr:\n\n{list}\n\nZusammen {EdenSetup.Size(size)}. In den Papierkorb verschieben?",
                    $"Everything is unpacked and sorted in. You don't need these archives anymore:\n\n{list}\n\nTogether {EdenSetup.Size(size)}. Move them to the recycle bin?"),
                L.T("Spiel hinzufügen", "Add game"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            foreach (string file in existing)
            {
                try
                {
                    FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    Log.Write("To the recycle bin: " + file);
                }
                catch (Exception ex) { Log.Write("Recycle bin failed: " + file + " (" + ex.Message + ")"); }
            }
        }

        // ---------------------------------------------------------------- Closing

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (importing)
            {
                e.Cancel = true;
                if (!opts.IsSandbox && Confirm(L.T("Es werden gerade Spiele einsortiert. Wirklich abbrechen?",
                        "Games are being sorted in right now. Really cancel?")))
                {
                    closeAfterCancel = true;
                    importCts.Cancel();
                }
                return;
            }
            if (phase != Phase.Running) return;
            e.Cancel = true;
            if (!opts.IsSandbox && ConfirmCancel())
            {
                closeAfterCancel = true;
                cts.Cancel();
            }
        }

        bool ConfirmCancel() => Confirm(L.T("Die Installation läuft noch. Wirklich abbrechen?", "The installation is still running. Really cancel?"));

        bool Confirm(string question) =>
            MessageBox.Show(this, question, "Eden Installer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // ---------------------------------------------------------------- IInstallerUi (also called from background threads)

        public void SetStep(int index, StepState state, string detail)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                steps[index].State = state;
                if (detail != null) steps[index].Detail = detail;
            }));
        }

        public void SetProgress(int step, string label, double? fraction, string value)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ProgressLabel.Text = label;
                ProgressValue.Text = value;
                Bar.Value = fraction ?? 0;
                if (opts.IsSandbox && !progressShotTaken && step == 4 && fraction > 0.4)
                {
                    progressShotTaken = true;
                    Capture("2-download.png");
                }
            }));
        }

        // ---------------------------------------------------------------- IImportUi (from the background thread)

        public void Progress(string label, double? fraction, string value)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ImportLabel.Text = label;
                ImportValue.Text = value;
                ImportBar.Value = fraction ?? 0;
            }));
        }

        public ImportItem AddItem(string name) => Dispatcher.Invoke(() =>
        {
            var item = new ImportItem(name);
            imports.Add(item);
            ResultsScroll.ScrollToEnd();
            return item;
        });

        public GameKind? Ask(string fileName) => Dispatcher.Invoke(() =>
        {
            var dialog = new KindDialog(this, fileName);
            if (opts.IsSandbox)
                dialog.ContentRendered += (s, e) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Capture((FrameworkElement)dialog.Content, "6-question.png");
                    dialog.Choose(GameKind.Update);   // test mode answers on its own
                }), DispatcherPriority.ApplicationIdle);
            dialog.ShowDialog();
            return dialog.Answer;
        });

        // ---------------------------------------------------------------- Test mode

        async void RunSandbox()
        {
            // Wait for the version search first, so the screenshots show the versions found
            try { await releases; } catch (OperationCanceledException) { }
            await Task.Delay(300);
            if (await installerCheck != null)
            {
                await Task.Delay(300);
                Capture("7-update.png");
                OnSelfUpdate(null, null);   // test mode updates on its own
                return;
            }
            if (installedDir == null)
            {
                Capture("1-start.png");
                StartInstall();
                return;
            }
            Capture("4-home.png");
            if (opts.ImportFiles.Count > 0) RunImport(opts.ImportFiles.ToArray());
            else FinishSandbox(null, 0);
        }

        void FinishSandbox(string shot, int exitCode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (shot != null) Capture(shot);
                Application.Current.Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
        }

        void Capture(string fileName) => Capture(Root, fileName);

        void Capture(FrameworkElement element, string fileName)
        {
            if (!opts.IsSandbox) return;
            element.UpdateLayout();
            const double scale = 1.5;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(opts.SandboxDir, fileName))) encoder.Save(file);
        }
    }

    public sealed class StepItem : INotifyPropertyChanged
    {
        StepState state;
        string detail;

        public StepItem(int number, string title)
        {
            Number = number;
            Title = title;
        }

        public int Number { get; }
        public string Title { get; }

        public StepState State
        {
            get => state;
            set { state = value; Changed(nameof(State)); }
        }

        public string Detail
        {
            get => detail;
            set { detail = value; Changed(nameof(Detail)); Changed(nameof(HasDetail)); }
        }

        public bool HasDetail => !string.IsNullOrEmpty(detail);

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

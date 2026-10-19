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

        const string AddGameHintText = "ZIP, RAR, 7z, NSP oder XCI auswählen. Spiele, Updates und DLCs landen von selbst im richtigen Ordner.";

        readonly AppOptions opts;
        readonly ObservableCollection<StepItem> steps = new ObservableCollection<StepItem>();
        readonly ObservableCollection<ImportItem> imports = new ObservableCollection<ImportItem>();
        readonly string stateFile;
        string installDir;
        /// <summary>Wo Eden schon fertig eingerichtet ist, sonst null. Dann gibt es die Startansicht.</summary>
        string installedDir;
        /// <summary>Was die Merkdatei als installiert kennt, sonst null.</summary>
        ReleaseSet installedVersions;
        /// <summary>Suche nach den neuesten Versionen, läuft ab dem Start im Hintergrund.</summary>
        Task<ReleaseSet> releases;
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

            for (int i = 0; i < EdenSetup.StepTitles.Length; i++)
                steps.Add(new StepItem(i + 1, EdenSetup.StepTitles[i]));
            StepsList.ItemsSource = steps;
            ResultsList.ItemsSource = imports;
            AddGameHint.Text = AddGameHintText;

            // Eden selbst kommt zu den Programmen des Benutzers (ohne Adminrechte), auf den Desktop nur die Verknüpfung.
            string localAppData = opts.IsSandbox
                ? Path.Combine(opts.SandboxDir, "LocalAppData")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            installDir = Path.Combine(localAppData, "Programs", "Eden");
            stateFile = Path.Combine(localAppData, "Eden-Installer", "installation.ini");
            installedDir = FindInstallation();
            installedVersions = InstallState.LoadVersions(stateFile);
            releases = ReleaseFinder.FindAsync(CancellationToken.None);
            ShowReleasesWhenFound();
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
        /// Erst die Merkdatei, sonst der Standardort (Installationen von vor der Merkdatei haben keine).
        /// Fehlt eden.exe, gilt Eden als nicht installiert.
        /// </summary>
        string FindInstallation()
        {
            string dir = InstallState.Load(stateFile);
            if (dir != null && File.Exists(Path.Combine(dir, "eden.exe"))) return dir;

            var standard = BuildPaths();
            if (File.Exists(standard.EdenExe) && Directory.Exists(standard.GamesDir) && Directory.Exists(standard.DlcDir))
            {
                Log.Write("Installation ohne Merkdatei gefunden: " + standard.InstallDir);
                InstallState.Save(stateFile, standard, null);
                return standard.InstallDir;
            }
            return null;
        }

        // ---------------------------------------------------------------- Ansichten

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
                ? "Noch keine Spiele einsortiert."
                : $"Einsortiert: {games} {(games == 1 ? "Spiel" : "Spiele")}, dazu {extras} {(extras == 1 ? "Update oder DLC" : "Updates und DLCs")}.";
        }

        // ---------------------------------------------------------------- Versionen

        ReleaseSet Found => releases.Status == TaskStatus.RanToCompletion ? releases.Result : null;

        async void ShowReleasesWhenFound()
        {
            var task = releases;
            try { await task; }
            catch (OperationCanceledException) { return; }
            if (task != releases) return;   // inzwischen läuft eine neue Suche
            UpdateChips();
            UpdateNews();
            if (!onHome && phase == Phase.Ready) ProgressLabel.Text = ReadyLabel();
        }

        /// <summary>Startansicht: was installiert ist. Installation: was gleich geladen wird.</summary>
        void UpdateChips()
        {
            ReleaseSet shown = onHome ? installedVersions : Found;
            bool searching = !onHome && shown == null;
            ChipEden.Text = Chip("Eden v", "Eden", shown?.EdenVersion, searching);
            ChipFirmware.Text = Chip("Firmware ", "Firmware", shown?.FirmwareVersion, searching);
            ChipKeys.Text = Chip("Keys ", "Keys", shown?.KeysVersion, searching);
            string waiting = !onHome ? Found?.NewerFirmwareWithoutKeys : null;
            ChipFirmware.ToolTip = waiting != null
                ? $"Firmware {waiting} gibt es schon, passende Keys aber noch nicht. Firmware und Keys müssen gleich sein."
                : null;
        }

        static string Chip(string prefix, string name, string version, bool searching) =>
            version != null ? prefix + version : searching ? name + " …" : name;

        /// <summary>In der Startansicht ein Hinweis, wenn es neuere Versionen gibt. Firmware zählt nur mit passenden Keys.</summary>
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
                    news.Add("Firmware und Keys " + latest.FirmwareVersion);
            }
            NewsText.Text = news.Count == 0 ? ""
                : $"Neu verfügbar: {string.Join(" sowie ", news)}. Über „Neu installieren“ holst du es, deine Spiele bleiben drin.";
            NewsText.Visibility = news.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        string ReadyLabel()
        {
            ReleaseSet found = Found;
            return found?.NewerFirmwareWithoutKeys != null
                ? $"Bereit. Für Firmware {found.NewerFirmwareWithoutKeys} gibt es noch keine Keys, also {found.FirmwareVersion}."
                : "Bereit";
        }

        // ---------------------------------------------------------------- Installation

        async void StartInstall()
        {
            if (phase == Phase.Running) return;
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
                // Ging die Suche beim Start nicht (etwa ohne Internet), jetzt noch einmal
                if (releases.IsCompleted && (Found == null || Found.UsedBuiltIn))
                {
                    releases = ReleaseFinder.FindAsync(cts.Token);
                    ShowReleasesWhenFound();
                }
                if (!releases.IsCompleted) SetProgress(-1, "Suche die neuesten Versionen", null, "");
                await Task.WhenAny(releases, Task.Delay(Timeout.Infinite, cts.Token));
                cts.Token.ThrowIfCancellationRequested();
                ReleaseSet found = releases.Result;
                UpdateChips();

                setup = new EdenSetup(paths, found, this);
                await setup.RunAsync(cts.Token);
                InstallState.Save(stateFile, paths, found);   // ab jetzt startet der Installer gleich mit den Knöpfen
                installedVersions = found;
                installedDir = installDir;
                SetPhase(Phase.Done);
                ShowHome("Installation abgeschlossen. Mit „Spiel hinzufügen“ kommen Spiele, Updates und DLCs gleich in den richtigen Ordner.",
                    "SuccessBrush");
                if (opts.IsSandbox) FinishSandbox("3-fertig.png", 0);
            }
            catch (Exception ex) when (ex is OperationCanceledException || cts.IsCancellationRequested)
            {
                Log.Write("Abgebrochen");
                setup?.FailCurrentStep("Abgebrochen");
                SetPhase(Phase.Failed, "Installation abgebrochen.");
                if (closeAfterCancel) Close();
            }
            catch (Exception ex)
            {
                Log.Write("Fehler: " + ex);
                string message = EdenSetup.Describe(ex);
                setup?.FailCurrentStep(message);
                SetPhase(Phase.Failed, "Das hat nicht geklappt. Details stehen beim roten Schritt.");
                if (opts.IsSandbox) FinishSandbox("3-fehler.png", 1);
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
            // Kommt man über "Neu installieren" hierher, geht es mit "Zurück" wieder zu den Knöpfen
            string leave = installedDir != null ? "Zurück" : "Schließen";
            switch (next)
            {
                case Phase.Ready:
                    PrimaryButton.Content = "Installieren";
                    SecondaryButton.Content = leave;
                    SetStatus("Lädt rund 400 MB, das dauert je nach Internet ein paar Minuten.", "MutedBrush");
                    ProgressLabel.Text = ReadyLabel();
                    ProgressValue.Text = "";
                    Bar.Value = 0;
                    break;
                case Phase.Running:
                    PrimaryButton.Content = "Installiere…";
                    SecondaryButton.Content = "Abbrechen";
                    SetStatus("Lass das Fenster einfach offen, der Rest läuft von selbst.", "MutedBrush");
                    break;
                case Phase.Done:
                    ProgressLabel.Text = "Fertig";
                    ProgressValue.Text = "";
                    Bar.Value = 1;
                    break;
                case Phase.Failed:
                    PrimaryButton.Content = "Erneut versuchen";
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
            DataText.Text = "Auf den Desktop kommt nur die Verknüpfung „Eden“. Keys und Firmware landen in "
                + (opts.IsSandbox ? Path.Combine(opts.SandboxDir, "AppData", "eden") : "%APPDATA%\\eden") + ".";
        }

        // ---------------------------------------------------------------- Knöpfe der Installation

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
                Description = "Wähle, wo der Eden-Ordner angelegt werden soll.",
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

        // ---------------------------------------------------------------- Knöpfe der Startansicht

        void OnReinstall(object sender, RoutedEventArgs e) => ShowInstall();

        void OnLaunchEden(object sender, RoutedEventArgs e)
        {
            string exe = BuildPaths().EdenExe;
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, "Die eden.exe fehlt. Bitte über „Neu installieren“ wieder einrichten.", "Eden Installer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir });
        }

        void OnOpenGames(object sender, RoutedEventArgs e) => OpenFolder(BuildPaths().GamesDir);

        void OnOpenUpdates(object sender, RoutedEventArgs e) => OpenFolder(BuildPaths().DlcDir);

        static void OpenFolder(string dir)
        {
            Directory.CreateDirectory(dir);   // falls ihn jemand gelöscht hat
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void OnAddGame(object sender, RoutedEventArgs e)
        {
            if (importing)
            {
                importCts.Cancel();
                AddGameTitle.Text = "Wird abgebrochen…";
                return;
            }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Spiele, Updates oder DLCs auswählen",
                Filter = GameImporter.DialogFilter,
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) == true) RunImport(dialog.FileNames);
        }

        // ---------------------------------------------------------------- Spiel hinzufügen

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
                Log.Write("Spiel hinzufügen abgebrochen");
                ImportLabel.Text = "Abgebrochen";
                ImportValue.Text = "";
                SetHomeStatus("Abgebrochen. Was schon einsortiert war, bleibt in den Ordnern.", "MutedBrush");
            }
            catch (Exception ex)
            {
                Log.Write("Fehler beim Hinzufügen: " + ex);
                SetHomeStatus(EdenSetup.Describe(ex), "ErrorBrush");
            }
            finally
            {
                importing = false;
                SetImporting(false);
                RefreshCounts();
                if (closeAfterCancel) Close();
                else if (opts.IsSandbox) FinishSandbox("5-hinzugefuegt.png", 0);
            }
        }

        void SetImporting(bool on)
        {
            AddGameTitle.Text = on ? "Abbrechen" : "Spiel hinzufügen";
            AddGameHint.Text = on ? "Was schon einsortiert ist, bleibt drin." : AddGameHintText;
            ReinstallButton.IsEnabled = !on;
            ImportPanel.Visibility = Visibility.Visible;
            if (on)
            {
                ImportLabel.Text = "Wird vorbereitet";
                ImportValue.Text = "";
                ImportBar.Value = 0;
            }
        }

        static string Summarize(ImportResult r)
        {
            var parts = new List<string>();
            if (r.Games > 0) parts.Add(r.Games == 1 ? "1 Spiel" : r.Games + " Spiele");
            if (r.Updates > 0) parts.Add(r.Updates == 1 ? "1 Update" : r.Updates + " Updates");
            if (r.Dlcs > 0) parts.Add(r.Dlcs == 1 ? "1 DLC" : r.Dlcs + " DLCs");

            string text = parts.Count > 0 ? "Fertig, " + JoinGerman(parts) + " einsortiert."
                : r.Already > 0 ? "Fertig, alles lag schon im richtigen Ordner."
                : "Nichts einsortiert.";
            if (r.Failed > 0)
                text += r.Failed == 1 ? " Eine Datei ging nicht, siehe rote Zeile." : $" {r.Failed} Dateien gingen nicht, siehe rote Zeilen.";
            if (r.Placed > 0 && Process.GetProcessesByName("eden").Length > 0)
                text += " Eden ist gerade offen, starte es einmal neu, damit es die neuen Dateien findet.";
            return text;
        }

        static string JoinGerman(List<string> parts) =>
            parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " und " + parts[parts.Count - 1];

        /// <summary>Ausgepackte Archive braucht keiner mehr. Auf Nachfrage in den Papierkorb, nie endgültig löschen.</summary>
        void OfferRecycle(List<string> archives)
        {
            var existing = archives.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (existing.Count == 0) return;
            if (opts.IsSandbox)
            {
                Log.Write("Testmodus: keine Papierkorb-Frage für " + string.Join(", ", existing.Select(Path.GetFileName)));
                return;
            }

            long size = existing.Sum(f => new FileInfo(f).Length);
            string list = string.Join("\n", existing.Take(8).Select(f => "• " + Path.GetFileName(f)))
                + (existing.Count > 8 ? $"\n… und {existing.Count - 8} weitere" : "");
            var answer = MessageBox.Show(this,
                $"Alles ist ausgepackt und einsortiert. Diese Archive brauchst du nicht mehr:\n\n{list}\n\n"
                + $"Zusammen {EdenSetup.Size(size)}. In den Papierkorb verschieben?",
                "Spiel hinzufügen", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            foreach (string file in existing)
            {
                try
                {
                    FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    Log.Write("In den Papierkorb: " + file);
                }
                catch (Exception ex) { Log.Write("Papierkorb ging nicht: " + file + " (" + ex.Message + ")"); }
            }
        }

        // ---------------------------------------------------------------- Schließen

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (importing)
            {
                e.Cancel = true;
                if (!opts.IsSandbox && Confirm("Es werden gerade Spiele einsortiert. Wirklich abbrechen?"))
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

        bool ConfirmCancel() => Confirm("Die Installation läuft noch. Wirklich abbrechen?");

        bool Confirm(string question) =>
            MessageBox.Show(this, question, "Eden Installer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // ---------------------------------------------------------------- IInstallerUi (kommt auch aus Hintergrund-Threads)

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

        // ---------------------------------------------------------------- IImportUi (aus dem Hintergrund-Thread)

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
                    Capture((FrameworkElement)dialog.Content, "6-frage.png");
                    dialog.Choose(GameKind.Update);   // Testmodus antwortet selbst
                }), DispatcherPriority.ApplicationIdle);
            dialog.ShowDialog();
            return dialog.Answer;
        });

        // ---------------------------------------------------------------- Testmodus

        async void RunSandbox()
        {
            // Erst die Versionssuche abwarten, damit die Bilder die gefundenen Versionen zeigen
            try { await releases; } catch (OperationCanceledException) { }
            await Task.Delay(300);
            if (installedDir == null)
            {
                Capture("1-start.png");
                StartInstall();
                return;
            }
            Capture("4-start-installiert.png");
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EdenInstaller
{
    public enum GameKind { Base, Update, Dlc }

    public enum ImportTone { Busy, Game, Update, Dlc, Mixed, Skip, Error }

    public interface IImportUi
    {
        /// <summary>A null fraction means: progress unknown.</summary>
        void Progress(string label, double? fraction, string value);
        /// <summary>New row in the list, one per selected file.</summary>
        ImportItem AddItem(string name);
        /// <summary>Asks what a file is when it can't be recognized. null means skip.</summary>
        GameKind? Ask(string fileName);
    }

    public sealed class ImportResult
    {
        public int Games, Updates, Dlcs, Already, Skipped, Failed;
        /// <summary>Archives (including further RAR parts) whose content is fully sorted. These may go to the recycle bin.</summary>
        public readonly List<string> DoneArchives = new List<string>();
        public int Placed => Games + Updates + Dlcs;
    }

    /// <summary>
    /// "Add game": unpacks ZIP, RAR and 7z and puts NSP, NSZ, XCI and XCZ in the right place, base games into Games,
    /// updates and DLCs into the updates and DLCs folder.
    /// Recognized in this order: cnmt.xml in the package, tickets in the package (named after the title ID),
    /// title ID in the file name, words like "Update" or "DLC" in the name. If none of that helps, the user is asked.
    /// </summary>
    public sealed class GameImporter
    {
        public static string DialogFilter =>
            L.T("Spiele und Archive", "Games and archives") + " (*.zip;*.rar;*.7z;*.nsp;*.nsz;*.xci;*.xcz)|*.zip;*.rar;*.7z;*.nsp;*.nsz;*.xci;*.xcz|"
            + L.T("Alle Dateien", "All files") + " (*.*)|*.*";

        static readonly string[] GameExtensions = { ".nsp", ".nsz", ".xci", ".xcz" };

        // Title IDs: a base game has the lower 13 bits clear, its update is base | 0x800,
        // a DLC is base + 0x1000 + number. Tickets are named <title ID><16 more digits>.tik.
        static readonly Regex TitleIdInName = new Regex(@"(?<![0-9A-Fa-f])01[0-9A-Fa-f]{14}(?![0-9A-Fa-f])");
        static readonly Regex TicketName = new Regex(@"^(01[0-9A-Fa-f]{14})[0-9A-Fa-f]{16}\.tik$", RegexOptions.IgnoreCase);
        static readonly Regex UpdateWord = new Regex(@"(^|[^a-z])(update|upd)([^a-z]|$)", RegexOptions.IgnoreCase);
        static readonly Regex DlcWord = new Regex(@"(^|[^a-z])dlc([^a-z]|$)", RegexOptions.IgnoreCase);
        static readonly Regex RarPart = new Regex(@"^(.*)\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase);
        static readonly Regex OldRarPart = new Regex(@"\.r\d{2}$", RegexOptions.IgnoreCase);

        readonly string gamesDir, dlcDir, stagingDir;
        readonly IImportUi ui;
        readonly ImportResult result = new ImportResult();
        readonly Stopwatch uiThrottle = Stopwatch.StartNew();
        int stagingCounter;

        /// <param name="stagingDir">Unpacking happens here. It sits next to the target folders, so moving is instant.</param>
        public GameImporter(string gamesDir, string dlcDir, string stagingDir, IImportUi ui)
        {
            this.gamesDir = gamesDir;
            this.dlcDir = dlcDir;
            this.stagingDir = stagingDir;
            this.ui = ui;
        }

        public ImportResult Run(IList<string> files, CancellationToken ct)
        {
            Directory.CreateDirectory(gamesDir);
            Directory.CreateDirectory(dlcDir);
            TryDeleteDirectory(stagingDir);   // leftovers from a cancelled run

            var all = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var selected = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string file in all)
                {
                    ct.ThrowIfCancellationRequested();
                    string first = FirstRarPart(file);
                    if (first != null && !SamePath(first, file))
                    {
                        // Parts 2, 3 and so on are unpacked together with part 1
                        if (!selected.Contains(first))
                            Skip(file, L.T("Teil eines mehrteiligen Archivs, bitte Teil 1 auswählen",
                                "Part of a multi-part archive, please select part 1"));
                        continue;
                    }
                    ImportOne(file, ct);
                }
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }
            ui.Progress(L.T("Fertig", "Done"), 1, "");
            return result;
        }

        // ---------------------------------------------------------------- per selected file

        void ImportOne(string file, CancellationToken ct)
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            Log.Write("Add game: " + file);

            if (!IsGame(name) && ext != ".zip" && ext != ".rar" && ext != ".7z")
            {
                Skip(file, L.T("Weder Spiel noch Archiv", "Neither a game nor an archive"));
                return;
            }

            ImportItem item = ui.AddItem(name);
            var t = new Tally();
            try
            {
                if (IsGame(name)) PlaceGame(file, name, false, t, item, ct);
                else if (ext == ".zip") ImportZip(file, t, item, ct);
                else ImportWithTool(file, t, item, ct);
            }
            catch (OperationCanceledException)
            {
                item.Set(ImportTone.Skip, L.T("Abgebrochen", "Cancelled"), Describe(t));
                Count(t);
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("  Error: " + ex);
                item.Set(ImportTone.Error, L.T("Fehler", "Error"), EdenSetup.Describe(ex));
                Count(t);
                result.Failed++;
                return;
            }

            Count(t);
            if (t.Found == 0)
            {
                item.Set(ImportTone.Skip, L.T("Leer", "Empty"),
                    L.T("Keine Spieldatei (NSP, NSZ, XCI oder XCZ) drin", "No game file (NSP, NSZ, XCI or XCZ) inside"));
                result.Skipped++;
                return;
            }
            var (tone, badge) = Badge(t);
            item.Set(tone, badge, Describe(t));

            if (!IsGame(name) && t.Skipped == 0)
            {
                result.DoneArchives.Add(file);
                result.DoneArchives.AddRange(OtherParts(file));
            }
        }

        void Skip(string file, string reason)
        {
            Log.Write($"  skipped: {file} ({reason})");
            ui.AddItem(Path.GetFileName(file)).Set(ImportTone.Skip, L.T("Übersprungen", "Skipped"), reason);
            result.Skipped++;
        }

        void Count(Tally t)
        {
            result.Games += t.Games;
            result.Updates += t.Updates;
            result.Dlcs += t.Dlcs;
            result.Already += t.Already;
            result.Skipped += t.Skipped;
        }

        void ImportZip(string archive, Tally t, ImportItem item, CancellationToken ct)
        {
            string label = L.T("Entpacke ", "Unpacking ") + Path.GetFileName(archive);
            try
            {
                using (var zip = ZipFile.OpenRead(archive))
                {
                    var games = zip.Entries.Where(e => IsGame(e.Name)).ToList();
                    long total = games.Sum(e => e.Length), done = 0;
                    foreach (var entry in games)
                    {
                        ct.ThrowIfCancellationRequested();
                        EnsureSpace(stagingDir, entry.Length);
                        string fileName = CleanName(entry.Name);
                        string staged = Path.Combine(NewStagingFolder(), fileName);
                        item.Detail = L.T("Wird entpackt", "Unpacking");
                        try { CopyToFile(entry.Open(), staged, ref done, total, label, ct); }
                        catch { EdenSetup.DeleteFile(staged); throw; }
                        PlaceGame(staged, fileName, true, t, item, ct);
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                // Deflate64 for example: .NET can't, 7-Zip and tar can
                Log.Write("  Zip not readable: " + ex.Message);
                if (t.Found > 0 || FindTool() == null)
                    throw new InstallException(L.T(
                        "Das ZIP ließ sich nicht entpacken. Vielleicht ist es unvollständig, beschädigt oder hat ein Passwort.",
                        "The ZIP could not be unpacked. It may be incomplete, damaged or password protected."));
                ImportWithTool(archive, t, item, ct);
            }
        }

        /// <summary>RAR and 7z (and ZIPs .NET can't read) through 7-Zip or the tar that ships with Windows.</summary>
        void ImportWithTool(string archive, Tally t, ImportItem item, CancellationToken ct)
        {
            var tool = FindTool();
            if (tool == null)
                throw new InstallException(L.T(
                    "Zum Entpacken von RAR und 7z wird 7-Zip gebraucht (7-zip.org). Oder entpacke das Archiv selbst und wähle dann die NSP- oder XCI-Datei aus.",
                    "Unpacking RAR and 7z needs 7-Zip (7-zip.org). Or unpack the archive yourself and then select the NSP or XCI file."));

            string outDir = NewStagingFolder();
            long expected = new FileInfo(archive).Length + OtherParts(archive).Sum(f => new FileInfo(f).Length);
            EnsureSpace(outDir, expected + expected / 20);
            item.Detail = L.T("Wird entpackt", "Unpacking");

            var (exe, sevenZip) = tool.Value;
            string args = sevenZip
                ? $"x -y -bd \"-o{outDir}\" \"{archive}\""
                : $"-x -f \"{archive}\" -C \"{outDir}\"";
            Log.Write($"  {exe} {args}");

            var tail = new Queue<string>();
            DataReceivedEventHandler keep = (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (tail)
                {
                    tail.Enqueue(e.Data.Trim());
                    while (tail.Count > 6) tail.Dequeue();
                }
            };
            using (var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            })
            {
                proc.OutputDataReceived += keep;
                proc.ErrorDataReceived += keep;
                proc.Start();
                proc.StandardInput.Close();   // if the archive wants a password, it fails right away instead of waiting
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                string label = L.T("Entpacke ", "Unpacking ") + Path.GetFileName(archive);
                try
                {
                    while (!proc.WaitForExit(400))
                    {
                        ct.ThrowIfCancellationRequested();
                        long done = DirectorySize(outDir);
                        ui.Progress(label, expected > 0 ? Math.Min(0.99, (double)done / expected) : (double?)null,
                            EdenSetup.Size(done) + L.T(" von etwa ", " of about ") + EdenSetup.Size(expected));
                    }
                    proc.WaitForExit();   // collect the remaining output
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                    throw;
                }

                string output;
                lock (tail) output = string.Join(" | ", tail);
                Log.Write($"  exit code {proc.ExitCode}: {output}");
                // 7-Zip returns 1 for warnings, the files are there anyway
                if (proc.ExitCode > (sevenZip ? 1 : 0))
                    throw new InstallException(L.T(
                        "Das Archiv ließ sich nicht entpacken. Vielleicht ist es unvollständig, beschädigt oder hat ein Passwort.",
                        "The archive could not be unpacked. It may be incomplete, damaged or password protected."));
            }

            foreach (string path in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Where(f => IsGame(f)).OrderBy(f => f))
            {
                ct.ThrowIfCancellationRequested();
                PlaceGame(path, CleanName(Path.GetFileName(path)), true, t, item, ct);
            }
            TryDeleteDirectory(outDir);
        }

        // ---------------------------------------------------------------- sorting

        void PlaceGame(string path, string fileName, bool staged, Tally t, ImportItem item, CancellationToken ct)
        {
            GameKind? kind = Classify(path, fileName, out string how);
            Log.Write($"  {fileName}: {KindName(kind)} ({how})");
            if (kind == null)
            {
                kind = ui.Ask(fileName);
                Log.Write("  answer: " + KindName(kind));
                if (kind == null)
                {
                    t.Skipped++;
                    if (staged) EdenSetup.DeleteFile(path);
                    return;
                }
            }

            string target = Path.Combine(kind == GameKind.Base ? gamesDir : dlcDir, fileName);
            if (SamePath(path, target))
            {
                t.Already++;
                return;
            }
            if (File.Exists(target))
            {
                if (new FileInfo(target).Length == new FileInfo(path).Length)
                {
                    Log.Write("  already there: " + target);
                    t.Already++;
                    if (staged) EdenSetup.DeleteFile(path);
                    return;
                }
                Log.Write("  replaces the older file with the same name");
                EdenSetup.DeleteFile(target);
            }
            Move(path, target, item, ct);
            Log.Write("  to " + target);
            t.Add(kind.Value);
        }

        /// <summary>Instant on the same drive, otherwise copy with progress and delete the original afterwards.</summary>
        void Move(string source, string target, ImportItem item, CancellationToken ct)
        {
            if (SameDrive(source, target))
            {
                File.Move(source, target);
                return;
            }
            long length = new FileInfo(source).Length, done = 0;
            EnsureSpace(target, length);
            string part = target + ".part";
            item.Detail = L.T("Wird kopiert", "Copying");
            try
            {
                CopyToFile(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17), part,
                    ref done, length, L.T("Kopiere ", "Copying ") + Path.GetFileName(target), ct);
                File.Move(part, target);
            }
            catch
            {
                EdenSetup.DeleteFile(part);
                throw;
            }
            EdenSetup.DeleteFile(source);
        }

        // ---------------------------------------------------------------- recognizing

        public static GameKind? Classify(string path, string fileName, out string how)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".xci" || ext == ".xcz")
            {
                how = "cartridge image";
                return GameKind.Base;
            }

            if (ext == ".nsp" || ext == ".nsz")
            {
                GameKind? inside = FromPackage(path, out how);
                if (inside != null) return inside;
            }

            GameKind? byId = Strongest(TitleIdInName.Matches(fileName).Cast<Match>()
                .Select(m => KindOf(ulong.Parse(m.Value, NumberStyles.HexNumber))));
            if (byId != null)
            {
                how = "title ID in name";
                return byId;
            }

            if (DlcWord.IsMatch(fileName))
            {
                how = "DLC in name";
                return GameKind.Dlc;
            }
            if (UpdateWord.IsMatch(fileName))
            {
                how = "update in name";
                return GameKind.Update;
            }
            how = "not recognized";
            return null;
        }

        static GameKind? KindOf(ulong titleId)
        {
            ulong low = titleId & 0x1FFF;
            if (low == 0) return GameKind.Base;
            if (low == 0x800) return GameKind.Update;
            if (low > 0x1000) return GameKind.Dlc;
            return null;
        }

        /// <summary>Bundles count by their most important part: with a game as game, otherwise with an update as update.</summary>
        static GameKind? Strongest(IEnumerable<GameKind?> kinds)
        {
            var list = kinds.Where(k => k != null).Select(k => k.Value).ToList();
            if (list.Count == 0) return null;
            if (list.Contains(GameKind.Base)) return GameKind.Base;
            return list.Contains(GameKind.Update) ? GameKind.Update : GameKind.Dlc;
        }

        /// <summary>Looks into the file table of an NSP or NSZ, which is not encrypted.</summary>
        static GameKind? FromPackage(string path, out string how)
        {
            how = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var entries = ReadPfs0(fs, out long dataStart);

                    GameKind? fromXml = Strongest(entries
                        .Where(e => e.Name.EndsWith(".cnmt.xml", StringComparison.OrdinalIgnoreCase))
                        .Select(e => FromCnmtXml(fs, dataStart + e.Offset, e.Size)).ToList());
                    if (fromXml != null)
                    {
                        how = "cnmt.xml in package";
                        return fromXml;
                    }

                    GameKind? fromTickets = Strongest(entries
                        .Select(e => TicketName.Match(e.Name)).Where(m => m.Success)
                        .Select(m => KindOf(ulong.Parse(m.Groups[1].Value, NumberStyles.HexNumber))).ToList());
                    if (fromTickets != null)
                    {
                        how = "ticket in package";
                        return fromTickets;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("  Package not readable: " + ex.Message);
            }
            return null;
        }

        struct PackageEntry
        {
            public string Name;
            public long Offset, Size;
        }

        /// <summary>File table of a PFS0 (NSP and NSZ). Empty if the file is not one.</summary>
        static List<PackageEntry> ReadPfs0(FileStream fs, out long dataStart)
        {
            var list = new List<PackageEntry>();
            dataStart = 0;
            var r = new BinaryReader(fs, Encoding.UTF8, true);
            if (fs.Length < 16 || r.ReadUInt32() != 0x30534650) return list;   // "PFS0"
            int count = r.ReadInt32(), stringSize = r.ReadInt32();
            r.ReadInt32();
            if (count <= 0 || count > 4096 || stringSize <= 0 || stringSize > (1 << 20)) return list;
            long headerSize = 16 + 24L * count + stringSize;
            if (headerSize > fs.Length) return list;

            var raw = new (long offset, long size, int nameOffset)[count];
            for (int i = 0; i < count; i++)
            {
                raw[i] = (r.ReadInt64(), r.ReadInt64(), r.ReadInt32());
                r.ReadInt32();
            }
            byte[] names = r.ReadBytes(stringSize);
            foreach (var e in raw)
            {
                if (e.nameOffset < 0 || e.nameOffset >= names.Length) continue;
                int end = Array.IndexOf(names, (byte)0, e.nameOffset);
                if (end < 0) end = names.Length;
                list.Add(new PackageEntry
                {
                    Name = Encoding.UTF8.GetString(names, e.nameOffset, end - e.nameOffset),
                    Offset = e.offset,
                    Size = e.size,
                });
            }
            dataStart = headerSize;
            return list;
        }

        /// <summary>Some NSPs also carry the metadata as readable XML, it names the type directly.</summary>
        static GameKind? FromCnmtXml(FileStream fs, long offset, long size)
        {
            if (size <= 0 || size > (1 << 20) || offset + size > fs.Length) return null;
            fs.Position = offset;
            var buffer = new byte[size];
            int read = 0, n;
            while (read < size && (n = fs.Read(buffer, read, (int)size - read)) > 0) read += n;
            string xml = Encoding.UTF8.GetString(buffer, 0, read);

            switch (Regex.Match(xml, @"<Type>\s*(\w+)\s*</Type>").Groups[1].Value)
            {
                case "Application": return GameKind.Base;
                case "Patch": return GameKind.Update;
                case "AddOnContent": return GameKind.Dlc;
            }
            var id = Regex.Match(xml, @"<Id>\s*0x([0-9A-Fa-f]{16})\s*</Id>");
            return id.Success ? KindOf(ulong.Parse(id.Groups[1].Value, NumberStyles.HexNumber)) : null;
        }

        // ---------------------------------------------------------------- helpers

        public static bool IsGame(string fileName) =>
            GameExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

        public static int CountGames(string dir)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(f => IsGame(f)) : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        static string KindName(GameKind? kind) =>
            kind == GameKind.Base ? "game" : kind == GameKind.Update ? "update" : kind == GameKind.Dlc ? "DLC" : "unknown";

        static (ImportTone, string) Badge(Tally t)
        {
            int kinds = (t.Games > 0 ? 1 : 0) + (t.Updates > 0 ? 1 : 0) + (t.Dlcs > 0 ? 1 : 0);
            if (kinds == 0)
                return t.Already > 0 ? (ImportTone.Skip, L.T("Schon da", "Already there")) : (ImportTone.Skip, L.T("Übersprungen", "Skipped"));
            if (kinds > 1) return (ImportTone.Mixed, L.T("Mehrere", "Several"));
            if (t.Games > 0) return (ImportTone.Game, L.T("Spiel", "Game"));
            return t.Updates > 0 ? (ImportTone.Update, "Update") : (ImportTone.Dlc, "DLC");
        }

        string Describe(Tally t)
        {
            var parts = new List<string>();
            if (t.Games > 0)
                parts.Add(L.T(t.Games == 1 ? "Spiel" : t.Games + " Spiele", t.Games == 1 ? "game" : t.Games + " games")
                    + L.T(" in ", " in ") + L.Q(Path.GetFileName(gamesDir)));
            var extra = new List<string>();
            if (t.Updates > 0) extra.Add(t.Updates == 1 ? L.T("Update", "update") : t.Updates + L.T(" Updates", " updates"));
            if (t.Dlcs > 0) extra.Add(t.Dlcs == 1 ? "DLC" : t.Dlcs + " DLCs");
            if (extra.Count > 0) parts.Add(L.Join(extra) + " in " + L.Q(Path.GetFileName(dlcDir)));
            if (t.Already > 0)
                parts.Add(L.T(t.Already == 1 ? "lag schon im richtigen Ordner" : t.Already + " lagen schon im richtigen Ordner",
                    t.Already == 1 ? "was already in the right folder" : t.Already + " were already in the right folder"));
            if (t.Skipped > 0)
                parts.Add(L.T(t.Skipped == 1 ? "eine Datei übersprungen" : t.Skipped + " Dateien übersprungen",
                    t.Skipped == 1 ? "one file skipped" : t.Skipped + " files skipped"));
            string s = string.Join(", ", parts);
            return s.Length == 0 ? "" : char.ToUpper(s[0], L.Culture) + s.Substring(1);
        }

        /// <summary>For "x.part2.rar" the path of part 1, for "x.part1.rar" the file itself, otherwise null.</summary>
        static string FirstRarPart(string file)
        {
            var m = RarPart.Match(Path.GetFileName(file));
            if (!m.Success) return null;
            if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) == 1) return file;
            string prefix = m.Groups[1].Value;
            try
            {
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(file), prefix + ".part*.rar"))
                {
                    var o = RarPart.Match(Path.GetFileName(f));
                    if (o.Success && string.Equals(o.Groups[1].Value, prefix, StringComparison.OrdinalIgnoreCase)
                        && int.Parse(o.Groups[2].Value, CultureInfo.InvariantCulture) == 1)
                        return f;
                }
            }
            catch (IOException) { }
            return Path.Combine(Path.GetDirectoryName(file), prefix + ".part1.rar");
        }

        /// <summary>Further parts of a multi-part RAR that are read along when unpacking.</summary>
        static List<string> OtherParts(string archive)
        {
            var list = new List<string>();
            string dir = Path.GetDirectoryName(archive), name = Path.GetFileName(archive);
            try
            {
                var m = RarPart.Match(name);
                if (m.Success)
                    list.AddRange(Directory.GetFiles(dir, m.Groups[1].Value + ".part*.rar").Where(f =>
                    {
                        var o = RarPart.Match(Path.GetFileName(f));
                        return o.Success && string.Equals(o.Groups[1].Value, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) && !SamePath(f, archive);
                    }));
                else if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    list.AddRange(Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(name) + ".r??").Where(f => OldRarPart.IsMatch(f)));
            }
            catch (IOException) { }
            return list;
        }

        /// <summary>
        /// The 7-Zip inside the exe first (known version, handles RAR5 and multi-part archives), then an installed
        /// 7-Zip, last the tar of Windows 10 and 11 (handles RAR and 7z, but not multi-part RAR).
        /// </summary>
        (string exe, bool sevenZip)? FindTool()
        {
            string bundled = BundledSevenZip();
            if (bundled != null) return (bundled, true);

            foreach (string root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string sevenZip = Path.Combine(root, "7-Zip", "7z.exe");
                if (File.Exists(sevenZip)) return (sevenZip, true);
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string tar in new[] { Path.Combine(windows, "Sysnative", "tar.exe"), Path.Combine(windows, "System32", "tar.exe") })
                if (File.Exists(tar)) return (tar, false);
            return null;
        }

        /// <summary>Unpacks 7z.exe and 7z.dll from the exe into the staging folder, which is deleted at the end anyway.</summary>
        string BundledSevenZip()
        {
            string dir = Path.Combine(stagingDir, "7-Zip"), exe = Path.Combine(dir, "7z.exe");
            if (File.Exists(exe)) return exe;
            try
            {
                EnsureStagingRoot();
                Directory.CreateDirectory(dir);
                foreach (string name in new[] { "7z.dll", "7z.exe" })
                    using (Stream src = typeof(GameImporter).Assembly.GetManifestResourceStream(name))
                    {
                        if (src == null) throw new FileNotFoundException("Resource missing: " + name);
                        using (var dst = File.Create(Path.Combine(dir, name))) src.CopyTo(dst);
                    }
                return exe;
            }
            catch (Exception ex)
            {
                Log.Write("  Bundled 7-Zip not usable: " + ex.Message);
                TryDeleteDirectory(dir);
                return null;
            }
        }

        void EnsureStagingRoot()
        {
            if (!Directory.Exists(stagingDir))
                Directory.CreateDirectory(stagingDir).Attributes |= FileAttributes.Hidden;
        }

        string NewStagingFolder()
        {
            EnsureStagingRoot();
            string dir = Path.Combine(stagingDir, (++stagingCounter).ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            return dir;
        }

        void CopyToFile(Stream source, string target, ref long done, long total, string label, CancellationToken ct)
        {
            using (source)
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, n);
                    done += n;
                    if (Throttled())
                        ui.Progress(label, total > 0 ? (double)done / total : (double?)null, EdenSetup.Of(done, total));
                }
            }
            ui.Progress(label, total > 0 ? (double)done / total : (double?)null, EdenSetup.Of(done, total));
        }

        bool Throttled()
        {
            if (uiThrottle.ElapsedMilliseconds < 100) return false;
            uiThrottle.Restart();
            return true;
        }

        static void EnsureSpace(string path, long bytes)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            long free;
            try { free = new DriveInfo(root).AvailableFreeSpace; }
            catch { return; }
            long need = bytes + 200L * 1024 * 1024;
            if (free < need) throw new InstallException(EdenSetup.NotEnoughSpace(root, need, free));
        }

        static long DirectorySize(string dir)
        {
            long sum = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        // Through an own handle, the size in the directory lags behind for files being written
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            sum += fs.Length;
                    }
                    catch (IOException)
                    {
                        try { sum += new FileInfo(f).Length; } catch { }
                    }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return sum;
        }

        static string CleanName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        static bool SameDrive(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        static void TryDeleteDirectory(string dir)
        {
            try { EdenSetup.DeleteDirectory(dir); }
            catch (Exception ex) { Log.Write("  Folder stays: " + dir + " (" + ex.Message + ")"); }
        }

        sealed class Tally
        {
            public int Games, Updates, Dlcs, Already, Skipped;
            public int Found => Games + Updates + Dlcs + Already + Skipped;

            public void Add(GameKind kind)
            {
                if (kind == GameKind.Base) Games++;
                else if (kind == GameKind.Update) Updates++;
                else Dlcs++;
            }
        }
    }

    /// <summary>One row in the list below "Add game".</summary>
    public sealed class ImportItem : INotifyPropertyChanged
    {
        ImportTone tone = ImportTone.Busy;
        string badge = "…";
        string detail = L.T("Wird geprüft", "Checking");

        public ImportItem(string name) => Name = name;

        public string Name { get; }
        public ImportTone Tone => tone;
        public string Badge => badge;

        public string Detail
        {
            get => detail;
            set { detail = value; Changed(nameof(Detail)); }
        }

        public void Set(ImportTone tone, string badge, string detail)
        {
            this.tone = tone;
            this.badge = badge;
            this.detail = detail;
            Changed(nameof(Tone));
            Changed(nameof(Badge));
            Changed(nameof(Detail));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

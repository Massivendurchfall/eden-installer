using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EdenInstaller
{
    /// <summary>
    /// Kleiner Bearbeiter für Edens config\qt-config.ini. Die Ordnerlisten stehen dort im Abschnitt [UI]
    /// als Arrays, so wie Edens QtConfig sie schreibt:
    ///   Paths\gamedirs\1\path=...              (Spieleverzeichnisse)
    ///   Paths\external_content_dirs\1\path=... (Externer Inhalt: Updates und DLCs ohne NAND-Installation)
    /// plus jeweils ...\size. Alles andere in der Datei bleibt Zeile für Zeile unverändert.
    /// </summary>
    sealed class EdenConfigFile
    {
        const string Section = "[UI]";
        const string GameDirs = @"Paths\gamedirs\";
        const string ExternalDirs = @"Paths\external_content_dirs\";

        readonly string file;
        readonly string newline;
        readonly string separator;
        readonly List<string> lines;
        readonly int start;

        public EdenConfigFile(string file)
        {
            this.file = file;
            string text = File.Exists(file) ? File.ReadAllText(file, new UTF8Encoding(false)) : "";
            newline = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            separator = lines.Any(l => l.Contains(" = ")) ? " = " : "=";

            start = lines.FindIndex(l => l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add(Section);
                start = lines.Count - 1;
            }
        }

        /// <summary>Spieleverzeichnis mit "Unterordner durchsuchen". False, wenn es schon drinsteht.</summary>
        public bool AddGameDir(string dir)
        {
            string value = IniPath(dir);
            int size = Size(GameDirs);
            if (Contains(GameDirs, size, value)) return false;

            var dirs = new List<(string path, bool deepScan)>();
            // Bei leerer Liste legt Eden SD-Karte und NAND selbst an, sonst nicht. Also mit eintragen.
            if (size == 0)
                foreach (string special in new[] { "SDMC", "UserNAND", "SysNAND" }) dirs.Add((special, false));
            dirs.Add((value, true));

            var added = new List<string>();
            foreach (var (path, deepScan) in dirs)
            {
                int i = ++size;
                added.Add(Line($@"{GameDirs}{i}\path", path));
                added.Add(Line($@"{GameDirs}{i}\deep_scan\default", deepScan ? "false" : "true"));
                added.Add(Line($@"{GameDirs}{i}\deep_scan", deepScan ? "true" : "false"));
                added.Add(Line($@"{GameDirs}{i}\expanded\default", "true"));
                added.Add(Line($@"{GameDirs}{i}\expanded", "true"));
            }
            AppendToArray(GameDirs, added, size);
            return true;
        }

        /// <summary>Ordner für Updates und DLCs ("Externer Inhalt"). False, wenn er schon drinsteht.</summary>
        public bool AddExternalContentDir(string dir)
        {
            string value = IniPath(dir);
            int size = Size(ExternalDirs);
            if (Contains(ExternalDirs, size, value)) return false;
            size++;
            AppendToArray(ExternalDirs, new List<string> { Line($@"{ExternalDirs}{size}\path", value) }, size);
            return true;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, string.Join(newline, lines) + newline, new UTF8Encoding(false));
        }

        int SectionEnd()
        {
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
            return end;
        }

        string Read(string key)
        {
            int end = SectionEnd();
            for (int i = start + 1; i < end; i++)
                if (KeyOf(lines[i]) == key) return lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
            return null;
        }

        int Size(string array)
        {
            int.TryParse(Read(array + "size"), out int n);
            return Math.Max(0, n);
        }

        bool Contains(string array, int size, string value)
        {
            for (int i = 1; i <= size; i++)
                if (SamePath(Read($@"{array}{i}\path"), value)) return true;
            return false;
        }

        /// <summary>Neue Einträge und die neue Anzahl ans Ende des Abschnitts, die alte size-Zeile fliegt raus.</summary>
        void AppendToArray(string array, List<string> added, int size)
        {
            int end = SectionEnd();
            for (int i = end - 1; i > start; i--)
                if (KeyOf(lines[i]) == array + "size") { lines.RemoveAt(i); end--; }
            added.Add(Line(array + "size", size.ToString()));
            int insert = end;
            while (insert > start + 1 && lines[insert - 1].Trim().Length == 0) insert--;
            lines.InsertRange(insert, added);
        }

        string Line(string key, string value) => key + separator + value;

        static string KeyOf(string line)
        {
            int eq = line.IndexOf('=');
            return eq > 0 ? line.Substring(0, eq).Trim() : null;
        }

        /// <summary>Wie Edens AdjustOutputString: Schrägstriche nach vorn, bei Sonderzeichen in Anführungszeichen.</summary>
        static string IniPath(string path)
        {
            string s = path.Replace('\\', '/');
            while (s.Contains("//")) s = s.Replace("//", "/");
            return s.IndexOfAny("!#$%^&*|;'\",<>?`~=".ToCharArray()) >= 0 ? "\"" + s + "\"" : s;
        }

        static bool SamePath(string a, string b) => a != null && NormPath(a) == NormPath(b);
        static string NormPath(string s) => s.Replace("\"", "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }
}

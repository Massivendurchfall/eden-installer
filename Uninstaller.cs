using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace EdenInstaller
{
    public sealed class UninstallChoice
    {
        /// <summary>Keep the Games folder and the updates and DLCs folder.</summary>
        public bool KeepGames;
        /// <summary>Keep Eden's data folder: saves, settings, mods, keys and firmware.</summary>
        public bool KeepData;
    }

    /// <summary>
    /// Removes what the installer set up: the Eden program folder, the desktop shortcut and the state file, plus games
    /// and Eden's data folder if the user does not keep them. Guards make sure no unrelated folder is ever touched.
    /// </summary>
    public static class Uninstaller
    {
        /// <summary>Why the uninstall must not run, or null if everything looks like our own install.</summary>
        public static string Refuse(SetupPaths p)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(p.InstallDir));
            if (string.Equals(Path.GetFullPath(p.InstallDir).TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(p.EdenExe))
                return L.T("Im Eden-Ordner liegt keine eden.exe, deshalb lösche ich dort nichts.",
                    "There is no eden.exe in the Eden folder, so nothing is deleted there.");
            return null;
        }

        /// <summary>Eden's data folder is only deleted when it is named like one: "eden", or "user" in portable mode.</summary>
        static bool LooksLikeEdenData(string dir)
        {
            string name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('\\'));
            string parent = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\'));
            return parent != null && (name.Equals("eden", StringComparison.OrdinalIgnoreCase) || name.Equals("user", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True if the desktop shortcut points to this Eden. Needs the UI thread (COM).</summary>
        public static bool ShortcutIsOurs(SetupPaths p)
        {
            if (!File.Exists(p.ShortcutPath)) return false;
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                try
                {
                    dynamic link = shell.CreateShortcut(p.ShortcutPath);
                    string target = link.TargetPath;
                    Marshal.FinalReleaseComObject(link);
                    return SamePath(target, p.EdenExe);
                }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception ex)
            {
                Log.Write("  Shortcut not readable, it stays: " + ex.Message);
                return false;
            }
        }

        /// <returns>Paths that could not be removed.</returns>
        public static List<string> Run(SetupPaths p, string stateFile, UninstallChoice choice, bool removeShortcut)
        {
            Log.Write($"Uninstall from {p.InstallDir}: keep games {choice.KeepGames}, keep data {choice.KeepData} ({p.DataDir})");
            var failed = new List<string>();

            var keep = new List<string>();
            if (choice.KeepGames)
            {
                keep.Add(p.GamesDir);
                keep.Add(p.DlcDir);
            }
            if (choice.KeepData) keep.Add(p.DataDir);   // matters in portable mode, where it sits inside the Eden folder

            // Program folder: everything except what stays
            foreach (string entry in Directory.EnumerateFileSystemEntries(p.InstallDir).ToList())
                if (!keep.Any(k => SamePath(k, entry))) Delete(entry, failed);
            if (!Directory.EnumerateFileSystemEntries(p.InstallDir).Any()) Delete(p.InstallDir, failed);

            if (!choice.KeepData)
            {
                if (LooksLikeEdenData(p.DataDir)) Delete(p.DataDir, failed);
                else Log.Write("  Data folder does not look like Eden's, it stays: " + p.DataDir);
            }

            if (removeShortcut) Delete(p.ShortcutPath, failed);

            // The state file goes, so the next start offers a fresh installation
            Delete(stateFile, failed);
            string stateDir = Path.GetDirectoryName(stateFile);
            if (Directory.Exists(stateDir) && !Directory.EnumerateFileSystemEntries(stateDir).Any()) Delete(stateDir, failed);

            Log.Write(failed.Count == 0 ? "Uninstall finished" : $"Uninstall finished, {failed.Count} items stay");
            return failed;
        }

        public static long Size(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                    : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        static void Delete(string path, List<string> failed)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // Read-only files would block Directory.Delete
                    foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, FileAttributes.Normal);
                    EdenSetup.DeleteDirectory(path);
                }
                else if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    EdenSetup.DeleteFile(path);
                }
                else return;
                Log.Write("  removed " + path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("  could not remove " + path + ": " + ex.Message);
                failed.Add(path);
            }
        }

        static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}

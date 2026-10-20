using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace EdenInstaller
{
    /// <summary>A newer installer release on GitHub.</summary>
    public sealed class InstallerRelease
    {
        public Version Version;
        public string ExeUrl;
        public long Size;
        /// <summary>Lowercase hex, or null if GitHub gave no digest.</summary>
        public string Sha256;
    }

    /// <summary>
    /// The installer updates itself from the GitHub releases of this repository. The new exe is downloaded next to the
    /// running one, the running exe is renamed to .old (Windows allows renaming a running exe, not deleting it), the new
    /// one takes its name and is started. It then deletes the .old file once this process is gone.
    /// </summary>
    public static class SelfUpdate
    {
        const string LatestApi = "https://api.github.com/repos/Massivendurchfall/eden-installer/releases/latest";
        static readonly Regex VersionTag = new Regex(@"(\d+)\.(\d+)\.(\d+)");

        public static Version Current
        {
            get
            {
                Version v = typeof(SelfUpdate).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        static string ExePath => Assembly.GetEntryAssembly().Location;

        /// <summary>The newer release, or null if there is none or GitHub can't be reached. Never throws.</summary>
        public static async Task<InstallerRelease> FindNewerAsync()
        {
            try
            {
                string json;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, LatestApi))
                {
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using (var resp = await EdenSetup.Http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} at {LatestApi}");
                        json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }

                var release = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                var m = VersionTag.Match((string)release["tag_name"] ?? "");
                if (!m.Success) return null;
                var latest = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                Log.Write($"Installer v{Current}, newest on GitHub v{latest}");
                if (latest <= Current) return null;

                var assets = ((object[])release["assets"]).Cast<Dictionary<string, object>>().ToList();
                var exe = assets.FirstOrDefault(a => string.Equals((string)a["name"], "Eden-Installer.exe", StringComparison.OrdinalIgnoreCase))
                    ?? assets.FirstOrDefault(a => ((string)a["name"]).EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (exe == null) return null;

                string digest = exe.TryGetValue("digest", out object d) ? d as string : null;
                return new InstallerRelease
                {
                    Version = latest,
                    ExeUrl = (string)exe["browser_download_url"],
                    Size = Convert.ToInt64(exe["size"]),
                    Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest.Substring(7).ToLowerInvariant()
                        : null,
                };
            }
            catch (Exception ex)
            {
                Log.Write("Installer update check failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Downloads the new exe, swaps it in and starts it. The caller shuts the app down afterwards.
        /// </summary>
        /// <param name="args">Arguments for the new instance, besides the cleanup of the old exe.</param>
        public static async Task ApplyAsync(InstallerRelease release, string args, Action<string> progress)
        {
            string exe = ExePath, fresh = exe + ".new", old = exe + ".old";
            Log.Write($"Installer update to v{release.Version}: {release.ExeUrl}");
            try
            {
                using (var resp = await EdenSetup.Http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new InstallException(L.T($"Download fehlgeschlagen ({(int)resp.StatusCode}). Bitte später noch einmal versuchen.",
                            $"Download failed ({(int)resp.StatusCode}). Please try again later."));
                    long total = resp.Content.Headers.ContentLength ?? release.Size, done = 0;
                    string hash;
                    var throttle = Stopwatch.StartNew();
                    using (var sha = SHA256.Create())
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[1 << 16];
                        int n;
                        while ((n = await src.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            dst.Write(buffer, 0, n);
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            done += n;
                            if (throttle.ElapsedMilliseconds > 100)
                            {
                                throttle.Restart();
                                progress(L.T("Lade ", "Downloading ") + EdenSetup.Of(done, total));
                            }
                        }
                        sha.TransformFinalBlock(buffer, 0, 0);
                        hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    }
                    Check(release, fresh, done, hash);   // only after the file is closed
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Write("  No write access: " + ex.Message);
                EdenSetup.DeleteFile(fresh);
                throw new InstallException(L.T(
                    "Kein Schreibzugriff auf den Ordner, in dem der Installer liegt. Lade die neue Version bitte selbst von GitHub.",
                    "No write access to the folder the installer is in. Please download the new version from GitHub yourself."));
            }
            catch
            {
                EdenSetup.DeleteFile(fresh);
                throw;
            }

            progress(L.T("Starte neu…", "Restarting…"));
            EdenSetup.DeleteFile(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch
            {
                File.Move(old, exe);   // put everything back
                EdenSetup.DeleteFile(fresh);
                throw;
            }
            try { File.SetAttributes(old, File.GetAttributes(old) | FileAttributes.Hidden); } catch { }

            Log.Write("  Swapped, starting the new exe");
            Process.Start(new ProcessStartInfo(exe, $"--updated-from \"{old}\" {args}".Trim())
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
        }

        static void Check(InstallerRelease release, string file, long size, string sha256)
        {
            bool sizeOk = release.Size <= 0 || size == release.Size;
            bool hashOk = release.Sha256 == null || release.Sha256 == sha256;
            bool isExe;
            using (var fs = File.OpenRead(file)) isExe = fs.Length > 2 && fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
            Log.Write($"  {size} bytes, sha256 {sha256}, size {(sizeOk ? "ok" : "wrong")}, hash {(release.Sha256 == null ? "not given" : hashOk ? "ok" : "wrong")}");
            if (!sizeOk || !hashOk || !isExe)
                throw new InstallException(L.T("Die heruntergeladene Datei ist beschädigt. Bitte später noch einmal versuchen.",
                    "The downloaded file is damaged. Please try again later."));
        }

        /// <summary>
        /// Deletes the .old and .new leftovers next to this exe once the previous instance is gone. Only these two
        /// names are touched, whatever --updated-from says.
        /// </summary>
        public static void CleanUp()
        {
            string exe = ExePath;
            Task.Run(async () =>
            {
                foreach (string leftover in new[] { exe + ".old", exe + ".new" })
                {
                    for (int i = 0; i < 40 && File.Exists(leftover); i++)
                    {
                        try
                        {
                            File.SetAttributes(leftover, FileAttributes.Normal);
                            File.Delete(leftover);
                            Log.Write("Removed " + leftover);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            await Task.Delay(500).ConfigureAwait(false);   // the old instance is still closing
                        }
                    }
                }
            });
        }
    }
}

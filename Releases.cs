using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace EdenInstaller
{
    /// <summary>Was installiert wird: Eden sowie Firmware und Keys, die zueinander passen.</summary>
    public sealed class ReleaseSet
    {
        public string EdenVersion, EdenUrl;
        public string FirmwareVersion, FirmwareUrl;
        public string KeysVersion, KeysUrl;
        /// <summary>Gibt es schon eine neuere Firmware ohne passende Keys, steht ihre Version hier.</summary>
        public string NewerFirmwareWithoutKeys;
        /// <summary>Die Suche im Netz ging nicht, es gelten (teils) die eingebauten Versionen.</summary>
        public bool UsedBuiltIn;

        /// <summary>Bekannt gutes Paar, falls die Suche nichts findet.</summary>
        public static ReleaseSet BuiltIn() => new ReleaseSet
        {
            EdenVersion = "0.2.1",
            EdenUrl = "https://stable.eden-emu.dev/v0.2.1/Eden-Windows-v0.2.1-amd64-msvc-standard.zip",
            FirmwareVersion = "22.5.0",
            FirmwareUrl = "https://github.com/THZoria/NX_Firmware/releases/download/22.5.0/Firmware.22.5.0.zip",
            KeysVersion = "22.5.0",
            KeysUrl = "https://files.prodkeys.net/ProdKeys.NET-v22.5.0.zip",
            UsedBuiltIn = true,
        };
    }

    /// <summary>
    /// Sucht beim Start die neuesten Versionen. Eden kommt aus den Releases auf git.eden-emu.dev, die Firmware aus
    /// THZoria/NX_Firmware auf GitHub, die Keys von prodkeys.net. Firmware und Keys müssen immer genau dieselbe
    /// Version haben, sonst gibt es Probleme. Deshalb gilt die neueste Firmware, für die es Keys derselben Version gibt.
    /// </summary>
    public static class ReleaseFinder
    {
        const string EdenApi = "https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases/latest";
        const string FirmwareApi = "https://api.github.com/repos/THZoria/NX_Firmware/releases?per_page=20";
        const string KeysSite = "https://prodkeys.net/";
        const string KeysFiles = "https://files.prodkeys.net/";

        // Die Keys heißen mal "ProdKeys.NET-v22.5.0.zip", mal "ProdKeys.Net-21.2.0.zip" oder "Prodkeys.NET_v21-0-0.zip"
        static readonly Regex VersionInName = new Regex(@"(?<!\d)(\d{1,2})[.\-_](\d{1,2})[.\-_](\d{1,2})(?!\d)");
        static readonly Regex KeysZipLink = new Regex(@"https://files\.prodkeys\.net/[^""'\s<>]*?keys[^""'\s<>]*?\.zip", RegexOptions.IgnoreCase);
        static readonly Regex KeysPageLink = new Regex(@"https://prodkeys\.net/[a-z0-9\-]*prod-keys[a-z0-9\-]*/", RegexOptions.IgnoreCase);
        static readonly Regex EdenWindowsZip = new Regex(@"^Eden-Windows-.+-amd64-msvc-standard\.zip$", RegexOptions.IgnoreCase);
        static readonly Regex EdenAnyAmd64Zip = new Regex(@"^Eden-Windows-.+-amd64-.+\.zip$", RegexOptions.IgnoreCase);

        /// <summary>Wirft nur beim Abbrechen. Was nicht zu finden ist, kommt aus den eingebauten Versionen.</summary>
        public static async Task<ReleaseSet> FindAsync(CancellationToken ct)
        {
            var builtIn = ReleaseSet.BuiltIn();
            var set = new ReleaseSet();
            Log.Write("Suche die neuesten Versionen");

            try
            {
                (set.EdenVersion, set.EdenUrl) = await FindEdenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Write("  Eden nicht gefunden, nehme v" + builtIn.EdenVersion + ": " + ex.Message);
                set.EdenVersion = builtIn.EdenVersion;
                set.EdenUrl = builtIn.EdenUrl;
                set.UsedBuiltIn = true;
            }

            try
            {
                await FindFirmwareAndKeysAsync(set, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Write("  Firmware und Keys nicht gefunden, nehme " + builtIn.FirmwareVersion + ": " + ex.Message);
                set.FirmwareVersion = builtIn.FirmwareVersion;
                set.FirmwareUrl = builtIn.FirmwareUrl;
                set.KeysVersion = builtIn.KeysVersion;
                set.KeysUrl = builtIn.KeysUrl;
                set.UsedBuiltIn = true;
            }

            Log.Write($"  Eden v{set.EdenVersion}: {set.EdenUrl}");
            Log.Write($"  Firmware {set.FirmwareVersion}: {set.FirmwareUrl}");
            Log.Write($"  Keys {set.KeysVersion}: {set.KeysUrl}");
            if (set.NewerFirmwareWithoutKeys != null)
                Log.Write($"  Firmware {set.NewerFirmwareWithoutKeys} gibt es schon, Keys dafür noch nicht");
            return set;
        }

        static async Task<(string version, string url)> FindEdenAsync(CancellationToken ct)
        {
            var release = (Dictionary<string, object>)Json(await GetTextAsync(EdenApi, ct).ConfigureAwait(false));
            string tag = (string)release["tag_name"];
            var assets = Assets(release);
            var zip = assets.FirstOrDefault(a => EdenWindowsZip.IsMatch(a.name));
            if (zip.url == null)
                zip = assets.FirstOrDefault(a => EdenAnyAmd64Zip.IsMatch(a.name) && a.name.IndexOf("rog-ally", StringComparison.OrdinalIgnoreCase) < 0);
            if (zip.url == null) throw new InvalidDataException($"Release {tag} hat kein Windows-Paket");
            return (tag.TrimStart('v', 'V'), zip.url);
        }

        static async Task FindFirmwareAndKeysAsync(ReleaseSet set, CancellationToken ct)
        {
            var firmware = new List<(Version version, string url)>();
            foreach (Dictionary<string, object> release in (object[])Json(await GetTextAsync(FirmwareApi, ct).ConfigureAwait(false)))
            {
                if (true.Equals(release["draft"]) || true.Equals(release["prerelease"])) continue;
                Version version = ParseVersion((string)release["tag_name"]);
                var zip = Assets(release).FirstOrDefault(a => a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (version != null && zip.url != null) firmware.Add((version, zip.url));
            }
            if (firmware.Count == 0) throw new InvalidDataException("keine Firmware-Releases");
            firmware = firmware.OrderByDescending(f => f.version).ToList();

            var keys = await FindKeysAsync(firmware.Select(f => f.version).ToList(), ct).ConfigureAwait(false);
            if (keys.Count == 0) throw new InvalidDataException("keine Keys gefunden");
            Log.Write("  Firmware online: " + string.Join(", ", firmware.Take(6).Select(f => f.version)));
            Log.Write("  Keys online: " + string.Join(", ", keys.Keys.OrderByDescending(v => v).Take(6)));

            // Nur genau gleiche Versionen, sonst gibt es Probleme. Gibt es kein solches Paar, gilt das eingebaute.
            var pick = firmware.FirstOrDefault(f => keys.ContainsKey(f.version));
            if (pick.url == null) throw new InvalidDataException("keine Firmware mit genau gleichen Keys");

            set.FirmwareVersion = pick.version.ToString();
            set.FirmwareUrl = pick.url;
            set.KeysVersion = pick.version.ToString();
            set.KeysUrl = keys[pick.version];
            if (firmware[0].version > pick.version) set.NewerFirmwareWithoutKeys = firmware[0].version.ToString();
        }

        /// <summary>Version zu Download-Link. Erst die Links auf prodkeys.net, dann für neuere Firmware die üblichen Namen direkt.</summary>
        static async Task<Dictionary<Version, string>> FindKeysAsync(List<Version> firmware, CancellationToken ct)
        {
            var found = new Dictionary<Version, string>();
            void Collect(string html)
            {
                foreach (Match m in KeysZipLink.Matches(html))
                {
                    Version v = ParseVersion(Uri.UnescapeDataString(m.Value.Substring(KeysFiles.Length)));
                    if (v != null && !found.ContainsKey(v)) found[v] = m.Value;
                }
            }

            try
            {
                string home = await GetTextAsync(KeysSite, ct).ConfigureAwait(false);
                Collect(home);
                var pages = KeysPageLink.Matches(home).Cast<Match>().Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p.IndexOf("eden", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).Take(4);
                foreach (string page in pages)
                {
                    try { Collect(await GetTextAsync(page, ct).ConfigureAwait(false)); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("  " + page + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("  prodkeys.net nicht lesbar: " + ex.Message); }

            Version newest = found.Count > 0 ? found.Keys.Max() : new Version(0, 0, 0);
            foreach (Version v in firmware.Where(v => v > newest).Take(4))
                foreach (string name in new[] { $"ProdKeys.NET-v{v}.zip", $"ProdKeys.NET-{v}.zip" })
                    if (await ExistsAsync(KeysFiles + name, ct).ConfigureAwait(false))
                    {
                        found[v] = KeysFiles + name;
                        break;
                    }
            return found;
        }

        static Version ParseVersion(string text)
        {
            var m = VersionInName.Match(text ?? "");
            return m.Success
                ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
                : null;
        }

        /// <summary>True, wenn latest eine neuere Version ist. Bei unlesbaren Versionen lieber false.</summary>
        public static bool IsNewer(string latest, string installed)
        {
            Version a = ParseVersion(latest), b = ParseVersion(installed);
            return a != null && b != null && a > b;
        }

        static List<(string name, string url)> Assets(Dictionary<string, object> release) =>
            ((object[])release["assets"]).Cast<Dictionary<string, object>>()
                .Select(a => ((string)a["name"], (string)a["browser_download_url"])).ToList();

        static object Json(string text) => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);

        static async Task<string> GetTextAsync(string url, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using (var resp = await EdenSetup.Http.GetAsync(url, timeout.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} bei {url}");
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        static async Task<bool> ExistsAsync(string url, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Head, url))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    using (var resp = await EdenSetup.Http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                        return resp.IsSuccessStatusCode;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException || ex is OperationCanceledException))
                {
                    return false;
                }
            }
        }
    }
}

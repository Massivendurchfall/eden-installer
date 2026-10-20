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
    /// <summary>What gets installed: Eden plus firmware and keys that match each other.</summary>
    public sealed class ReleaseSet
    {
        public string EdenVersion, EdenUrl;
        public string FirmwareVersion, FirmwareUrl;
        public string KeysVersion, KeysUrl;
        /// <summary>If there is already a newer firmware without matching keys, its version is here.</summary>
        public string NewerFirmwareWithoutKeys;
        /// <summary>The online search failed, the built-in versions apply (partly).</summary>
        public bool UsedBuiltIn;

        /// <summary>Known good set in case the search finds nothing.</summary>
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
    /// Looks up the latest versions at startup. Eden comes from the releases on git.eden-emu.dev, the firmware from
    /// THZoria/NX_Firmware on GitHub, the keys from prodkeys.net. Firmware and keys must always have exactly the same
    /// version, anything else causes trouble. So the newest firmware wins for which keys of the same version exist.
    /// </summary>
    public static class ReleaseFinder
    {
        const string EdenApi = "https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases/latest";
        const string FirmwareApi = "https://api.github.com/repos/THZoria/NX_Firmware/releases?per_page=20";
        const string KeysSite = "https://prodkeys.net/";
        // This page lists all versions with direct links to files.prodkeys.net, the others are only a fallback
        const string KeysListPage = "https://prodkeys.net/yuzu-prod-keys-update-6/";
        const string KeysFiles = "https://files.prodkeys.net/";

        // The keys are named "ProdKeys.NET-v22.5.0.zip", "ProdKeys.Net-21.2.0.zip" or "Prodkeys.NET_v21-0-0.zip"
        static readonly Regex VersionInName = new Regex(@"(?<!\d)(\d{1,2})[.\-_](\d{1,2})[.\-_](\d{1,2})(?!\d)");
        static readonly Regex KeysZipLink = new Regex(@"https://files\.prodkeys\.net/[^""'\s<>]*?keys[^""'\s<>]*?\.zip", RegexOptions.IgnoreCase);
        static readonly Regex KeysPageLink = new Regex(@"https://prodkeys\.net/[a-z0-9\-]*prod-keys[a-z0-9\-]*/", RegexOptions.IgnoreCase);
        static readonly Regex EdenWindowsZip = new Regex(@"^Eden-Windows-.+-amd64-msvc-standard\.zip$", RegexOptions.IgnoreCase);
        static readonly Regex EdenAnyAmd64Zip = new Regex(@"^Eden-Windows-.+-amd64-.+\.zip$", RegexOptions.IgnoreCase);

        /// <summary>Only throws on cancel. Whatever can't be found comes from the built-in versions.</summary>
        public static async Task<ReleaseSet> FindAsync(CancellationToken ct)
        {
            var builtIn = ReleaseSet.BuiltIn();
            var set = new ReleaseSet();
            Log.Write("Looking for the latest versions");

            try
            {
                (set.EdenVersion, set.EdenUrl) = await FindEdenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Write("  Eden not found, using v" + builtIn.EdenVersion + ": " + ex.Message);
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
                Log.Write("  Firmware and keys not found, using " + builtIn.FirmwareVersion + ": " + ex.Message);
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
                Log.Write($"  Firmware {set.NewerFirmwareWithoutKeys} is already out, keys for it are not yet");
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
            if (zip.url == null) throw new InvalidDataException($"Release {tag} has no Windows package");
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
            if (firmware.Count == 0) throw new InvalidDataException("no firmware releases");
            firmware = firmware.OrderByDescending(f => f.version).ToList();

            var keys = await FindKeysAsync(firmware.Select(f => f.version).ToList(), ct).ConfigureAwait(false);
            if (keys.Count == 0) throw new InvalidDataException("no keys found");
            Log.Write("  Firmware online: " + string.Join(", ", firmware.Take(6).Select(f => f.version)));
            Log.Write("  Keys online: " + string.Join(", ", keys.Keys.OrderByDescending(v => v).Take(6)));

            // Only exactly equal versions, anything else causes trouble. Without such a pair the built-in one applies.
            var pick = firmware.FirstOrDefault(f => keys.ContainsKey(f.version));
            if (pick.url == null) throw new InvalidDataException("no firmware with exactly matching keys");

            set.FirmwareVersion = pick.version.ToString();
            set.FirmwareUrl = pick.url;
            set.KeysVersion = pick.version.ToString();
            set.KeysUrl = keys[pick.version];
            if (firmware[0].version > pick.version) set.NewerFirmwareWithoutKeys = firmware[0].version.ToString();
        }

        /// <summary>
        /// Version to download link. First the version list on prodkeys.net, if that fails the pages linked from the
        /// home page. Then, for newer firmware, the usual file names directly.
        /// </summary>
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

            try { Collect(await GetTextAsync(KeysListPage, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("  " + KeysListPage + ": " + ex.Message); }

            if (found.Count == 0)
            {
                try
                {
                    string home = await GetTextAsync(KeysSite, ct).ConfigureAwait(false);
                    Collect(home);
                    var pages = KeysPageLink.Matches(home).Cast<Match>().Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(p => !p.Equals(KeysListPage, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(p => p.IndexOf("eden", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).Take(4);
                    foreach (string page in pages)
                    {
                        try { Collect(await GetTextAsync(page, ct).ConfigureAwait(false)); }
                        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("  " + page + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("  prodkeys.net not readable: " + ex.Message); }
            }

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

        /// <summary>True if latest is a newer version. With unreadable versions rather false.</summary>
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
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} at {url}");
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

// Looks for a newer release on GitHub when the app starts, and installs it when asked.
// The check sends GitHub only the request itself (your IP address and the app's name and version).
// The installer is checked against the SHA-256 GitHub publishes for it before it runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

[assembly: AssemblyVersion(PdfReaderApp.AppInfo.Version + ".0")]

namespace PdfReaderApp
{
    static class AppInfo
    {
        // Keep in step with Setup.cs; build.ps1 checks that they match.
        public const string Version = "1.4.0";
    }

    class Release
    {
        public string Version, PageUrl, DownloadUrl, Sha256;
        public long Size;
    }

    static class Updater
    {
        const string Api = "https://api.github.com/repos/fahillion/pdf-reader/releases/latest";
        const string Downloads = "https://github.com/fahillion/pdf-reader/releases/download/";
        const string Pages = "https://github.com/fahillion/pdf-reader/releases/";
        const long MaxSize = 100L * 1024 * 1024;

        // For tests: PDFREADER_TEST_UPDATES points at a local stand-in for GitHub. Only localhost is accepted.
        static readonly string Test = TestBase();
        static readonly HttpClient Http = MakeClient();

        static string TestBase()
        {
            string t = Environment.GetEnvironmentVariable("PDFREADER_TEST_UPDATES");
            return t != null && t.StartsWith("http://127.0.0.1:") ? t.TrimEnd('/') + "/" : null;
        }

        static HttpClient MakeClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromMinutes(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PDF-Reader/" + AppInfo.Version);
            return c;
        }

        public static string Folder { get { return Path.Combine(Path.GetTempPath(), "PdfReader-update"); } }

        // Returns the latest release if it's newer than this copy, or null.
        public static async Task<Release> Check()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Test != null ? Test + "latest" : Api);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (HttpResponseMessage response = await Http.SendAsync(request, cts.Token))
            {
                if (!response.IsSuccessStatusCode) return null;
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                return Parse(json);
            }
        }

        static Release Parse(Dictionary<string, object> json)
        {
            string tag = json.ContainsKey("tag_name") ? json["tag_name"] as string : null;
            if (tag == null || !Regex.IsMatch(tag, @"^v\d{1,4}\.\d{1,4}\.\d{1,4}$")) return null;
            if (json.ContainsKey("draft") && true.Equals(json["draft"])) return null;
            if (json.ContainsKey("prerelease") && true.Equals(json["prerelease"])) return null;
            string version = tag.Substring(1);
            if (!IsNewer(version, AppInfo.Version)) return null;

            var release = new Release { Version = version, PageUrl = Pages + "tag/" + tag };
            string wanted = "PDFReaderSetup-" + version + ".exe";
            string allowed = Test ?? (Downloads + tag + "/");
            var assets = json.ContainsKey("assets") ? json["assets"] as IEnumerable : null;
            if (assets == null) return null;
            foreach (object o in assets)
            {
                var a = o as Dictionary<string, object>;
                if (a == null || !wanted.Equals(a.ContainsKey("name") ? a["name"] as string : null)) continue;
                string url = a.ContainsKey("browser_download_url") ? a["browser_download_url"] as string : null;
                string digest = a.ContainsKey("digest") ? a["digest"] as string : null;
                // Only the expected file, from this repo's own release, with a published hash.
                if (url == null || url != allowed + wanted) return null;
                if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$")) return null;
                release.DownloadUrl = url;
                release.Sha256 = digest.Substring(7);
                release.Size = a.ContainsKey("size") ? Convert.ToInt64(a["size"]) : 0;
                return release.Size > 0 && release.Size <= MaxSize ? release : null;
            }
            return null;
        }

        public static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            return System.Version.TryParse(candidate, out a) && System.Version.TryParse(current, out b) && a > b;
        }

        // Downloads the installer and checks its hash. Returns its path. Reports progress from 0 to 1.
        public static async Task<string> Download(Release release, Action<double> progress)
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "PDFReaderSetup-" + release.Version + ".exe");
            string partial = path + ".part";
            using (HttpResponseMessage response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (Stream input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxSize) throw new IOException("The download is larger than expected.");
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        await output.WriteAsync(buffer, 0, n);
                        progress(Math.Min(1.0, (double)total / release.Size));
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (hash != release.Sha256) throw new IOException("The download didn't match the published checksum.");
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(partial, path);
            return path;
        }

        // Clears installers left from an earlier update.
        public static void CleanUp()
        {
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
            catch (Exception) { }
        }
    }

    partial class MainWindow
    {
        FrameworkElement updateBar;
        TextBlock updateTitle, updateBody, updateToggleText;
        Button updateNow, updateLater, updateNotes;
        Release pendingUpdate;
        bool updating;

        bool UpdatesOn { get { return store.Get("updates", "on") != "off"; } }

        void InitUpdates()
        {
            updateBar = Find<FrameworkElement>("UpdateBar");
            updateTitle = Find<TextBlock>("UpdateTitle");
            updateBody = Find<TextBlock>("UpdateBody");
            updateNow = Find<Button>("UpdateNowButton");
            updateLater = Find<Button>("UpdateLaterButton");
            updateNotes = Find<Button>("UpdateNotesButton");
            updateToggleText = Find<TextBlock>("UpdateToggleText");
            Find<TextBlock>("VersionText").Text = "PDF Reader " + AppInfo.Version;
            Find<Button>("UpdateToggleButton").Click += delegate
            {
                store.Set("updates", UpdatesOn ? "off" : "on");
                store.Save();
                ShowUpdateSetting();
                if (UpdatesOn) CheckForUpdate(true);
                else updateBar.Visibility = Visibility.Collapsed;
            };
            updateNow.Click += delegate { InstallUpdate(); };
            updateLater.Click += delegate
            {
                if (pendingUpdate != null) { store.Set("update_skip", pendingUpdate.Version); store.Save(); }
                updateBar.Visibility = Visibility.Collapsed;
            };
            updateNotes.Click += delegate { if (pendingUpdate != null) OpenLink(pendingUpdate.PageUrl); };
            ShowUpdateSetting();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += delegate { timer.Stop(); Updater.CleanUp(); CheckForUpdate(false); };
            timer.Start();
        }

        void ShowUpdateSetting()
        {
            updateToggleText.Text = UpdatesOn ? "Check for updates at start: on" : "Check for updates at start: off";
        }

        // At most once a day, unless an update was already found (then every start, until it's installed or skipped).
        async void CheckForUpdate(bool now)
        {
            if (!UpdatesOn || updating) return;
            DateTime last;
            bool recent = DateTime.TryParse(store.Get("update_checked", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)
                && DateTime.UtcNow - last < TimeSpan.FromHours(20) && DateTime.UtcNow >= last;
            string found = store.Get("update_found", "");
            if (!now && recent && !Updater.IsNewer(found, AppInfo.Version)) return;

            Release release;
            try { release = await Updater.Check(); }
            catch (Exception) { return; }   // Offline or GitHub unavailable: say nothing.
            store.Set("update_checked", DateTime.UtcNow.ToString("o"));
            store.Set("update_found", release != null ? release.Version : "");
            store.Save();
            if (release == null || release.Version == store.Get("update_skip", "")) return;

            pendingUpdate = release;
            updateTitle.Text = "Version " + release.Version + " is available";
            updateBody.Text = "PDF Reader will close and reopen to update.";
            updateNow.IsEnabled = true;
            updateLater.Visibility = Visibility.Visible;
            updateNotes.Visibility = Visibility.Visible;
            ((TextBlock)updateNow.Content).Text = "Update";
            updateBar.Visibility = Visibility.Visible;
        }

        async void InstallUpdate()
        {
            Release release = pendingUpdate;
            if (release == null || updating) return;
            updating = true;
            updateNow.IsEnabled = false;
            updateLater.Visibility = Visibility.Collapsed;
            updateTitle.Text = "Downloading version " + release.Version + "...";
            string installer;
            try
            {
                installer = await Updater.Download(release, delegate(double p)
                {
                    Dispatcher.BeginInvoke(new Action(delegate { updateBody.Text = (int)(p * 100) + "%"; }));
                });
            }
            catch (Exception ex)
            {
                updating = false;
                updateTitle.Text = "The update didn't download";
                updateBody.Text = ex is IOException ? ex.Message : "Check your connection and try again.";
                updateNow.IsEnabled = true;
                ((TextBlock)updateNow.Content).Text = "Try again";
                updateLater.Visibility = Visibility.Visible;
                return;
            }

            // The installer waits for this window to close, installs, then opens the app again with this PDF.
            string args = "/update";
            if (doc != null && filePath != null) args += " /open \"" + filePath + "\"";
            try
            {
                Process.Start(new ProcessStartInfo(installer, args) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
            }
            catch (Exception)
            {
                updating = false;
                updateTitle.Text = "The update couldn't start";
                updateBody.Text = "Try again, or download it from the release page.";
                updateNow.IsEnabled = true;
                ((TextBlock)updateNow.Content).Text = "Try again";
                updateLater.Visibility = Visibility.Visible;
                return;
            }
            store.Set("update_found", "");
            store.Save();
            Close();
        }
    }
}

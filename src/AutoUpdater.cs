using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;

namespace SleepPicker
{
    /// <summary>
    /// Anonymous, zero-library auto-updater for release-distributed binaries.
    ///
    /// Periodically queries GitHub's latest release endpoint using an HTTP HEAD request.
    /// Because GitHub responds with an HTTP 302 redirect pointing at the target tag
    /// URL (e.g. /releases/tag/v1.8.0), the latest version can be inspected from the
    /// Location header without making unauthenticated calls to api.github.com, avoiding
    /// rate limiting entirely.
    ///
    /// When an update is detected:
    /// 1. The new binary is downloaded in the background to SleepPicker.exe.download.
    /// 2. Once verified, it is staged as SleepPicker.exe.new.
    /// 3. The tray menu displays "Updating on restart (v1.x -> v1.y)".
    /// 4. When the application exits (either via user exit or system shutdown),
    ///    the running SleepPicker.exe is renamed to SleepPicker.exe.old and
    ///    SleepPicker.exe.new is moved into place.
    /// 5. On the subsequent launch, SleepPicker.exe.old is deleted.
    /// </summary>
    internal static class AutoUpdater
    {
#if ENABLE_AUTO_UPDATE
        private const string LatestReleaseUrl = "https://github.com/VladislavEkimtcov/SleepPicker/releases/latest";
        private const string AssetDownloadUrlFormat = "https://github.com/VladislavEkimtcov/SleepPicker/releases/download/{0}/SleepPicker.exe";

        private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromHours(4);

        private static Timer _checkTimer;
        private static int _isChecking;
        private static int _swapApplied;
        private static string _targetTag;
        private static string _statusText;

        public static event EventHandler StatusChanged;

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string CurrentVersionString
        {
            get { return FormatVersion(CurrentVersion); }
        }

        public static string StatusText
        {
            get
            {
                if (string.IsNullOrEmpty(_statusText))
                {
                    return "Up to date (" + CurrentVersionString + ")";
                }
                return _statusText;
            }
        }

        public static bool IsUpdatePending
        {
            get { return !string.IsNullOrEmpty(_targetTag); }
        }

        public static void Start()
        {
            CleanupOldBinary();
            CheckExistingStagedUpdate();

            _checkTimer = new Timer(OnTimerTick, null, InitialCheckDelay, PeriodicCheckInterval);
        }

        public static void CheckForUpdatesAsync()
        {
            if (IsUpdatePending)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                PerformCheck();
            });
        }

        private static void OnTimerTick(object state)
        {
            CheckForUpdatesAsync();
        }

        private static void PerformCheck()
        {
            if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
            {
                return;
            }

            try
            {
                EnsureTls12();

                string tag = FetchLatestReleaseTag();
                if (string.IsNullOrEmpty(tag))
                {
                    return;
                }

                Version latestVersion = ParseVersion(tag);
                if (latestVersion == null)
                {
                    return;
                }

                if (CompareVersions(latestVersion, CurrentVersion) <= 0)
                {
                    return;
                }

                string exePath = GetExecutablePath();
                string dir = Path.GetDirectoryName(exePath);
                string baseName = Path.GetFileName(exePath);
                string newPath = Path.Combine(dir, baseName + ".new");
                string downloadPath = Path.Combine(dir, baseName + ".download");

                // If already staged and matching the version, nothing more to download.
                if (File.Exists(newPath) && IsValidExecutable(newPath))
                {
                    SetPendingUpdate(tag);
                    return;
                }

                string downloadUrl = string.Format(AssetDownloadUrlFormat, tag);
                if (TryDownloadAsset(downloadUrl, downloadPath))
                {
                    if (File.Exists(newPath))
                    {
                        TryDeleteFile(newPath);
                    }
                    File.Move(downloadPath, newPath);
                    SetPendingUpdate(tag);
                }
            }
            catch (Exception)
            {
                // Network down or permission issue; try again at next interval.
            }
            finally
            {
                Interlocked.Exchange(ref _isChecking, 0);
            }
        }

        private static string FetchLatestReleaseTag()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseUrl);
            request.Method = "HEAD";
            request.AllowAutoRedirect = false;
            request.UserAgent = "SleepPicker";
            request.Timeout = 15000;

            string location = null;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    location = response.Headers["Location"];
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode >= 300 && statusCode < 400)
                    {
                        location = response.Headers["Location"];
                    }
                    response.Close();
                }
            }

            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            string trimmed = location.TrimEnd('/');
            int lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= trimmed.Length - 1)
            {
                return null;
            }

            return trimmed.Substring(lastSlash + 1);
        }

        private static bool TryDownloadAsset(string url, string destinationPath)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    TryDeleteFile(destinationPath);
                }

                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "SleepPicker";
                    client.DownloadFile(url, destinationPath);
                }

                if (!File.Exists(destinationPath) || !IsValidExecutable(destinationPath))
                {
                    TryDeleteFile(destinationPath);
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                TryDeleteFile(destinationPath);
                return false;
            }
        }

        private static bool IsValidExecutable(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 32768)
                {
                    return false;
                }

                using (FileStream fs = File.OpenRead(path))
                {
                    int b1 = fs.ReadByte();
                    int b2 = fs.ReadByte();
                    return b1 == 0x4D && b2 == 0x5A; // 'MZ' PE header
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void SetPendingUpdate(string tag)
        {
            _targetTag = tag;
            _statusText = "Updating on restart (" + CurrentVersionString + " -> " + FormatTag(tag) + ")";
            OnStatusChanged();
        }

        private static void CheckExistingStagedUpdate()
        {
            try
            {
                string exePath = GetExecutablePath();
                string dir = Path.GetDirectoryName(exePath);
                string baseName = Path.GetFileName(exePath);
                string newPath = Path.Combine(dir, baseName + ".new");

                if (!File.Exists(newPath) || !IsValidExecutable(newPath))
                {
                    return;
                }

                FileVersionInfo info = FileVersionInfo.GetVersionInfo(newPath);
                if (info != null && !string.IsNullOrEmpty(info.FileVersion))
                {
                    Version stagedVer;
                    if (Version.TryParse(info.FileVersion, out stagedVer))
                    {
                        if (CompareVersions(stagedVer, CurrentVersion) > 0)
                        {
                            SetPendingUpdate(FormatVersion(stagedVer));
                            return;
                        }
                    }
                }

                // If staged file is not newer than current, remove it.
                TryDeleteFile(newPath);
            }
            catch (Exception)
            {
            }
        }

        public static void ApplyPendingUpdate()
        {
            if (Interlocked.CompareExchange(ref _swapApplied, 1, 0) != 0)
            {
                return;
            }

            try
            {
                string exePath = GetExecutablePath();
                string dir = Path.GetDirectoryName(exePath);
                string baseName = Path.GetFileName(exePath);
                string newPath = Path.Combine(dir, baseName + ".new");
                string oldPath = Path.Combine(dir, baseName + ".old");

                if (!File.Exists(newPath))
                {
                    return;
                }

                if (File.Exists(oldPath))
                {
                    TryDeleteFile(oldPath);
                }

                File.Move(exePath, oldPath);
                File.Move(newPath, exePath);
            }
            catch (Exception)
            {
                // Silent on exit / shutdown so as not to hang process termination.
            }
        }

        public static void CleanupOldBinary()
        {
            try
            {
                string exePath = GetExecutablePath();
                string dir = Path.GetDirectoryName(exePath);
                string baseName = Path.GetFileName(exePath);
                string oldPath = Path.Combine(dir, baseName + ".old");
                if (File.Exists(oldPath))
                {
                    TryDeleteFile(oldPath);
                }
            }
            catch (Exception)
            {
            }
        }

        private static string GetExecutablePath()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            if (entry != null && !string.IsNullOrEmpty(entry.Location))
            {
                return entry.Location;
            }
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return !File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }

            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(1);
            }

            Version v;
            if (Version.TryParse(s, out v))
            {
                return v;
            }
            return null;
        }

        private static int CompareVersions(Version a, Version b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int aMajor = a.Major >= 0 ? a.Major : 0;
            int bMajor = b.Major >= 0 ? b.Major : 0;
            if (aMajor != bMajor) return aMajor.CompareTo(bMajor);

            int aMinor = a.Minor >= 0 ? a.Minor : 0;
            int bMinor = b.Minor >= 0 ? b.Minor : 0;
            if (aMinor != bMinor) return aMinor.CompareTo(bMinor);

            int aBuild = a.Build >= 0 ? a.Build : 0;
            int bBuild = b.Build >= 0 ? b.Build : 0;
            if (aBuild != bBuild) return aBuild.CompareTo(bBuild);

            int aRev = a.Revision >= 0 ? a.Revision : 0;
            int bRev = b.Revision >= 0 ? b.Revision : 0;
            return aRev.CompareTo(bRev);
        }

        private static string FormatVersion(Version v)
        {
            if (v == null) return "v0.0.0";
            if (v.Build >= 0)
            {
                return "v" + v.Major + "." + v.Minor + "." + v.Build;
            }
            return "v" + v.Major + "." + v.Minor + ".0";
        }

        private static string FormatTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string s = tag.Trim();
            if (!s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                return "v" + s;
            }
            return s;
        }

        private static void EnsureTls12()
        {
            try
            {
                // SecurityProtocolType.Tls12 has value 3072.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch (Exception)
            {
            }
        }

        private static void OnStatusChanged()
        {
            EventHandler handler = StatusChanged;
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }
#else
        // No-op stubs for local development builds.
        public static string StatusText { get { return null; } }
        public static void Start() { }
        public static void CheckForUpdatesAsync() { }
        public static void ApplyPendingUpdate() { }
        public static void CleanupOldBinary() { }
#endif
    }
}

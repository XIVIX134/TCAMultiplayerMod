using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Updating
{
    /// <summary>
    /// Checks GitHub releases for a newer TCAMP package, verifies the release
    /// checksum, and stages the plugin DLL for replacement after the game exits.
    /// </summary>
    public sealed class ModUpdater
    {
        private const string Tag = "UPDATER";
        private const string PackageSuffix = "-plugin.zip";
        private const string PluginDllName = "TCAMP.dll";
        private const int MaxPackageBytes = 64 * 1024 * 1024;
        private const int MaxPluginDllBytes = 32 * 1024 * 1024;

        private readonly UpdateSettings _settings;
        private bool _isChecking;

        public ModUpdater(UpdateSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public event Action<string> OnStatusChanged;
        public event Action<ModUpdateResult> OnUpdateReady;

        public string StatusMessage { get; private set; } = "";

        public bool IsChecking => _isChecking;

        public async UniTask CheckForUpdatesAsync()
        {
            if (_isChecking)
                return;

            _isChecking = true;
            try
            {
                await UniTask.SwitchToThreadPool();
                CheckForUpdatesBlocking();
            }
            catch (Exception ex)
            {
                SetStatus("Update check failed: " + ex.Message);
                Log.Warning(Tag, $"Update check failed: {ex}");
            }
            finally
            {
                _isChecking = false;
            }
        }

        private void CheckForUpdatesBlocking()
        {
            if (string.IsNullOrWhiteSpace(_settings.LatestReleaseApiUrl))
            {
                SetStatus("Updater disabled: no GitHub release URL configured");
                return;
            }

            SetStatus("Checking for TCAMP updates...");

            string json = DownloadString(_settings.LatestReleaseApiUrl);
            var release = ParseReleaseJson(json);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                SetStatus("No GitHub release information found");
                return;
            }

            int versionComparison = CompareReleaseVersion(release.TagName, _settings.CurrentVersion);
            if (versionComparison < 0)
            {
                SetStatus($"TCAMP is up to date ({_settings.CurrentVersion})");
                return;
            }

            if (!TryChooseReleaseAssets(release, out var packageAsset, out var checksumAsset, out string assetError))
            {
                SetStatus(assetError);
                return;
            }

            SetStatus(versionComparison > 0
                ? $"Downloading TCAMP {NormalizeTag(release.TagName)}..."
                : $"Checking TCAMP {NormalizeTag(release.TagName)} release files...");
            string expectedPackageSha = GetExpectedPackageSha(packageAsset, checksumAsset);
            byte[] packageBytes = DownloadBytes(packageAsset.DownloadUrl, MaxPackageBytes);
            string actualPackageSha = ComputeSha256(packageBytes);
            if (!string.Equals(actualPackageSha, expectedPackageSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Package SHA256 mismatch: expected {expectedPackageSha}, got {actualPackageSha}");
            }

            byte[] pluginDll = ReadPluginDllFromPackage(packageBytes);
            string newDllSha = ComputeSha256(pluginDll);
            string currentDllPath = GetCurrentPluginPath(_settings.CurrentPluginPath);
            string currentDllSha = File.Exists(currentDllPath)
                ? ComputeFileSha256(currentDllPath)
                : "";

            if (string.Equals(currentDllSha, newDllSha, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"TCAMP {NormalizeTag(release.TagName)} already matches installed files");
                return;
            }

            var result = StagePluginUpdate(release.TagName, actualPackageSha, newDllSha, currentDllPath, pluginDll);
            SetStatus(result.Message);
            OnUpdateReady?.Invoke(result);
        }

        private string GetExpectedPackageSha(GitHubAssetInfo packageAsset, GitHubAssetInfo checksumAsset)
        {
            if (checksumAsset != null && !string.IsNullOrWhiteSpace(checksumAsset.DownloadUrl))
            {
                string shaText = DownloadString(checksumAsset.DownloadUrl);
                return ParseSha256(shaText);
            }

            if (!string.IsNullOrWhiteSpace(packageAsset?.Digest)
                && packageAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                return ParseSha256(packageAsset.Digest.Substring("sha256:".Length));
            }

            throw new InvalidDataException("Release has no SHA256 checksum asset");
        }

        private ModUpdateResult StagePluginUpdate(
            string tagName,
            string packageSha256,
            string pluginSha256,
            string currentDllPath,
            byte[] pluginDll)
        {
            string pluginDir = Path.GetDirectoryName(currentDllPath);
            if (string.IsNullOrWhiteSpace(pluginDir))
                throw new InvalidDataException("Installed plugin directory could not be resolved");

            Directory.CreateDirectory(pluginDir);
            string pendingPath = Path.Combine(pluginDir, PluginDllName + ".pending");
            string pendingShaPath = pendingPath + ".sha256";
            string pendingVersionPath = pendingPath + ".version";
            string scriptPath = Path.Combine(pluginDir, "TCAMP.ApplyUpdate.cmd");

            if (File.Exists(pendingPath)
                && string.Equals(ComputeFileSha256(pendingPath), pluginSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ModUpdateResult
                {
                    TagName = NormalizeTag(tagName),
                    PackageSha256 = packageSha256,
                    PluginSha256 = pluginSha256,
                    PendingPath = pendingPath,
                    Message = $"TCAMP {NormalizeTag(tagName)} is already staged. Restart the game to finish updating.",
                    RebootRequired = true
                };
            }

            File.WriteAllBytes(pendingPath, pluginDll);
            File.WriteAllText(pendingShaPath, pluginSha256 + "  " + PluginDllName + Environment.NewLine, Encoding.ASCII);
            File.WriteAllText(pendingVersionPath, NormalizeTag(tagName) + Environment.NewLine, Encoding.ASCII);

            WriteApplyScript(scriptPath, pendingPath, currentDllPath, pendingShaPath, pendingVersionPath);
            StartApplyScript(scriptPath);

            return new ModUpdateResult
            {
                TagName = NormalizeTag(tagName),
                PackageSha256 = packageSha256,
                PluginSha256 = pluginSha256,
                PendingPath = pendingPath,
                Message = $"TCAMP {NormalizeTag(tagName)} downloaded and verified. Close the game, then launch it again to finish updating.",
                RebootRequired = true
            };
        }

        private static void WriteApplyScript(
            string scriptPath,
            string pendingPath,
            string targetPath,
            string pendingShaPath,
            string pendingVersionPath)
        {
            int pid = Process.GetCurrentProcess().Id;
            string script = "@echo off\r\n"
                + "setlocal\r\n"
                + $"set \"TCAMP_PID={pid}\"\r\n"
                + $"set \"PENDING={pendingPath}\"\r\n"
                + $"set \"TARGET={targetPath}\"\r\n"
                + $"set \"PENDING_SHA={pendingShaPath}\"\r\n"
                + $"set \"PENDING_VERSION={pendingVersionPath}\"\r\n"
                + ":wait_for_game\r\n"
                + "tasklist /FI \"PID eq %TCAMP_PID%\" 2>NUL | find \"%TCAMP_PID%\" >NUL\r\n"
                + "if not errorlevel 1 (\r\n"
                + "  timeout /t 1 /nobreak >NUL\r\n"
                + "  goto wait_for_game\r\n"
                + ")\r\n"
                + "if exist \"%PENDING%\" copy /Y \"%PENDING%\" \"%TARGET%\" >NUL\r\n"
                + "if exist \"%PENDING%\" del /F /Q \"%PENDING%\" >NUL\r\n"
                + "if exist \"%PENDING_SHA%\" del /F /Q \"%PENDING_SHA%\" >NUL\r\n"
                + "if exist \"%PENDING_VERSION%\" del /F /Q \"%PENDING_VERSION%\" >NUL\r\n"
                + "del /F /Q \"%~f0\" >NUL\r\n";

            File.WriteAllText(scriptPath, script, Encoding.ASCII);
        }

        private static void StartApplyScript(string scriptPath)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"\" /min \"" + scriptPath + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Update was staged, but the apply script could not be started: {ex.Message}");
            }
        }

        private string DownloadString(string url)
        {
            byte[] bytes = DownloadBytes(url, MaxPackageBytes);
            return Encoding.UTF8.GetString(bytes);
        }

        private byte[] DownloadBytes(string url, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidDataException("Download URL is empty");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new TimeoutWebClient(_settings.TimeoutMilliseconds))
            {
                client.Headers[HttpRequestHeader.UserAgent] =
                    $"TCAMP-Updater/{_settings.CurrentVersion} (+https://github.com/XIVIX134/TCAMultiplayerMod)";
                client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json, application/octet-stream";
                byte[] bytes = client.DownloadData(url);
                if (bytes == null || bytes.Length == 0)
                    throw new InvalidDataException("Downloaded file is empty");
                if (maxBytes > 0 && bytes.Length > maxBytes)
                    throw new InvalidDataException($"Downloaded file is too large ({bytes.Length} bytes)");
                return bytes;
            }
        }

        private void SetStatus(string message)
        {
            message = message ?? "";
            StatusMessage = message;
            Log.Info(Tag, message);
            OnStatusChanged?.Invoke(message);
        }

        public static GitHubReleaseInfo ParseReleaseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var serializer = new DataContractJsonSerializer(typeof(GitHubReleaseInfo));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(ms) as GitHubReleaseInfo;
            }
        }

        public static bool IsVersionNewer(string latestTag, string currentVersion)
        {
            return CompareReleaseVersion(latestTag, currentVersion) > 0;
        }

        public static int CompareReleaseVersion(string latestTag, string currentVersion)
        {
            if (!TryParseVersion(latestTag, out var latest)
                || !TryParseVersion(currentVersion, out var current))
            {
                return 0;
            }

            return latest.CompareTo(current);
        }

        public static string ParseSha256(string value)
        {
            var match = Regex.Match(value ?? "", @"\b[a-fA-F0-9]{64}\b");
            if (!match.Success)
                throw new InvalidDataException("SHA256 checksum was not found");
            return match.Value.ToLowerInvariant();
        }

        public static bool TryChooseReleaseAssets(
            GitHubReleaseInfo release,
            out GitHubAssetInfo packageAsset,
            out GitHubAssetInfo checksumAsset,
            out string error)
        {
            packageAsset = null;
            checksumAsset = null;
            error = "";

            var assets = release?.Assets ?? new List<GitHubAssetInfo>();
            packageAsset = assets.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a?.Name)
                    && a.Name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.DownloadUrl))
                ?? assets.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a?.Name)
                    && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.DownloadUrl));

            if (packageAsset == null)
            {
                error = "Latest TCAMP release has no plugin zip asset";
                return false;
            }

            string expectedShaAssetName = packageAsset.Name + ".sha256";
            checksumAsset = assets.FirstOrDefault(a =>
                    string.Equals(a?.Name, expectedShaAssetName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.DownloadUrl))
                ?? assets.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a?.Name)
                    && a.Name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.DownloadUrl));

            if (checksumAsset == null
                && (string.IsNullOrWhiteSpace(packageAsset.Digest)
                    || !packageAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Latest TCAMP release has no plugin zip SHA256 checksum";
                return false;
            }

            return true;
        }

        public static byte[] ReadPluginDllFromPackage(byte[] packageBytes)
        {
            if (packageBytes == null || packageBytes.Length == 0)
                throw new InvalidDataException("Update package is empty");
            if (packageBytes.Length > MaxPackageBytes)
                throw new InvalidDataException("Update package is too large");

            using (var ms = new MemoryStream(packageBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('\\', '/').EndsWith("/" + PluginDllName, StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e =>
                        string.Equals(Path.GetFileName(e.FullName), PluginDllName, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                    throw new InvalidDataException("Update package does not contain TCAMP.dll");
                if (entry.Length <= 0 || entry.Length > MaxPluginDllBytes)
                    throw new InvalidDataException($"TCAMP.dll has an invalid size ({entry.Length} bytes)");

                using (var input = entry.Open())
                using (var output = new MemoryStream())
                {
                    input.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        public static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(bytes ?? new byte[0]));
            }
        }

        public static string NormalizeTag(string tag)
        {
            tag = (tag ?? "").Trim();
            return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? tag
                : "v" + tag;
        }

        private static string GetCurrentPluginPath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return configuredPath;

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new InvalidDataException("Current plugin path is unavailable");
            return assemblyPath;
        }

        private static bool TryParseVersion(string value, out Version version)
        {
            version = null;
            value = (value ?? "").Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);

            int suffix = value.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                value = value.Substring(0, suffix);

            return Version.TryParse(value, out version);
        }

        private static string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMilliseconds;

            public TimeoutWebClient(int timeoutMilliseconds)
            {
                _timeoutMilliseconds = timeoutMilliseconds <= 0 ? 15000 : timeoutMilliseconds;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = _timeoutMilliseconds;
                    request.Proxy = WebRequest.DefaultWebProxy;
                }
                return request;
            }
        }
    }

    public sealed class UpdateSettings
    {
        public string LatestReleaseApiUrl;
        public string CurrentVersion;
        public string CurrentPluginPath;
        public int TimeoutMilliseconds = 15000;
    }

    public sealed class ModUpdateResult
    {
        public string TagName;
        public string PackageSha256;
        public string PluginSha256;
        public string PendingPath;
        public string Message;
        public bool RebootRequired;
    }

    [DataContract]
    public sealed class GitHubReleaseInfo
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "assets")]
        public List<GitHubAssetInfo> Assets { get; set; }
    }

    [DataContract]
    public sealed class GitHubAssetInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }

        [DataMember(Name = "digest")]
        public string Digest { get; set; }
    }
}

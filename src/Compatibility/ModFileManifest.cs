using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Compatibility
{
    /// <summary>
    /// Hashes and syncs game mod data under Tiny Combat Arena's Mods folder.
    /// Sync deliberately excludes executable/plugin-style files and arbitrary paths.
    /// </summary>
    public sealed class ModFileManifest
    {
        private const string Tag = "MOD-FILES";
        private const int FormatVersion = 1;
        private const string PackageMagic = "TCAMP_MOD_SYNC";
        private const int MaxManifestEntries = 8192;
        private const int MaxSyncPackageFiles = 8192;
        private const long MaxSyncFileBytes = 64L * 1024L * 1024L;
        public const int MaxSyncPackageBytes = 128 * 1024 * 1024;
        private const long MaxPackageBytes = MaxSyncPackageBytes;
        private const long MaxExpandedPackageBytes = MaxSyncPackageBytes;

        private static readonly HashSet<string> BlockedSyncExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".asi",
                ".bat",
                ".cmd",
                ".com",
                ".cs",
                ".dll",
                ".dylib",
                ".exe",
                ".jar",
                ".js",
                ".lnk",
                ".msi",
                ".pif",
                ".ps1",
                ".py",
                ".reg",
                ".scr",
                ".sh",
                ".so",
                ".url",
                ".vbe",
                ".vbs",
                ".wsf"
            };

        public List<ModFileEntry> Files { get; } = new List<ModFileEntry>();
        public string RootLabel { get; private set; } = "Mods";

        public string ManifestHash => ComputeManifestHash(Serialize());

        public static string ModsRoot
        {
            get
            {
                try
                {
                    string gameRoot = Paths.GameRootPath;
                    if (!string.IsNullOrWhiteSpace(gameRoot))
                        return Path.Combine(gameRoot, "Mods");
                }
                catch
                {
                }

                try
                {
                    string cwd = Environment.CurrentDirectory;
                    return string.IsNullOrWhiteSpace(cwd) ? null : Path.Combine(cwd, "Mods");
                }
                catch
                {
                    return null;
                }
            }
        }

        public static ModFileManifest Collect()
        {
            var manifest = new ModFileManifest();
            string root = ModsRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Log.Warning(Tag, $"Mods folder not found: {root ?? "(unknown)"}");
                return manifest;
            }

            string rootFull = Path.GetFullPath(root);
            foreach (var path in Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string relativePath = GetSafeRelativePath(rootFull, path);
                    if (!IsAllowedManifestPath(relativePath))
                        continue;

                    var info = new FileInfo(path);
                    var entry = new ModFileEntry
                    {
                        Path = relativePath,
                        Size = info.Length,
                        Sha256 = ComputeFileHash(path),
                        SyncAllowed = IsAllowedSyncPath(relativePath, info.Length)
                    };
                    manifest.Files.Add(entry);
                }
                catch (Exception ex)
                {
                    Log.Warning(Tag, $"Skipping mod file '{path}': {ex.Message}");
                }
            }

            manifest.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            Log.Info(Tag, $"Collected {manifest.Files.Count} mod files from {rootFull}");
            return manifest;
        }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(FormatVersion);
                w.Write(RootLabel ?? "Mods");
                w.Write(Files.Count);
                foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    w.Write(file.Path ?? "");
                    w.Write(file.Sha256 ?? "");
                    w.Write(file.Size);
                    w.Write(file.SyncAllowed);
                }
                return ms.ToArray();
            }
        }

        public static ModFileManifest Deserialize(byte[] data)
        {
            var manifest = new ModFileManifest();
            if (data == null || data.Length == 0)
                return manifest;

            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                int version = r.ReadInt32();
                if (version != FormatVersion)
                    throw new InvalidDataException($"Unsupported mod manifest version {version}");

                manifest.RootLabel = r.ReadString();
                int count = r.ReadInt32();
                if (count < 0 || count > MaxManifestEntries)
                    throw new InvalidDataException($"Invalid mod manifest entry count {count}");

                for (int i = 0; i < count; i++)
                {
                    string path = NormalizeManifestPath(r.ReadString());
                    string sha256 = r.ReadString() ?? "";
                    long size = r.ReadInt64();
                    bool syncAllowed = r.ReadBoolean();
                    if (!IsAllowedManifestPath(path))
                        throw new InvalidDataException($"Unsafe mod manifest path '{path}'");

                    manifest.Files.Add(new ModFileEntry
                    {
                        Path = path,
                        Sha256 = sha256,
                        Size = size,
                        SyncAllowed = syncAllowed && IsAllowedSyncPath(path, size)
                    });
                }
            }

            manifest.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return manifest;
        }

        public ModManifestDiff CompareTo(ModFileManifest host)
        {
            var diff = new ModManifestDiff();
            if (host == null)
                return diff;

            var localByPath = Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var hostByPath = host.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

            foreach (var hostFile in host.Files)
            {
                if (!localByPath.TryGetValue(hostFile.Path, out var localFile))
                {
                    diff.Missing.Add(hostFile);
                    if (!hostFile.SyncAllowed)
                        diff.Unsyncable.Add(hostFile.Path);
                    continue;
                }

                if (!string.Equals(localFile.Sha256, hostFile.Sha256, StringComparison.Ordinal)
                    || localFile.Size != hostFile.Size)
                {
                    diff.Changed.Add(hostFile);
                    if (!hostFile.SyncAllowed)
                        diff.Unsyncable.Add(hostFile.Path);
                }
            }

            foreach (var localFile in Files)
            {
                if (!hostByPath.ContainsKey(localFile.Path))
                    diff.Extra.Add(localFile);
            }

            return diff;
        }

        public byte[] BuildSyncPackage(ModFileManifest client)
        {
            if (client == null)
                client = new ModFileManifest();

            var diff = client.CompareTo(this);
            if (diff.Unsyncable.Count > 0)
                throw new InvalidOperationException("Cannot sync blocked mod files: " + string.Join(", ", diff.Unsyncable.Take(5)));

            string root = ModsRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException("Mods folder not found");

            string rootFull = Path.GetFullPath(root);
            using (var package = new MemoryStream())
            using (var writer = new BinaryWriter(package, Encoding.UTF8))
            {
                writer.Write(PackageMagic);
                writer.Write(FormatVersion);

                byte[] manifestData = Serialize();
                writer.Write(manifestData.Length);
                writer.Write(manifestData);

                var required = diff.RequiredHostFiles.ToList();
                writer.Write(required.Count);
                foreach (var file in required)
                {
                    if (!file.SyncAllowed)
                        throw new InvalidOperationException($"File is not allowed to sync: {file.Path}");
                    if (!IsAllowedSyncPath(file.Path, file.Size))
                        throw new InvalidOperationException($"Unsafe sync file: {file.Path}");

                    string sourcePath = ResolveSafePath(rootFull, file.Path);
                    if (!File.Exists(sourcePath))
                        throw new FileNotFoundException($"Host mod file missing during sync: {file.Path}", sourcePath);
                    if (new FileInfo(sourcePath).Length != file.Size)
                        throw new InvalidOperationException($"Host mod file changed during sync: {file.Path}");

                    byte[] bytes = File.ReadAllBytes(sourcePath);
                    writer.Write(file.Path);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);

                    if (package.Length > MaxPackageBytes)
                        throw new InvalidOperationException("Mod sync package is too large");
                }

                var deletions = diff.Extra
                    .Where(extra => IsAllowedSyncPath(extra.Path, extra.Size))
                    .ToList();
                writer.Write(deletions.Count);
                foreach (var extra in deletions)
                {
                    writer.Write(extra.Path);
                    if (package.Length > MaxPackageBytes)
                        throw new InvalidOperationException("Mod sync package is too large");
                }

                return package.ToArray();
            }
        }

        public static SyncApplyResult ApplySyncPackage(byte[] packageData, string expectedHostManifestHash)
        {
            if (packageData == null || packageData.Length == 0)
                return SyncApplyResult.Failed("Empty sync package");
            if (packageData.Length > MaxPackageBytes)
                return SyncApplyResult.Failed("Sync package is too large");

            string root = ModsRoot;
            if (string.IsNullOrWhiteSpace(root))
                return SyncApplyResult.Failed("Mods folder path is unavailable");

            Directory.CreateDirectory(root);
            string rootFull = Path.GetFullPath(root);

            try
            {
                int filesWritten = 0;
                int filesDeleted = 0;
                long expandedBytes = 0;
                string backupPath;

                using (var ms = new MemoryStream(packageData))
                using (var reader = new BinaryReader(ms, Encoding.UTF8))
                {
                    string magic = reader.ReadString();
                    if (!string.Equals(magic, PackageMagic, StringComparison.Ordinal))
                        return SyncApplyResult.Failed("Invalid mod sync package");

                    int version = reader.ReadInt32();
                    if (version != FormatVersion)
                        return SyncApplyResult.Failed($"Unsupported mod sync package version {version}");

                    int manifestLength = reader.ReadInt32();
                    if (manifestLength <= 0 || manifestLength > MaxPackageBytes)
                        return SyncApplyResult.Failed("Invalid host manifest in sync package");
                    byte[] hostManifestData = reader.ReadBytes(manifestLength);

                    string actualManifestHash = ComputeManifestHash(hostManifestData);
                    if (!string.IsNullOrWhiteSpace(expectedHostManifestHash)
                        && !string.Equals(actualManifestHash, expectedHostManifestHash, StringComparison.Ordinal))
                    {
                        return SyncApplyResult.Failed("Host manifest hash changed during sync");
                    }

                    int fileCount = reader.ReadInt32();
                    if (fileCount < 0 || fileCount > MaxSyncPackageFiles)
                        return SyncApplyResult.Failed("Invalid mod sync file count");

                    var writes = new List<SyncPackageFile>(fileCount);
                    for (int i = 0; i < fileCount; i++)
                    {
                        string relative = NormalizeManifestPath(reader.ReadString());
                        int length = reader.ReadInt32();
                        if (!IsAllowedSyncPath(relative, length))
                            return SyncApplyResult.Failed($"Blocked unsafe mod file: {relative}");

                        expandedBytes += length;
                        if (expandedBytes > MaxExpandedPackageBytes)
                            return SyncApplyResult.Failed("Sync package expands too large");

                        byte[] bytes = reader.ReadBytes(length);
                        if (bytes.Length != length)
                            return SyncApplyResult.Failed("Truncated mod sync package");

                        ResolveSafePath(rootFull, relative);
                        writes.Add(new SyncPackageFile { RelativePath = relative, Bytes = bytes });
                    }

                    int deleteCount = reader.ReadInt32();
                    if (deleteCount < 0 || deleteCount > MaxSyncPackageFiles)
                        return SyncApplyResult.Failed("Invalid mod sync delete count");

                    var deletions = new List<string>(deleteCount);
                    for (int i = 0; i < deleteCount; i++)
                    {
                        string relative = NormalizeManifestPath(reader.ReadString());
                        if (!IsAllowedSyncPath(relative, 0))
                            continue;

                        ResolveSafePath(rootFull, relative);
                        deletions.Add(relative);
                    }

                    backupPath = BackupModsFolder(rootFull);
                    Log.Info(Tag, $"Backed up Mods folder before sync: {backupPath}");

                    foreach (var file in writes)
                    {
                        string target = ResolveSafePath(rootFull, file.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.WriteAllBytes(target, file.Bytes);
                        filesWritten++;
                    }

                    foreach (var relative in deletions)
                    {
                        string target = ResolveSafePath(rootFull, relative);
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                            filesDeleted++;
                        }
                    }

                    var hostManifest = Deserialize(hostManifestData);
                    var localManifest = Collect();
                    var remaining = localManifest.CompareTo(hostManifest);
                    if (!remaining.IsCompatible)
                        return SyncApplyResult.Failed("Local mods still differ after sync");
                }

                return new SyncApplyResult
                {
                    Success = true,
                    FilesWritten = filesWritten,
                    FilesDeleted = filesDeleted,
                    BackupPath = backupPath,
                    Message = $"Synced {filesWritten} file(s), removed {filesDeleted} extra file(s). Backup: {backupPath}"
                };
            }
            catch (Exception ex)
            {
                return SyncApplyResult.Failed(ex.Message);
            }
        }

        public static bool IsAllowedSyncPath(string path, long size)
        {
            if (!IsAllowedManifestPath(path))
                return false;
            if (size < 0 || size > MaxSyncFileBytes)
                return false;

            string ext = Path.GetExtension(path) ?? "";
            return !BlockedSyncExtensions.Contains(ext);
        }

        private static bool IsAllowedManifestPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (Path.IsPathRooted(path))
                return false;
            if (path.IndexOf(':') >= 0)
                return false;
            if (path.Split('/').Any(part => part == "." || part == ".." || part.Length == 0))
                return false;

            // Files must belong to a mod folder. This keeps stray root-level
            // files out while allowing normal TCA mod data, metadata, audio,
            // images, and Unity asset payloads such as "assets<mod name>".
            return path.Split('/').Length >= 2;
        }

        private static string GetSafeRelativePath(string rootFull, string fullPath)
        {
            var rootUri = new Uri(AppendDirectorySeparator(rootFull));
            var fileUri = new Uri(Path.GetFullPath(fullPath));
            string relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString());
            return NormalizeManifestPath(relative);
        }

        private static string NormalizeManifestPath(string path)
        {
            path = (path ?? "").Replace('\\', '/').TrimStart('/');
            while (path.Contains("//"))
                path = path.Replace("//", "/");
            return path;
        }

        private static string ResolveSafePath(string rootFull, string relativePath)
        {
            relativePath = NormalizeManifestPath(relativePath);
            if (!IsAllowedManifestPath(relativePath))
                throw new InvalidDataException($"Unsafe mod path '{relativePath}'");

            string target = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSlash = AppendDirectorySeparator(rootFull);
            if (!target.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Mod path escapes Mods folder: {relativePath}");
            return target;
        }

        private static string BackupModsFolder(string rootFull)
        {
            string backupRoot = Path.Combine(GetGameRootFromModsRoot(rootFull), "TCAMP_ModBackups");
            Directory.CreateDirectory(backupRoot);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string target = Path.Combine(backupRoot, "Mods_" + stamp);
            int suffix = 1;
            while (Directory.Exists(target))
            {
                target = Path.Combine(backupRoot, "Mods_" + stamp + "_" + suffix.ToString("00"));
                suffix++;
            }

            Directory.CreateDirectory(target);
            CopyDirectory(rootFull, target);
            return target;
        }

        private static string GetGameRootFromModsRoot(string rootFull)
        {
            string trimmed = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed);
            return string.IsNullOrWhiteSpace(parent) ? rootFull : parent;
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = GetSafeRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = GetSafeRelativePath(source, file);
                string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: false);

                var sourceInfo = new FileInfo(file);
                var targetInfo = new FileInfo(target);
                targetInfo.Attributes = FileAttributes.Normal;
                targetInfo.CreationTimeUtc = sourceInfo.CreationTimeUtc;
                targetInfo.LastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
                targetInfo.LastAccessTimeUtc = sourceInfo.LastAccessTimeUtc;
                targetInfo.Attributes = sourceInfo.Attributes;
            }
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            char last = path[path.Length - 1];
            return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string ComputeFileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string ComputeManifestHash(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data ?? new byte[0]));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public sealed class ModFileEntry
        {
            public string Path;
            public string Sha256;
            public long Size;
            public bool SyncAllowed;
        }

        public sealed class ModManifestDiff
        {
            public List<ModFileEntry> Missing { get; } = new List<ModFileEntry>();
            public List<ModFileEntry> Changed { get; } = new List<ModFileEntry>();
            public List<ModFileEntry> Extra { get; } = new List<ModFileEntry>();
            public List<string> Unsyncable { get; } = new List<string>();
            public bool IsCompatible => Missing.Count == 0 && Changed.Count == 0 && Extra.Count == 0;
            public IEnumerable<ModFileEntry> RequiredHostFiles => Missing.Concat(Changed);

            public string ToSummary()
            {
                if (IsCompatible)
                    return "Mods match";

                var parts = new List<string>();
                if (Missing.Count > 0) parts.Add($"{Missing.Count} missing");
                if (Changed.Count > 0) parts.Add($"{Changed.Count} changed");
                if (Extra.Count > 0) parts.Add($"{Extra.Count} extra");
                if (Unsyncable.Count > 0) parts.Add($"{Unsyncable.Count} blocked");
                return string.Join(", ", parts.ToArray());
            }
        }

        public sealed class SyncApplyResult
        {
            public bool Success;
            public int FilesWritten;
            public int FilesDeleted;
            public string BackupPath;
            public string Message;

            public static SyncApplyResult Failed(string message)
            {
                return new SyncApplyResult { Success = false, Message = message ?? "Sync failed" };
            }
        }

        private sealed class SyncPackageFile
        {
            public string RelativePath;
            public byte[] Bytes;
        }
    }
}

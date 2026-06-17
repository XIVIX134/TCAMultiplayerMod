using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using Falcon;
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
        private const int FormatVersion = 2;
        private const int MinSupportedFormatVersion = 1;
        private const string PackageMagic = "TCAMP_MOD_SYNC";
        private const string ModLoadOrderFileName = "ModLoadOrder.json";
        private const int MaxManifestEntries = 8192;
        private const int MaxSyncPackageFiles = 8192;
        private const long MaxSyncFileBytes = 64L * 1024L * 1024L;
        private const long MaxMetadataFileBytes = 1024L * 1024L;
        public const int MaxSyncPackageBytes = 128 * 1024 * 1024;
        private const long MaxPackageBytes = MaxSyncPackageBytes;
        private const long MaxExpandedPackageBytes = MaxSyncPackageBytes;

        internal static Func<string> GameRootResolver = ResolveGameRootPath;
        internal static Func<string, IEnumerable<ExternalModSource>> ExternalModSourceResolver =
            ResolveLoadedGameModSources;

        internal static void ResetResolversForTests()
        {
            GameRootResolver = ResolveGameRootPath;
            ExternalModSourceResolver = ResolveLoadedGameModSources;
        }

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

        public static string ModsRoot => GetModsRoot();

        public static ModFileManifest Collect()
        {
            return Collect(null);
        }

        private static ModFileManifest Collect(ISet<string> suppressedExternalDestinations)
        {
            var manifest = new ModFileManifest();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            string root = ModsRoot;
            string rootFull = null;
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                rootFull = Path.GetFullPath(root);
                foreach (var path in Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        AddFileEntry(manifest, seenPaths, rootFull, path, null);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(Tag, $"Skipping mod file '{path}': {ex.Message}");
                    }
                }
            }
            else
            {
                Log.Warning(Tag, $"Mods folder not found: {root ?? "(unknown)"}");
            }

            AddExternalModSources(manifest, seenPaths, rootFull, suppressedExternalDestinations);
            AddModLoadOrder(manifest, seenPaths);

            manifest.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            Log.Info(Tag, $"Collected {manifest.Files.Count} mod files from {root ?? "(unknown)"}");
            return manifest;
        }

        private static void AddExternalModSources(
            ModFileManifest manifest,
            HashSet<string> seenPaths,
            string modsRootFull,
            ISet<string> suppressedExternalDestinations)
        {
            IEnumerable<ExternalModSource> sources;
            try
            {
                sources = ExternalModSourceResolver?.Invoke(modsRootFull) ?? Enumerable.Empty<ExternalModSource>();
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not enumerate loaded Workshop mods: {ex.Message}");
                return;
            }

            foreach (var source in sources)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.SourceRoot))
                    continue;
                if (string.IsNullOrWhiteSpace(source.DestinationFolder))
                    continue;

                try
                {
                    string sourceRootFull = Path.GetFullPath(source.SourceRoot);
                    if (!Directory.Exists(sourceRootFull))
                        continue;
                    if (!string.IsNullOrWhiteSpace(modsRootFull) && IsSameOrUnder(sourceRootFull, modsRootFull))
                        continue;

                    string destinationFolder = NormalizeManifestPath(source.DestinationFolder);
                    if (destinationFolder.Contains("/") || !IsSafePathSegment(destinationFolder))
                    {
                        Log.Warning(Tag, $"Skipping external mod with unsafe target '{source.DestinationFolder}'");
                        continue;
                    }
                    if (suppressedExternalDestinations != null
                        && suppressedExternalDestinations.Contains(destinationFolder))
                    {
                        continue;
                    }

                    foreach (var path in Directory.GetFiles(sourceRootFull, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            AddFileEntry(manifest, seenPaths, sourceRootFull, path, destinationFolder);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(Tag, $"Skipping external mod file '{path}': {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(Tag, $"Skipping external mod source '{source.SourceRoot}': {ex.Message}");
                }
            }
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
                if (version < MinSupportedFormatVersion || version > FormatVersion)
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

                bool sizeChanged = !IsModLoadOrderPath(hostFile.Path) && localFile.Size != hostFile.Size;
                if (!string.Equals(localFile.Sha256, hostFile.Sha256, StringComparison.Ordinal)
                    || sizeChanged)
                {
                    diff.Changed.Add(hostFile);
                    if (!hostFile.SyncAllowed)
                        diff.Unsyncable.Add(hostFile.Path);
                }
            }

            foreach (var localFile in Files)
            {
                if (IsModLoadOrderPath(localFile.Path) && !hostByPath.ContainsKey(localFile.Path))
                    continue;
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

                    string sourcePath = ResolveSourcePath(rootFull, file);
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
                    .Where(extra => !IsModLoadOrderPath(extra.Path) && IsAllowedSyncPath(extra.Path, extra.Size))
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

                        ResolveApplyPath(rootFull, relative);
                        writes.Add(new SyncPackageFile { RelativePath = relative, Bytes = bytes });
                    }

                    int deleteCount = reader.ReadInt32();
                    if (deleteCount < 0 || deleteCount > MaxSyncPackageFiles)
                        return SyncApplyResult.Failed("Invalid mod sync delete count");

                    var deletions = new List<string>(deleteCount);
                    for (int i = 0; i < deleteCount; i++)
                    {
                        string relative = NormalizeManifestPath(reader.ReadString());
                        if (IsModLoadOrderPath(relative) || !IsAllowedSyncPath(relative, 0))
                            continue;

                        ResolveModsSafePath(rootFull, relative);
                        deletions.Add(relative);
                    }

                    backupPath = BackupModsFolder(rootFull);
                    Log.Info(Tag, $"Backed up Mods folder before sync: {backupPath}");

                    foreach (var file in writes)
                    {
                        string target = ResolveApplyPath(rootFull, file.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.WriteAllBytes(target, file.Bytes);
                        filesWritten++;
                    }

                    foreach (var relative in deletions)
                    {
                        string target = ResolveModsSafePath(rootFull, relative);
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                            filesDeleted++;
                        }
                    }

                    var disabledExternalMods = DisableExternalModsForDeletedPaths(deletions, rootFull);
                    var hostManifest = Deserialize(hostManifestData);
                    var localManifest = Collect(disabledExternalMods.DestinationFolders);
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
            if (IsModLoadOrderPath(path))
                return size >= 0 && size <= MaxMetadataFileBytes;
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
            path = NormalizeManifestPath(path);
            if (IsModLoadOrderPath(path))
                return true;
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

        private static void AddFileEntry(
            ModFileManifest manifest,
            HashSet<string> seenPaths,
            string sourceRootFull,
            string sourcePath,
            string destinationFolder)
        {
            string relativePath = GetSafeRelativePath(sourceRootFull, sourcePath);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
                relativePath = NormalizeManifestPath(destinationFolder + "/" + relativePath);

            if (!IsAllowedManifestPath(relativePath))
                return;

            var info = new FileInfo(sourcePath);
            var entry = new ModFileEntry
            {
                Path = relativePath,
                Size = info.Length,
                Sha256 = ComputeFileHash(sourcePath),
                SyncAllowed = IsAllowedSyncPath(relativePath, info.Length),
                SourcePath = sourcePath
            };
            AddEntry(manifest, seenPaths, entry);
        }

        private static void AddModLoadOrder(ModFileManifest manifest, HashSet<string> seenPaths)
        {
            string loadOrderPath = ModLoadOrderPath;
            if (string.IsNullOrWhiteSpace(loadOrderPath) || !File.Exists(loadOrderPath))
                return;

            try
            {
                var info = new FileInfo(loadOrderPath);
                var entry = new ModFileEntry
                {
                    Path = ModLoadOrderFileName,
                    Size = info.Length,
                    Sha256 = ComputeModLoadOrderHash(loadOrderPath),
                    SyncAllowed = IsAllowedSyncPath(ModLoadOrderFileName, info.Length),
                    SourcePath = loadOrderPath
                };
                AddEntry(manifest, seenPaths, entry);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Skipping {ModLoadOrderFileName}: {ex.Message}");
            }
        }

        private static void AddEntry(
            ModFileManifest manifest,
            HashSet<string> seenPaths,
            ModFileEntry entry)
        {
            entry.Path = NormalizeManifestPath(entry.Path);
            if (!seenPaths.Add(entry.Path))
                return;
            manifest.Files.Add(entry);
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

        private static string ResolveSourcePath(string modsRootFull, ModFileEntry file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));
            if (IsModLoadOrderPath(file.Path))
            {
                string path = ModLoadOrderPath;
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidDataException($"{ModLoadOrderFileName} path is unavailable");
                return path;
            }
            if (!string.IsNullOrWhiteSpace(file.SourcePath))
                return Path.GetFullPath(file.SourcePath);
            return ResolveModsSafePath(modsRootFull, file.Path);
        }

        private static string ResolveApplyPath(string modsRootFull, string relativePath)
        {
            relativePath = NormalizeManifestPath(relativePath);
            if (IsModLoadOrderPath(relativePath))
            {
                string path = ModLoadOrderPath;
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidDataException($"{ModLoadOrderFileName} path is unavailable");
                return path;
            }
            return ResolveModsSafePath(modsRootFull, relativePath);
        }

        private static ExternalModDisablePlan DisableExternalModsForDeletedPaths(
            IEnumerable<string> deletedRelativePaths,
            string modsRootFull)
        {
            var plan = BuildExternalModDisablePlan(deletedRelativePaths, modsRootFull);
            if (plan.ModNames.Count == 0)
                return plan;

            string loadOrderPath = ModLoadOrderPath;
            if (string.IsNullOrWhiteSpace(loadOrderPath))
                return plan;

            if (!TryReadModLoadOrder(loadOrderPath, out var loadOrder))
                return plan;

            bool changed = false;
            foreach (string modName in plan.ModNames)
            {
                if (string.IsNullOrWhiteSpace(modName))
                    continue;

                var entry = loadOrder.Mods
                    .FirstOrDefault(mod => mod != null && string.Equals(mod.Name, modName, StringComparison.Ordinal));
                if (entry == null)
                {
                    loadOrder.Mods.Add(new ModLoadOrderData.ModEntry
                    {
                        Name = modName,
                        IsEnabled = false
                    });
                    plan.EntriesChanged++;
                    changed = true;
                }
                else if (entry.IsEnabled)
                {
                    entry.IsEnabled = false;
                    plan.EntriesChanged++;
                    changed = true;
                }
            }

            if (!changed)
                return plan;

            string loadOrderDirectory = Path.GetDirectoryName(loadOrderPath);
            if (!string.IsNullOrWhiteSpace(loadOrderDirectory))
                Directory.CreateDirectory(loadOrderDirectory);
            File.WriteAllText(loadOrderPath, SerializeModLoadOrder(loadOrder));
            Log.Info(Tag, $"Disabled {plan.EntriesChanged} extra Workshop mod(s) in {ModLoadOrderFileName}");
            return plan;
        }

        private static ExternalModDisablePlan BuildExternalModDisablePlan(
            IEnumerable<string> deletedRelativePaths,
            string modsRootFull)
        {
            var plan = new ExternalModDisablePlan();
            var deletedTopFolders = new HashSet<string>(StringComparer.Ordinal);
            foreach (string relativePath in deletedRelativePaths ?? Enumerable.Empty<string>())
            {
                string topFolder = GetTopLevelManifestFolder(relativePath);
                if (!string.IsNullOrWhiteSpace(topFolder))
                    deletedTopFolders.Add(topFolder);
            }

            if (deletedTopFolders.Count == 0)
                return plan;

            foreach (var source in ResolveExternalModSourcesSafely(modsRootFull))
            {
                if (source == null || string.IsNullOrWhiteSpace(source.DestinationFolder))
                    continue;

                string destinationFolder = NormalizeManifestPath(source.DestinationFolder);
                if (!deletedTopFolders.Contains(destinationFolder))
                    continue;

                string sourceRootFull = null;
                try
                {
                    sourceRootFull = string.IsNullOrWhiteSpace(source.SourceRoot)
                        ? null
                        : Path.GetFullPath(source.SourceRoot);
                }
                catch
                {
                }

                if (string.IsNullOrWhiteSpace(sourceRootFull)
                    || !Directory.Exists(sourceRootFull)
                    || (!string.IsNullOrWhiteSpace(modsRootFull) && IsSameOrUnder(sourceRootFull, modsRootFull)))
                {
                    continue;
                }

                plan.DestinationFolders.Add(destinationFolder);
                string modName = !string.IsNullOrWhiteSpace(source.ModName)
                    ? source.ModName
                    : TryReadModName(sourceRootFull);
                if (!string.IsNullOrWhiteSpace(modName))
                    plan.ModNames.Add(modName);
            }

            return plan;
        }

        private static IEnumerable<ExternalModSource> ResolveExternalModSourcesSafely(string modsRootFull)
        {
            try
            {
                return ExternalModSourceResolver?.Invoke(modsRootFull) ?? Enumerable.Empty<ExternalModSource>();
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not enumerate loaded Workshop mods: {ex.Message}");
                return Enumerable.Empty<ExternalModSource>();
            }
        }

        private static string GetTopLevelManifestFolder(string relativePath)
        {
            relativePath = NormalizeManifestPath(relativePath);
            int slash = relativePath.IndexOf('/');
            return slash <= 0 ? null : relativePath.Substring(0, slash);
        }

        private static string TryReadModName(string sourceRootFull)
        {
            try
            {
                string modDefinitionPath = Path.Combine(sourceRootFull, "Mod.json");
                if (!File.Exists(modDefinitionPath))
                    return null;

                return TryReadJsonStringProperty(File.ReadAllText(modDefinitionPath), "Name", out string name)
                    ? name
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveModsSafePath(string rootFull, string relativePath)
        {
            relativePath = NormalizeManifestPath(relativePath);
            if (!IsAllowedManifestPath(relativePath) || IsModLoadOrderPath(relativePath))
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
            CopyModLoadOrderToBackup(target);
            return target;
        }

        private static void CopyModLoadOrderToBackup(string backupPath)
        {
            string loadOrderPath = ModLoadOrderPath;
            if (string.IsNullOrWhiteSpace(loadOrderPath) || !File.Exists(loadOrderPath))
                return;

            string target = Path.Combine(backupPath, ModLoadOrderFileName);
            File.Copy(loadOrderPath, target, overwrite: false);
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

        private static string ComputeModLoadOrderHash(string path)
        {
            if (!TryReadModLoadOrder(path, out var loadOrder))
                return ComputeFileHash(path);

            var sb = new StringBuilder();
            sb.Append("enabled-order-v1\n");
            if (loadOrder.Mods != null)
            {
                foreach (var mod in loadOrder.Mods)
                {
                    if (mod == null || !mod.IsEnabled || string.IsNullOrWhiteSpace(mod.Name))
                        continue;
                    sb.Append(mod.Name).Append('\n');
                }
            }

            return ComputeManifestHash(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static bool TryReadModLoadOrder(string path, out ModLoadOrderData loadOrder)
        {
            loadOrder = new ModLoadOrderData();
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return true;

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return true;

                return TryParseModLoadOrder(json, out loadOrder);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not parse {ModLoadOrderFileName}: {ex.Message}");
                loadOrder = new ModLoadOrderData();
                return false;
            }
        }

        private static bool TryParseModLoadOrder(string json, out ModLoadOrderData loadOrder)
        {
            loadOrder = new ModLoadOrderData();
            if (loadOrder.Mods == null)
                loadOrder.Mods = new List<ModLoadOrderData.ModEntry>();

            if (string.IsNullOrWhiteSpace(json))
                return true;
            if (!TryGetJsonArrayBody(json, "Mods", out string modsBody))
                return false;

            foreach (Match match in Regex.Matches(modsBody, @"\{[^{}]*\}"))
            {
                string entryJson = match.Value;
                TryReadJsonStringProperty(entryJson, "Name", out string name);
                bool isEnabled = true;
                TryReadJsonBoolProperty(entryJson, "IsEnabled", out isEnabled);

                loadOrder.Mods.Add(new ModLoadOrderData.ModEntry
                {
                    Name = name ?? "",
                    IsEnabled = isEnabled
                });
            }

            return true;
        }

        private static string SerializeModLoadOrder(ModLoadOrderData loadOrder)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"Mods\": [");
            var mods = loadOrder?.Mods ?? new List<ModLoadOrderData.ModEntry>();
            for (int i = 0; i < mods.Count; i++)
            {
                var mod = mods[i] ?? new ModLoadOrderData.ModEntry();
                sb.AppendLine("    {");
                sb.Append("      \"Name\": ");
                AppendJsonString(sb, mod.Name ?? "");
                sb.AppendLine(",");
                sb.Append("      \"IsEnabled\": ");
                sb.AppendLine(mod.IsEnabled ? "true" : "false");
                sb.Append("    }");
                if (i < mods.Count - 1)
                    sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static bool TryGetJsonArrayBody(string json, string propertyName, out string body)
        {
            body = null;
            var match = Regex.Match(json ?? "", "\"" + Regex.Escape(propertyName) + "\"\\s*:");
            if (!match.Success)
                return false;

            int start = json.IndexOf('[', match.Index + match.Length);
            if (start < 0)
                return false;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }
                if (c == '[')
                {
                    depth++;
                    continue;
                }
                if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        body = json.Substring(start + 1, i - start - 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadJsonStringProperty(string json, string propertyName, out string value)
        {
            value = null;
            var match = Regex.Match(
                json ?? "",
                "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!match.Success)
                return false;

            value = UnescapeJsonString(match.Groups[1].Value);
            return true;
        }

        private static bool TryReadJsonBoolProperty(string json, string propertyName, out bool value)
        {
            value = false;
            var match = Regex.Match(
                json ?? "",
                "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*(true|false)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            value = string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string UnescapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
                return value ?? "";

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char escaped = value[++i];
                switch (escaped)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < value.Length
                            && int.TryParse(
                                value.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out int codePoint))
                        {
                            sb.Append((char)codePoint);
                            i += 4;
                        }
                        break;
                    default:
                        sb.Append(escaped);
                        break;
                }
            }

            return sb.ToString();
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
            internal string SourcePath;
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

        private sealed class ExternalModDisablePlan
        {
            public HashSet<string> DestinationFolders { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> ModNames { get; } = new HashSet<string>(StringComparer.Ordinal);
            public int EntriesChanged;
        }

        internal sealed class ExternalModSource
        {
            public string SourceRoot;
            public string DestinationFolder;
            public string ModName;
        }

        // Plain DTO mirroring the on-disk ModLoadOrder.json shape. We parse and
        // serialize that file ourselves (regex + StringBuilder) and never hand
        // the object to the game, so we deliberately avoid the game's
        // Assembly-CSharp ModLoadOrder type — that keeps this logic loadable and
        // testable outside a running game.
        private sealed class ModLoadOrderData
        {
            public List<ModEntry> Mods = new List<ModEntry>();

            public sealed class ModEntry
            {
                public string Name;
                public bool IsEnabled;
            }
        }

        private static string GetModsRoot()
        {
            string gameRoot = GetGameRoot();
            return string.IsNullOrWhiteSpace(gameRoot) ? null : Path.Combine(gameRoot, "Mods");
        }

        private static string ModLoadOrderPath
        {
            get
            {
                string gameRoot = GetGameRoot();
                return string.IsNullOrWhiteSpace(gameRoot)
                    ? null
                    : Path.Combine(gameRoot, ModLoadOrderFileName);
            }
        }

        private static string GetGameRoot()
        {
            try
            {
                return GameRootResolver?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveGameRootPath()
        {
            try
            {
                string gameRoot = Paths.GameRootPath;
                if (!string.IsNullOrWhiteSpace(gameRoot))
                    return gameRoot;
            }
            catch
            {
            }

            try
            {
                string cwd = Environment.CurrentDirectory;
                return string.IsNullOrWhiteSpace(cwd) ? null : cwd;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ExternalModSource> ResolveLoadedGameModSources(string modsRootFull)
        {
            var result = new List<ExternalModSource>();
            try
            {
                foreach (var mod in GameData.Mods)
                {
                    if (mod == null || mod.Paths == null || mod.Definition == null)
                        continue;
                    if (!mod.IsEnabled)
                        continue;

                    string sourceRoot = mod.Paths.BasePath;
                    if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
                        continue;
                    if (!string.IsNullOrWhiteSpace(modsRootFull) && IsSameOrUnder(sourceRoot, modsRootFull))
                        continue;

                    string destinationFolder = GetDestinationFolderForLoadedMod(mod);
                    if (string.IsNullOrWhiteSpace(destinationFolder))
                        continue;

                    result.Add(new ExternalModSource
                    {
                        SourceRoot = sourceRoot,
                        DestinationFolder = destinationFolder,
                        ModName = mod.Definition.Name
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Loaded Workshop mod enumeration failed: {ex.Message}");
            }

            return result;
        }

        private static string GetDestinationFolderForLoadedMod(GameMod mod)
        {
            if (mod.Definition.Id != 0)
                return mod.Definition.Id.ToString();

            string name = mod.Definition.Name;
            if (string.IsNullOrWhiteSpace(name) && mod.Paths != null)
                name = new DirectoryInfo(mod.Paths.BasePath).Name;
            return SanitizeFolderName(name);
        }

        private static string SanitizeFolderName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (char c in value.Trim())
            {
                if (invalid.Contains(c) || c == '/' || c == '\\' || c == ':')
                    sb.Append('_');
                else
                    sb.Append(c);
            }

            string result = sb.ToString().Trim('.', ' ');
            if (result.Length > 96)
                result = result.Substring(0, 96).Trim('.', ' ');
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static bool IsSafePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (value == "." || value == "..")
                return false;
            if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
            return value.IndexOf('/') < 0 && value.IndexOf('\\') < 0 && value.IndexOf(':') < 0;
        }

        private static bool IsModLoadOrderPath(string path)
        {
            return string.Equals(NormalizeManifestPath(path), ModLoadOrderFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameOrUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
                return false;

            string fullPath = Path.GetFullPath(path);
            string fullRoot = AppendDirectorySeparator(Path.GetFullPath(root));
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}

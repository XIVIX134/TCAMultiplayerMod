using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using TCAMultiplayer;

namespace TCAMultiplayer.ModCompatibility
{
    /// <summary>
    /// Represents a single BepInEx plugin/mod
    /// </summary>
    [Serializable]
    public class ModInfo
    {
        public string Guid;
        public string Name;
        public string Version;
        public string AssemblyHash; // MD5 hash of assembly for integrity check
        
        public bool IsValid => !string.IsNullOrEmpty(Guid);
    }

    /// <summary>
    /// Represents a TCA game mod (from Mods/ folder with Mod.json)
    /// </summary>
    [Serializable]
    public class GameModInfo
    {
        public string Name;
        public string DisplayName;
        public long Id; // Steam workshop ID
        public bool IsEnabled;
        public string FolderHash; // Hash of mod folder contents for integrity
    }

    /// <summary>
    /// Represents custom game content (aircraft, weapons, bullets)
    /// </summary>
    [Serializable]
    public class ContentInfo
    {
        public string Type; // "Aircraft", "Gun", "Bullet", "Store", "Missile"
        public string Name;
        public string Hash; // Simple hash of key properties
    }

    /// <summary>
    /// Complete mod manifest for a player - sent during connection handshake
    /// </summary>
    [Serializable]
    public class ModManifest
    {
        public string PlayerName;
        public string GameVersion;
        public string ModVersion;
        public List<ModInfo> LoadedPlugins;     // BepInEx plugins
        public List<GameModInfo> GameMods;       // TCA game mods (Mods/ folder)
        public List<ContentInfo> CustomContent;  // All loaded game content (aircraft, weapons, etc.)
        public long Timestamp;

        public ModManifest()
        {
            LoadedPlugins = new List<ModInfo>();
            GameMods = new List<GameModInfo>();
            CustomContent = new List<ContentInfo>();
            Timestamp = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Serialize to byte array for network transmission
        /// </summary>
        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(PlayerName ?? "");
                writer.Write(GameVersion ?? "");
                writer.Write(ModVersion ?? "");
                writer.Write(Timestamp);
                
                // Write BepInEx plugins
                writer.Write(LoadedPlugins?.Count ?? 0);
                if (LoadedPlugins != null)
                {
                    foreach (var mod in LoadedPlugins)
                    {
                        writer.Write(mod.Guid ?? "");
                        writer.Write(mod.Name ?? "");
                        writer.Write(mod.Version ?? "");
                        writer.Write(mod.AssemblyHash ?? "");
                    }
                }

                // Write game mods
                writer.Write(GameMods?.Count ?? 0);
                if (GameMods != null)
                {
                    foreach (var gmod in GameMods)
                    {
                        writer.Write(gmod.Name ?? "");
                        writer.Write(gmod.DisplayName ?? "");
                        writer.Write(gmod.Id);
                        writer.Write(gmod.IsEnabled);
                        writer.Write(gmod.FolderHash ?? "");
                    }
                }
                
                // Write content
                writer.Write(CustomContent?.Count ?? 0);
                if (CustomContent != null)
                {
                    foreach (var content in CustomContent)
                    {
                        writer.Write(content.Type ?? "");
                        writer.Write(content.Name ?? "");
                        writer.Write(content.Hash ?? "");
                    }
                }
                
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserialize from byte array
        /// </summary>
        public static ModManifest Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var manifest = new ModManifest
                {
                    PlayerName = reader.ReadString(),
                    GameVersion = reader.ReadString(),
                    ModVersion = reader.ReadString(),
                    Timestamp = reader.ReadInt64()
                };
                
                // Read BepInEx plugins
                int pluginCount = reader.ReadInt32();
                manifest.LoadedPlugins = new List<ModInfo>();
                for (int i = 0; i < pluginCount; i++)
                {
                    manifest.LoadedPlugins.Add(new ModInfo
                    {
                        Guid = reader.ReadString(),
                        Name = reader.ReadString(),
                        Version = reader.ReadString(),
                        AssemblyHash = reader.ReadString()
                    });
                }

                // Read game mods
                int gameModCount = reader.ReadInt32();
                manifest.GameMods = new List<GameModInfo>();
                for (int i = 0; i < gameModCount; i++)
                {
                    manifest.GameMods.Add(new GameModInfo
                    {
                        Name = reader.ReadString(),
                        DisplayName = reader.ReadString(),
                        Id = reader.ReadInt64(),
                        IsEnabled = reader.ReadBoolean(),
                        FolderHash = reader.ReadString()
                    });
                }
                
                // Read content
                int contentCount = reader.ReadInt32();
                manifest.CustomContent = new List<ContentInfo>();
                for (int i = 0; i < contentCount; i++)
                {
                    manifest.CustomContent.Add(new ContentInfo
                    {
                        Type = reader.ReadString(),
                        Name = reader.ReadString(),
                        Hash = reader.ReadString()
                    });
                }
                
                return manifest;
            }
        }

        /// <summary>
        /// Calculate a hash of the entire manifest for quick comparison.
        /// Includes plugins, game mods, and content.
        /// </summary>
        public string CalculateHash()
        {
            var sb = new StringBuilder();

            // Hash BepInEx plugins (sorted for deterministic comparison)
            foreach (var mod in LoadedPlugins.OrderBy(m => m.Guid))
            {
                sb.Append($"P:{mod.Guid}:{mod.Version};");
            }

            // Hash game mods (sorted by name, enabled only)
            foreach (var gmod in GameMods.Where(g => g.IsEnabled).OrderBy(g => g.Name))
            {
                sb.Append($"G:{gmod.Name}:{gmod.Id}:{gmod.FolderHash};");
            }

            // Hash content (sorted by type+name)
            foreach (var content in CustomContent.OrderBy(c => c.Type + c.Name))
            {
                sb.Append($"C:{content.Type}:{content.Name}:{content.Hash};");
            }
            
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
            }
        }
    }

    /// <summary>
    /// Result of a mod compatibility check between host and client
    /// </summary>
    public class CompatibilityResult
    {
        public bool IsCompatible;
        public List<string> MissingPlugins;    // BepInEx plugins host has that client doesn't
        public List<string> ExtraPlugins;      // BepInEx plugins client has that host doesn't
        public List<string> VersionMismatches; // Plugins with different versions
        public List<string> MissingGameMods;   // TCA game mods host has that client doesn't
        public List<string> ExtraGameMods;     // TCA game mods client has that host doesn't
        public List<string> MissingContent;    // Content host has that client doesn't
        public List<string> ExtraContent;      // Content client has that host doesn't
        public List<string> Warnings;
        public string HostHash;
        public string ClientHash;

        public CompatibilityResult()
        {
            MissingPlugins = new List<string>();
            ExtraPlugins = new List<string>();
            VersionMismatches = new List<string>();
            MissingGameMods = new List<string>();
            ExtraGameMods = new List<string>();
            MissingContent = new List<string>();
            ExtraContent = new List<string>();
            Warnings = new List<string>();
        }

        public string GetSummary()
        {
            if (IsCompatible)
                return "All mods compatible!";

            var parts = new List<string>();
            if (MissingPlugins.Count > 0)
                parts.Add($"Missing {MissingPlugins.Count} plugin(s)");
            if (MissingGameMods.Count > 0)
                parts.Add($"Missing mod(s): {string.Join(", ", MissingGameMods)}");
            if (ExtraGameMods.Count > 0)
                parts.Add($"Extra mod(s): {string.Join(", ", ExtraGameMods)}");
            if (VersionMismatches.Count > 0)
                parts.Add($"{VersionMismatches.Count} version mismatch(es)");
            if (MissingContent.Count > 0)
                parts.Add($"Missing {MissingContent.Count} content item(s)");

            return string.Join(" | ", parts);
        }

        /// <summary>
        /// Get a detailed multi-line report for UI display
        /// </summary>
        public string GetDetailedReport()
        {
            if (IsCompatible) return "Mods are compatible!";

            var sb = new StringBuilder();
            sb.AppendLine("MOD COMPATIBILITY FAILED:");

            if (MissingGameMods.Count > 0)
            {
                sb.AppendLine("Missing Game Mods (you need these):");
                foreach (var m in MissingGameMods) sb.AppendLine($"  - {m}");
            }
            if (ExtraGameMods.Count > 0)
            {
                sb.AppendLine("Extra Game Mods (host doesn't have):");
                foreach (var m in ExtraGameMods) sb.AppendLine($"  - {m}");
            }
            if (MissingPlugins.Count > 0)
            {
                sb.AppendLine("Missing Plugins:");
                foreach (var m in MissingPlugins) sb.AppendLine($"  - {m}");
            }
            if (VersionMismatches.Count > 0)
            {
                sb.AppendLine("Version Mismatches:");
                foreach (var m in VersionMismatches) sb.AppendLine($"  - {m}");
            }
            if (MissingContent.Count > 0)
            {
                sb.AppendLine($"Missing Content: {MissingContent.Count} item(s)");
                foreach (var m in MissingContent.Take(10)) sb.AppendLine($"  - {m}");
                if (MissingContent.Count > 10) sb.AppendLine($"  ... and {MissingContent.Count - 10} more");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Collects and manages mod information for multiplayer compatibility.
    /// Collects: BepInEx plugins, TCA game mods (Mods/ folder), and all game content.
    /// </summary>
    public static class ModManifestCollector
    {
        private static bool _initialized = false;
        private static ModManifest _cachedManifest = null;
        private static DateTime _lastCacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheValidity = TimeSpan.FromSeconds(30);

        // TCA game root (parent of Mods/ folder)
        private static string _gameRootPath;
        private static string _modsPath;

        /// <summary>
        /// Initialize the collector - must be called once at startup
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _gameRootPath = Path.GetDirectoryName(Application.dataPath);
                _modsPath = Path.Combine(_gameRootPath, "Mods");
                Plugin.Log?.LogInfo($"[ModManifestCollector] Initialized. Game root: {_gameRootPath}");
                Plugin.Log?.LogInfo($"[ModManifestCollector] Mods path: {_modsPath} (exists: {Directory.Exists(_modsPath)})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ModManifestCollector] Init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Collect the current mod manifest (cached for 30s)
        /// </summary>
        public static ModManifest CollectManifest(bool forceRefresh = false)
        {
            if (!_initialized) Initialize();

            // Use cached version if valid
            if (!forceRefresh && _cachedManifest != null && 
                DateTime.Now - _lastCacheTime < CacheValidity)
            {
                return _cachedManifest;
            }

            var manifest = new ModManifest
            {
                PlayerName = Plugin.Instance?.Lobby?.LocalPlayerName ?? "Unknown",
                GameVersion = Application.version,
                ModVersion = PluginInfo.VERSION
            };

            // 1. Collect BepInEx plugins
            CollectBepInExPlugins(manifest);

            // 2. Collect TCA game mods from Mods/ folder
            CollectGameMods(manifest);

            // 3. Collect all loaded game content (aircraft, weapons, etc.)
            CollectAllGameContent(manifest);

            // Cache it
            _cachedManifest = manifest;
            _lastCacheTime = DateTime.Now;

            Plugin.Log?.LogInfo($"[ModManifestCollector] Manifest: {manifest.LoadedPlugins.Count} plugins, {manifest.GameMods.Count} game mods, {manifest.CustomContent.Count} content items");

            return manifest;
        }

        /// <summary>
        /// Force refresh the cached manifest
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedManifest = null;
            _lastCacheTime = DateTime.MinValue;
        }

        #region BepInEx Plugin Collection

        /// <summary>
        /// Collect all loaded BepInEx plugins via Chainloader.PluginInfos (BepInEx 5 API)
        /// </summary>
        private static void CollectBepInExPlugins(ModManifest manifest)
        {
            try
            {
                // BepInEx 5: Chainloader.PluginInfos is a static Dictionary<string, PluginInfo>
                var pluginInfos = Chainloader.PluginInfos;
                if (pluginInfos == null)
                {
                    Plugin.Log?.LogWarning("[ModManifestCollector] Chainloader.PluginInfos is null");
                    return;
                }

                foreach (var kvp in pluginInfos)
                {
                    try
                    {
                        var pluginInfo = kvp.Value;
                        if (pluginInfo?.Metadata == null) continue;

                        var modInfo = new ModInfo
                        {
                            Guid = pluginInfo.Metadata.GUID ?? "unknown",
                            Name = pluginInfo.Metadata.Name ?? "Unknown",
                            Version = pluginInfo.Metadata.Version?.ToString() ?? "0.0.0"
                        };

                        // Calculate assembly hash for integrity check
                        if (pluginInfo.Instance != null)
                        {
                            modInfo.AssemblyHash = CalculateAssemblyHash(pluginInfo.Instance.GetType().Assembly);
                        }

                        if (modInfo.IsValid)
                        {
                            manifest.LoadedPlugins.Add(modInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[ModManifestCollector] Error processing plugin '{kvp.Key}': {ex.Message}");
                    }
                }

                Plugin.Log?.LogInfo($"[ModManifestCollector] Found {manifest.LoadedPlugins.Count} BepInEx plugins");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ModManifestCollector] Error collecting BepInEx plugins: {ex.Message}");
            }
        }

        #endregion

        #region TCA Game Mod Collection

        /// <summary>
        /// Collect TCA game mods from the Mods/ folder.
        /// Reads Mod.json from each mod folder and ModLoadOrder.json for enable state.
        /// </summary>
        private static void CollectGameMods(ModManifest manifest)
        {
            try
            {
                if (string.IsNullOrEmpty(_modsPath) || !Directory.Exists(_modsPath))
                {
                    Plugin.Log?.LogInfo("[ModManifestCollector] No Mods folder found");
                    return;
                }

                // Read ModLoadOrder.json for enable state
                var enabledMods = ReadModLoadOrder();

                // Scan each subfolder for Mod.json
                var modDirs = Directory.GetDirectories(_modsPath);
                foreach (var modDir in modDirs)
                {
                    try
                    {
                        var modJsonPath = Path.Combine(modDir, "Mod.json");
                        if (!File.Exists(modJsonPath)) continue;

                        var jsonContent = File.ReadAllText(modJsonPath);
                        var modData = ParseModJson(jsonContent);
                        if (modData == null) continue;

                        // Check if enabled in ModLoadOrder
                        bool isEnabled = true;
                        if (enabledMods != null && enabledMods.TryGetValue(modData.Name, out bool enabled))
                        {
                            isEnabled = enabled;
                        }

                        var gameModInfo = new GameModInfo
                        {
                            Name = modData.Name,
                            DisplayName = modData.DisplayName ?? modData.Name,
                            Id = modData.Id,
                            IsEnabled = isEnabled,
                            FolderHash = CalculateModFolderHash(modDir)
                        };

                        manifest.GameMods.Add(gameModInfo);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[ModManifestCollector] Error reading mod in {Path.GetFileName(modDir)}: {ex.Message}");
                    }
                }

                Plugin.Log?.LogInfo($"[ModManifestCollector] Found {manifest.GameMods.Count} TCA game mods ({manifest.GameMods.Count(m => m.IsEnabled)} enabled)");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ModManifestCollector] Error collecting game mods: {ex.Message}");
            }
        }

        /// <summary>
        /// Simple JSON parser for Mod.json (avoids dependency on JSON library)
        /// </summary>
        private static ModJsonData ParseModJson(string json)
        {
            try
            {
                var data = new ModJsonData();
                data.Name = ExtractJsonString(json, "Name");
                data.DisplayName = ExtractJsonString(json, "DisplayName");
                
                // Id is a number without quotes in JSON
                var idMatch = System.Text.RegularExpressions.Regex.Match(json, @"""Id""\s*:\s*(\d+)");
                if (idMatch.Success) long.TryParse(idMatch.Groups[1].Value, out data.Id);

                return string.IsNullOrEmpty(data.Name) ? null : data;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\"\\s*:\\s*\"([^\"]+)\"";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        private class ModJsonData
        {
            public string Name;
            public string DisplayName;
            public long Id;
        }

        /// <summary>
        /// Read ModLoadOrder.json to determine which mods are enabled
        /// </summary>
        private static Dictionary<string, bool> ReadModLoadOrder()
        {
            try
            {
                var loadOrderPath = Path.Combine(_gameRootPath, "ModLoadOrder.json");
                if (!File.Exists(loadOrderPath)) return null;

                var json = File.ReadAllText(loadOrderPath);
                var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                // Parse the Mods array entries
                var nameMatches = System.Text.RegularExpressions.Regex.Matches(json, 
                    @"""Name""\s*:\s*""([^""]+)""\s*,\s*""IsEnabled""\s*:\s*(true|false)", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in nameMatches)
                {
                    string name = match.Groups[1].Value;
                    bool isEnabled = match.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    result[name] = isEnabled;
                }

                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ModManifestCollector] Error reading ModLoadOrder.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate a lightweight hash of a mod folder (based on file list + sizes)
        /// </summary>
        private static string CalculateModFolderHash(string modDir)
        {
            try
            {
                var sb = new StringBuilder();
                
                var files = Directory.GetFiles(modDir, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f)
                    .ToArray();

                sb.Append($"files:{files.Length};");
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    string relativePath = file.Substring(modDir.Length);
                    sb.Append($"{relativePath}:{info.Length};");
                }

                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
                }
            }
            catch
            {
                return "error";
            }
        }

        #endregion

        #region Game Content Collection

        /// <summary>
        /// Content categories to scan on disk. Maps subdirectory path → content type label.
        /// </summary>
        private static readonly (string SubDir, string ContentType)[] _contentDirectories = new[]
        {
            ("Aircraft2", "Aircraft"),
            ("Loadouts", "Loadout"),
            ("Stores/Missiles", "Missile"),
            ("Stores/Bomb", "Bomb"),
            ("Stores/FuelTank", "FuelTank"),
            ("Weapons/Guns", "Gun"),
            ("Weapons/Bullets", "Bullet"),
            ("Database", "Database"),
        };

        /// <summary>
        /// Collect ALL game content by scanning data files on disk.
        /// Scans both vanilla (StreamingAssets/Data/) and mod (Mods/*/Data/) directories.
        /// This is more reliable than runtime reflection since it works regardless of game state.
        /// </summary>
        private static void CollectAllGameContent(ModManifest manifest)
        {
            try
            {
                // 1. Scan vanilla data from StreamingAssets
                var vanillaDataPath = Path.Combine(_gameRootPath, "Arena_Data", "StreamingAssets", "Data");
                if (Directory.Exists(vanillaDataPath))
                {
                    ScanDataDirectory(manifest, vanillaDataPath, "vanilla");
                }
                else
                {
                    Plugin.Log?.LogWarning($"[ModManifestCollector] Vanilla data path not found: {vanillaDataPath}");
                }

                // 2. Also collect top-level data files (Flyables.json, BulletConstants.json, etc.)
                CollectTopLevelDataFiles(manifest, vanillaDataPath, "vanilla");

                // 3. Scan each mod's Data/ directory
                if (Directory.Exists(_modsPath))
                {
                    foreach (var modDir in Directory.GetDirectories(_modsPath))
                    {
                        var modDataPath = Path.Combine(modDir, "Data");
                        if (!Directory.Exists(modDataPath)) continue;

                        string modName = Path.GetFileName(modDir);
                        ScanDataDirectory(manifest, modDataPath, modName);
                        CollectTopLevelDataFiles(manifest, modDataPath, modName);
                    }
                }

                Plugin.Log?.LogInfo($"[ModManifestCollector] Content scan complete: {manifest.CustomContent.Count} data files found");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ModManifestCollector] Error collecting game content: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan known content subdirectories within a data root path
        /// </summary>
        private static void ScanDataDirectory(ModManifest manifest, string dataRoot, string source)
        {
            foreach (var (subDir, contentType) in _contentDirectories)
            {
                var fullPath = Path.Combine(dataRoot, subDir);
                if (!Directory.Exists(fullPath)) continue;

                try
                {
                    var jsonFiles = Directory.GetFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly);
                    foreach (var file in jsonFiles.OrderBy(f => f))
                    {
                        try
                        {
                            string fileName = Path.GetFileNameWithoutExtension(file);
                            string hash = CalculateFileHash(file);

                            manifest.CustomContent.Add(new ContentInfo
                            {
                                Type = contentType,
                                Name = $"{source}/{fileName}",
                                Hash = hash
                            });
                        }
                        catch { /* skip individual files that fail */ }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[ModManifestCollector] Error scanning {source}/{subDir}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Collect top-level data files (Flyables.json, RWRCodes.json, MapList.json, BulletConstants.json)
        /// </summary>
        private static void CollectTopLevelDataFiles(ModManifest manifest, string dataRoot, string source)
        {
            if (!Directory.Exists(dataRoot)) return;

            try
            {
                var topLevelFiles = Directory.GetFiles(dataRoot, "*.json", SearchOption.TopDirectoryOnly);
                foreach (var file in topLevelFiles.OrderBy(f => f))
                {
                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        string hash = CalculateFileHash(file);

                        manifest.CustomContent.Add(new ContentInfo
                        {
                            Type = "DataFile",
                            Name = $"{source}/{fileName}",
                            Hash = hash
                        });
                    }
                    catch { /* skip individual files that fail */ }
                }
            }
            catch { /* skip if top-level scan fails */ }
        }

        /// <summary>
        /// Calculate MD5 hash of a file's contents (first 8 hex chars)
        /// </summary>
        private static string CalculateFileHash(string filePath)
        {
            try
            {
                var fileBytes = File.ReadAllBytes(filePath);
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(fileBytes);
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
                }
            }
            catch
            {
                return "error";
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Calculate MD5 hash of an assembly file
        /// </summary>
        private static string CalculateAssemblyHash(Assembly assembly)
        {
            try
            {
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location)) return "unknown";

                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(location))
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
                }
            }
            catch
            {
                return "error";
            }
        }

        #endregion

        #region Compatibility Checking

        /// <summary>
        /// Compare two manifests for compatibility.
        /// Both players need the same enabled game mods and the same game content.
        /// BepInEx plugin differences are warnings only (except for TCAMultiplayer itself).
        /// </summary>
        public static CompatibilityResult CheckCompatibility(ModManifest host, ModManifest client)
        {
            var result = new CompatibilityResult
            {
                HostHash = host.CalculateHash(),
                ClientHash = client.CalculateHash()
            };

            // Quick hash comparison
            if (result.HostHash == result.ClientHash)
            {
                result.IsCompatible = true;
                return result;
            }

            // --- Check TCA Game Mods (STRICT - must match) ---
            var hostGameMods = host.GameMods.Where(m => m.IsEnabled)
                .ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);
            var clientGameMods = client.GameMods.Where(m => m.IsEnabled)
                .ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

            foreach (var hostMod in hostGameMods)
            {
                if (!clientGameMods.ContainsKey(hostMod.Key))
                {
                    result.MissingGameMods.Add($"{hostMod.Value.DisplayName} (ID: {hostMod.Value.Id})");
                }
            }
            foreach (var clientMod in clientGameMods)
            {
                if (!hostGameMods.ContainsKey(clientMod.Key))
                {
                    result.ExtraGameMods.Add($"{clientMod.Value.DisplayName} (ID: {clientMod.Value.Id})");
                }
            }

            // --- Check BepInEx Plugins (strict only for TCAMultiplayer, warn for others) ---
            var hostPlugins = host.LoadedPlugins.ToDictionary(m => m.Guid, m => m);
            var clientPlugins = client.LoadedPlugins.ToDictionary(m => m.Guid, m => m);

            foreach (var hostPlugin in host.LoadedPlugins)
            {
                if (!clientPlugins.ContainsKey(hostPlugin.Guid))
                {
                    if (hostPlugin.Guid == PluginInfo.GUID)
                        result.MissingPlugins.Add($"{hostPlugin.Name} v{hostPlugin.Version} (REQUIRED)");
                    else
                        result.Warnings.Add($"Host has plugin: {hostPlugin.Name} v{hostPlugin.Version}");
                }
                else if (hostPlugin.Guid == PluginInfo.GUID)
                {
                    var clientVersion = clientPlugins[hostPlugin.Guid].Version;
                    if (clientVersion != hostPlugin.Version)
                    {
                        result.VersionMismatches.Add($"TCAMultiplayer: Host={hostPlugin.Version}, Client={clientVersion}");
                    }
                }
            }

            foreach (var clientPlugin in client.LoadedPlugins)
            {
                if (!hostPlugins.ContainsKey(clientPlugin.Guid))
                {
                    result.Warnings.Add($"Client has plugin: {clientPlugin.Name} v{clientPlugin.Version}");
                }
            }

            // --- Check Game Content (STRICT - must match for gameplay) ---
            var hostContent = host.CustomContent.ToDictionary(c => c.Type + ":" + c.Name, c => c);
            var clientContent = client.CustomContent.ToDictionary(c => c.Type + ":" + c.Name, c => c);

            foreach (var hostItem in host.CustomContent)
            {
                var key = hostItem.Type + ":" + hostItem.Name;
                if (!clientContent.ContainsKey(key))
                {
                    result.MissingContent.Add($"{hostItem.Type}: {hostItem.Name}");
                }
                else if (clientContent[key].Hash != hostItem.Hash)
                {
                    result.Warnings.Add($"{hostItem.Type} '{hostItem.Name}' different properties");
                }
            }

            foreach (var clientItem in client.CustomContent)
            {
                var key = clientItem.Type + ":" + clientItem.Name;
                if (!hostContent.ContainsKey(key))
                {
                    result.ExtraContent.Add($"{clientItem.Type}: {clientItem.Name}");
                }
            }

            // --- Determine overall compatibility ---
            result.IsCompatible = result.MissingGameMods.Count == 0 && 
                                  result.ExtraGameMods.Count == 0 &&
                                  result.MissingPlugins.Count == 0 &&
                                  result.VersionMismatches.Count == 0 &&
                                  result.MissingContent.Count == 0;

            return result;
        }

        #endregion
    }
}

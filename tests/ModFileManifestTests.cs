using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using TCAMultiplayer.Compatibility;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class ModFileManifestTests
    {
        private string _tempRoot;

        [OneTimeSetUp]
        public void SetUpLogging()
        {
            if (!Log.IsInitialized)
                Log.Init(new ManualLogSource("Test"));
        }

        [TearDown]
        public void TearDown()
        {
            ModFileManifest.ResetResolversForTests();

            if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        [TestCase("3433472232/Data/Database/SAMs.json")]
        [TestCase("3433472232/assetlist.txt")]
        [TestCase("3433472232/assetsTiny Surface Combatants")]
        [TestCase("3433449845/Data/Stores/Missiles/PACT/USSR/ALKALI/dont bother punching out.mp3")]
        [TestCase("ModLoadOrder.json")]
        public void IsAllowedSyncPath_AllowsNormalModFiles(string path)
        {
            Assert.IsTrue(ModFileManifest.IsAllowedSyncPath(path, 1024));
        }

        [TestCase("3433472232/plugin.dll")]
        [TestCase("3433472232/install.ps1")]
        [TestCase("3433472232/run.bat")]
        [TestCase("3433472232/tool.exe")]
        public void IsAllowedSyncPath_BlocksExecutableFiles(string path)
        {
            Assert.IsFalse(ModFileManifest.IsAllowedSyncPath(path, 1024));
        }

        [Test]
        public void CompareTo_MissingAssetPayload_IsSyncable()
        {
            var client = new ModFileManifest();
            var host = new ModFileManifest();
            host.Files.Add(new ModFileManifest.ModFileEntry
            {
                Path = "3433472232/assetsTiny Surface Combatants",
                Sha256 = "abc",
                Size = 2628401,
                SyncAllowed = true
            });

            var diff = client.CompareTo(host);

            Assert.AreEqual(1, diff.Missing.Count);
            Assert.AreEqual(0, diff.Unsyncable.Count);
        }

        [Test]
        public void CompareTo_MissingBlockedDll_IsUnsyncable()
        {
            var client = new ModFileManifest();
            var host = new ModFileManifest();
            host.Files.Add(new ModFileManifest.ModFileEntry
            {
                Path = "3433472232/plugin.dll",
                Sha256 = "abc",
                Size = 1024,
                SyncAllowed = false
            });

            var diff = client.CompareTo(host);

            Assert.AreEqual(1, diff.Missing.Count);
            Assert.AreEqual(1, diff.Unsyncable.Count);
        }

        [Test]
        public void CompareTo_ChangedModLoadOrder_IsSyncableMismatch()
        {
            var client = new ModFileManifest();
            var host = new ModFileManifest();
            client.Files.Add(new ModFileManifest.ModFileEntry
            {
                Path = "ModLoadOrder.json",
                Sha256 = "client",
                Size = 32,
                SyncAllowed = true
            });
            host.Files.Add(new ModFileManifest.ModFileEntry
            {
                Path = "ModLoadOrder.json",
                Sha256 = "host",
                Size = 32,
                SyncAllowed = true
            });

            var diff = client.CompareTo(host);

            Assert.AreEqual(1, diff.Changed.Count);
            Assert.AreEqual(0, diff.Unsyncable.Count);
        }

        [Test]
        public void CompareTo_ModLoadOrderIgnoresDisabledLocalExtras()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "TCAMP_ModSync_" + Guid.NewGuid().ToString("N"));
            string hostRoot = Path.Combine(_tempRoot, "Host");
            string clientRoot = Path.Combine(_tempRoot, "Client");
            Directory.CreateDirectory(Path.Combine(hostRoot, "Mods"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "Mods"));

            File.WriteAllText(Path.Combine(hostRoot, "ModLoadOrder.json"),
                "{\"Mods\":[{\"Name\":\"Host Mod\",\"IsEnabled\":true}]}");
            File.WriteAllText(Path.Combine(clientRoot, "ModLoadOrder.json"),
                "{\"Mods\":[{\"Name\":\"Host Mod\",\"IsEnabled\":true},{\"Name\":\"Extra Workshop\",\"IsEnabled\":false}]}");

            ModFileManifest.ExternalModSourceResolver = _ => Enumerable.Empty<ModFileManifest.ExternalModSource>();
            ModFileManifest.GameRootResolver = () => hostRoot;
            var hostManifest = ModFileManifest.Collect();

            ModFileManifest.GameRootResolver = () => clientRoot;
            var clientManifest = ModFileManifest.Collect();

            var diff = clientManifest.CompareTo(hostManifest);

            Assert.IsTrue(diff.IsCompatible, diff.ToSummary());
        }

        [Test]
        public void BuildSyncPackage_CopiesExternalWorkshopSourceAndLoadOrder()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "TCAMP_ModSync_" + Guid.NewGuid().ToString("N"));
            string hostRoot = Path.Combine(_tempRoot, "Host");
            string clientRoot = Path.Combine(_tempRoot, "Client");
            string workshopRoot = Path.Combine(_tempRoot, "SteamWorkshop", "3433472232");
            Directory.CreateDirectory(Path.Combine(hostRoot, "Mods"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "Mods"));
            Directory.CreateDirectory(Path.Combine(workshopRoot, "Data", "Database"));

            string loadOrderJson = "{\n  \"Mods\": [\n    { \"Name\": \"Tiny Surface Combatants\", \"IsEnabled\": true }\n  ]\n}";
            File.WriteAllText(Path.Combine(hostRoot, "ModLoadOrder.json"), loadOrderJson);
            File.WriteAllText(Path.Combine(workshopRoot, "Mod.json"),
                "{ \"Name\": \"Tiny Surface Combatants\", \"Description\": \"Ships\", \"Id\": 3433472232, \"Assets\": [] }");
            File.WriteAllText(Path.Combine(workshopRoot, "Data", "Database", "SAMs.json"), "{ \"Name\": \"SAM\" }");

            ModFileManifest.GameRootResolver = () => hostRoot;
            ModFileManifest.ExternalModSourceResolver = _ => new[]
            {
                new ModFileManifest.ExternalModSource
                {
                    SourceRoot = workshopRoot,
                    DestinationFolder = "3433472232"
                }
            };

            var hostManifest = ModFileManifest.Collect();
            Assert.IsTrue(hostManifest.Files.Any(f => f.Path == "ModLoadOrder.json"));
            Assert.IsTrue(hostManifest.Files.Any(f => f.Path == "3433472232/Mod.json"));
            Assert.IsTrue(hostManifest.Files.Any(f => f.Path == "3433472232/Data/Database/SAMs.json"));

            byte[] package = hostManifest.BuildSyncPackage(new ModFileManifest());

            ModFileManifest.GameRootResolver = () => clientRoot;
            ModFileManifest.ExternalModSourceResolver = _ => Enumerable.Empty<ModFileManifest.ExternalModSource>();

            var result = ModFileManifest.ApplySyncPackage(package, hostManifest.ManifestHash);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(loadOrderJson, File.ReadAllText(Path.Combine(clientRoot, "ModLoadOrder.json")));
            Assert.IsTrue(File.Exists(Path.Combine(clientRoot, "Mods", "3433472232", "Mod.json")));
            Assert.IsTrue(File.Exists(Path.Combine(clientRoot, "Mods", "3433472232", "Data", "Database", "SAMs.json")));
        }

        [Test]
        public void ApplySyncPackage_DisablesExtraExternalWorkshopMod()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "TCAMP_ModSync_" + Guid.NewGuid().ToString("N"));
            string hostRoot = Path.Combine(_tempRoot, "Host");
            string clientRoot = Path.Combine(_tempRoot, "Client");
            string extraWorkshopRoot = Path.Combine(_tempRoot, "SteamWorkshop", "2222222222");
            Directory.CreateDirectory(Path.Combine(hostRoot, "Mods", "1111111111", "Data"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "Mods"));
            Directory.CreateDirectory(Path.Combine(extraWorkshopRoot, "Data"));

            File.WriteAllText(Path.Combine(hostRoot, "ModLoadOrder.json"),
                "{\"Mods\":[{\"Name\":\"Host Mod\",\"IsEnabled\":true}]}");
            File.WriteAllText(Path.Combine(hostRoot, "Mods", "1111111111", "Mod.json"),
                "{ \"Name\": \"Host Mod\", \"Description\": \"Host\", \"Id\": 1111111111, \"Assets\": [] }");
            File.WriteAllText(Path.Combine(hostRoot, "Mods", "1111111111", "Data", "Host.json"), "{}");

            File.WriteAllText(Path.Combine(clientRoot, "ModLoadOrder.json"),
                "{\"Mods\":[{\"Name\":\"Extra Workshop\",\"IsEnabled\":true}]}");
            File.WriteAllText(Path.Combine(extraWorkshopRoot, "Mod.json"),
                "{ \"Name\": \"Extra Workshop\", \"Description\": \"Extra\", \"Id\": 2222222222, \"Assets\": [] }");
            File.WriteAllText(Path.Combine(extraWorkshopRoot, "Data", "Extra.json"), "{}");

            ModFileManifest.GameRootResolver = () => hostRoot;
            ModFileManifest.ExternalModSourceResolver = _ => Enumerable.Empty<ModFileManifest.ExternalModSource>();
            var hostManifest = ModFileManifest.Collect();

            ModFileManifest.GameRootResolver = () => clientRoot;
            ModFileManifest.ExternalModSourceResolver = _ => new[]
            {
                new ModFileManifest.ExternalModSource
                {
                    SourceRoot = extraWorkshopRoot,
                    DestinationFolder = "2222222222",
                    ModName = "Extra Workshop"
                }
            };
            var clientManifest = ModFileManifest.Collect();

            ModFileManifest.GameRootResolver = () => hostRoot;
            ModFileManifest.ExternalModSourceResolver = _ => Enumerable.Empty<ModFileManifest.ExternalModSource>();
            byte[] package = hostManifest.BuildSyncPackage(clientManifest);

            ModFileManifest.GameRootResolver = () => clientRoot;
            ModFileManifest.ExternalModSourceResolver = _ => new[]
            {
                new ModFileManifest.ExternalModSource
                {
                    SourceRoot = extraWorkshopRoot,
                    DestinationFolder = "2222222222",
                    ModName = "Extra Workshop"
                }
            };

            var result = ModFileManifest.ApplySyncPackage(package, hostManifest.ManifestHash);

            Assert.IsTrue(result.Success, result.Message);
            Assert.IsTrue(File.Exists(Path.Combine(clientRoot, "Mods", "1111111111", "Mod.json")));
            string loadOrder = File.ReadAllText(Path.Combine(clientRoot, "ModLoadOrder.json"));
            StringAssert.Contains("Host Mod", loadOrder);
            StringAssert.Contains("Extra Workshop", loadOrder);
            StringAssert.Contains("false", loadOrder);
        }
    }
}

using NUnit.Framework;
using TCAMultiplayer.Compatibility;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class ModFileManifestTests
    {
        [TestCase("3433472232/Data/Database/SAMs.json")]
        [TestCase("3433472232/assetlist.txt")]
        [TestCase("3433472232/assetsTiny Surface Combatants")]
        [TestCase("3433449845/Data/Stores/Missiles/PACT/USSR/ALKALI/dont bother punching out.mp3")]
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
    }
}

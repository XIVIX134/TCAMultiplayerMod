using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using TCAMultiplayer.Updating;

namespace TCAMP.Tests
{
    [TestFixture]
    public class ModUpdaterTests
    {
        [Test]
        public void IsVersionNewer_HandlesVPrefixedTags()
        {
            Assert.IsTrue(ModUpdater.IsVersionNewer("v0.2.3", "0.2.2"));
            Assert.IsFalse(ModUpdater.IsVersionNewer("v0.2.2", "0.2.2"));
            Assert.IsFalse(ModUpdater.IsVersionNewer("v0.2.1", "0.2.2"));
            Assert.Greater(ModUpdater.CompareReleaseVersion("v0.2.3", "0.2.2"), 0);
            Assert.AreEqual(0, ModUpdater.CompareReleaseVersion("v0.2.2", "0.2.2"));
        }

        [Test]
        public void ParseSha256_ReadsReleaseChecksumLine()
        {
            string sha = "4285a1a8b5c0558dd524974bf50009d58297f1270217bdc8952e2a3088d5c30a";
            string line = sha + "  TCAMP-v0.2.2-plugin.zip";

            Assert.AreEqual(sha, ModUpdater.ParseSha256(line));
        }

        [Test]
        public void ParseReleaseJson_MapsTagAndAssetFields()
        {
            string json = @"{
  ""tag_name"": ""v0.2.3"",
  ""assets"": [
    {
      ""name"": ""TCAMP-v0.2.3-plugin.zip"",
      ""browser_download_url"": ""https://example.test/plugin.zip"",
      ""digest"": ""sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa""
    },
    {
      ""name"": ""TCAMP-v0.2.3-plugin.zip.sha256"",
      ""browser_download_url"": ""https://example.test/plugin.zip.sha256""
    }
  ]
}";

            var release = ModUpdater.ParseReleaseJson(json);

            Assert.AreEqual("v0.2.3", release.TagName);
            Assert.AreEqual(2, release.Assets.Count);
            Assert.AreEqual("TCAMP-v0.2.3-plugin.zip", release.Assets[0].Name);
            Assert.AreEqual("https://example.test/plugin.zip", release.Assets[0].DownloadUrl);
        }

        [Test]
        public void TryChooseReleaseAssets_FindsPluginZipAndChecksum()
        {
            var release = ModUpdater.ParseReleaseJson(@"{
  ""tag_name"": ""v0.2.3"",
  ""assets"": [
    { ""name"": ""source.zip"", ""browser_download_url"": ""https://example.test/source.zip"" },
    { ""name"": ""TCAMP-v0.2.3-plugin.zip"", ""browser_download_url"": ""https://example.test/plugin.zip"" },
    { ""name"": ""TCAMP-v0.2.3-plugin.zip.sha256"", ""browser_download_url"": ""https://example.test/plugin.zip.sha256"" }
  ]
}");

            bool ok = ModUpdater.TryChooseReleaseAssets(
                release,
                out var package,
                out var checksum,
                out string error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual("TCAMP-v0.2.3-plugin.zip", package.Name);
            Assert.AreEqual("TCAMP-v0.2.3-plugin.zip.sha256", checksum.Name);
        }

        [Test]
        public void ReadPluginDllFromPackage_ExtractsNestedPluginDll()
        {
            byte[] expected = Encoding.ASCII.GetBytes("fake dll bytes");
            byte[] package;

            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("BepInEx/plugins/TCAMP.dll");
                    using (var stream = entry.Open())
                    {
                        stream.Write(expected, 0, expected.Length);
                    }
                }
                package = ms.ToArray();
            }

            byte[] actual = ModUpdater.ReadPluginDllFromPackage(package);

            CollectionAssert.AreEqual(expected, actual);
        }
    }
}

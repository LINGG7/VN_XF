using System;
using NUnit.Framework;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace VNovelizer.Tests
{
    public class TapPersistentBundleCacheTests
    {
        private static AssetBundleRequestOptions Options(string hash = "aabbcc", uint crc = 17, long size = 1024)
        {
            return new AssetBundleRequestOptions { Hash = hash, Crc = crc, BundleSize = size };
        }

        [Test]
        public void SignedUrlsAndBackupCdnShareTheSameContentCacheEntry()
        {
            var options = Options();
            string key = TapPersistentBundleCache.CreateKey("https://primary.example/v1/WebGL/chapter.bundle?token=first", options);
            Assert.That(TapPersistentBundleCache.CreateKey("https://backup.example/v1/WebGL/chapter.bundle?token=second", options), Is.EqualTo(key));
            Assert.That(key, Does.Match("^[a-f0-9]{64}$"));
        }

        [Test]
        public void ChangedContentHashRequiresANewCacheEntry()
        {
            const string url = "https://cdn.example/v1/WebGL/chapter.bundle";
            Assert.That(TapPersistentBundleCache.CreateKey(url, Options("new-version")),
                Is.Not.EqualTo(TapPersistentBundleCache.CreateKey(url, Options("old-version"))));
        }

        [Test]
        public void ChangedIntegrityMetadataCannotReuseTheOldEntry()
        {
            const string url = "https://cdn.example/v1/WebGL/chapter.bundle";
            string key = TapPersistentBundleCache.CreateKey(url, Options());
            Assert.That(TapPersistentBundleCache.CreateKey(url, Options(crc: 18)), Is.Not.EqualTo(key));
            Assert.That(TapPersistentBundleCache.CreateKey(url, Options(size: 2048)), Is.Not.EqualTo(key));
        }

        [Test]
        public void DifferentBuildPlatformsCannotShareABundleCacheEntry()
        {
            var options = Options();
            Assert.That(TapPersistentBundleCache.CreateKey("https://cdn.example/v1/Android/chapter.bundle", options),
                Is.Not.EqualTo(TapPersistentBundleCache.CreateKey("https://cdn.example/v1/WebGL/chapter.bundle", options)));
        }

        [TestCase("file:///SaveData/save_0.json")]
        [TestCase("../SaveData/save_0.json")]
        public void NonRemoteLocationsCannotProduceResourceCacheEntries(string path)
        {
            Assert.Throws<ArgumentException>(() => TapPersistentBundleCache.CreateKey(path, Options()));
        }
    }
}
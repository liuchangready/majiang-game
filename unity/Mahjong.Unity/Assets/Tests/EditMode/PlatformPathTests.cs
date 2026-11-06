using System;
using System.IO;
using NUnit.Framework;
using Mahjong.Unity.Platform;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class PlatformPathTests
    {
        private string _tempRoot = null!;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "MahjongTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
            {
                try { Directory.Delete(_tempRoot, true); } catch { }
            }
        }

        [Test]
        public void AppPaths_Subdirectories_AreUnderRoot()
        {
            var paths = new UnityAppPaths(_tempRoot);

            Assert.AreEqual(_tempRoot, paths.PersistentRoot);
            StringAssert.StartsWith(_tempRoot, paths.OutboxDirectory);
            StringAssert.StartsWith(_tempRoot, paths.LocalSaveDirectory);
            StringAssert.StartsWith(_tempRoot, paths.LogDirectory);
        }

        [Test]
        public void AppPaths_EnsureDirectoriesCreated_CreatesFolders()
        {
            var paths = new UnityAppPaths(_tempRoot);
            paths.EnsureDirectoriesCreated();

            Assert.IsTrue(Directory.Exists(paths.OutboxDirectory));
            Assert.IsTrue(Directory.Exists(paths.LocalSaveDirectory));
            Assert.IsTrue(Directory.Exists(paths.LogDirectory));
        }

        [Test]
        public void AppPaths_ValidateAndNormalize_RejectsPathTraversal()
        {
            var paths = new UnityAppPaths(_tempRoot);

            // Traversal attempt escaping persistent root
            Assert.Throws<InvalidOperationException>(() =>
            {
                paths.ValidateAndNormalize("../../../etc/passwd");
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                paths.ValidateAndNormalize("C:\\Windows\\System32");
            });

            // Valid subpath
            string valid = paths.ValidateAndNormalize("saves/slot1.dat");
            StringAssert.StartsWith(_tempRoot, valid);
        }
    }
}

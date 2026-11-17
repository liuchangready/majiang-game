using System.Reflection;
using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;

namespace Mahjong.Unity.Tests.EditMode
{
    public sealed class PrototypeStartupLayoutTests
    {
        [Test]
        public void ExplicitLayoutOverridesDefaultAndMissingValueDoesNotLoadDefault()
        {
            MethodInfo method = typeof(MahjongWallLayoutEditor).GetMethod("StartupLayoutPath",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            string Resolve(params string[] args) => (string)method.Invoke(null, new object[] { args, "default.json" });
            Assert.AreEqual("default.json", Resolve("player"));
            Assert.AreEqual("candidate.json", Resolve("player", "--prototype-layout", "candidate.json"));
            Assert.AreEqual(string.Empty, Resolve("player", "--prototype-layout"));
        }
    }
}

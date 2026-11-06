using NUnit.Framework;
using UnityEngine;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class FontFallbackPolicyTests
    {
        [Test]
        public void FallbackResolver_LogsMissingGlyphOnlyOnce()
        {
            var catalog = ScriptableObject.CreateInstance<UiFontCatalog>();
            var resolver = new FontFallbackResolver(catalog);

            // Test character U+20BB7 (CJK Ext-B char missing in NotoSansSC-Regular)
            int testCodepoint = 0x20BB7;

            Assert.IsFalse(resolver.IsGlyphMissingLogged(testCodepoint));

            // First lookup fails and logs warning
            bool found1 = resolver.HasGlyph(testCodepoint);
            Assert.IsFalse(found1);
            Assert.IsTrue(resolver.IsGlyphMissingLogged(testCodepoint));
            Assert.AreEqual(1, resolver.MissingGlyphsLoggedCount);

            // Second lookup of same codepoint does not log again
            bool found2 = resolver.HasGlyph(testCodepoint);
            Assert.IsFalse(found2);
            Assert.AreEqual(1, resolver.MissingGlyphsLoggedCount);
        }
    }
}

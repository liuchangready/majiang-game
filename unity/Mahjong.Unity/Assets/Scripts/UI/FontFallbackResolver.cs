using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    public sealed class FontFallbackResolver
    {
        private readonly UiFontCatalog _catalog;
        private readonly HashSet<int> _missingGlyphsLogged = new HashSet<int>();
        private readonly object _gate = new object();

        public FontFallbackResolver(UiFontCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public bool HasGlyph(int unicodeCodepoint)
        {
            return ResolveCharacter(unicodeCodepoint, out _);
        }

        public bool ResolveCharacter(int unicodeCodepoint, out TMP_FontAsset? matchingFont)
        {
            matchingFont = null;

            if (_catalog.PrimaryFont != null && _catalog.PrimaryFont.HasCharacter(unicodeCodepoint))
            {
                matchingFont = _catalog.PrimaryFont;
                return true;
            }

            foreach (var fallback in _catalog.FallbackFonts)
            {
                if (fallback != null && fallback.HasCharacter(unicodeCodepoint))
                {
                    matchingFont = fallback;
                    return true;
                }
            }

            lock (_gate)
            {
                if (!_missingGlyphsLogged.Contains(unicodeCodepoint))
                {
                    _missingGlyphsLogged.Add(unicodeCodepoint);
                    Debug.LogWarning($"UI_FONT_GLYPH_MISSING:U+{unicodeCodepoint:X4}");
                }
            }

            return false;
        }

        public bool IsGlyphMissingLogged(int unicodeCodepoint)
        {
            lock (_gate)
            {
                return _missingGlyphsLogged.Contains(unicodeCodepoint);
            }
        }

        public int MissingGlyphsLoggedCount
        {
            get
            {
                lock (_gate)
                {
                    return _missingGlyphsLogged.Count;
                }
            }
        }
    }
}

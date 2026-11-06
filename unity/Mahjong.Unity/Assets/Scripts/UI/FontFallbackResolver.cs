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

        private static FontFallbackResolver? _instance;
        private static bool _eventHooked;

        public static FontFallbackResolver Instance => _instance ??= InitializeDefault();

        public static FontFallbackResolver InitializeDefault(UiFontCatalog? catalog = null)
        {
            if (_instance != null)
            {
                HookEvent();
                return _instance;
            }

            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<UiFontCatalog>();
            }
            _instance = new FontFallbackResolver(catalog);
            HookEvent();
            return _instance;
        }

        public static void HookEvent()
        {
            if (_eventHooked) return;
            _eventHooked = true;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
        }

        private static void OnTextChanged(UnityEngine.Object obj)
        {
            if (obj is TMP_Text tmp)
            {
                var resolver = Instance;
                if (resolver._catalog.PrimaryFont == null && tmp.font != null)
                {
                    resolver._catalog.PrimaryFont = tmp.font;
                }

                if (tmp.font != null && tmp.font.fallbackFontAssetTable != null)
                {
                    foreach (var fb in tmp.font.fallbackFontAssetTable)
                    {
                        if (fb != null) resolver._catalog.AddFallback(fb);
                    }
                }

                string s = tmp.text;
                if (!string.IsNullOrEmpty(s))
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        int codepoint = char.ConvertToUtf32(s, i);
                        if (char.IsSurrogatePair(s, i))
                        {
                            i++;
                        }
                        resolver.ResolveCharacter(codepoint, out _);
                    }
                }
            }
        }

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

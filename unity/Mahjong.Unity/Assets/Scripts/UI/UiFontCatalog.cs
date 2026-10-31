using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    [CreateAssetMenu(fileName = "UiFontCatalog", menuName = "Mahjong/UiFontCatalog")]
    public sealed class UiFontCatalog : ScriptableObject
    {
        [SerializeField] private TMP_FontAsset? _primaryFont;
        [SerializeField] private List<TMP_FontAsset> _fallbackFonts = new List<TMP_FontAsset>();

        public TMP_FontAsset? PrimaryFont
        {
            get => _primaryFont;
            set => _primaryFont = value;
        }

        public IReadOnlyList<TMP_FontAsset> FallbackFonts => _fallbackFonts;

        public void AddFallback(TMP_FontAsset font)
        {
            if (font != null && !_fallbackFonts.Contains(font))
            {
                _fallbackFonts.Add(font);
            }
        }
    }
}

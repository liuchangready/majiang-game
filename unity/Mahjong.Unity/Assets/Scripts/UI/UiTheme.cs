using System;
using TMPro;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    [CreateAssetMenu(fileName = "UiTheme", menuName = "Mahjong/UiTheme")]
    public sealed class UiTheme : ScriptableObject
    {
        [Header("Colors")]
        public Color primaryColor = new Color(0.12f, 0.45f, 0.35f, 1f);
        public Color secondaryColor = new Color(0.2f, 0.25f, 0.3f, 1f);
        public Color backgroundColor = new Color(0.08f, 0.12f, 0.1f, 1f);
        public Color textColor = Color.white;
        public Color disabledTextColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        public Color warningColor = new Color(0.9f, 0.6f, 0.1f, 1f);
        public Color errorColor = new Color(0.85f, 0.2f, 0.2f, 1f);

        [Header("Fonts")]
        public TMP_FontAsset? fontAsset;
        public float titleFontSize = 56f;
        public float buttonFontSize = 32f;
        public float bodyFontSize = 28f;

        [Header("Sprites")]
        public Sprite? buttonPrimarySprite;
        public Sprite? buttonSecondarySprite;
        public Sprite? buttonFocusSprite;
        public Sprite? backgroundSprite;
        public Sprite? menuIconsSprite;
        public Sprite? heroSprite;
    }
}

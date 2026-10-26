using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    public sealed class UiThemeProvider
    {
        private static UiThemeProvider? _instance;
        public static UiThemeProvider Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new UiThemeProvider();
                }
                EnsureLineBreakingRules();
                return _instance;
            }
        }

        private UiTheme? _currentTheme;

        public UiThemeProvider(UiTheme? theme = null)
        {
            _currentTheme = theme;
        }

        public UiTheme? CurrentTheme
        {
            get => _currentTheme;
            set => _currentTheme = value;
        }

        public void SetTheme(UiTheme theme)
        {
            _currentTheme = theme;
            EnsureLineBreakingRules();
        }

        public static void EnsureLineBreakingRules()
        {
            try
            {
                var settings = TMP_Settings.instance;
                if (settings != null)
                {
                    var field = typeof(TMP_Settings).GetField("m_linebreakingRules", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field != null && field.GetValue(settings) == null)
                    {
                        var table = new TMP_Settings.LineBreakingTable
                        {
                            leadingCharacters = new HashSet<uint>(),
                            followingCharacters = new HashSet<uint>()
                        };
                        field.SetValue(settings, table);
                    }
                }
            }
            catch
            {
                // Suppress if TMP settings not present
            }
        }
    }
}

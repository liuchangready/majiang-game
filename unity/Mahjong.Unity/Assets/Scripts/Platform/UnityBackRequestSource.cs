#nullable enable
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Mahjong.Unity.Platform
{
    public sealed class UnityBackRequestSource : IBackRequestSource
    {
        public static void EnsureInputModule(GameObject eventSystemGo)
        {
            if (eventSystemGo == null) return;
            var module = eventSystemGo.GetComponent<InputSystemUIInputModule>();
            if (module == null)
            {
                module = eventSystemGo.AddComponent<InputSystemUIInputModule>();
            }

#if UNITY_EDITOR
            if (module.actionsAsset == null || module.actionsAsset.name != "InputSystem_Actions")
            {
                var assetType = typeof(InputActionAsset);
                var assetDb = Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                var loadMethod = assetDb?.GetMethod("LoadAssetAtPath", new[] { typeof(string), typeof(Type) });
                var asset = loadMethod?.Invoke(null, new object[] { "Assets/Settings/InputSystem_Actions.inputactions", assetType }) as InputActionAsset;
                if (asset != null)
                {
                    module.actionsAsset = asset;
                }
            }
#endif

            if (module.actionsAsset != null)
            {
                var map = module.actionsAsset.FindActionMap("MahjongUi") ?? module.actionsAsset.FindActionMap("UI");
                if (map != null)
                {
                    if (module.point == null)
                    {
                        var a = map.FindAction("Point");
                        if (a != null) module.point = InputActionReference.Create(a);
                    }
                    if (module.leftClick == null)
                    {
                        var a = map.FindAction("LeftClick") ?? map.FindAction("Click");
                        if (a != null) module.leftClick = InputActionReference.Create(a);
                    }
                    if (module.move == null)
                    {
                        var a = map.FindAction("Navigate") ?? map.FindAction("Move");
                        if (a != null) module.move = InputActionReference.Create(a);
                    }
                    if (module.submit == null)
                    {
                        var a = map.FindAction("Submit");
                        if (a != null) module.submit = InputActionReference.Create(a);
                    }
                    if (module.cancel == null)
                    {
                        var a = map.FindAction("Cancel");
                        if (a != null) module.cancel = InputActionReference.Create(a);
                    }
                    if (module.scrollWheel == null)
                    {
                        var a = map.FindAction("ScrollWheel");
                        if (a != null) module.scrollWheel = InputActionReference.Create(a);
                    }
                }
            }
        }

        public bool TryConsumeBack()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                return true;
            }
            return false;
        }
    }
}

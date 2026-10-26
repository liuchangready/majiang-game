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
                    var aPoint = map.FindAction("Point");
                    if (aPoint != null) module.point = InputActionReference.Create(aPoint);

                    var aClick = map.FindAction("LeftClick") ?? map.FindAction("Click");
                    if (aClick != null) module.leftClick = InputActionReference.Create(aClick);

                    var aMove = map.FindAction("Navigate") ?? map.FindAction("Move");
                    if (aMove != null) module.move = InputActionReference.Create(aMove);

                    var aSubmit = map.FindAction("Submit");
                    if (aSubmit != null) module.submit = InputActionReference.Create(aSubmit);

                    var aScroll = map.FindAction("ScrollWheel");
                    if (aScroll != null) module.scrollWheel = InputActionReference.Create(aScroll);

                    module.cancel = null;
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

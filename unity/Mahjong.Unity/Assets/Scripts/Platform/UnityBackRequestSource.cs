#nullable enable
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Mahjong.Unity.Platform
{
    public sealed class UnityBackRequestSource : IBackRequestSource
    {
        private const string EmbeddedActionsJson = @"{
  ""name"": ""InputSystem_Actions"",
  ""maps"": [
    {
      ""name"": ""MahjongUi"",
      ""id"": ""7a3562a9-7c87-43df-9226-fcae393cb124"",
      ""actions"": [
        {
          ""name"": ""Navigate"",
          ""type"": ""Value"",
          ""id"": ""3bb3a469-8086-444a-a434-b9b5f54316d3"",
          ""expectedControlType"": ""Vector2"",
          ""processors"": """",
          ""interactions"": """",
          ""initialStateCheck"": true
        },
        {
          ""name"": ""FocusNext"",
          ""type"": ""Button"",
          ""id"": ""8bb3a469-8086-444a-a434-b9b5f54316d4"",
          ""expectedControlType"": ""Button"",
          ""processors"": """",
          ""interactions"": ""Press"",
          ""initialStateCheck"": false
        },
        {
          ""name"": ""FocusPrevious"",
          ""type"": ""Button"",
          ""id"": ""9bb3a469-8086-444a-a434-b9b5f54316d5"",
          ""expectedControlType"": ""Button"",
          ""processors"": """",
          ""interactions"": ""Press"",
          ""initialStateCheck"": false
        },
        {
          ""name"": ""Submit"",
          ""type"": ""Button"",
          ""id"": ""abb3a469-8086-444a-a434-b9b5f54316d6"",
          ""expectedControlType"": ""Button"",
          ""processors"": """",
          ""interactions"": ""Press"",
          ""initialStateCheck"": false
        },
        {
          ""name"": ""Cancel"",
          ""type"": ""Button"",
          ""id"": ""bbb3a469-8086-444a-a434-b9b5f54316d7"",
          ""expectedControlType"": ""Button"",
          ""processors"": """",
          ""interactions"": ""Press"",
          ""initialStateCheck"": false
        },
        {
          ""name"": ""Point"",
          ""type"": ""PassThrough"",
          ""id"": ""cbb3a469-8086-444a-a434-b9b5f54316d8"",
          ""expectedControlType"": ""Vector2"",
          ""processors"": """",
          ""interactions"": """",
          ""initialStateCheck"": true
        },
        {
          ""name"": ""LeftClick"",
          ""type"": ""PassThrough"",
          ""id"": ""dbb3a469-8086-444a-a434-b9b5f54316d9"",
          ""expectedControlType"": ""Button"",
          ""processors"": """",
          ""interactions"": ""Press"",
          ""initialStateCheck"": false
        },
        {
          ""name"": ""ScrollWheel"",
          ""type"": ""PassThrough"",
          ""id"": ""ebb3a469-8086-444a-a434-b9b5f54316da"",
          ""expectedControlType"": ""Vector2"",
          ""processors"": """",
          ""interactions"": """",
          ""initialStateCheck"": false
        }
      ],
      ""bindings"": [
        {
          ""name"": ""GamepadStick"",
          ""id"": ""11111111-1111-1111-1111-111111111111"",
          ""path"": ""<Gamepad>/leftStick"",
          ""action"": ""Navigate""
        },
        {
          ""name"": ""KeyboardArrows"",
          ""id"": ""22222222-2222-2222-2222-222222222222"",
          ""path"": ""2DVector"",
          ""action"": ""Navigate"",
          ""isComposite"": true
        },
        {
          ""name"": ""up"",
          ""id"": ""22222222-2222-2222-2222-222222222223"",
          ""path"": ""<Keyboard>/upArrow"",
          ""action"": ""Navigate"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": ""down"",
          ""id"": ""22222222-2222-2222-2222-222222222224"",
          ""path"": ""<Keyboard>/downArrow"",
          ""action"": ""Navigate"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": ""left"",
          ""id"": ""22222222-2222-2222-2222-222222222225"",
          ""path"": ""<Keyboard>/leftArrow"",
          ""action"": ""Navigate"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": ""right"",
          ""id"": ""22222222-2222-2222-2222-222222222226"",
          ""path"": ""<Keyboard>/rightArrow"",
          ""action"": ""Navigate"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": """",
          ""id"": ""44444444-4444-4444-4444-444444444441"",
          ""path"": ""<Keyboard>/tab"",
          ""action"": ""FocusNext""
        },
        {
          ""name"": ""LeftShiftTab"",
          ""id"": ""55555555-5555-5555-5555-555555555551"",
          ""path"": ""OneModifier"",
          ""action"": ""FocusPrevious"",
          ""isComposite"": true
        },
        {
          ""name"": ""modifier"",
          ""id"": ""55555555-5555-5555-5555-555555555552"",
          ""path"": ""<Keyboard>/leftShift"",
          ""action"": ""FocusPrevious"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": ""binding"",
          ""id"": ""55555555-5555-5555-5555-555555555553"",
          ""path"": ""<Keyboard>/tab"",
          ""action"": ""FocusPrevious"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": ""RightShiftTab"",
          ""id"": ""55555555-5555-5555-5555-555555555554"",
          ""path"": ""OneModifier"",
          ""action"": ""FocusPrevious"",
          ""isComposite"": true
        },
        {
          ""name"": ""modifier"",
          ""id"": ""55555555-5555-5555-5555-555555555555"",
          ""path"": ""<Keyboard>/rightShift"",
          ""action"": ""FocusPrevious"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": ""binding"",
          ""id"": ""55555555-5555-5555-5555-555555555556"",
          ""path"": ""<Keyboard>/tab"",
          ""action"": ""FocusPrevious"",
          ""isPartOfComposite"": true
        },
        {
          ""name"": """",
          ""id"": ""66666666-6666-6666-6666-666666666661"",
          ""path"": ""<Keyboard>/enter"",
          ""action"": ""Submit""
        },
        {
          ""name"": """",
          ""id"": ""66666666-6666-6666-6666-666666666662"",
          ""path"": ""<Keyboard>/space"",
          ""action"": ""Submit""
        },
        {
          ""name"": """",
          ""id"": ""66666666-6666-6666-6666-666666666663"",
          ""path"": ""<Gamepad>/buttonSouth"",
          ""action"": ""Submit""
        },
        {
          ""name"": """",
          ""id"": ""77777777-7777-7777-7777-777777777771"",
          ""path"": ""<Keyboard>/escape"",
          ""action"": ""Cancel""
        },
        {
          ""name"": """",
          ""id"": ""88888888-8888-8888-8888-888888888881"",
          ""path"": ""<Pointer>/position"",
          ""action"": ""Point""
        },
        {
          ""name"": """",
          ""id"": ""99999999-9999-9999-9999-999999999991"",
          ""path"": ""<Mouse>/leftButton"",
          ""action"": ""LeftClick""
        },
        {
          ""name"": """",
          ""id"": ""99999999-9999-9999-9999-999999999992"",
          ""path"": ""<Touchscreen>/primaryTouch/press"",
          ""action"": ""LeftClick""
        },
        {
          ""name"": """",
          ""id"": ""aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"",
          ""path"": ""<Mouse>/scroll"",
          ""action"": ""ScrollWheel""
        }
      ]
    }
  ],
  ""controlSchemes"": []
}";

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

            if (module.actionsAsset == null)
            {
                module.actionsAsset = InputActionAsset.FromJson(EmbeddedActionsJson);
            }

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

                    // Unbind cancel action so BackRouter exclusively handles Escape
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

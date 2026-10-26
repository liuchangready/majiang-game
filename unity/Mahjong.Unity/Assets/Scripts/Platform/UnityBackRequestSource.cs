#nullable enable
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Mahjong.Unity.Platform
{
    public sealed class UnityBackRequestSource : IBackRequestSource
    {
        public static void EnsureInputModule(GameObject eventSystemGo)
        {
            if (eventSystemGo != null && eventSystemGo.GetComponent<InputSystemUIInputModule>() == null)
            {
                eventSystemGo.AddComponent<InputSystemUIInputModule>();
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

using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mahjong.Unity.App
{
    public sealed class SceneGuard : MonoBehaviour
    {
        private void Awake()
        {
            if (AppRoot.Instance == null)
            {
                Debug.LogWarning("BOOT_REQUIRED: No AppRoot found, redirecting to Boot scene.");
                SceneManager.LoadScene(0, LoadSceneMode.Single);
            }
        }
    }
}

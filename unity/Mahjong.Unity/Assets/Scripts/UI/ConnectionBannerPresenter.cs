using UnityEngine;
using TMPro;
using Mahjong.Client;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.UI
{
    public sealed class ConnectionBannerPresenter : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI? _bannerText;
        [SerializeField] private GameObject? _bannerRoot;

        public void UpdateState(SessionViewState? state)
        {
            if (state == null)
            {
                if (_bannerRoot != null) _bannerRoot.SetActive(false);
                return;
            }

            if (_bannerRoot != null) _bannerRoot.SetActive(true);

            if (_bannerText == null) return;

            switch (state.State)
            {
                case TableConnectionState.Ready:
                    _bannerText.text = "已连接";
                    break;
                case TableConnectionState.Connecting:
                    _bannerText.text = "正在连接...";
                    break;
                case TableConnectionState.Reconnecting:
                    _bannerText.text = "正在恢复连接...";
                    break;
                case TableConnectionState.Faulted:
                    _bannerText.text = $"连接异常: {state.LastError ?? "未知错误"}";
                    break;
                case TableConnectionState.Closed:
                    _bannerText.text = "连接已关闭";
                    break;
                default:
                    _bannerText.text = $"状态: {state.State}";
                    break;
            }
        }
    }
}

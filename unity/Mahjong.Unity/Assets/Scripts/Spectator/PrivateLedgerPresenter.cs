#nullable enable
using System.Text;
using TMPro;
using UnityEngine;

namespace Mahjong.Unity.Spectator
{
    public sealed class PrivateLedgerPresenter : MonoBehaviour
    {
        [SerializeField] private GameObject? _ledgerRoot;
        [SerializeField] private TextMeshProUGUI? _balanceText;
        [SerializeField] private TextMeshProUGUI? _entriesText;
        [SerializeField] private TextMeshProUGUI? _authStatusText;
        [SerializeField] private TextMeshProUGUI? _localGameTag;

        public bool IsVisible => _ledgerRoot != null && _ledgerRoot.activeSelf;
        public GameObject? LedgerRoot => _ledgerRoot;
        public TextMeshProUGUI? BalanceText => _balanceText;
        public TextMeshProUGUI? EntriesText => _entriesText;
        public TextMeshProUGUI? AuthStatusText => _authStatusText;
        public TextMeshProUGUI? LocalGameTag => _localGameTag;

        public void Configure(
            GameObject? ledgerRoot = null,
            TextMeshProUGUI? balanceText = null,
            TextMeshProUGUI? entriesText = null,
            TextMeshProUGUI? authStatusText = null,
            TextMeshProUGUI? localGameTag = null)
        {
            if (ledgerRoot != null) _ledgerRoot = ledgerRoot;
            if (balanceText != null) _balanceText = balanceText;
            if (entriesText != null) _entriesText = entriesText;
            if (authStatusText != null) _authStatusText = authStatusText;
            if (localGameTag != null) _localGameTag = localGameTag;
        }

        public void UpdateView(AuthorizedPrivateLedgerView? view)
        {
            if (view == null || !view.IsAuthorized)
            {
                Clear();
                return;
            }

            if (_ledgerRoot != null)
            {
                _ledgerRoot.SetActive(true);
            }

            if (_authStatusText != null)
            {
                _authStatusText.text = $"出千账本 (授权席位: {view.AuthorizedSeat})";
            }

            if (_balanceText != null)
            {
                _balanceText.text = $"千术点余额: {view.CheatPoints}";
            }

            if (_localGameTag != null)
            {
                if (view.IsLocal)
                {
                    _localGameTag.gameObject.SetActive(true);
                    _localGameTag.text = "本地单机对局 (离线结果，无外部钱包/奖励)";
                }
                else
                {
                    _localGameTag.gameObject.SetActive(false);
                    _localGameTag.text = string.Empty;
                }
            }

            if (_entriesText != null)
            {
                if (view.Entries.Count == 0)
                {
                    _entriesText.text = "本局尚无千术费用记录";
                }
                else
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < view.Entries.Count; i++)
                    {
                        var entry = view.Entries[i];
                        string sign = entry.Amount >= 0 ? $"+{entry.Amount}" : entry.Amount.ToString();
                        sb.AppendLine($"[{entry.ReasonDisplay}] {sign} -> 结余: {entry.BalanceAfter}");
                    }
                    _entriesText.text = sb.ToString().TrimEnd();
                }
            }
        }

        public void Clear()
        {
            if (_ledgerRoot != null)
            {
                _ledgerRoot.SetActive(false);
            }

            if (_authStatusText != null) _authStatusText.text = string.Empty;
            if (_balanceText != null) _balanceText.text = string.Empty;
            if (_entriesText != null) _entriesText.text = string.Empty;
            if (_localGameTag != null)
            {
                _localGameTag.gameObject.SetActive(false);
                _localGameTag.text = string.Empty;
            }
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace Mahjong.Unity.Table
{
    /// <summary>
    /// Presentation model and controls for a single seat on the Mahjong table (0..3).
    /// Enforces strict privacy boundaries: opponent hidden tiles and private cheat
    /// states are never displayed or stored in game objects.
    /// </summary>
    [Serializable]
    public sealed class TableSeatView
    {
        [SerializeField] private GameObject? _root;
        [SerializeField] private TextMeshProUGUI? _seatNameText;
        [SerializeField] private TextMeshProUGUI? _scoreText;
        [SerializeField] private TextMeshProUGUI? _handText;
        [SerializeField] private GameObject? _riichiBadge;
        [SerializeField] private TextMeshProUGUI? _discardsText;
        [SerializeField] private TextMeshProUGUI? _meldsText;
        [SerializeField] private TextMeshProUGUI? _ankanText;

        public GameObject? Root => _root;
        public TextMeshProUGUI? SeatNameText => _seatNameText;
        public TextMeshProUGUI? ScoreText => _scoreText;
        public TextMeshProUGUI? HandText => _handText;
        public GameObject? RiichiBadge => _riichiBadge;
        public TextMeshProUGUI? DiscardsText => _discardsText;
        public TextMeshProUGUI? MeldsText => _meldsText;
        public TextMeshProUGUI? AnkanText => _ankanText;

        public void BindControls(
            GameObject? root,
            TextMeshProUGUI? seatNameText,
            TextMeshProUGUI? scoreText,
            TextMeshProUGUI? handText,
            GameObject? riichiBadge,
            TextMeshProUGUI? discardsText,
            TextMeshProUGUI? meldsText,
            TextMeshProUGUI? ankanText)
        {
            _root = root;
            _seatNameText = seatNameText;
            _scoreText = scoreText;
            _handText = handText;
            _riichiBadge = riichiBadge;
            _discardsText = discardsText;
            _meldsText = meldsText;
            _ankanText = ankanText;
        }

        public void Clear()
        {
            if (_seatNameText != null) _seatNameText.text = string.Empty;
            if (_scoreText != null) _scoreText.text = string.Empty;
            if (_handText != null) _handText.text = string.Empty;
            if (_riichiBadge != null) _riichiBadge.SetActive(false);
            if (_discardsText != null) _discardsText.text = string.Empty;
            if (_meldsText != null) _meldsText.text = string.Empty;
            if (_ankanText != null) _ankanText.text = string.Empty;
        }

        public void RenderSeat(PlayerSeatState seat, PlayerTableViewState view, bool isOwnSeat)
        {
            string seatLabel = TileTextHelper.FormatSeatName(seat.Seat);
            bool isCurrentTurn = view.Board.CurrentSeat == seat.Seat;
            bool isDealer = view.Board.DealerSeat == seat.Seat;
            long scorePoints = 0;
            for (int i = 0; i < view.Board.Scores.Count; i++)
            {
                if (view.Board.Scores[i].Seat == seat.Seat)
                {
                    scorePoints = view.Board.Scores[i].Score;
                    break;
                }
            }

            if (isOwnSeat && view.Own != null)
            {
                UpdateOwnSeat(seat, view.Own, seatLabel, isCurrentTurn, isDealer, scorePoints);
            }
            else
            {
                UpdateOpponentSeat(seat, seatLabel, isCurrentTurn, isDealer, scorePoints);
            }
        }

        public void UpdateOpponentSeat(
            PlayerSeatState seat,
            string seatLabel,
            bool isCurrentTurn,
            bool isDealer = false,
            long score = 25000)
        {
            if (_seatNameText != null)
            {
                string dealer = isDealer ? " [庄]" : "";
                string turn = isCurrentTurn ? " ◀" : "";
                _seatNameText.text = $"{seatLabel}{dealer}{turn}";
            }

            if (_scoreText != null)
            {
                _scoreText.text = $"{score}点";
            }

            if (_handText != null)
            {
                uint count = seat.HandCount ?? 13;
                var sb = new StringBuilder();
                sb.Append($"手牌: {count}张 ");
                for (int i = 0; i < count && i < 14; i++)
                {
                    sb.Append("[牌]");
                }
                _handText.text = sb.ToString();
            }

            if (_riichiBadge != null)
            {
                _riichiBadge.SetActive(seat.Riichi);
            }

            if (_discardsText != null)
            {
                _discardsText.text = FormatDiscards(seat.Discards);
            }

            if (_meldsText != null)
            {
                _meldsText.text = FormatMelds(seat.Melds, isOwnSeat: false);
            }

            if (_ankanText != null)
            {
                int ankanCount = 0;
                for (int i = 0; i < seat.Melds.Count; i++)
                {
                    if (seat.Melds[i].IsConcealedKan)
                    {
                        ankanCount++;
                    }
                }

                if (ankanCount > 0)
                {
                    _ankanText.text = $"暗杠: {ankanCount}组 (各4张无身份占位)";
                }
                else
                {
                    _ankanText.text = string.Empty;
                }
            }
        }

        public void UpdateOwnSeat(
            PlayerSeatState seat,
            PlayerOwnState own,
            string seatLabel,
            bool isCurrentTurn,
            bool isDealer,
            long score)
        {
            if (_seatNameText != null)
            {
                string dealer = isDealer ? " [庄]" : "";
                string turn = isCurrentTurn ? " ◀" : "";
                string furiten = own.Furiten ? " [振听]" : "";
                _seatNameText.text = $"{seatLabel} (自家){dealer}{turn}{furiten}";
            }

            if (_scoreText != null)
            {
                _scoreText.text = $"{score}点";
            }

            if (_handText != null)
            {
                var sb = new StringBuilder();
                sb.Append($"手牌: {own.ClosedTiles.Count}张");
                if (own.DrawnTileId.HasValue)
                {
                    sb.Append($" 摸牌:[{TileTextHelper.FormatTile(own.DrawnTileId.Value)}]");
                }
                _handText.text = sb.ToString();
            }

            if (_riichiBadge != null)
            {
                _riichiBadge.SetActive(seat.Riichi);
            }

            if (_discardsText != null)
            {
                _discardsText.text = FormatDiscards(seat.Discards);
            }

            if (_meldsText != null)
            {
                _meldsText.text = FormatMelds(seat.Melds, isOwnSeat: true);
            }

            if (_ankanText != null)
            {
                _ankanText.text = string.Empty;
            }
        }

        private static string FormatDiscards(IReadOnlyList<PlayerDiscardState> discards)
        {
            if (discards == null || discards.Count == 0) return "牌河: 无";
            var sb = new StringBuilder("牌河: ");
            for (int i = 0; i < discards.Count; i++)
            {
                var d = discards[i];
                string r = d.RiichiDiscard ? "[立]" : "";
                string t = d.Tsumogiri ? "'" : "";
                sb.Append($"{TileTextHelper.FormatTile(d.TileId)}{r}{t} ");
            }
            return sb.ToString().TrimEnd();
        }

        private static string FormatMelds(IReadOnlyList<PlayerMeldState> melds, bool isOwnSeat)
        {
            if (melds == null || melds.Count == 0) return "副露: 门清";
            var sb = new StringBuilder("副露: ");
            for (int i = 0; i < melds.Count; i++)
            {
                var m = melds[i];
                if (m.IsPlaceholder)
                {
                    sb.Append("[暗杠:4张] ");
                    continue;
                }
                string kind = TileTextHelper.FormatMeldKind(m.Kind);
                var tiles = new StringBuilder();
                for (int t = 0; t < m.TileIds.Count; t++)
                {
                    if (t > 0) tiles.Append(',');
                    tiles.Append(TileTextHelper.FormatTile(m.TileIds[t]));
                }
                sb.Append($"[{kind}:{tiles}] ");
            }
            return sb.ToString().TrimEnd();
        }
    }
}

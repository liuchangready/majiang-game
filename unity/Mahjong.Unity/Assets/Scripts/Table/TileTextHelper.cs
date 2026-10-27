#nullable enable
using System.Collections.Generic;
using System.Text;

namespace Mahjong.Unity.Table
{
    public static class TileTextHelper
    {
        private static readonly string[] Man = { "1万", "2万", "3万", "4万", "5万", "6万", "7万", "8万", "9万" };
        private static readonly string[] Pin = { "1筒", "2筒", "3筒", "4筒", "5筒", "6筒", "7筒", "8筒", "9筒" };
        private static readonly string[] Sou = { "1索", "2索", "3索", "4索", "5索", "6索", "7索", "8索", "9索" };
        private static readonly string[] Honors = { "东", "南", "西", "北", "白", "发", "中" };

        public static string FormatTile(uint tileId)
        {
            if (tileId > 135) return $"?[{tileId}]";
            int kind = (int)(tileId / 4);

            if (kind < 9) return Man[kind];
            if (kind < 18) return Sou[kind - 9];
            if (kind < 27) return Pin[kind - 18];
            if (kind < 34) return Honors[kind - 27];

            return $"?[{tileId}]";
        }

        public static string FormatTileList(IReadOnlyList<uint>? tiles)
        {
            if (tiles == null || tiles.Count == 0) return "无";
            var sb = new StringBuilder();
            for (int i = 0; i < tiles.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(FormatTile(tiles[i]));
            }
            return sb.ToString();
        }

        public static string FormatMarkedTile(uint tileId, int remainingRounds)
        {
            return $"★{FormatTile(tileId)}(余{remainingRounds}局)";
        }

        public static bool IsTileMarked(PlayerTileMarkState? markState, uint tileId)
        {
            if (markState == null || markState.Groups == null) return false;
            for (int g = 0; g < markState.Groups.Count; g++)
            {
                var group = markState.Groups[g];
                for (int t = 0; t < group.Tiles.Count; t++)
                {
                    if (group.Tiles[t].TileId == tileId) return true;
                }
            }
            return false;
        }

        public static string FormatWind(int wind)
        {
            return wind switch
            {
                0 => "东",
                1 => "南",
                2 => "西",
                3 => "北",
                _ => "东"
            };
        }

        public static string FormatSeatName(int seat)
        {
            return seat switch
            {
                0 => "东家 (起庄)",
                1 => "南家",
                2 => "西家",
                3 => "北家",
                _ => $"席位{seat}"
            };
        }

        public static string FormatMeldKind(PlayerMeldKind kind)
        {
            return kind switch
            {
                PlayerMeldKind.Chi => "吃",
                PlayerMeldKind.Pon => "碰",
                PlayerMeldKind.OpenKan => "大明杠",
                PlayerMeldKind.ClosedKan => "暗杠",
                PlayerMeldKind.AddedKan => "加杠",
                _ => "未知副露"
            };
        }

        public static string FormatActionKind(PlayerActionKind kind)
        {
            return kind switch
            {
                PlayerActionKind.DiscardTile => "打牌",
                PlayerActionKind.DeclareChi => "吃",
                PlayerActionKind.DeclarePon => "碰",
                PlayerActionKind.DeclareKan => "杠",
                PlayerActionKind.DeclareRiichi => "立直",
                PlayerActionKind.DeclareTsumo => "自摸",
                PlayerActionKind.DeclareRon => "荣和",
                PlayerActionKind.Pass => "跳过",
                PlayerActionKind.DeclareAbortiveDraw => "流局申请",
                _ => "操作"
            };
        }

        public static string FormatBeamSwapSlotKind(PlayerBeamSwapSlotKind slotKind, uint slotIndex)
        {
            return slotKind switch
            {
                PlayerBeamSwapSlotKind.Wall => $"牌山 槽位{slotIndex}",
                PlayerBeamSwapSlotKind.DeadWall => $"王牌死山 槽位{slotIndex}",
                PlayerBeamSwapSlotKind.Replacement => $"岭上牌 槽位{slotIndex}",
                _ => $"槽位{slotIndex}"
            };
        }
    }
}

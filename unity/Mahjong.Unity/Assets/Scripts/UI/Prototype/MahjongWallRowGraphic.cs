#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    public enum MahjongWallSeat
    {
        North,
        West,
        East
    }

    /// <summary>One projected seat row built from a shared, three-plane tile chassis.</summary>
    public sealed class MahjongWallRowGraphic : MaskableGraphic
    {
        [SerializeField] private MahjongWallSeat _seat;
        [SerializeField] private int _tileCount = 14;

        public void Configure(MahjongWallSeat seat)
        {
            _seat = seat;
            _tileCount = seat == MahjongWallSeat.North ? 14 : 11;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (int i = 0; i < _tileCount; i++)
            {
                float t = _tileCount <= 1 ? 0f : i / (float)(_tileCount - 1);
                float scale = _seat == MahjongWallSeat.North ? 1f : Mathf.Lerp(.82f, 1f, t);
                Vector2 origin;
                Vector2 rowAxis;
                Vector2 faceAxis;
                Vector2 depth;
                switch (_seat)
                {
                    case MahjongWallSeat.West:
                        origin = new Vector2(248f + i * 2.5f, 112f + i * 20.5f);
                        rowAxis = new Vector2(0f, 29f * scale);
                        faceAxis = new Vector2(32f * scale, 0f);
                        depth = new Vector2(5f * scale, 4f * scale);
                        break;
                    case MahjongWallSeat.East:
                        origin = new Vector2(1032f - i * 2.5f, 112f + i * 20.5f);
                        rowAxis = new Vector2(0f, 29f * scale);
                        faceAxis = new Vector2(-32f * scale, 0f);
                        depth = new Vector2(-5f * scale, 4f * scale);
                        break;
                    default:
                        origin = new Vector2(407f + i * 32f, 52f);
                        rowAxis = new Vector2(35f, 0f);
                        faceAxis = new Vector2(0f, 39f);
                        depth = new Vector2(4f, 6f);
                        break;
                }

                AddTile(vh, origin, rowAxis, faceAxis, depth, i);
            }
        }

        private static void AddTile(VertexHelper vh, Vector2 origin, Vector2 row, Vector2 face, Vector2 depth, int index)
        {
            Color shadow = new Color(.008f, .022f, .027f, .26f);
            Color top = new Color(.83f, .87f, .77f, 1f);
            Color side = new Color(.48f, .59f, .53f, 1f);
            float variation = (index % 3 - 1) * .018f;
            Color back = new Color(.08f + variation, .34f + variation, .31f + variation, 1f);
            Color edge = new Color(.48f, .75f, .64f, 1f);

            Vector2 p = origin;
            Vector2 frontTop = p + row;
            Vector2 frontBottom = p + face;
            Vector2 backTop = frontTop + depth;
            Vector2 backLeft = p + depth;
            Vector2 backBottom = p + face + depth;
            Vector2 backRight = frontTop + face + depth;

            AddQuad(vh, ToLocal(p + new Vector2(1f, 6f)), ToLocal(p + row + new Vector2(1f, 6f)),
                ToLocal(p + row + face + new Vector2(1f, 6f)), ToLocal(p + face + new Vector2(1f, 6f)), shadow);
            AddQuad(vh, ToLocal(frontTop), ToLocal(backTop), ToLocal(backRight), ToLocal(frontTop + face), side);
            AddQuad(vh, ToLocal(p), ToLocal(frontTop), ToLocal(backTop), ToLocal(backLeft), top);
            AddQuad(vh, ToLocal(backLeft), ToLocal(backTop), ToLocal(backRight), ToLocal(backBottom), back);

            Vector2 a = backLeft + row * .09f + face * .08f;
            Vector2 b = backLeft + row * .91f + face * .08f;
            Vector2 c = backLeft + row * .91f + face * .92f;
            Vector2 d = backLeft + row * .09f + face * .92f;
            AddQuad(vh, ToLocal(a), ToLocal(b), ToLocal(b + face * .035f), ToLocal(a + face * .035f), edge);
            AddQuad(vh, ToLocal(d - face * .035f), ToLocal(c - face * .035f), ToLocal(c), ToLocal(d), edge);
            AddQuad(vh, ToLocal(a), ToLocal(a + face * .035f), ToLocal(d - face * .035f), ToLocal(d), edge);
            AddQuad(vh, ToLocal(b), ToLocal(c), ToLocal(c - face * .035f), ToLocal(b - face * .035f), edge);

            Vector2 center = backLeft + row * .5f + face * .5f;
            Vector2 rw = row * .075f;
            Vector2 rh = face * .12f;
            AddTriangle(vh, ToLocal(center - rh), ToLocal(center + rw), ToLocal(center + rh), new Color(.62f, .82f, .69f, 1f));
            AddTriangle(vh, ToLocal(center - rh), ToLocal(center + rh), ToLocal(center - rw), new Color(.31f, .58f, .49f, 1f));
        }

        private static Vector2 ToLocal(Vector2 topLeftPoint) => new Vector2(topLeftPoint.x, -topLeftPoint.y);

        private static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int start = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero);
            vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero);
            vh.AddVert(d, color, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int start = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero);
            vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
        }
    }
}

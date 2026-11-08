#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    /// <summary>Maps source atlas borders to independent logical UI borders without stretching corners.</summary>
    public sealed class PrototypeNineSliceImage : Image
    {
        [SerializeField] private Vector4 _sourceBorder;
        [SerializeField] private Vector4 _targetBorder;

        public Vector4 SourceBorder => _sourceBorder;
        public Vector4 TargetBorder => _targetBorder;

        public void Configure(Vector4 sourceBorder, Vector4 targetBorder)
        {
            _sourceBorder = sourceBorder;
            _targetBorder = targetBorder;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            if (overrideSprite == null || _sourceBorder == Vector4.zero)
            {
                base.OnPopulateMesh(vh);
                return;
            }

            vh.Clear();
            Rect rect = GetPixelAdjustedRect();
            Vector4 target = ClampTarget(_targetBorder, rect.width, rect.height);
            Vector4 source = ClampSource(_sourceBorder, overrideSprite.rect.width, overrideSprite.rect.height);
            Rect spriteRect = overrideSprite.rect;
            Texture texture = overrideSprite.texture;
            float[] xs = { rect.xMin, rect.xMin + target.x, rect.xMax - target.z, rect.xMax };
            float[] ys = { rect.yMin, rect.yMin + target.y, rect.yMax - target.w, rect.yMax };
            float[] us = { spriteRect.xMin / texture.width, (spriteRect.xMin + source.x) / texture.width,
                (spriteRect.xMax - source.z) / texture.width, spriteRect.xMax / texture.width };
            float[] vs = { spriteRect.yMin / texture.height, (spriteRect.yMin + source.y) / texture.height,
                (spriteRect.yMax - source.w) / texture.height, spriteRect.yMax / texture.height };

            Color32 tint = color;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                var vertex = UIVertex.simpleVert;
                vertex.color = tint;
                vertex.position = new Vector3(xs[x], ys[y]);
                vertex.uv0 = new Vector2(us[x], vs[y]);
                vh.AddVert(vertex);
            }
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                int bottomLeft = y * 4 + x;
                int topLeft = bottomLeft + 4;
                vh.AddTriangle(bottomLeft, topLeft, topLeft + 1);
                vh.AddTriangle(bottomLeft, topLeft + 1, bottomLeft + 1);
            }
        }

        private static Vector4 ClampTarget(Vector4 border, float width, float height)
        {
            float xScale = border.x + border.z > width ? width / (border.x + border.z) : 1f;
            float yScale = border.y + border.w > height ? height / (border.y + border.w) : 1f;
            return new Vector4(border.x * xScale, border.y * yScale, border.z * xScale, border.w * yScale);
        }

        private static Vector4 ClampSource(Vector4 border, float width, float height)
        {
            float xScale = border.x + border.z > width ? width / (border.x + border.z) : 1f;
            float yScale = border.y + border.w > height ? height / (border.y + border.w) : 1f;
            return new Vector4(border.x * xScale, border.y * yScale, border.z * xScale, border.w * yScale);
        }
    }
}

#nullable enable
using UnityEngine;

namespace Mahjong.Unity.UI.Prototype
{
    public static class MahjongTileFaceAtlas
    {
        private static Texture2D? _texture;
        private static Sprite? _front;

        public static Texture2D? Texture
        {
            get
            {
                if (_texture == null) _texture = Resources.Load<Texture2D>("Prototype/TileFaces/mahjong-tile-atlas");
                return _texture;
            }
        }

        public static Sprite? Front
        {
            get
            {
                if (_front == null)
                {
                    Texture2D? texture = Resources.Load<Texture2D>("Prototype/TileFaces/tile-front");
                    if (texture != null)
                        _front = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                            new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect);
                }
                return _front;
            }
        }

        private static Rect UvRect(float x, float y, float width, float height) =>
            new Rect(x / 1024f, 1f - (y + height) / 1024f, width / 1024f, height / 1024f);

        public static Rect BackUv => UvRect(344f, 0f, 340f, 540f);
        public static Rect SideUv => UvRect(688f, 0f, 240f, 540f);
        public static Rect BottomUv => UvRect(0f, 548f, 340f, 240f);
    }
}

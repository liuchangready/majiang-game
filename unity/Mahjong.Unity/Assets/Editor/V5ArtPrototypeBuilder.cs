#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.EditorTools
{
    public static class V5ArtPrototypeBuilder
    {
        public const string ScenePath = "Assets/Scenes/Prototypes/V5ArtPrototype.unity";
        public const string PrefabPath = "Assets/Prefabs/Prototypes/V5ArtPrototype.prefab";
        private const string TableRoot = "Assets/Art/UI/V4Prototype/Table/";
        private const string TileRoot = "Assets/Art/UI/V4Prototype/Tiles/";
        private const string Tile2D5Root = "Assets/Art/UI/V4Prototype/Tiles2D5/";

        private static readonly (string name, Rect rect)[] ButtonRects =
        {
            ("prototype_button_red", new Rect(40, 1024 - 96 - 384, 1456, 384)),
            ("prototype_button_jade", new Rect(40, 1024 - 544 - 384, 1456, 384))
        };
        private static readonly (string name, Rect rect)[] PortraitRects =
        {
            ("octopus", new Rect(0, 1254 - 0 - 627, 627, 627)),
            ("fox", new Rect(627, 1254 - 0 - 627, 627, 627)),
            ("panda", new Rect(0, 1254 - 627 - 627, 627, 627)),
            ("magpie", new Rect(627, 1254 - 627 - 627, 627, 627))
        };
        private static readonly (string name, Rect rect)[] TargetRects =
        {
            ("CHINITSU_MAN", new Rect(16, 1024 - 8 - 324, 744, 324)),
            ("CHINITSU_SOU", new Rect(776, 1024 - 8 - 324, 744, 324)),
            ("CHINITSU_PIN", new Rect(16, 1024 - 340 - 324, 744, 324)),
            ("DAISANGEN", new Rect(776, 1024 - 340 - 324, 744, 324)),
            ("KOKUSHI", new Rect(16, 1024 - 672 - 324, 744, 324)),
            ("DAISUUSHII", new Rect(776, 1024 - 672 - 324, 744, 324))
        };

        [MenuItem("Tools/Mahjong/V5 Art Prototype/Create and Open Preview")]
        public static void BuildAndOpenV5ArtPrototype()
        {
            bool sceneExists = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null;
            bool prefabExists = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null;
            if (sceneExists && prefabExists)
            {
                ConfigurePrototypeSprites();
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Debug.Log("V5_ART_PROTOTYPE_OPENED existing scene=" + ScenePath + " prefab=" + PrefabPath);
                return;
            }

            RebuildV5ArtPrototype();
        }

        [MenuItem("Tools/Mahjong/V5 Art Prototype/Rebuild Scene and Prefab (Force)")]
        public static void RebuildV5ArtPrototype()
        {
            ConfigurePrototypeSprites();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EnsureFolder("Assets/Scenes/Prototypes");
            EnsureFolder("Assets/Prefabs/Prototypes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("V5 Art Prototype");

            Type? prototypeType = Type.GetType("Mahjong.Unity.UI.Prototype.V5ArtPrototype, Mahjong.Unity.UI");
            if (prototypeType == null)
            {
                throw new InvalidOperationException("V5ArtPrototype component has not compiled in Mahjong.Unity.UI assembly.");
            }

            var component = root.AddComponent(prototypeType);

            Assign(component, "_theme", AssetDatabase.LoadMainAssetAtPath("Assets/Art/UI/UiTheme.asset"));
            Assign(component, "_mainBackground", RequireSprite("Assets/Art/UI/MainMenu/club-background.png"));
            Assign(component, "_octopus", RequireSprite("Assets/Art/UI/MainMenu/octopus-hero.png"));
            Assign(component, "_primaryButton", RequireSprite("Assets/Art/UI/MainMenu/button-primary.png"));
            Assign(component, "_secondaryButton", RequireSprite("Assets/Art/UI/MainMenu/button-secondary.png"));
            Assign(component, "_focusButton", RequireSprite("Assets/Art/UI/MainMenu/button-focus.png"));
            Assign(component, "_menuIconAtlas", RequireSprite("Assets/Art/UI/MainMenu/menu-icons.png"));

            Assign(component, "_environment", RequireSprite(TableRoot + "club-environment.png"));
            Assign(component, "_board", RequireSprite(TableRoot + "jade-table-board.png"));
            Assign(component, "_panel", RequireSprite(TableRoot + "jade-panel-skin.png"));
            Assign(component, "_redButton", RequireSprite(TableRoot + "button-skins.png", "prototype_button_red"));
            Assign(component, "_jadeButton", RequireSprite(TableRoot + "button-skins.png", "prototype_button_jade"));
            Assign(component, "_portraits", new[] {
                RequireSprite(TableRoot + "animal-medallions.png", "octopus"),
                RequireSprite(TableRoot + "animal-medallions.png", "magpie"),
                RequireSprite(TableRoot + "animal-medallions.png", "panda"),
                RequireSprite(TableRoot + "animal-medallions.png", "fox")
            });
            Assign(component, "_targetCards", TargetRects.Select(x => RequireSprite(TableRoot + "target-card-scenes.png", x.name)).ToArray());
            Assign(component, "_compass", RequireSprite(TableRoot + "round-compass.png"));
            Assign(component, "_tiles", OrderedTilePaths().Select(p => RequireSprite(p)).ToArray());

            Assign(component, "_tileStandingChassis", RequireSprite(Tile2D5Root + "tile_standing_front.png"));
            Assign(component, "_tileFlatFrontChassis", RequireSprite(Tile2D5Root + "tile_flat_front.png"));
            Assign(component, "_tileFlatBackChassis", RequireSprite(Tile2D5Root + "tile_flat_back.png"));
            Assign(component, "_tileSoftShadow", RequireSprite(Tile2D5Root + "tile_shadow_soft.png"));
            Assign(component, "_tileGlow", RequireSprite(Tile2D5Root + "tile_glow.png"));
            Assign(component, "_riichiStick", RequireSprite(Tile2D5Root + "riichi_stick.png"));

            // Let component build its serialized preview pages
            MethodInfo? buildMethod = prototypeType.GetMethod("BuildSerializedPreviewPages");
            if (buildMethod == null)
            {
                throw new InvalidOperationException("Missing BuildSerializedPreviewPages method on V5ArtPrototype.");
            }
            buildMethod.Invoke(component, null);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var prototype = PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene) as GameObject;
            if (prototype == null)
            {
                throw new InvalidOperationException("Could not instantiate V5 Art prototype prefab.");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log("V5_ART_PROTOTYPE_READY scene=" + ScenePath + " prefab=" + PrefabPath);
        }

        private static void ConfigureSingleSprite(string path, Vector4 border = default)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.sRGBTexture = true;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.spriteBorder = border;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        public static void ConfigurePrototypeSprites()
        {
            ConfigureSingleSprite(TableRoot + "club-environment.png");
            ConfigureSingleSprite(TableRoot + "jade-table-board.png");
            ConfigureSingleSprite(TableRoot + "jade-panel-skin.png", new Vector4(340, 340, 340, 340));
            ConfigureSingleSprite(TableRoot + "round-compass.png");

            ConfigureSingleSprite(Tile2D5Root + "tile_standing_front.png");
            ConfigureSingleSprite(Tile2D5Root + "tile_flat_front.png");
            ConfigureSingleSprite(Tile2D5Root + "tile_flat_back.png");
            ConfigureSingleSprite(Tile2D5Root + "tile_shadow_soft.png");
            ConfigureSingleSprite(Tile2D5Root + "tile_glow.png");
            ConfigureSingleSprite(Tile2D5Root + "riichi_stick.png");

            foreach (string tile in OrderedTilePaths()) ConfigureSingleSprite(tile);
            ConfigureSpriteSheet(TableRoot + "button-skins.png", ButtonRects, new Vector4(248, 120, 248, 120));
            ConfigureSpriteSheet(TableRoot + "animal-medallions.png", PortraitRects, Vector4.zero);
            ConfigureSpriteSheet(TableRoot + "target-card-scenes.png", TargetRects, Vector4.zero);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void ConfigureSpriteSheet(string path, (string name, Rect rect)[] sprites, Vector4 border)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.mipmapEnabled = false;
            importer.sRGBTexture = true;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.spritesheet = sprites.Select(s => new SpriteMetaData
            {
                name = s.name,
                rect = s.rect,
                alignment = (int)SpriteAlignment.Center,
                pivot = new Vector2(.5f, .5f),
                border = border
            }).ToArray();
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static IEnumerable<string> OrderedTilePaths()
        {
            foreach (string family in new[] { "man", "pin", "sou" })
                for (int rank = 1; rank <= 9; rank++) yield return TileRoot + family + rank + ".png";
            for (int rank = 1; rank <= 4; rank++) yield return TileRoot + "wind" + rank + ".png";
            for (int rank = 1; rank <= 3; rank++) yield return TileRoot + "dragon" + rank + ".png";
            yield return TileRoot + "man5r.png";
            yield return TileRoot + "pin5r.png";
            yield return TileRoot + "sou5r.png";
        }

        private static Sprite RequireSprite(string path, string? name = null)
        {
            Sprite? sprite = name == null
                ? AssetDatabase.LoadAssetAtPath<Sprite>(path)
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault(s => s.name == name);
            return sprite != null ? sprite : throw new InvalidOperationException("Missing Unity Sprite: " + path + (name == null ? string.Empty : "#" + name));
        }

        private static void Assign(Component component, string field, UnityEngine.Object? value)
        {
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(field);
            if (property == null) throw new InvalidOperationException("Missing serialized prototype field: " + field);
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(Component component, string field, UnityEngine.Object[] values)
        {
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(field);
            if (property == null || !property.isArray) throw new InvalidOperationException("Missing serialized prototype array: " + field);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            string name = path.Substring(path.LastIndexOf('/') + 1);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}

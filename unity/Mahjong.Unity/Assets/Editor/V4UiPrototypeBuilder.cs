#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace Mahjong.Unity.EditorTools
{
    public static class V4UiPrototypeBuilder
    {
        private const string ScenePath = "Assets/Scenes/Prototypes/V4UiPrototype.unity";
        private const string PrefabPath = "Assets/Prefabs/Prototypes/V4UiPrototype.prefab";
        private const string TableRoot = "Assets/Art/UI/V4Prototype/Table/";
        private const string TileRoot = "Assets/Art/UI/V4Prototype/Tiles/";

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

        [MenuItem("Tools/Mahjong/V4 UI Prototype/Create and Open Preview")]
        public static void BuildAndOpenPrototype()
        {
            bool sceneExists = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null;
            bool prefabExists = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null;
            if (sceneExists && prefabExists)
            {
                ConfigurePrototypeSprites();
                UpgradeLegacyEmptyPrefab();
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Debug.Log("V4_UI_PROTOTYPE_OPENED existing scene=" + ScenePath + " prefab=" + PrefabPath);
                return;
            }
            if (sceneExists || prefabExists)
                throw new InvalidOperationException("Prototype scene/prefab pair is incomplete; refusing to overwrite either asset.");

            ConfigurePrototypeSprites();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EnsureFolder("Assets/Scenes/Prototypes");
            EnsureFolder("Assets/Prefabs/Prototypes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("V4 UI Prototype");
            Type prototypeType = Type.GetType("Mahjong.Unity.UI.Prototype.V4UiPrototype, Mahjong.Unity.UI");
            if (prototypeType == null) throw new InvalidOperationException("V4UiPrototype component has not compiled.");
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
                RequireSprite(TableRoot + "animal-medallions.png", "fox") });
            Assign(component, "_targetCards", TargetRects.Select(x => RequireSprite(TableRoot + "target-card-scenes.png", x.name)).ToArray());
            Assign(component, "_compass", RequireSprite(TableRoot + "round-compass.png"));
            Assign(component, "_tiles", OrderedTilePaths().Select(p => RequireSprite(p)).ToArray());
            prototypeType.GetMethod("BuildSerializedPreviewPages")!.Invoke(component, null);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            var prototype = PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene) as GameObject;
            if (prototype == null) throw new InvalidOperationException("Could not instantiate V4 UI prototype prefab.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log("V4_UI_PROTOTYPE_READY scene=" + ScenePath + " prefab=" + PrefabPath);
        }

        public static void BuildAndBuildPrototypePlayer()
        {
            BuildAndOpenPrototype();
            BuildPrototypePlayer();
        }

        public static void BuildPrototypePlayer()
        {
            string output = Environment.GetEnvironmentVariable("V4_UI_PROTOTYPE_PLAYER_PATH") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Set V4_UI_PROTOTYPE_PLAYER_PATH to a fresh unique output directory.");
            if (System.IO.Directory.Exists(output)) throw new InvalidOperationException("Refusing to overwrite existing Player output: " + output);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("V4 UI prototype Player build failed: " + report.summary.result + " errors=" + report.summary.totalErrors);
            Debug.Log("V4_UI_PROTOTYPE_PLAYER_PASS path=" + output + " size=" + report.summary.totalSize + " bytes");
        }

        private static void ConfigureSingleSprite(string path, Vector4 border = default)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Missing image importer: " + path);
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

        private static void ConfigurePrototypeSprites()
        {
            ConfigureSingleSprite(TableRoot + "club-environment.png");
            ConfigureSingleSprite(TableRoot + "jade-table-board.png");
            ConfigureSingleSprite(TableRoot + "jade-panel-skin.png", new Vector4(340, 340, 340, 340));
            ConfigureSingleSprite(TableRoot + "round-compass.png");
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
            if (importer == null) throw new InvalidOperationException("Missing image importer: " + path);
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

        private static void UpgradeLegacyEmptyPrefab()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Type prototypeType = Type.GetType("Mahjong.Unity.UI.Prototype.V4UiPrototype, Mahjong.Unity.UI");
                var prototype = prototypeType != null ? root.GetComponent(prototypeType) : null;
                if (prototype == null) throw new InvalidOperationException("Existing prototype prefab has no V4 UI component.");
                bool hasPages = (bool)(prototypeType!.GetProperty("HasSerializedPreviewPages")!.GetValue(prototype) ?? false);
                if (hasPages)
                {
                    bool changed = RefreshNineSliceBorderMetadata(root);
                    changed |= RefreshLegacyHudPositions(root);
                    if (changed)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                        AssetDatabase.SaveAssets();
                        Debug.Log("V4_UI_PROTOTYPE_NINESLICE_METADATA_REFRESHED prefab=" + PrefabPath);
                    }
                    return;
                }
                if (root.transform.childCount != 0)
                    throw new InvalidOperationException("Existing prefab has hand-authored children but no serialized page references; refusing to overwrite it.");
                prototypeType.GetMethod("BuildSerializedPreviewPages")!.Invoke(prototype, null);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("V4_UI_PROTOTYPE_UPGRADED legacy empty prefab=" + PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool RefreshLegacyHudPositions(GameObject root)
        {
            Transform? stage = root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == "Design stage · 1280×720");
            if (stage == null) return false;
            bool changed = false;
            changed |= MoveSeatIfLegacy(stage.Find("Preparation"), 1,
                new Rect(16, 184, 144, 144), new Rect(16, 332, 176, 60),
                new Rect(800, 178, 128, 128), new Rect(796, 310, 140, 60),
                new Rect(22, 336, 164, 52), new Rect(802, 314, 128, 52));
            changed |= MoveSeatIfLegacy(stage.Find("Preparation"), 3,
                new Rect(800, 178, 128, 128), new Rect(796, 310, 140, 60),
                new Rect(16, 184, 144, 144), new Rect(16, 332, 176, 60),
                new Rect(802, 314, 128, 52), new Rect(22, 336, 164, 52));
            changed |= MoveSeatIfLegacy(stage.Find("Target drawer"), 3,
                new Rect(800, 178, 128, 128), new Rect(796, 310, 140, 60),
                new Rect(16, 184, 144, 144), new Rect(16, 332, 176, 60),
                new Rect(802, 314, 128, 52), new Rect(22, 336, 164, 52));
            changed |= MoveSeatIfLegacy(stage.Find("Confirmation receipt"), 1,
                new Rect(16, 184, 144, 144), new Rect(16, 332, 176, 60),
                new Rect(800, 178, 128, 128), new Rect(796, 310, 140, 60),
                new Rect(22, 336, 164, 52), new Rect(802, 314, 128, 52));
            changed |= MoveSeatIfLegacy(stage.Find("Confirmation receipt"), 3,
                new Rect(800, 178, 128, 128), new Rect(796, 310, 140, 60),
                new Rect(16, 184, 144, 144), new Rect(16, 332, 176, 60),
                new Rect(802, 314, 128, 52), new Rect(22, 336, 164, 52));
            return changed;
        }

        private static bool MoveSeatIfLegacy(Transform? page, int index, Rect oldPortrait, Rect oldPlate,
            Rect newPortrait, Rect newPlate, Rect oldLabel, Rect newLabel)
        {
            if (page == null) return false;
            RectTransform? portrait = page.GetComponentsInChildren<RectTransform>(true)
                .FirstOrDefault(t => t.name == "Animal portrait " + index);
            RectTransform? plate = page.GetComponentsInChildren<RectTransform>(true)
                .FirstOrDefault(t => t.name == "Seat plate " + index);
            Text? label = page.GetComponentsInChildren<Text>(true)
                .FirstOrDefault(t => t.text.StartsWith(index == 1 ? "P1 · 南" : "P3 · 北", StringComparison.Ordinal));
            bool changed = false;
            changed |= MoveTopLeftIfExact(portrait, oldPortrait, newPortrait);
            changed |= MoveTopLeftIfExact(plate, oldPlate, newPlate);
            changed |= MoveTopLeftIfExact(label != null ? label.rectTransform : null, oldLabel, newLabel);
            return changed;
        }

        private static bool MoveTopLeftIfExact(RectTransform? rect, Rect oldRect, Rect newRect)
        {
            if (rect == null || Mathf.Abs(rect.anchoredPosition.x - oldRect.x) > 0.01f
                || Mathf.Abs(-rect.anchoredPosition.y - oldRect.y) > 0.01f
                || (rect.sizeDelta - new Vector2(oldRect.width, oldRect.height)).sqrMagnitude > 0.01f) return false;
            rect.anchoredPosition = new Vector2(newRect.x, -newRect.y);
            rect.sizeDelta = new Vector2(newRect.width, newRect.height);
            return true;
        }

        private static bool RefreshNineSliceBorderMetadata(GameObject root)
        {
            bool changed = false;
            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null || component.GetType().FullName != "Mahjong.Unity.UI.Prototype.PrototypeNineSliceImage") continue;
                var image = component as UnityEngine.UI.Image;
                if (image == null || image.sprite == null) continue;
                Sprite sprite = image.sprite;
                Vector4 source = sprite.border.sqrMagnitude > 0f ? sprite.border
                    : sprite.name.StartsWith("button-", StringComparison.Ordinal) ? new Vector4(18f, 18f, 18f, 18f) : Vector4.zero;
                Vector4 target = sprite.name == "jade-panel-skin" ? new Vector4(44f, 44f, 44f, 44f)
                    : sprite.name.StartsWith("prototype_button_", StringComparison.Ordinal) ? new Vector4(32f, 18f, 32f, 18f)
                    : new Vector4(18f, 18f, 18f, 18f);
                var sourceProperty = component.GetType().GetProperty("SourceBorder");
                var targetProperty = component.GetType().GetProperty("TargetBorder");
                Vector4 currentSource = sourceProperty != null ? (Vector4)sourceProperty.GetValue(component) : Vector4.zero;
                Vector4 currentTarget = targetProperty != null ? (Vector4)targetProperty.GetValue(component) : Vector4.zero;
                if (currentSource == source && currentTarget == target) continue;
                component.GetType().GetMethod("Configure")?.Invoke(component, new object[] { source, target });
                changed = true;
            }
            return changed;
        }
    }
}

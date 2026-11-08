#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

public sealed class V4UiRound3ReviewTests
{
    private const string PrefabPath = "Assets/Prefabs/Prototypes/V4UiPrototype.prefab";
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private GameObject? _root;
    private Camera? _camera;
    private Canvas? _canvas;
    private RenderTexture? _target;
    private int _originalSizeIndex;
    private object? _gameViewSizes;
    private object? _standaloneGroup;
    private MethodInfo? _removeCustomSize;
    private PropertyInfo? _selectedSize;
    private int _builtinCount;
    private int _firstAddedSize;
    private int _addedSizes;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _camera = null;
        _canvas = null;
        _target = null;
        _selectedSize = null;
        _standaloneGroup = null;
        _gameViewSizes = null;
        _addedSizes = 0;
        _root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root);
        if (_camera != null) UnityEngine.Object.Destroy(_camera.gameObject);
        if (_target != null) _target.Release();
        if (_selectedSize != null && _standaloneGroup != null)
        {
            _selectedSize.SetValue(EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")!), _originalSizeIndex);
            for (int i = _addedSizes - 1; i >= 0; i--) _removeCustomSize!.Invoke(_standaloneGroup, new object[] { _firstAddedSize + i });
            _addedSizes = 0;
            _selectedSize = null;
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator EveryMainPageRendersAndNavigatesAtThreeGameViewSizes()
    {
        PrepareGameViewSizes();
        string captureRoot = Environment.GetEnvironmentVariable("V4_UI_REVIEW_CAPTURE_DIR")
            ?? Path.Combine(Application.temporaryCachePath, "v4-ui-review-captures");
        Directory.CreateDirectory(captureRoot);
        ConfigureRenderCamera();

        var sizes = new[] { (1280, 720, "1280x720"), (960, 540, "960x540"), (1600, 600, "1600x600") };
        for (int i = 0; i < sizes.Length; i++)
        {
            SelectGameViewSize(i);
            for (int frame = 0; frame < 120 && (Screen.width != sizes[i].Item1 || Screen.height != sizes[i].Item2); frame++) yield return null;
            Assert.That(Screen.width, Is.EqualTo(sizes[i].Item1));
            Assert.That(Screen.height, Is.EqualTo(sizes[i].Item2));
            yield return null;
            Canvas.ForceUpdateCanvases();
            AssertStageCentered(sizes[i].Item1, sizes[i].Item2);

            Show("ShowMenu");
            yield return new WaitForSecondsRealtime(.9f);
            Capture(captureRoot, sizes[i].Item3, "menu");

            Click(FindButton("单机对局"));
            yield return null;
            AssertPage("Preparation");
            AssertSeatLayout("Preparation", false);
            Capture(captureRoot, sizes[i].Item3, "preparation");

            Click(FindButton("偷梁换柱"));
            yield return null;
            AssertPage("Target drawer");
            AssertSeatLayout("Target drawer", true);
            Capture(captureRoot, sizes[i].Item3, "drawer");

            Click(FindButton("直接开局"));
            yield return null;
            AssertPage("Confirmation receipt");
            AssertSeatLayout("Confirmation receipt", false);
            Capture(captureRoot, sizes[i].Item3, "receipt");

            Click(FindButton("发牌预览"));
            yield return null;
            AssertPage("Dealt table");
            AssertSeatLayout("Dealt table", false);
            Capture(captureRoot, sizes[i].Item3, "playing");
        }
    }

    [UnityTest]
    public IEnumerator ReenteringMenuCancelsOldEntranceAndRestoresSerializedCoordinates()
    {
        Vector3[] serializedButtons = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath)!.transform
            .Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Main menu")!
            .GetComponentsInChildren<Button>(true).Select(b => b.transform.localPosition).ToArray();
        Button[] buttons = _root!.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Main menu")!
            .GetComponentsInChildren<Button>(true);
        Show("ShowMenu");
        Assert.That(buttons.All(b => !b.interactable), Is.True, "re-entering menu must restart behind the entrance input gate");
        Show("ShowMenu");
        Assert.That(buttons.All(b => !b.interactable), Is.True, "only the newest entrance may own button input");
        Show("ShowPreparation");
        Assert.That(buttons.All(b => b.interactable), Is.True, "leaving menu must cancel entrance and restore input");
        Show("ShowMenu");
        Assert.That(buttons.All(b => !b.interactable), Is.True);
        yield return new WaitForSecondsRealtime(1.4f);
        for (int i = 0; i < buttons.Length; i++)
        {
            Vector3 home = serializedButtons[i];
            Assert.That(buttons[i].transform.localPosition.x, Is.EqualTo(home.x).Within(.01f), buttons[i].name);
            Assert.That(buttons[i].transform.localPosition.y, Is.EqualTo(home.y).Within(.01f), buttons[i].name);
            Assert.That(buttons[i].interactable, Is.True, buttons[i].name);
        }
    }

    private void PrepareGameViewSizes()
    {
        Assembly assembly = typeof(EditorWindow).Assembly;
        Type sizesType = assembly.GetType("UnityEditor.GameViewSizes")!;
        _gameViewSizes = sizesType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;
        Type groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType")!;
        _standaloneGroup = sizesType.GetMethod("GetGroup")!.Invoke(_gameViewSizes, new[] { Enum.Parse(groupType, "Standalone") })!;
        Type sizeType = assembly.GetType("UnityEditor.GameViewSize")!;
        Type sizeKind = assembly.GetType("UnityEditor.GameViewSizeType")!;
        MethodInfo add = _standaloneGroup.GetType().GetMethod("AddCustomSize")!;
        string[] names = { "V4 review 1280x720", "V4 review 960x540", "V4 review 1600x600" };
        int[,] dimensions = { { 1280, 720 }, { 960, 540 }, { 1600, 600 } };
        _builtinCount = (int)_standaloneGroup.GetType().GetMethod("GetBuiltinCount")!.Invoke(_standaloneGroup, null)!;
        _firstAddedSize = _builtinCount + (int)_standaloneGroup.GetType().GetMethod("GetCustomCount")!.Invoke(_standaloneGroup, null)!;
        for (int i = 0; i < names.Length; i++)
        {
            object size = Activator.CreateInstance(sizeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { Enum.Parse(sizeKind, "FixedResolution"), dimensions[i, 0], dimensions[i, 1], names[i] }, null)!;
            add.Invoke(_standaloneGroup, new[] { size });
            _addedSizes++;
        }
        _removeCustomSize = _standaloneGroup.GetType().GetMethod("RemoveCustomSize");
        Type gameViewType = assembly.GetType("UnityEditor.GameView")!;
        EditorWindow window = EditorWindow.GetWindow(gameViewType);
        _selectedSize = gameViewType.GetProperty("selectedSizeIndex", InstancePrivate | BindingFlags.Public);
        _originalSizeIndex = (int)_selectedSize!.GetValue(window)!;
    }

    private void SelectGameViewSize(int index)
    {
        Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")!;
        _selectedSize!.SetValue(EditorWindow.GetWindow(gameViewType), _firstAddedSize + index);
    }

    private void ConfigureRenderCamera()
    {
        _canvas = _root!.transform.Find("Prototype Canvas")!.GetComponent<Canvas>();
        _camera = new GameObject("Round 3 actual GameView capture camera").AddComponent<Camera>();
        _camera.orthographic = true;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = Color.black;
        _camera.nearClipPlane = .1f;
        _camera.farClipPlane = 100f;
        _camera.transform.position = new Vector3(0, 0, -10);
    }

    private void AssertStageCentered(int width, int height)
    {
        RectTransform stage = _root!.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720")!.GetComponent<RectTransform>();
        Canvas.ForceUpdateCanvases();
        Vector2 center = RectTransformUtility.WorldToScreenPoint(_canvas!.worldCamera, stage.TransformPoint(stage.rect.center));
        Debug.Log("ROUND3_STAGE_CENTER=" + center + " SCREEN=" + width + "x" + height + " scale=" + _canvas.scaleFactor);
        Assert.That(center.x, Is.EqualTo(width * .5f).Within(1f));
        Assert.That(center.y, Is.EqualTo(height * .5f).Within(1f));
    }

    private void AssertSeatLayout(string pageName, bool drawer)
    {
        RectTransform page = _root!.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/" + pageName)!.GetComponent<RectTransform>();
        RectTransform p1 = page.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == "Animal portrait 1");
        RectTransform p3 = page.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == "Animal portrait 3");
        float expectedP1X = drawer ? 544 : pageName == "Dealt table" ? 1144 : 800;
        Assert.That(p1.anchoredPosition.x, Is.EqualTo(expectedP1X).Within(.01f), pageName + " P1 remains right seat");
        Assert.That(p3.anchoredPosition.x, Is.EqualTo(16).Within(.01f), pageName + " P3 remains left seat");
        if (!drawer) return;
        foreach (RectTransform portrait in page.GetComponentsInChildren<RectTransform>(true).Where(t => t.name.StartsWith("Animal portrait", StringComparison.Ordinal)))
            Assert.That(portrait.anchoredPosition.x + portrait.rect.width, Is.LessThanOrEqualTo(688f), portrait.name + " is under the target drawer");
        foreach (RectTransform plate in page.GetComponentsInChildren<RectTransform>(true).Where(t => t.name.StartsWith("Seat plate", StringComparison.Ordinal)))
            Assert.That(plate.anchoredPosition.x + plate.rect.width, Is.LessThanOrEqualTo(688f), plate.name + " is under the target drawer");
        foreach (Text label in page.GetComponentsInChildren<Text>(true).Where(t => t.text.StartsWith("P0 ·", StringComparison.Ordinal)
                     || t.text.StartsWith("P1 ·", StringComparison.Ordinal) || t.text.StartsWith("P2 ·", StringComparison.Ordinal)
                     || t.text.StartsWith("P3 ·", StringComparison.Ordinal)))
            Assert.That(label.rectTransform.anchoredPosition.x + label.rectTransform.rect.width, Is.LessThanOrEqualTo(688f),
                label.name + " nameplate text is under the target drawer");
        RectTransform compass = page.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == "Wind compass");
        Assert.That(compass.anchoredPosition.x + compass.rect.width, Is.LessThan(688f));
    }

    private void AssertPage(string pageName)
    {
        Assert.That(_root!.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/" + pageName)!.gameObject.activeSelf, Is.True);
    }

    private Button FindButton(string namePart) => _root!.GetComponentsInChildren<Button>(true).Single(b => b.name.Contains(namePart, StringComparison.Ordinal));

    private void Click(Button button)
    {
        Canvas.ForceUpdateCanvases();
        var pointer = new PointerEventData(EventSystem.current)
        {
            button = PointerEventData.InputButton.Left,
            position = RectTransformUtility.WorldToScreenPoint(_canvas!.worldCamera, button.transform.position)
        };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        Assert.That(hits.Count, Is.GreaterThan(0), "screen coordinate must hit a UI graphic");
        Assert.That(hits[0].gameObject.transform == button.transform || hits[0].gameObject.transform.IsChildOf(button.transform), Is.True,
            "screen coordinate must hit the intended button");
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }

    private void Capture(string directory, string size, string state)
    {
        _target?.Release();
        _target = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
        _camera!.targetTexture = _target;
        _camera.aspect = Screen.width / (float)Screen.height;
        _camera.orthographicSize = Screen.height / 200f;
        RenderMode previousMode = _canvas!.renderMode;
        Camera? previousCamera = _canvas.worldCamera;
        float previousDistance = _canvas.planeDistance;
        _canvas.renderMode = RenderMode.ScreenSpaceCamera;
        _canvas.worldCamera = _camera;
        _canvas.planeDistance = 10f;
        Canvas.ForceUpdateCanvases();
        _camera.Render();
        RenderTexture old = RenderTexture.active;
        RenderTexture.active = _target;
        var image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
        image.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        image.Apply(false, false);
        string path = Path.Combine(directory, size + "-" + state + ".png");
        File.WriteAllBytes(path, image.EncodeToPNG());
        Debug.Log("ROUND3_SCREENSHOT=" + path + " state=" + state + " size=" + Screen.width + "x" + Screen.height);
        UnityEngine.Object.Destroy(image);
        RenderTexture.active = old;
        _canvas.renderMode = previousMode;
        _canvas.worldCamera = previousCamera;
        _canvas.planeDistance = previousDistance;
        Canvas.ForceUpdateCanvases();
    }

    private void Show(string method) => typeof(V4UiPrototype).GetMethod(method, InstancePrivate)!.Invoke(_root!.GetComponent<V4UiPrototype>(), null);
}

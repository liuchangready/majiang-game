using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    /// <summary>Runtime drag handles and JSON persistence for every registered table wall.</summary>
    public sealed class MahjongWallLayoutEditor : MonoBehaviour
    {
        private const float HandleHitRadius = 22f;
        private const string LastLayoutPathKey = "MahjongWallLayoutEditor.LastLayoutPath";
        private const string DefaultLayoutFileName = "mahjong-wall-layout-table.json";
        private static readonly Color HandleColor = new Color(1f, .73f, .20f, 1f);
        private static readonly Color EdgeColor = new Color(1f, .73f, .20f, .72f);
        private static readonly Color SkeletonColor = new Color(1f, .78f, .30f, .96f);
        private static readonly Color SkeletonControlNodeColor = new Color(1f, .48f, .08f, 1f);
        private static readonly Color SkeletonDerivedNodeColor = new Color(1f, .93f, .70f, 1f);
        private static readonly Color TileFrameColor = new Color(.30f, .88f, 1f, .92f);

        [Serializable]
        private sealed class LayoutDocument
        {
            public int version;
            public LayoutEntry[] walls = Array.Empty<LayoutEntry>();
            public string[] deletedWallIds = Array.Empty<string>();
        }

        [Serializable]
        private sealed class LayoutEntry
        {
            public string wallId;
            public bool isRuntimeCopy;
            public int seat;
            public float axis1X, axis1Y;
            public float axis2X, axis2Y;
            public float axis3X, axis3Y;
            public float axisLength;
            public float axisSlopeDegrees;
            public float faceWidthAxisOffsetDegrees;
            public float faceUvRotationDegrees;
            public float widthShrinkPercent;
            public bool hasWidthShrinkPercent;
            public float widthFalloffPerScreenY;
            public bool hasWidthFalloffPerScreenY;
            public bool explicitTileGeometry;
            public bool hasPlacement;
            public float faceWidthOverride, extraGap;
        }

        private sealed class WallState
        {
            public string WallId;
            public MahjongWallSeat Seat;
            public MaskableGraphic Graphic;
            public Action<MahjongWallAxisFrame> SetAxisFrame;
            public bool PerTileFrontRenderer;
            public bool WidthByYAxis;
            public MahjongWallAxisFrame Frame;
            public MahjongWallTextureMode TextureMode;
            public MahjongWallFrontFace FrontFace;
            public MahjongWallOuterPoint OuterPoint;
            public float FaceWidthOverride;
            public float DefaultFaceWidthOverride, DefaultExtraGap;
            public float WidthShrinkPercent;
            public float WidthFalloffPerScreenY;
            public Action<MahjongWallAxisFrame, float, float, string, bool> CopyAtFrame;
            public bool IsRuntimeCopy;
            public int GapAfterTileIndex;
            public float ExtraGap;
            public int TileCountOverride;
            public Image[] SkeletonEdges;
            public Image[] SkeletonNodes;
            public Text[] SkeletonLabels;
            public readonly List<Image> TileFrameLines = new List<Image>();
            public readonly List<MahjongWallFaceRevealGraphic> FaceGraphics =
                new List<MahjongWallFaceRevealGraphic>();
        }

        private readonly List<WallState> _walls = new List<WallState>(3);
        private readonly Dictionary<MahjongWallSeat, MahjongWallAxisFrame> _loadedFrames =
            new Dictionary<MahjongWallSeat, MahjongWallAxisFrame>();
        private readonly Dictionary<MahjongWallSeat, float> _loadedWidthShrinkPercents =
            new Dictionary<MahjongWallSeat, float>();
        private readonly Dictionary<MahjongWallSeat, float> _loadedWidthFalloffsPerScreenY =
            new Dictionary<MahjongWallSeat, float>();
        private readonly Dictionary<MahjongWallSeat, Action<MahjongWallAxisFrame, float, float, string, bool>>
            _wallFactoriesBySeat = new Dictionary<MahjongWallSeat, Action<MahjongWallAxisFrame, float, float, string, bool>>();
        private readonly HashSet<string> _deletedWallIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _restoredSerializedCopyIds = new HashSet<string>(StringComparer.Ordinal);
        private LayoutDocument _serializedLayout;
        private bool _hasSerializedLayout;
        private bool _serializedLayoutIsLegacy;
        private readonly Image[] _handles = new Image[4];
        private readonly Image[] _edges = new Image[4];
        private readonly Text[] _handleLabels = new Text[4];
        private RectTransform _coordinateRoot;
        private int _selectedWall = -1;
        private int _draggingHandle = -1;
        private int _lastShortcutFrame = -1;
        private MahjongWallPoint2 _lastPointer;
        private bool _loaded;
        private bool _saveKeyHeld;
        private bool _loadKeyHeld;
        private bool _skeletonKeyHeld;
        private bool _increaseDepthKeyHeld;
        private bool _decreaseDepthKeyHeld;
        private bool _copyKeyHeld;
        private bool _deleteKeyHeld;
        private int _runtimeCopyCount;
        private bool _showSkeleton;
        private bool _showTileFrames;
        private bool _fileDialogOpen;
        private Text _widthShrinkLabel;
        private bool _tuningPanel, _isolate, _panelSkeleton, _previewA;
        private bool _showHelp;
        private int _viewMode = 2, _viewPosition = 1;
        private int _diagnosticFace;
        private static readonly MahjongWallSurface[] DiagnosticFaces = { MahjongWallSurface.Front, MahjongWallSurface.Back, MahjongWallSurface.Top, MahjongWallSurface.Bottom, MahjongWallSurface.LeftSide, MahjongWallSurface.RightSide };
        private static readonly Color[] DiagnosticColors = { Color.cyan, Color.magenta, Color.green, Color.yellow, new Color(1f, .5f, 0f), Color.red };
        private Rect _panelRect = new Rect(20, 20, 360, 620);
        private Vector2 _panelScroll;
        private string _fieldName, _fieldText, _fieldError;
        private string _textUndoField, _textUndoWallId;
        private KeyCode _guiArrowHeld;
        private readonly Stack<string> _textUndo = new Stack<string>();
        private string _loadedLayoutPath = "", _savedLayoutPath = "";
        private bool _capturing;
        private bool _movingByKeyboard;
        private string _aWallId;
        private MahjongWallAxisFrame _aFrame, _bFrame;
        private float _aShrink, _aFalloff, _bShrink, _bFalloff;
        private float _aWidth, _aGap, _bWidth, _bGap;
        private readonly Stack<LayoutEntry> _undo = new Stack<LayoutEntry>();
        private readonly Stack<LayoutEntry> _redo = new Stack<LayoutEntry>();
        private readonly Dictionary<KeyCode, int> _tuningShortcutFrames = new Dictionary<KeyCode, int>();
        private readonly Dictionary<string, LayoutEntry> _baselineEntries = new Dictionary<string, LayoutEntry>();
        public bool HasInputFocus { get; private set; }

        public bool CanCopySelectedWall => _selectedWall >= 0 && _selectedWall < _walls.Count
            && _walls[_selectedWall].CopyAtFrame != null;
        public bool CopyShortcutConsumed => _copyKeyHeld;

        public void Configure(RectTransform coordinateRoot)
        {
            _coordinateRoot = coordinateRoot;
        }

        public MahjongWallAxisFrame ResolveFrame(MahjongWallSeat seat, MahjongWallAxisFrame fallback)
        {
            LoadDefaultLayoutOnce();
            return _loadedFrames.TryGetValue(seat, out MahjongWallAxisFrame frame)
                ? frame : NormalizeLoadedFrame(seat, fallback);
        }

        public float ResolveWidthShrinkPercent(MahjongWallSeat seat, float fallback)
        {
            LoadDefaultLayoutOnce();
            return _loadedWidthShrinkPercents.TryGetValue(seat, out float value) ? value : fallback;
        }

        public float ResolveWidthFalloffPerScreenY(MahjongWallSeat seat, float fallback)
        {
            LoadDefaultLayoutOnce();
            return _loadedWidthFalloffsPerScreenY.TryGetValue(seat, out float value) ? value : fallback;
        }

        public void Register(MahjongWallSeat seat, MaskableGraphic graphic,
            Action<MahjongWallAxisFrame> setAxisFrame,
            MahjongWallAxisFrame frame, MahjongWallTextureMode textureMode,
            float faceWidthOverride, int gapAfterTileIndex, float extraGap,
            int tileCountOverride, IReadOnlyList<MahjongWallFaceRevealGraphic> faceGraphics,
            MahjongWallFrontFace frontFace = MahjongWallFrontFace.Automatic,
            bool perTileFrontRenderer = false,
            MahjongWallOuterPoint outerPoint = MahjongWallOuterPoint.B1,
            float widthShrinkPercent = 0f, bool widthByYAxis = false,
            float widthFalloffPerScreenY = MahjongExplicitFrontWallGraphic.DefaultWidthFalloffPerScreenY,
            Action<MahjongWallAxisFrame, float, float, string, bool> copyAtFrame = null,
            bool isRuntimeCopy = false, string wallId = null)
        {
            WallState state = null;
            wallId = string.IsNullOrEmpty(wallId) ? BaseWallId(seat) : wallId;
            for (int i = 0; i < _walls.Count; i++)
            {
                if (!string.Equals(_walls[i].WallId, wallId, StringComparison.Ordinal)) continue;
                state = _walls[i];
                break;
            }

            if (state == null)
            {
                state = new WallState { Seat = seat, WallId = wallId };
                _walls.Add(state);
            }
            state.Graphic = graphic;
            state.SetAxisFrame = setAxisFrame;
            state.PerTileFrontRenderer = perTileFrontRenderer;
            state.WidthByYAxis = widthByYAxis;
            state.Frame = frame;
            state.TextureMode = textureMode;
            state.FrontFace = frontFace;
            state.OuterPoint = outerPoint;
            state.FaceWidthOverride = faceWidthOverride;
            state.DefaultFaceWidthOverride = faceWidthOverride;
            state.DefaultExtraGap = extraGap;
            state.WidthShrinkPercent = Mathf.Clamp(widthShrinkPercent, 0f, 100f);
            state.WidthFalloffPerScreenY = Mathf.Max(0f, widthFalloffPerScreenY);
            state.CopyAtFrame = copyAtFrame;
            state.IsRuntimeCopy = isRuntimeCopy;
            state.GapAfterTileIndex = gapAfterTileIndex;
            state.ExtraGap = extraGap;
            state.TileCountOverride = tileCountOverride;
            state.FaceGraphics.Clear();
            if (faceGraphics != null) state.FaceGraphics.AddRange(faceGraphics);
            if (!isRuntimeCopy) _wallFactoriesBySeat[seat] = copyAtFrame;

            if (_hasSerializedLayout && !_serializedLayoutIsLegacy)
                ApplySerializedEntryIfPresent(state);

            if (!isRuntimeCopy && _deletedWallIds.Contains(wallId))
            {
                DestroyWallObjects(state);
                _walls.Remove(state);
                RestoreSerializedCopiesForSeat(seat);
                RefreshSkeleton();
                RefreshOverlay();
                return;
            }

            EnsureOverlay();
            EnsureSkeleton(state);
            ApplySkeletonVisibility(state);
            RefreshSkeleton();
            if (!isRuntimeCopy) RestoreSerializedCopiesForSeat(seat);
        }

        private void Update()
        {
            if (_coordinateRoot == null) return;
            if (Mouse.current == null || !Mouse.current.leftButton.isPressed || !Application.isFocused)
                _draggingHandle = -1;
            if (HasInputFocus) return;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.f1Key.wasPressedThisFrame) HandleTuningShortcut(KeyCode.F1, false, false);
                if (keyboard.f2Key.wasPressedThisFrame) HandleTuningShortcut(KeyCode.F2, false, false);
                if (keyboard.f3Key.wasPressedThisFrame) HandleTuningShortcut(KeyCode.F3, false, false);
                if (keyboard.f4Key.wasPressedThisFrame) HandleTuningShortcut(KeyCode.F4, false, false);
                if (keyboard.f5Key.wasPressedThisFrame) HandleTuningShortcut(KeyCode.F5, false, false);
                if (keyboard.f6Key.wasPressedThisFrame) HandleTuningShortcut(KeyCode.F6, false, false);
                if (keyboard.tabKey.wasPressedThisFrame) HandleTuningShortcut(KeyCode.Tab, keyboard.shiftKey.isPressed, false);
                if (keyboard.ctrlKey.isPressed && keyboard.zKey.wasPressedThisFrame) HandleTuningShortcut(KeyCode.Z, false, true);
                if (keyboard.ctrlKey.isPressed && keyboard.yKey.wasPressedThisFrame) HandleTuningShortcut(KeyCode.Y, false, true);
                if (_previewA)
                {
                    if (keyboard.sKey.wasPressedThisFrame) RequestSave();
                    if (keyboard.lKey.wasPressedThisFrame) { ToggleComparison(); RequestLoad(); }
                    return;
                }
                if (keyboard.qKey.wasPressedThisFrame && !_skeletonKeyHeld)
                {
                    _skeletonKeyHeld = true;
                    ToggleSkeleton();
                }
                if (keyboard.qKey.wasReleasedThisFrame) _skeletonKeyHeld = false;
                if (keyboard.sKey.wasPressedThisFrame && !_saveKeyHeld)
                {
                    _saveKeyHeld = true;
                    RequestSave();
                }
                if (keyboard.sKey.wasReleasedThisFrame) _saveKeyHeld = false;
                if (keyboard.lKey.wasPressedThisFrame && !_loadKeyHeld)
                {
                    _loadKeyHeld = true;
                    RequestLoad();
                }
                if (keyboard.lKey.wasReleasedThisFrame) _loadKeyHeld = false;
                if (keyboard.pKey.wasPressedThisFrame && !_increaseDepthKeyHeld)
                {
                    _increaseDepthKeyHeld = true;
                    if (_selectedWall >= 0) RememberEdit(_walls[_selectedWall]);
                    AdjustSelectedWidthShrink(1f);
                }
                if (keyboard.pKey.wasReleasedThisFrame) _increaseDepthKeyHeld = false;
                if (keyboard.mKey.wasPressedThisFrame && !_decreaseDepthKeyHeld)
                {
                    _decreaseDepthKeyHeld = true;
                    if (_selectedWall >= 0) RememberEdit(_walls[_selectedWall]);
                    AdjustSelectedWidthShrink(-1f);
                }
                if (keyboard.mKey.wasReleasedThisFrame) _decreaseDepthKeyHeld = false;
                if (keyboard.cKey.wasPressedThisFrame && !_copyKeyHeld)
                {
                    if (CanCopySelectedWall)
                    {
                        _copyKeyHeld = true;
                        DuplicateSelectedWall();
                    }
                }
                if (keyboard.cKey.wasReleasedThisFrame) _copyKeyHeld = false;
                if (keyboard.dKey.wasPressedThisFrame && !_deleteKeyHeld)
                {
                    _deleteKeyHeld = true;
                    DeleteSelectedWall();
                }
                if (keyboard.dKey.wasReleasedThisFrame) _deleteKeyHeld = false;
                MoveSelectedWithArrowKeys(keyboard);
            }
            if (Mouse.current == null) return;

            Vector2 screen = Mouse.current.position.ReadValue();
            if (_tuningPanel && _panelRect.Contains(new Vector2(screen.x, Screen.height - screen.y))) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _coordinateRoot, screen, null, out Vector2 local)) return;
            MahjongWallPoint2 pointer = new MahjongWallPoint2(
                local.x + _coordinateRoot.rect.width * .5f,
                _coordinateRoot.rect.height * .5f - local.y);

            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                BeginPointerAction(pointer);
                if (_draggingHandle >= 0 && _selectedWall >= 0) RememberEdit(_walls[_selectedWall]);
                RefreshView();
            }
            if (_draggingHandle >= 0 && Mouse.current.leftButton.isPressed)
                DragHandle(pointer);
            if (Mouse.current.leftButton.wasReleasedThisFrame)
                _draggingHandle = -1;
        }

        private void OnGUI()
        {
            if (_coordinateRoot == null || Event.current == null) return;
            if (_tuningPanel)
            {
                Font previous = GUI.skin.font;
                if (_widthShrinkLabel != null && _widthShrinkLabel.font != null) GUI.skin.font = _widthShrinkLabel.font;
                _panelRect = GUI.Window(GetInstanceID(), _panelRect, DrawTuningPanel, "牌组调试 (F2)");
                GUI.skin.font = previous;
            }
            if (_showHelp) GUI.Box(new Rect(400, 20, 410, 110), "F2 panel  |  F3 isolate  |  F4 tile range\nF5 compare  |  Tab select group\nQ skeleton  |  P/M Y scale  |  C copy  |  D delete\nS save  |  L load  |  arrows move\nEnter commits a numeric field");
            if (_tuningPanel && Event.current.type == EventType.MouseDown && !_panelRect.Contains(Event.current.mousePosition)) GUI.FocusControl(null);
            HasInputFocus = _tuningPanel && !string.IsNullOrEmpty(GUI.GetNameOfFocusedControl());
            if (HasInputFocus) { _guiArrowHeld = KeyCode.None; return; }
            _textUndoField = null; _textUndoWallId = null; _textUndo.Clear();
            if (Event.current.type == EventType.KeyDown && HandleTuningShortcut(Event.current.keyCode, Event.current.shift, Event.current.control || Event.current.command, true)) { Event.current.Use(); return; }
            _fieldName = null;
            if (_previewA) return;
            if (Event.current.type == EventType.KeyUp)
            {
                if (Event.current.keyCode == _guiArrowHeld) _guiArrowHeld = KeyCode.None;
                if (Event.current.keyCode == KeyCode.Q) _skeletonKeyHeld = false;
                if (Event.current.keyCode == KeyCode.S) _saveKeyHeld = false;
                if (Event.current.keyCode == KeyCode.L) _loadKeyHeld = false;
                if (Event.current.keyCode == KeyCode.P) _increaseDepthKeyHeld = false;
                if (Event.current.keyCode == KeyCode.M) _decreaseDepthKeyHeld = false;
                if (Event.current.keyCode == KeyCode.C) _copyKeyHeld = false;
                if (Event.current.keyCode == KeyCode.D) _deleteKeyHeld = false;
                return;
            }
            if (Event.current.type != EventType.KeyDown) return;
            if (MoveSelectedWithGuiArrow(Event.current)) { Event.current.Use(); return; }
            if (Event.current.keyCode == KeyCode.S && !_saveKeyHeld)
            {
                _saveKeyHeld = true;
                RequestSave();
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.L && !_loadKeyHeld)
            {
                _loadKeyHeld = true;
                RequestLoad();
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.Q)
            {
                if (!_skeletonKeyHeld)
                {
                    _skeletonKeyHeld = true;
                    ToggleSkeleton();
                }
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.P && !_increaseDepthKeyHeld)
            {
                _increaseDepthKeyHeld = true;
                if (_selectedWall >= 0) RememberEdit(_walls[_selectedWall]);
                AdjustSelectedWidthShrink(1f);
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.M && !_decreaseDepthKeyHeld)
            {
                _decreaseDepthKeyHeld = true;
                if (_selectedWall >= 0) RememberEdit(_walls[_selectedWall]);
                AdjustSelectedWidthShrink(-1f);
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.C && !_copyKeyHeld && CanCopySelectedWall)
            {
                _copyKeyHeld = true;
                DuplicateSelectedWall();
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.D && !_deleteKeyHeld)
            {
                _deleteKeyHeld = true;
                DeleteSelectedWall();
                Event.current.Use();
            }
        }

        private bool HandleTuningShortcut(KeyCode key, bool shift, bool control, bool fromGui = false)
        {
            bool supported = key == KeyCode.F1 || key == KeyCode.F2 || key == KeyCode.F3 || key == KeyCode.F4
                || key == KeyCode.F5 || key == KeyCode.F6 || key == KeyCode.Tab
                || (control && (key == KeyCode.Z || key == KeyCode.Y));
            if (!supported) return false;
            int stamp = Time.frameCount * 2 + (fromGui ? 1 : 0);
            if (_tuningShortcutFrames.TryGetValue(key, out int previous) && previous / 2 == Time.frameCount && previous != stamp) return true;
            _tuningShortcutFrames[key] = stamp;
            switch (key)
            {
                case KeyCode.F1: _showHelp = !_showHelp; break;
                case KeyCode.F2:
                    _tuningPanel = !_tuningPanel;
                    if (_tuningPanel && _baselineEntries.Count == 0) CaptureBaseline();
                    if (_tuningPanel && _selectedWall < 0 && _walls.Count > 0) _selectedWall = 0;
                    if (!_tuningPanel) { _isolate = false; _viewMode = 2; }
                    RefreshView(); break;
                case KeyCode.F3: _isolate = !_isolate; RefreshView(); break;
                case KeyCode.F4: _viewMode = (_viewMode + 1) % 3; RefreshView(); break;
                case KeyCode.F5: ToggleComparison(); break;
                case KeyCode.F6: if (!_capturing) StartCoroutine(ExportScreenshot()); break;
                case KeyCode.Tab:
                    if (_walls.Count == 0) break;
                    if (_previewA) ToggleComparison();
                    int step = shift ? -1 : 1;
                    _selectedWall = _selectedWall < 0 ? (step < 0 ? _walls.Count - 1 : 0) : (_selectedWall + step + _walls.Count) % _walls.Count;
                    RefreshView(); RefreshOverlay(); break;
                case KeyCode.Z: RestoreHistory(_undo, _redo); break;
                case KeyCode.Y: RestoreHistory(_redo, _undo); break;
            }
            return true;
        }

        private void DrawTuningPanel(int id)
        {
            if (_selectedWall < 0 && _walls.Count > 0) _selectedWall = 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_isolate ? "仅所选组" : "全桌")) { _isolate = !_isolate; RefreshView(); }
            if (GUILayout.Button(new[] { "单张", "三张", "整组" }[_viewMode])) { _viewMode = (_viewMode + 1) % 3; RefreshView(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 3; i++)
                if (GUILayout.Button(new[] { "近端", "中间", "远端" }[i])) { _viewPosition = i; RefreshView(); }
            GUILayout.EndHorizontal();
            _panelScroll = GUILayout.BeginScrollView(_panelScroll);
            for (int i = 0; i < _walls.Count; i++)
            {
                int index = i;
                if (GUILayout.Button((_selectedWall == i ? "> " : "") + _walls[i].Seat + "  " + _walls[i].WallId))
                {
                    if (_previewA) ToggleComparison();
                    GUI.FocusControl(null); _fieldName = null; _fieldText = null; _fieldError = null;
                    _selectedWall = index; RefreshView(); RefreshOverlay(); RefreshSkeleton();
                }
            }
            if (_selectedWall >= 0 && _selectedWall < _walls.Count)
            {
                WallState wall = _walls[_selectedWall];
                GUILayout.Label("当前: " + wall.Seat + " / " + wall.WallId + (_previewA ? " / A (只读)" : " / B"));
                GUILayout.Label("已加载: " + (string.IsNullOrEmpty(_loadedLayoutPath) ? "无" : _loadedLayoutPath));
                GUILayout.Label("已保存: " + (string.IsNullOrEmpty(_savedLayoutPath) ? "无" : _savedLayoutPath));
                if (!string.IsNullOrEmpty(_fieldName)) GUILayout.Label("未提交: " + _fieldName + " = " + _fieldText);
                if (_baselineEntries.TryGetValue(wall.WallId, out LayoutEntry preset))
                    GUILayout.Label("预设轴长: " + preset.axisLength.ToString("G7") + " / 当前: " + wall.Frame.AxisLength.ToString("G7"));
                if (GUILayout.Button("导出截图及布局 (F6)") && !_capturing) StartCoroutine(ExportScreenshot());
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("设为 A") && !_previewA) SetComparisonA(wall);
                if (GUILayout.Button("A / B (F5)")) ToggleComparison();
                if (GUILayout.Button("撤销")) RestoreHistory(_undo, _redo);
                if (GUILayout.Button("重做")) RestoreHistory(_redo, _undo);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("恢复初值") && !_previewA && _baselineEntries.TryGetValue(wall.WallId, out LayoutEntry baseline))
                {
                    RememberEdit(wall); ApplyEntry(wall, baseline); RefreshOverlay();
                }
                if (GUILayout.Button(_panelSkeleton ? "骨架叠加: 开" : "骨架叠加: 关"))
                {
                    _panelSkeleton = !_panelSkeleton; _showSkeleton = _panelSkeleton; _showTileFrames = _panelSkeleton;
                    foreach (WallState item in _walls) ApplySkeletonVisibility(item);
                    RefreshSkeleton(); RefreshOverlay(); RefreshView();
                }
                if (_panelSkeleton)
                {
                    if (GUILayout.Button("Face: " + DiagnosticFaces[_diagnosticFace])) { _diagnosticFace = (_diagnosticFace + 1) % DiagnosticFaces.Length; RefreshSkeleton(); }
                    ShowFaceDiagnostic(wall);
                }
                GUI.enabled = !_previewA;
                MahjongWallPoint2 height = wall.Frame.Axis2Start - wall.Frame.Axis1Start;
                MahjongWallPoint2 depth = wall.Frame.Axis3Start - wall.Frame.Axis1Start;
                EditNumber(wall, "Face length", Mathf.Sqrt(height.X * height.X + height.Y * height.Y), 10);
                EditNumber(wall, "Face angle", Mathf.Atan2(height.Y, height.X) * Mathf.Rad2Deg, 11);
                EditNumber(wall, "Depth length", Mathf.Sqrt(depth.X * depth.X + depth.Y * depth.Y), 12);
                EditNumber(wall, "Depth angle", Mathf.Atan2(depth.Y, depth.X) * Mathf.Rad2Deg, 13);
                EditNumber(wall, "Position X", wall.Frame.Axis1Start.X, 0);
                EditNumber(wall, "Position Y", wall.Frame.Axis1Start.Y, 1);
                GUILayout.Label("Advanced control points");
                EditNumber(wall, "A1 X", wall.Frame.Axis1Start.X, 16);
                EditNumber(wall, "A1 Y", wall.Frame.Axis1Start.Y, 17);
                EditNumber(wall, "B1 X", wall.Frame.Axis2Start.X, 2);
                EditNumber(wall, "B1 Y", wall.Frame.Axis2Start.Y, 3);
                EditNumber(wall, "C1 X", wall.Frame.Axis3Start.X, 4);
                EditNumber(wall, "C1 Y", wall.Frame.Axis3Start.Y, 5);
                EditNumber(wall, "Axis length", wall.Frame.AxisLength, 6);
                MahjongWallSkeleton diagnostic = MahjongExplicitFrontWallGraphic.BuildSkeleton(wall.Frame, TileCount(wall), wall.FaceWidthOverride, wall.GapAfterTileIndex, wall.ExtraGap);
                EditNumber(wall, "Width before Y", diagnostic.TileFaceWidth, 14);
                int tileIndex = SelectedTileIndex(wall, TileCount(wall));
                MahjongExplicitTileGeometry[] actualTiles = MahjongExplicitFrontWallGraphic.BuildTileGeometry(diagnostic,
                    TileCount(wall), wall.GapAfterTileIndex, wall.ExtraGap, wall.WidthShrinkPercent,
                    wall.WidthByYAxis, wall.WidthFalloffPerScreenY);
                if (tileIndex >= 0 && tileIndex < actualTiles.Length)
                {
                    MahjongWallPoint2[] edge = actualTiles[tileIndex].BackFace;
                    GUILayout.Label("Tile " + tileIndex + " actual width: " + Distance(edge[0], edge[3]).ToString("G5"));
                    if (tileIndex + 1 < actualTiles.Length)
                        GUILayout.Label("Actual gap to next: " + Distance(edge[3], actualTiles[tileIndex + 1].BackFace[0]).ToString("G5"));
                }
                if (wall.GapAfterTileIndex >= 0) EditNumber(wall, "Extra gap", wall.ExtraGap, 15);
                EditNumber(wall, "Axis angle", wall.Frame.AxisSlopeDegrees, 7);
                EditNumber(wall, "Y falloff", wall.WidthFalloffPerScreenY, 8);
                EditNumber(wall, "Face rotation", wall.Frame.FaceUvRotationDegrees, 9);
                if (GUILayout.Button("Rotate face 90°"))
                {
                    RememberEdit(wall);
                    MahjongWallAxisFrame frame = wall.Frame;
                    frame.FaceUvRotationDegrees += 90f;
                    ApplyFrame(wall, frame); RefreshOverlay();
                }
                GUI.enabled = true;
                if (!string.IsNullOrEmpty(_fieldError)) GUILayout.Label(_fieldError);
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void EditNumber(WallState wall, string label, float value, int field)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(110));
            string name = "wall-field-" + field;
            bool focused = GUI.GetNameOfFocusedControl() == name;
            if (focused && (_textUndoField != name || _textUndoWallId != wall.WallId))
            {
                _textUndoField = name; _textUndoWallId = wall.WallId; _textUndo.Clear();
            }
            bool textUndo = focused && Event.current.type == EventType.KeyDown && (Event.current.control || Event.current.command)
                && Event.current.keyCode == KeyCode.Z;
            if (textUndo)
            {
                if (_textUndo.Count > 0) { _fieldName = name; _fieldText = _textUndo.Pop(); }
                Event.current.Use();
            }
            string displayed = _fieldName == name ? _fieldText : value.ToString("G7", System.Globalization.CultureInfo.InvariantCulture);
            bool keyboardCommit = GUI.GetNameOfFocusedControl() == name && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            GUI.SetNextControlName(name);
            string text = GUILayout.TextField(displayed ?? "");
            if (focused && !textUndo && text != displayed) _textUndo.Push(displayed ?? "");
            if (keyboardCommit || GUI.GetNameOfFocusedControl() == name) { _fieldName = name; _fieldText = text; }
            bool decrease = GUILayout.Button("−", GUILayout.Width(25));
            bool increase = GUILayout.Button("+", GUILayout.Width(25));
            if (decrease || increase)
            {
                float step = field == 8 ? .0001f : 1f;
                _fieldText = (value + (increase ? step : -step)).ToString("G7", System.Globalization.CultureInfo.InvariantCulture);
                _fieldName = name;
            }
            bool commit = keyboardCommit || decrease || increase;
            if (commit)
            {
                float parsed;
                if (!float.TryParse(_fieldText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed) || float.IsNaN(parsed) || float.IsInfinity(parsed) || Mathf.Abs(parsed) > 100000f || (field == 6 && (parsed < 40f || parsed <= wall.ExtraGap)) || ((field == 10 || field == 12 || field == 14) && parsed <= .0001f) || (field == 15 && (parsed < 0f || parsed >= wall.Frame.AxisLength)) || (field == 8 && (parsed < 0f || parsed > .05f)))
                    _fieldError = "Invalid value: " + label;
                else
                {
                    _fieldError = null;
                    MahjongWallAxisFrame frame = wall.Frame;
                    switch (field)
                    {
                        case 0: { float d = parsed - frame.Axis1Start.X; frame.Axis1Start.X += d; frame.Axis2Start.X += d; frame.Axis3Start.X += d; break; }
                        case 1: { float d = parsed - frame.Axis1Start.Y; frame.Axis1Start.Y += d; frame.Axis2Start.Y += d; frame.Axis3Start.Y += d; break; }
                        case 2: frame.Axis2Start.X = parsed; break;
                        case 3: frame.Axis2Start.Y = parsed; break;
                        case 4: frame.Axis3Start.X = parsed; break;
                        case 5: frame.Axis3Start.Y = parsed; break;
                        case 6: frame.AxisLength = parsed; break;
                        case 7: frame.AxisSlopeDegrees = parsed; break;
                        case 8: break;
                        case 9: frame.FaceUvRotationDegrees = parsed; break;
                        case 10: frame.Axis2Start = WithLengthAndAngle(frame.Axis1Start, frame.Axis2Start, parsed, null); break;
                        case 11: frame.Axis2Start = WithLengthAndAngle(frame.Axis1Start, frame.Axis2Start, null, parsed); break;
                        case 12: frame.Axis3Start = WithLengthAndAngle(frame.Axis1Start, frame.Axis3Start, parsed, null); break;
                        case 13: frame.Axis3Start = WithLengthAndAngle(frame.Axis1Start, frame.Axis3Start, null, parsed); break;
                        case 16: frame.Axis1Start.X = parsed; break;
                        case 17: frame.Axis1Start.Y = parsed; break;
                    }
                    float candidateWidth = field == 14 ? parsed : wall.FaceWidthOverride;
                    float candidateGap = field == 15 ? parsed : wall.ExtraGap;
                    bool changesPlacement = field == 14 || field == 15 || field == 6;
                    bool candidateOverride = HasPlacementOverride(wall, candidateWidth, candidateGap);
                    if (changesPlacement && (field == 14 || candidateOverride)
                        && !PlacementValid(wall, frame.AxisLength, candidateWidth, candidateGap))
                        _fieldError = "Invalid value: tile width exceeds pitch or gap exceeds axis";
                    else if (Distance(frame.Axis1Start, frame.Axis2Start) < .0001f || Distance(frame.Axis1Start, frame.Axis3Start) < .0001f)
                        _fieldError = "Invalid value: " + label + " (zero cross-section)";
                    else
                    {
                        RememberEdit(wall);
                        if (field == 8) SetWidthFalloffPerScreenY(wall, parsed);
                        if (field == 14) SetPlacement(wall, parsed, wall.ExtraGap);
                        if (field == 15) SetPlacement(wall, wall.FaceWidthOverride, parsed);
                        ApplyFrame(wall, frame); RefreshOverlay();
                    }
                }
                GUI.FocusControl(null); _fieldName = null; _textUndoField = null; _textUndoWallId = null; _textUndo.Clear(); if (keyboardCommit) Event.current.Use();
            }
            GUILayout.EndHorizontal();
        }

        private void RememberEdit(WallState wall) { _undo.Push(ToEntry(wall)); _redo.Clear(); }

        private static bool HasPlacementOverride(WallState wall, float width, float gap) =>
            !Mathf.Approximately(width, wall.DefaultFaceWidthOverride)
            || !Mathf.Approximately(gap, wall.DefaultExtraGap);

        private static bool PlacementValid(WallState wall, float axisLength, float width, float extraGap)
        {
            int count = TileCount(wall);
            float gap = wall.GapAfterTileIndex >= 0 && wall.GapAfterTileIndex < count - 1 ? extraGap : 0f;
            if (!IsFinite(axisLength) || !IsFinite(width) || !IsFinite(extraGap)
                || width < 0f || extraGap < 0f || gap >= axisLength) return false;
            return width <= .0001f || count <= 1 || width <= (axisLength - gap) / (count - 1);
        }

        private void CaptureBaseline()
        {
            _baselineEntries.Clear();
            foreach (WallState wall in _walls) _baselineEntries[wall.WallId] = ToEntry(wall);
        }

        private void ApplyEntry(WallState wall, LayoutEntry entry)
        {
            ApplyFrame(wall, FromEntry(entry));
            SetWidthShrinkPercent(wall, entry.widthShrinkPercent);
            SetWidthFalloffPerScreenY(wall, entry.widthFalloffPerScreenY);
            SetPlacement(wall, entry.faceWidthOverride, entry.extraGap);
            RefreshSkeleton();
        }

        private static MahjongWallPoint2 WithLengthAndAngle(MahjongWallPoint2 origin, MahjongWallPoint2 end, float? length, float? angle)
        {
            MahjongWallPoint2 delta = end - origin;
            float magnitude = length ?? Mathf.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            float radians = angle.HasValue ? angle.Value * Mathf.Deg2Rad : Mathf.Atan2(delta.Y, delta.X);
            return origin + new MahjongWallPoint2(Mathf.Cos(radians) * magnitude, Mathf.Sin(radians) * magnitude);
        }

        private void RestoreHistory(Stack<LayoutEntry> from, Stack<LayoutEntry> to)
        {
            if (_previewA || from.Count == 0) return;
            LayoutEntry entry = from.Pop();
            WallState wall = _walls.Find(item => item.WallId == entry.wallId);
            if (wall == null) return;
            to.Push(ToEntry(wall));
            ApplyEntry(wall, entry);
            RefreshOverlay();
        }

        private void SetComparisonA(WallState wall)
        {
            _aWallId = wall.WallId; _aFrame = wall.Frame; _aShrink = wall.WidthShrinkPercent;
            _aFalloff = wall.WidthFalloffPerScreenY; _aWidth = wall.FaceWidthOverride; _aGap = wall.ExtraGap;
        }

        private void ToggleComparison()
        {
            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return;
            WallState wall = _walls[_selectedWall];
            if (wall.WallId != _aWallId) return;
            if (!_previewA) { _bFrame = wall.Frame; _bShrink = wall.WidthShrinkPercent; _bFalloff = wall.WidthFalloffPerScreenY; _bWidth = wall.FaceWidthOverride; _bGap = wall.ExtraGap; }
            _previewA = !_previewA;
            ApplyFrame(wall, _previewA ? _aFrame : _bFrame);
            SetWidthShrinkPercent(wall, _previewA ? _aShrink : _bShrink);
            SetWidthFalloffPerScreenY(wall, _previewA ? _aFalloff : _bFalloff);
            SetPlacement(wall, _previewA ? _aWidth : _bWidth, _previewA ? _aGap : _bGap);
            RefreshSkeleton(); RefreshOverlay();
        }

        private void RefreshView()
        {
            for (int i = 0; i < _walls.Count; i++)
            {
                WallState wall = _walls[i];
                bool selected = i == _selectedWall;
                bool visible = !_isolate || selected;
                if (wall.Graphic != null) wall.Graphic.enabled = visible && (!_showSkeleton || _panelSkeleton);
                int start = -1, end = -1;
                if (selected && _viewMode != 2)
                {
                    int count = TileCount(wall);
                    int index = SelectedTileIndex(wall, count);
                    int length = _viewMode == 0 ? 1 : Math.Min(3, count);
                    start = Mathf.Clamp(index - length / 2, 0, count - length); end = start + length - 1;
                }
                if (wall.Graphic is MahjongExplicitFrontWallGraphic graphic) graphic.SetVisibleRange(start, end);
                foreach (MahjongWallFaceRevealGraphic face in wall.FaceGraphics)
                    if (face != null) { face.enabled = visible && (!_showSkeleton || _panelSkeleton); face.SetVisibleRange(start, end); }
            }
            RefreshSkeleton();
        }

        private bool TileInView(WallState wall, int index, int count)
        {
            if (_viewMode == 2) return true;
            int center = SelectedTileIndex(wall, count);
            int length = _viewMode == 0 ? 1 : Math.Min(3, count);
            int start = Mathf.Clamp(center - length / 2, 0, count - length);
            return index >= start && index < start + length;
        }

        private int SelectedTileIndex(WallState wall, int count)
        {
            if (_viewPosition == 1 || count < 2) return count / 2;
            MahjongWallPoint2[] corners = BuildDisplaySkeletonCorners(wall);
            float startY = ToEditorPoint(wall, corners[0]).Y;
            float endY = ToEditorPoint(wall, corners[4]).Y;
            int near = startY >= endY ? 0 : count - 1;
            return _viewPosition == 0 ? near : count - 1 - near;
        }

        private void ShowFaceDiagnostic(WallState wall)
        {
            int count = TileCount(wall);
            int index = SelectedTileIndex(wall, count);
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(wall.Frame, count, wall.FaceWidthOverride, wall.GapAfterTileIndex, wall.ExtraGap);
            MahjongExplicitTileGeometry[] tiles = MahjongExplicitFrontWallGraphic.BuildTileGeometry(skeleton, count, wall.GapAfterTileIndex, wall.ExtraGap, wall.WidthShrinkPercent, wall.WidthByYAxis, wall.WidthFalloffPerScreenY);
            if (index < 0 || index >= tiles.Length) return;
            MahjongWallSurface surface = DiagnosticFaces[_diagnosticFace];
            MahjongWallPoint2[] face = MahjongExplicitFrontWallGraphic.Surface(tiles[index], surface);
            float twiceArea = 0f, maxEdgeSquared = 0f;
            for (int i = 0; i < face.Length; i++)
            {
                MahjongWallPoint2 next = face[(i + 1) % face.Length];
                twiceArea += face[i].X * next.Y - next.X * face[i].Y;
                float dx = next.X - face[i].X, dy = next.Y - face[i].Y;
                maxEdgeSquared = Mathf.Max(maxEdgeSquared, dx * dx + dy * dy);
            }
            float area = Mathf.Abs(twiceArea) * .5f;
            bool nearCollinear = maxEdgeSquared < .000001f || area / maxEdgeSquared < .001f;
            bool submitted = Array.IndexOf(MahjongExplicitFrontWallGraphic.GetOuterSurfaces(tiles[index], wall.OuterPoint, index, count), surface) >= 0;
            GUILayout.Label("Tile " + index + ", " + surface + (submitted ? " / mesh" : " / diagnostic only") + ", area " + area.ToString("G5") + (nearCollinear ? " (near collinear)" : ""));
            for (int i = 0; i < face.Length; i++) GUILayout.Label(i + ": " + face[i].X.ToString("F2") + ", " + face[i].Y.ToString("F2"));
        }

        private IEnumerator ExportScreenshot()
        {
            _capturing = true;
            string stem = Path.Combine(Application.persistentDataPath, "wall-export",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + (_previewA ? "A-" : "B-") + Guid.NewGuid().ToString("N"));
            LayoutDocument document = CaptureDocument();
            bool panel = _tuningPanel, help = _showHelp, skeleton = _showSkeleton, frames = _showTileFrames, overlay = _panelSkeleton, isolate = _isolate;
            int selected = _selectedWall;
            _tuningPanel = false; _showHelp = false; _showSkeleton = false; _showTileFrames = false; _panelSkeleton = false;
            // Keep the selection for filtering; hide only the editor's visual overlays.
            RefreshView(); RefreshOverlay();
            foreach (Image handle in _handles) if (handle != null) handle.gameObject.SetActive(false);
            foreach (Image edge in _edges) if (edge != null) edge.gameObject.SetActive(false);
            foreach (Text label in _handleLabels) if (label != null) label.gameObject.SetActive(false);
            yield return new WaitForEndOfFrame();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stem));
                var image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                image.Apply();
                File.WriteAllBytes(stem + ".png", image.EncodeToPNG());
                Destroy(image);
                File.WriteAllText(stem + ".json", JsonUtility.ToJson(document, true));
                Debug.Log("[MahjongWallLayoutEditor] Exported " + stem);
            }
            catch (Exception exception) { Debug.LogError("[MahjongWallLayoutEditor] Export failed: " + exception.Message); }
            finally
            {
                _selectedWall = selected; _isolate = isolate; _tuningPanel = panel; _showHelp = help; _showSkeleton = skeleton; _showTileFrames = frames; _panelSkeleton = overlay;
                RefreshView(); RefreshSkeleton(); RefreshOverlay(); _capturing = false;
            }
        }

        private void DeleteSelectedWall()
        {
            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return;
            WallState wall = _walls[_selectedWall];
            if (!wall.IsRuntimeCopy) _deletedWallIds.Add(wall.WallId);
            DestroyWallObjects(wall);
            _walls.RemoveAt(_selectedWall);
            _selectedWall = _walls.Count == 0 ? -1 : Mathf.Min(_selectedWall, _walls.Count - 1);
            _draggingHandle = -1;
            RefreshView();
            RefreshOverlay();
            Debug.Log("[MahjongWallLayoutEditor] Deleted selected wall.");
        }

        private static void DestroyOverlayObjects<T>(T[] objects) where T : Component
        {
            if (objects == null) return;
            for (int i = 0; i < objects.Length; i++)
                if (objects[i] != null) Destroy(objects[i].gameObject);
        }

        private static string BaseWallId(MahjongWallSeat seat) => "base:" + (int)seat;

        private void DestroyWallObjects(WallState wall)
        {
            if (wall.Graphic != null) Destroy(wall.Graphic.gameObject);
            for (int i = 0; i < wall.FaceGraphics.Count; i++)
                if (wall.FaceGraphics[i] != null) Destroy(wall.FaceGraphics[i].gameObject);
            DestroyOverlayObjects(wall.SkeletonEdges);
            DestroyOverlayObjects(wall.SkeletonNodes);
            DestroyOverlayObjects(wall.SkeletonLabels);
            for (int i = 0; i < wall.TileFrameLines.Count; i++)
                if (wall.TileFrameLines[i] != null) Destroy(wall.TileFrameLines[i].gameObject);
        }

        private void DuplicateSelectedWall()
        {
            if (!CanCopySelectedWall) return;
            WallState source = _walls[_selectedWall];
            MahjongWallAxisFrame frame = source.Frame;
            Rect sourceBounds = GetDisplayBoundsInEditor(source);
            float stride = sourceBounds.width + 16f;
            float offsetRight = stride;
            bool foundSlot = false;
            int slotsToCheck = Mathf.CeilToInt((_coordinateRoot.rect.width + stride) / 8f) + 1;
            for (int slot = 0; slot < slotsToCheck; slot++)
            {
                offsetRight = stride - slot * 8f;
                if (!CanPlaceCopyAtOffset(source, offsetRight)) continue;
                foundSlot = true;
                break;
            }
            if (!foundSlot)
                Debug.LogWarning("[MahjongWallLayoutEditor] No free on-screen slot for the selected wall copy.");
            frame = new MahjongWallAxisFrame(
                new MahjongWallPoint2(frame.Axis1Start.X + offsetRight, frame.Axis1Start.Y),
                new MahjongWallPoint2(frame.Axis2Start.X + offsetRight, frame.Axis2Start.Y),
                new MahjongWallPoint2(frame.Axis3Start.X + offsetRight, frame.Axis3Start.Y),
                frame.AxisLength, frame.AxisSlopeDegrees,
                frame.FaceWidthAxisOffsetDegrees, frame.FaceUvRotationDegrees);
            source.CopyAtFrame(frame, source.WidthShrinkPercent, source.WidthFalloffPerScreenY,
                Guid.NewGuid().ToString("N"), true);
            _selectedWall = _walls.Count - 1;
            SetPlacement(_walls[_selectedWall], source.FaceWidthOverride, source.ExtraGap);
            _baselineEntries[_walls[_selectedWall].WallId] = ToEntry(_walls[_selectedWall]);
            _runtimeCopyCount++;
            RefreshView();
            RefreshOverlay();
            Debug.Log("[MahjongWallLayoutEditor] Duplicated selected wall to the right (copy "
                + _runtimeCopyCount + ").");
        }

        private bool CanPlaceCopyAtOffset(WallState source, float offsetX)
        {
            Rect candidate = GetDisplayBoundsInEditor(source, offsetX);
            if (candidate.xMin < 0f || candidate.xMax > _coordinateRoot.rect.width
                || candidate.yMin < 0f || candidate.yMax > _coordinateRoot.rect.height) return false;
            for (int i = 0; i < _walls.Count; i++)
            {
                Rect other = GetDisplayBoundsInEditor(_walls[i]);
                if (candidate.xMin < other.xMax + 8f && candidate.xMax + 8f > other.xMin
                    && candidate.yMin < other.yMax && candidate.yMax > other.yMin) return false;
            }
            return true;
        }

        private Rect GetDisplayBoundsInEditor(WallState wall, float offsetX = 0f)
        {
            MahjongWallPoint2[] corners = BuildDisplaySkeletonCorners(wall);
            MahjongWallPoint2 first = corners[0];
            MahjongWallPoint2 firstInEditor = ToEditorPoint(wall,
                new MahjongWallPoint2(first.X + offsetX, first.Y));
            float minX = firstInEditor.X, maxX = firstInEditor.X;
            float minY = firstInEditor.Y, maxY = firstInEditor.Y;
            for (int i = 1; i < corners.Length; i++)
            {
                MahjongWallPoint2 point = ToEditorPoint(wall,
                    new MahjongWallPoint2(corners[i].X + offsetX, corners[i].Y));
                minX = Mathf.Min(minX, point.X);
                maxX = Mathf.Max(maxX, point.X);
                minY = Mathf.Min(minY, point.Y);
                maxY = Mathf.Max(maxY, point.Y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private void AdjustSelectedWidthShrink(float delta)
        {
            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return;
            WallState wall = _walls[_selectedWall];
            if (!SupportsWidthAdjustment(wall)) return;
            if (wall.WidthByYAxis)
            {
                SetWidthFalloffPerScreenY(wall, wall.WidthFalloffPerScreenY + delta * .0001f);
                RefreshSkeleton();
                RefreshOverlay();
                return;
            }
            SetWidthShrinkPercent(wall, wall.WidthShrinkPercent + delta);
            RefreshSkeleton();
            RefreshOverlay();
        }

        private static bool SupportsWidthAdjustment(WallState wall) =>
            wall.PerTileFrontRenderer;

        private static void SetPlacement(WallState wall, float width, float gap)
        {
            wall.FaceWidthOverride = width;
            wall.ExtraGap = gap;
            if (wall.Graphic is MahjongExplicitFrontWallGraphic graphic) graphic.SetPlacement(width, gap);
            foreach (MahjongWallFaceRevealGraphic face in wall.FaceGraphics)
                if (face != null) face.SetPlacement(width, gap);
        }

        private void SetWidthFalloffPerScreenY(WallState wall, float value)
        {
            wall.WidthFalloffPerScreenY = Mathf.Clamp(Mathf.Round(value * 100000f) / 100000f, 0f, .05f);
            if (wall.Graphic is MahjongExplicitFrontWallGraphic explicitGraphic)
                explicitGraphic.SetWidthFalloffPerScreenY(wall.WidthFalloffPerScreenY);
            for (int i = 0; i < wall.FaceGraphics.Count; i++)
                wall.FaceGraphics[i].SetWidthFalloffPerScreenY(wall.WidthFalloffPerScreenY);
            _loadedWidthFalloffsPerScreenY[wall.Seat] = wall.WidthFalloffPerScreenY;
        }

        private void SetWidthShrinkPercent(WallState wall, float percent)
        {
            wall.WidthShrinkPercent = Mathf.Clamp(percent, 0f, 100f);
            if (wall.Graphic is MahjongExplicitFrontWallGraphic explicitGraphic)
                explicitGraphic.SetWidthShrinkPercent(wall.WidthShrinkPercent);
            for (int i = 0; i < wall.FaceGraphics.Count; i++)
                wall.FaceGraphics[i].SetWidthShrinkPercent(wall.WidthShrinkPercent);
            _loadedWidthShrinkPercents[wall.Seat] = wall.WidthShrinkPercent;
        }

        private void ToggleSkeleton()
        {
            _panelSkeleton = false;
            if (!_showSkeleton) _showSkeleton = true;
            else if (!_showTileFrames) _showTileFrames = true;
            else
            {
                _showSkeleton = false;
                _showTileFrames = false;
            }
            if (_showSkeleton && _selectedWall < 0 && _walls.Count > 0)
            {
                _selectedWall = 0;
                for (int i = 0; i < _walls.Count; i++)
                {
                    if (!SupportsWidthAdjustment(_walls[i])) continue;
                    _selectedWall = i;
                    break;
                }
            }
            for (int i = 0; i < _walls.Count; i++)
                ApplySkeletonVisibility(_walls[i]);
            RefreshSkeleton();
            RefreshOverlay();
            RefreshView();
        }

        private void ApplySkeletonVisibility(WallState wall)
        {
            if (wall.Graphic != null) wall.Graphic.enabled = !_showSkeleton;
            for (int i = 0; i < wall.FaceGraphics.Count; i++)
                if (wall.FaceGraphics[i] != null) wall.FaceGraphics[i].enabled = !_showSkeleton;
        }

        private void RequestSave()
        {
            if (_fileDialogOpen || _lastShortcutFrame == Time.frameCount) return;
            _lastShortcutFrame = Time.frameCount;
            SaveLayout();
        }

        private void RequestLoad()
        {
            if (_fileDialogOpen || _lastShortcutFrame == Time.frameCount) return;
            _lastShortcutFrame = Time.frameCount;
            LoadLayoutFromDialog();
        }

        private void BeginPointerAction(MahjongWallPoint2 pointer)
        {
            if (_showSkeleton)
            {
                for (int i = _walls.Count - 1; i >= 0; i--)
                {
                    if (_panelSkeleton && i != _selectedWall) continue;
                    MahjongWallPoint2[] nodes = BuildDisplaySkeletonCorners(_walls[i]);
                    int[] controlNodes = { 0, 1, 2, 4 };
                    for (int handle = 0; handle < controlNodes.Length; handle++)
                    {
                        MahjongWallPoint2 editorPoint = ToEditorPoint(_walls[i], nodes[controlNodes[handle]]);
                        if (Distance(pointer, editorPoint) > HandleHitRadius) continue;
                        _selectedWall = i;
                        _draggingHandle = handle;
                        _lastPointer = pointer;
                        RefreshSkeleton();
                        RefreshOverlay();
                        return;
                    }
                }
            }

            if (_selectedWall >= 0)
            {
                MahjongWallPoint2[] controls = GetDisplayControlPointsInEditor(_walls[_selectedWall]);
                for (int handle = 0; handle < controls.Length; handle++)
                {
                    if (Distance(pointer, controls[handle]) > HandleHitRadius) continue;
                    _draggingHandle = handle;
                    _lastPointer = pointer;
                    return;
                }
            }

            for (int i = _walls.Count - 1; i >= 0; i--)
            {
                if (_isolate && i != _selectedWall) continue;
                if (!Contains(GetSelectionPolygon(_walls[i]), pointer)) continue;
                _selectedWall = i;
                _draggingHandle = 4;
                _lastPointer = pointer;
                RefreshSkeleton();
                RefreshOverlay();
                return;
            }

            _selectedWall = -1;
            _draggingHandle = -1;
            RefreshSkeleton();
            RefreshOverlay();
        }

        private void DragHandle(MahjongWallPoint2 pointer)
        {
            WallState wall = _walls[_selectedWall];
            MahjongWallAxisFrame frame = wall.Frame;
            MahjongWallPoint2 wallPointer = ToWallPoint(wall, pointer);
            MahjongWallPoint2 previousWallPointer = ToWallPoint(wall, _lastPointer);
            MahjongWallPoint2 delta = wallPointer - previousWallPointer;
            _lastPointer = pointer;

            if (_draggingHandle == 0) // A1; keep the independently adjustable A2 fixed.
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                if (!SetOuterControlPoints(wall, ref frame, outer[0] + delta, outer[1], outer[2], outer[3])) _fieldError = "Invalid placement: tile width exceeds pitch"; else _fieldError = null;
            }
            else if (_draggingHandle == 1)
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                if (!SetOuterControlPoints(wall, ref frame, outer[0], outer[1] + delta, outer[2], outer[3])) _fieldError = "Invalid placement: tile width exceeds pitch"; else _fieldError = null;
            }
            else if (_draggingHandle == 2)
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                if (!SetOuterControlPoints(wall, ref frame, outer[0], outer[1], outer[2] + delta, outer[3])) _fieldError = "Invalid placement: tile width exceeds pitch"; else _fieldError = null;
            }
            else if (_draggingHandle == 3)
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                if (!SetOuterControlPoints(wall, ref frame, outer[0], outer[1], outer[2], wallPointer)) _fieldError = "Invalid placement: tile width exceeds pitch"; else _fieldError = null;
            }
            else if (_draggingHandle == 4)
            {
                frame.Axis1Start += delta;
                frame.Axis2Start += delta;
                frame.Axis3Start += delta;
            }

            ApplyFrame(wall, frame);
            RefreshOverlay();
        }
        private void ApplyFrame(WallState wall, MahjongWallAxisFrame frame)
        {
            wall.Frame = frame;
            wall.SetAxisFrame?.Invoke(frame);
            for (int i = 0; i < wall.FaceGraphics.Count; i++)
                wall.FaceGraphics[i].SetAxisFrame(frame);
            RefreshSkeleton();
        }

        private void MoveSelectedWithArrowKeys(Keyboard keyboard)
        {
            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return;
            float x = (keyboard.rightArrowKey.isPressed ? 1f : 0f)
                - (keyboard.leftArrowKey.isPressed ? 1f : 0f);
            float y = (keyboard.downArrowKey.isPressed ? 1f : 0f)
                - (keyboard.upArrowKey.isPressed ? 1f : 0f);
            if (Math.Abs(x) < .001f && Math.Abs(y) < .001f) { _movingByKeyboard = false; return; }
            if (!_movingByKeyboard) { RememberEdit(_walls[_selectedWall]); _movingByKeyboard = true; }
            float speed = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? 600f : 160f;
            MoveSelectedByScreenDelta(x * speed * Time.unscaledDeltaTime, y * speed * Time.unscaledDeltaTime);
        }

        private bool MoveSelectedWithGuiArrow(Event input)
        {
            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return false;
            float x = input.keyCode == KeyCode.RightArrow ? 1f : input.keyCode == KeyCode.LeftArrow ? -1f : 0f;
            float y = input.keyCode == KeyCode.DownArrow ? 1f : input.keyCode == KeyCode.UpArrow ? -1f : 0f;
            if (x == 0f && y == 0f) return false;
            Keyboard keyboard = Keyboard.current;
            bool handledByKeyboard = keyboard != null && (keyboard.rightArrowKey.isPressed || keyboard.leftArrowKey.isPressed
                || keyboard.downArrowKey.isPressed || keyboard.upArrowKey.isPressed);
            if (handledByKeyboard) return false;
            if (_guiArrowHeld != input.keyCode) { RememberEdit(_walls[_selectedWall]); _guiArrowHeld = input.keyCode; }
            float step = input.shift ? 10f : 3f;
            MoveSelectedByScreenDelta(x * step, y * step);
            return true;
        }

        private void MoveSelectedByScreenDelta(float x, float y)
        {
            WallState wall = _walls[_selectedWall];
            MahjongWallPoint2 center = new MahjongWallPoint2(
                _coordinateRoot.rect.width * .5f, _coordinateRoot.rect.height * .5f);
            MahjongWallPoint2 localShift = ToWallPoint(wall, center + new MahjongWallPoint2(
                x, y))
                - ToWallPoint(wall, center);
            MahjongWallAxisFrame frame = wall.Frame;
            frame.Axis1Start += localShift;
            frame.Axis2Start += localShift;
            frame.Axis3Start += localShift;
            ApplyFrame(wall, frame);
            RefreshOverlay();
        }

        private static MahjongWallPoint2 RowUnit(MahjongWallAxisFrame frame)
        {
            double radians = frame.AxisSlopeDegrees * Math.PI / 180d;
            return new MahjongWallPoint2((float)Math.Cos(radians), (float)Math.Sin(radians));
        }

        private static int TileCount(WallState wall) => wall.TileCountOverride > 0
            ? wall.TileCountOverride : MahjongWallGeometry.TileCount(wall.Seat);

        private static void GetRenderedAxisOffsets(WallState wall, MahjongWallAxisFrame frame,
            out float firstOffset, out float lastOffset)
        {
            int count = TileCount(wall);
            float gap = wall.GapAfterTileIndex >= 0 && wall.GapAfterTileIndex < count - 1
                ? Mathf.Max(0f, wall.ExtraGap) : 0f;
            float pitch = count <= 1 ? frame.AxisLength
                : Mathf.Max(.0001f, frame.AxisLength - gap) / (count - 1);
            if (!wall.PerTileFrontRenderer
                && wall.TextureMode == MahjongWallTextureMode.SideDiscardWall
                && wall.Seat == MahjongWallSeat.EastDiscard)
            {
                firstOffset = -pitch * .5f;
                lastOffset = frame.AxisLength + pitch * .5f;
                return;
            }

            float faceWidth = wall.FaceWidthOverride > .0001f ? wall.FaceWidthOverride
                : Mathf.Max(1f, pitch - 1f);
            if (wall.Seat == MahjongWallSeat.North)
            {
                MahjongWallTileGeometry[] template = MahjongWallGeometry.BuildReferenceRow(
                    wall.Seat, wall.TileCountOverride);
                float templateWidth = Length(template[0].BackFace[1] - template[0].BackFace[0]);
                faceWidth = Mathf.Min(templateWidth, faceWidth);
            }
            firstOffset = 0f;
            lastOffset = frame.AxisLength + faceWidth;
        }

        private static float AxisLengthForRenderedExtent(WallState wall, float extent)
        {
            float low = 40f, high = Mathf.Max(40f, extent);
            MahjongWallAxisFrame candidate = wall.Frame;
            for (int i = 0; i < 24; i++)
            {
                float middle = (low + high) * .5f;
                candidate.AxisLength = middle;
                GetRenderedAxisOffsets(wall, candidate, out float first, out float last);
                if (last - first < extent) low = middle;
                else high = middle;
            }
            return (low + high) * .5f;
        }

        private static bool SetOuterControlPoints(WallState wall, ref MahjongWallAxisFrame frame,
            MahjongWallPoint2 a1, MahjongWallPoint2 b1, MahjongWallPoint2 c1, MahjongWallPoint2 a2)
        {
            MahjongWallPoint2 depth = a2 - a1;
            float extent = Length(depth);
            if (extent < 40f) return false;
            MahjongWallAxisFrame candidate = frame;
            candidate.AxisSlopeDegrees = (float)(Math.Atan2(depth.Y, depth.X) * 180f / Math.PI);
            candidate.AxisLength = AxisLengthForRenderedExtent(wall, extent);
            if (HasPlacementOverride(wall, wall.FaceWidthOverride, wall.ExtraGap)
                && !PlacementValid(wall, candidate.AxisLength, wall.FaceWidthOverride, wall.ExtraGap)) return false;
            MahjongWallPoint2 row = RowUnit(candidate);
            GetRenderedAxisOffsets(wall, candidate, out float first, out _);
            MahjongWallPoint2 shift = row * first;
            candidate.Axis1Start = a1 - shift;
            candidate.Axis2Start = b1 - shift;
            candidate.Axis3Start = c1 - shift;
            frame = candidate;
            return true;
        }

        private MahjongWallPoint2 ToWallPoint(WallState wall, MahjongWallPoint2 editorPoint)
        {
            Vector3 editorLocal = new Vector3(
                editorPoint.X - _coordinateRoot.rect.width * .5f,
                _coordinateRoot.rect.height * .5f - editorPoint.Y, 0f);
            Vector3 world = _coordinateRoot.TransformPoint(editorLocal);
            Vector3 local = wall.Graphic.rectTransform.InverseTransformPoint(world);
            return new MahjongWallPoint2(local.x, -local.y);
        }

        private static MahjongWallPoint2[] GetControlPoints(WallState wall)
        {
            MahjongWallAxisFrame frame = wall.Frame;
            GetRenderedAxisOffsets(wall, frame, out float first, out float last);
            MahjongWallPoint2 start = RowUnit(frame) * first;
            MahjongWallPoint2 end = RowUnit(frame) * last;
            return new[] { frame.Axis1Start + start, frame.Axis2Start + start,
                frame.Axis3Start + start, frame.Axis1Start + end };
        }

        private MahjongWallPoint2[] GetControlPointsInEditor(WallState wall)
        {
            MahjongWallPoint2[] controls = GetControlPoints(wall);
            for (int i = 0; i < controls.Length; i++) controls[i] = ToEditorPoint(wall, controls[i]);
            return controls;
        }

        private MahjongWallPoint2[] GetDisplayControlPointsInEditor(WallState wall)
        {
            if (!UsesExplicitDisplayGeometry(wall)) return GetControlPointsInEditor(wall);
            MahjongWallPoint2[] corners = BuildDisplaySkeletonCorners(wall);
            return new[] { ToEditorPoint(wall, corners[0]), ToEditorPoint(wall, corners[1]),
                ToEditorPoint(wall, corners[2]), ToEditorPoint(wall, corners[4]) };
        }

        private MahjongWallPoint2[] GetEditOutlineCorners(WallState wall)
        {
            MahjongWallPoint2[] controls = GetControlPoints(wall);
            MahjongWallPoint2 d1 = controls[1] + controls[2] - controls[0];
            return new[]
            {
                ToEditorPoint(wall, controls[0]), ToEditorPoint(wall, controls[1]),
                ToEditorPoint(wall, d1), ToEditorPoint(wall, controls[2])
            };
        }

        private MahjongWallPoint2[] GetSelectionPolygon(WallState wall)
        {
            List<MahjongWallPoint2> points;
            if (UsesExplicitDisplayGeometry(wall))
            {
                MahjongWallPoint2[] localCorners = BuildDisplaySkeletonCorners(wall);
                points = new List<MahjongWallPoint2>(localCorners.Length);
                for (int i = 0; i < localCorners.Length; i++)
                    points.Add(ToEditorPoint(wall, localCorners[i]));
            }
            else
            {
                MahjongWallPoint2[] controls = GetControlPoints(wall);
                MahjongWallPoint2 a1 = controls[0], b1 = controls[1], c1 = controls[2];
                MahjongWallPoint2 d1 = b1 + c1 - a1;
                MahjongWallPoint2 depth = controls[3] - a1;
                points = new List<MahjongWallPoint2>(8)
                {
                    ToEditorPoint(wall, a1), ToEditorPoint(wall, b1), ToEditorPoint(wall, c1),
                    ToEditorPoint(wall, d1), ToEditorPoint(wall, controls[3]),
                    ToEditorPoint(wall, b1 + depth), ToEditorPoint(wall, c1 + depth),
                    ToEditorPoint(wall, d1 + depth)
                };
            }
            points.Sort((left, right) => left.X != right.X
                ? left.X.CompareTo(right.X) : left.Y.CompareTo(right.Y));
            var hull = new List<MahjongWallPoint2>(16);
            for (int i = 0; i < points.Count; i++)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }
            int lowerCount = hull.Count;
            for (int i = points.Count - 2; i >= 0; i--)
            {
                while (hull.Count > lowerCount && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
            return hull.ToArray();
        }

        private static float Cross(MahjongWallPoint2 origin, MahjongWallPoint2 a, MahjongWallPoint2 b) =>
            (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
        private static bool Contains(MahjongWallPoint2[] polygon, MahjongWallPoint2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                MahjongWallPoint2 a = polygon[i], b = polygon[j];
                bool crosses = (a.Y > point.Y) != (b.Y > point.Y)
                    && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
                if (crosses) inside = !inside;
            }
            return inside;
        }

        private static float Distance(MahjongWallPoint2 a, MahjongWallPoint2 b)
        {
            MahjongWallPoint2 difference = a - b;
            return Length(difference);
        }

        private static float Length(MahjongWallPoint2 value) =>
            (float)Math.Sqrt(value.X * value.X + value.Y * value.Y);

        private void EnsureOverlay()
        {
            if (_coordinateRoot == null || _handles[0] != null) return;
            for (int i = 0; i < 4; i++)
            {
                _edges[i] = CreateOverlayImage("Wall edit edge " + i, EdgeColor, new Vector2(1f, 3f));
                _handles[i] = CreateOverlayImage("Wall edit corner " + i, HandleColor, new Vector2(18f, 18f));
                _handleLabels[i] = CreateControlLabel("Wall control " + i,
                    new[] { "A1", "B1", "C1", "A2" }[i]);
            }
            _widthShrinkLabel = CreateControlLabel("Selected wall width shrink", string.Empty);
            _widthShrinkLabel.fontSize = 18;
            _widthShrinkLabel.color = SkeletonControlNodeColor;
            _widthShrinkLabel.rectTransform.sizeDelta = new Vector2(180f, 30f);
            RefreshOverlay();
        }

        private void EnsureSkeleton(WallState wall)
        {
            if (wall.SkeletonEdges == null)
            {
                wall.SkeletonEdges = new Image[12];
                for (int i = 0; i < wall.SkeletonEdges.Length; i++)
                    wall.SkeletonEdges[i] = CreateOverlayImage(
                        "Wall bounds skeleton edge " + wall.Seat + " " + i,
                        SkeletonColor, new Vector2(1f, 2f));
            }
            if (wall.SkeletonNodes == null)
            {
                wall.SkeletonNodes = new Image[8];
                wall.SkeletonLabels = new Text[8];
                string[] names = { "A1", "B1", "C1", "D1", "A2", "B2", "C2", "D2" };
                for (int i = 0; i < wall.SkeletonNodes.Length; i++)
                {
                    wall.SkeletonNodes[i] = CreateOverlayImage(
                        "Wall bounds skeleton node " + wall.Seat + " " + i,
                        i == 0 || i == 1 || i == 2 || i == 4
                            ? SkeletonControlNodeColor : SkeletonDerivedNodeColor,
                        new Vector2(14f, 14f));
                    wall.SkeletonLabels[i] = CreateControlLabel(
                        "Wall bounds skeleton label " + wall.Seat + " " + names[i], names[i]);
                    wall.SkeletonLabels[i].fontSize = 11;
                    wall.SkeletonLabels[i].color = i == 0 || i == 1 || i == 2 || i == 4
                        ? SkeletonControlNodeColor : Color.white;
                }
            }
        }

        private Image CreateOverlayImage(string name, Color color, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_coordinateRoot, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            go.SetActive(false);
            return image;
        }

        private Text CreateControlLabel(string name, string value)
        {
            Text existingLabel = _coordinateRoot.GetComponentInChildren<Text>(true);
            Font font = existingLabel != null ? existingLabel.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(_coordinateRoot, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(32f, 20f);
            Text label = go.GetComponent<Text>();
            label.text = value;
            label.font = font;
            label.fontSize = 13;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            go.SetActive(false);
            return label;
        }

        private void RefreshOverlay()
        {
            bool selected = _selectedWall >= 0 && _selectedWall < _walls.Count;
            bool showWidth = selected && _showSkeleton
                && SupportsWidthAdjustment(_walls[_selectedWall]);
            if (_widthShrinkLabel != null)
            {
                _widthShrinkLabel.gameObject.SetActive(showWidth);
                if (showWidth)
                {
                    WallState selectedWidthWall = _walls[_selectedWall];
                    MahjongWallPoint2[] widthCorners = BuildDisplaySkeletonCorners(selectedWidthWall);
                    MahjongWallPoint2 center = new MahjongWallPoint2(0f, 0f);
                    for (int i = 0; i < widthCorners.Length; i++)
                        center += ToEditorPoint(selectedWidthWall, widthCorners[i]);
                    center *= 1f / widthCorners.Length;
                    _widthShrinkLabel.text = selectedWidthWall.WidthByYAxis
                        ? "Y系数 " + selectedWidthWall.WidthFalloffPerScreenY.ToString("0.00000")
                        : "牌宽递减 " + Mathf.RoundToInt(selectedWidthWall.WidthShrinkPercent) + "%";
                    PositionAt(_widthShrinkLabel.rectTransform, new MahjongWallPoint2(
                        center.X + (selectedWidthWall.WidthByYAxis ? 70f : 0f), center.Y));
                    _widthShrinkLabel.transform.SetAsLastSibling();
                }
            }
            for (int i = 0; i < 4; i++)
            {
                if (_handles[i] != null) _handles[i].gameObject.SetActive(selected && _showSkeleton);
                if (_edges[i] != null) _edges[i].gameObject.SetActive(selected && !_showSkeleton);
                if (_handleLabels[i] != null) _handleLabels[i].gameObject.SetActive(false);
            }
            if (!selected) return;

            WallState wall = _walls[_selectedWall];
            MahjongWallPoint2[] corners = GetEditOutlineCorners(wall);
            MahjongWallPoint2[] controls = GetDisplayControlPointsInEditor(wall);
            for (int i = 0; i < 4; i++)
            {
                if (!_showSkeleton) _edges[i].transform.SetAsLastSibling();
                _handles[i].transform.SetAsLastSibling();
                _handleLabels[i].transform.SetAsLastSibling();
                PositionAt(_handles[i].rectTransform, controls[i]);
                PositionAt(_handleLabels[i].rectTransform,
                    new MahjongWallPoint2(controls[i].X + 15f, controls[i].Y - 15f));
                if (_showSkeleton) continue;
                MahjongWallPoint2 next = corners[(i + 1) % 4];
                Vector2 delta = new Vector2(next.X - corners[i].X, corners[i].Y - next.Y);
                RectTransform edge = _edges[i].rectTransform;
                edge.anchoredPosition = new Vector2((corners[i].X + next.X) * .5f,
                    -(corners[i].Y + next.Y) * .5f);
                edge.sizeDelta = new Vector2(delta.magnitude, 3f);
                edge.localEulerAngles = new Vector3(0f, 0f,
                    Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            }
        }

        private void RefreshSkeleton()
        {
            for (int i = 0; i < _walls.Count; i++)
            {
                WallState wall = _walls[i];
                if (wall.SkeletonEdges == null) continue;
                bool visible = _showSkeleton && (!_panelSkeleton || i == _selectedWall);
                for (int node = 0; node < wall.SkeletonNodes.Length; node++)
                {
                    wall.SkeletonNodes[node].gameObject.SetActive(false);
                    wall.SkeletonLabels[node].gameObject.SetActive(false);
                }
                MahjongWallPoint2[] corners = BuildDisplaySkeletonCorners(wall);
                int[,] edges =
                {
                    { 0, 1 }, { 1, 3 }, { 3, 2 }, { 2, 0 },
                    { 4, 5 }, { 5, 7 }, { 7, 6 }, { 6, 4 },
                    { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
                };
                for (int edge = 0; edge < wall.SkeletonEdges.Length; edge++)
                {
                    Image line = wall.SkeletonEdges[edge];
                    line.gameObject.SetActive(visible);
                    if (!visible) continue;
                    PositionLine(line.rectTransform,
                        ToEditorPoint(wall, corners[edges[edge, 0]]),
                        ToEditorPoint(wall, corners[edges[edge, 1]]));
                    line.transform.SetAsLastSibling();
                }
                RefreshTileFrames(wall);
            }

            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return;
            WallState selectedWall = _walls[_selectedWall];
            MahjongWallPoint2[] selectedCorners = BuildDisplaySkeletonCorners(selectedWall);
            MahjongWallPoint2[] editorCorners = new MahjongWallPoint2[selectedCorners.Length];
            MahjongWallPoint2 center = new MahjongWallPoint2(0f, 0f);
            for (int i = 0; i < selectedCorners.Length; i++)
            {
                editorCorners[i] = ToEditorPoint(selectedWall, selectedCorners[i]);
                center += editorCorners[i];
            }
            center *= 1f / editorCorners.Length;
            bool selectedHorizontal = Mathf.Abs(Mathf.Cos(selectedWall.Frame.AxisSlopeDegrees * Mathf.Deg2Rad))
                >= Mathf.Abs(Mathf.Sin(selectedWall.Frame.AxisSlopeDegrees * Mathf.Deg2Rad));
            MahjongWallPoint2 row = editorCorners[4] - editorCorners[0];
            float rowLength = Mathf.Sqrt(row.X * row.X + row.Y * row.Y);
            row = rowLength > .001f ? row * (1f / rowLength) : new MahjongWallPoint2(0f, 1f);
            MahjongWallPoint2 userDirection = new MahjongWallPoint2(
                _coordinateRoot.rect.width * .5f - center.X,
                _coordinateRoot.rect.height + 32f - center.Y);
            MahjongWallPoint2 userNormal = new MahjongWallPoint2(-row.Y, row.X);
            float normalLength = Mathf.Sqrt(userNormal.X * userNormal.X + userNormal.Y * userNormal.Y);
            if (normalLength > .001f) userNormal *= 1f / normalLength;
            if (userNormal.X * userDirection.X + userNormal.Y * userDirection.Y < 0f)
                userNormal *= -1f;
            MahjongWallPoint2 userAlongRow = row;
            if (row.X * userDirection.X + row.Y * userDirection.Y < 0f)
                userAlongRow = row * -1f;
            for (int node = 0; node < selectedWall.SkeletonNodes.Length; node++)
            {
                Image marker = selectedWall.SkeletonNodes[node];
                marker.gameObject.SetActive(_showSkeleton);
                Text label = selectedWall.SkeletonLabels[node];
                label.gameObject.SetActive(_showSkeleton);
                if (!_showSkeleton) continue;
                bool isControl = node == 0 || node == 1 || node == 2 || node == 4;
                marker.rectTransform.sizeDelta = isControl
                    ? new Vector2(20f, 20f) : new Vector2(9f, 9f);
                marker.color = isControl ? SkeletonControlNodeColor : SkeletonDerivedNodeColor;
                MahjongWallPoint2 editorPoint = editorCorners[node];
                PositionAt(marker.rectTransform, editorPoint);
                marker.transform.SetAsLastSibling();
                float labelStagger = (node % 4 - 1.5f) * 8f;
                MahjongWallPoint2 labelPosition = selectedHorizontal
                    ? new MahjongWallPoint2(editorPoint.X - 22f, editorPoint.Y + labelStagger)
                    : editorPoint + userAlongRow * 22f + userNormal * labelStagger;
                label.alignment = selectedHorizontal ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
                PositionAt(label.rectTransform, labelPosition);
                label.transform.SetAsLastSibling();
            }
        }

        private void RefreshTileFrames(WallState wall)
        {
            if (!_showTileFrames)
            {
                for (int i = 0; i < wall.TileFrameLines.Count; i++)
                    wall.TileFrameLines[i].gameObject.SetActive(false);
                return;
            }

            int lineIndex = 0;
            if (wall.PerTileFrontRenderer)
            {
                MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(
                    wall.Frame, wall.TileCountOverride, wall.FaceWidthOverride,
                    wall.GapAfterTileIndex, wall.ExtraGap);
                MahjongExplicitTileGeometry[] explicitTiles = MahjongExplicitFrontWallGraphic.BuildTileGeometry(
                    skeleton, wall.TileCountOverride, wall.GapAfterTileIndex, wall.ExtraGap,
                    wall.WidthShrinkPercent, wall.WidthByYAxis, wall.WidthFalloffPerScreenY);
                for (int tile = 0; tile < explicitTiles.Length; tile++)
                {
                    if (_panelSkeleton && (_selectedWall < 0 || _selectedWall >= _walls.Count || wall != _walls[_selectedWall] || !TileInView(wall, tile, explicitTiles.Length))) continue;
                    MahjongWallSurface[] outerFaces = MahjongExplicitFrontWallGraphic.GetOuterSurfaces(
                        explicitTiles[tile], wall.OuterPoint, tile, explicitTiles.Length);
                    if (_panelSkeleton)
                    {
                        for (int f = 0; f < DiagnosticFaces.Length; f++)
                        {
                            Color color = DiagnosticColors[f];
                            if (Array.IndexOf(outerFaces, DiagnosticFaces[f]) < 0) color.a = .3f;
                            if (f != _diagnosticFace) color.a *= .55f;
                            AddTileFrameFace(wall, MahjongExplicitFrontWallGraphic.Surface(explicitTiles[tile], DiagnosticFaces[f]), ref lineIndex, color);
                        }
                        continue;
                    }
                    for (int face = 0; face < outerFaces.Length; face++)
                    {
                        AddTileFrameFace(wall, MahjongExplicitFrontWallGraphic.Surface(
                            explicitTiles[tile], outerFaces[face]), ref lineIndex);
                    }
                }
                for (int i = lineIndex; i < wall.TileFrameLines.Count; i++)
                    wall.TileFrameLines[i].gameObject.SetActive(false);
                return;
            }

            MahjongWallTileGeometry[] tiles = MahjongWallGeometry.BuildWallFromAxes(
                wall.Seat, 0, wall.Frame, wall.FaceWidthOverride,
                wall.GapAfterTileIndex, wall.ExtraGap, wall.TileCountOverride);
            int nearUserEnd = 0;
            float maxCenterY = float.MinValue;
            for (int tile = 0; tile < tiles.Length; tile++)
            {
                float centerY = 0f;
                for (int corner = 0; corner < tiles[tile].FrontFace.Length; corner++)
                    centerY += tiles[tile].FrontFace[corner].Y;
                centerY /= tiles[tile].FrontFace.Length;
                if (centerY <= maxCenterY) continue;
                maxCenterY = centerY;
                nearUserEnd = tile;
            }
            for (int tile = 0; tile < tiles.Length; tile++)
            {
                MahjongWallTileGeometry geometry = tiles[tile];
                if (wall.FrontFace != MahjongWallFrontFace.Automatic)
                {
                    MahjongWallSurface[] visible = MahjongWallGeometry.VisibleSurfaces(
                        wall.FrontFace, wall.TextureMode);
                    for (int surface = 0; surface < visible.Length; surface++)
                        AddTileFrameFace(wall, MahjongWallGeometry.Surface(geometry, visible[surface]), ref lineIndex);
                }
                else if (wall.TextureMode == MahjongWallTextureMode.SideDiscardWall)
                {
                    if (wall.Seat == MahjongWallSeat.EastDiscard)
                        AddTileFrameFace(wall, geometry.SideDiscardTop, ref lineIndex);
                    AddTileFrameFace(wall, geometry.SideDiscardSide, ref lineIndex);
                }
                else if (wall.TextureMode == MahjongWallTextureMode.FaceUp)
                {
                    AddTileFrameFace(wall, geometry.BottomFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.SideFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.TopFace, ref lineIndex);
                }
                else if (wall.TextureMode == MahjongWallTextureMode.FaceUpWithSideAndBottom)
                {
                    AddTileFrameFace(wall, geometry.BottomFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.SideFace, ref lineIndex);
                }
                else if (wall.Seat == MahjongWallSeat.North)
                {
                    AddTileFrameFace(wall, geometry.BottomFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.SideFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.TopFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.BackFace, ref lineIndex);
                }
                else
                {
                    AddTileFrameFace(wall, geometry.SideFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.TopFace, ref lineIndex);
                    AddTileFrameFace(wall, geometry.FrontFace, ref lineIndex);
                }
            }
            for (int i = lineIndex; i < wall.TileFrameLines.Count; i++)
                wall.TileFrameLines[i].gameObject.SetActive(false);
        }

        private void AddTileFrameFace(WallState wall, MahjongWallPoint2[] face, ref int lineIndex, Color? diagnosticColor = null)
        {
            for (int edge = 0; edge < face.Length; edge++)
            {
                Image line;
                if (lineIndex == wall.TileFrameLines.Count)
                    wall.TileFrameLines.Add(CreateOverlayImage(
                        "Wall tile frame " + wall.Seat + " " + lineIndex,
                        TileFrameColor, new Vector2(1f, 1.5f)));
                line = wall.TileFrameLines[lineIndex++];
                line.color = diagnosticColor ?? TileFrameColor;
                line.gameObject.SetActive(true);
                PositionLine(line.rectTransform, ToEditorPoint(wall, face[edge]),
                    ToEditorPoint(wall, face[(edge + 1) % face.Length]));
                line.transform.SetAsLastSibling();
            }
        }

        private static MahjongWallPoint2[] BuildSkeletonCorners(WallState wall)
        {
            MahjongWallPoint2[] controls = GetControlPoints(wall);
            MahjongWallPoint2 axis1 = controls[0];
            MahjongWallPoint2 axis2 = controls[1];
            MahjongWallPoint2 axis3 = controls[2];
            MahjongWallPoint2 axis4 = axis2 + axis3 - axis1;
            MahjongWallPoint2 length = controls[3] - axis1;
            return new[]
            {
                axis1, axis2, axis3, axis4,
                axis1 + length, axis2 + length, axis3 + length, axis4 + length
            };
        }

        private static MahjongWallPoint2[] BuildDisplaySkeletonCorners(WallState wall)
        {
            if (!UsesExplicitDisplayGeometry(wall)) return BuildSkeletonCorners(wall);
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(
                wall.Frame, wall.TileCountOverride, wall.FaceWidthOverride,
                wall.GapAfterTileIndex, wall.ExtraGap);
            MahjongExplicitTileGeometry[] tiles = MahjongExplicitFrontWallGraphic.BuildTileGeometry(
                skeleton, wall.TileCountOverride, wall.GapAfterTileIndex, wall.ExtraGap,
                wall.WidthShrinkPercent, wall.WidthByYAxis, wall.WidthFalloffPerScreenY);
            if (tiles.Length == 0) return BuildSkeletonCorners(wall);
            MahjongExplicitTileGeometry first = tiles[0];
            MahjongExplicitTileGeometry last = tiles[tiles.Length - 1];
            return new[]
            {
                first.BackFace[0], first.BackFace[1], first.FrontFace[0], first.FrontFace[1],
                last.BackFace[3], last.BackFace[2], last.FrontFace[3], last.FrontFace[2]
            };
        }

        private static bool UsesExplicitDisplayGeometry(WallState wall) =>
            wall.PerTileFrontRenderer && wall.WidthByYAxis;

        private MahjongWallPoint2 ToEditorPoint(WallState wall, MahjongWallPoint2 point)
        {
            Vector3 world = wall.Graphic.rectTransform.TransformPoint(
                new Vector3(point.X, -point.Y, 0f));
            Vector3 local = _coordinateRoot.InverseTransformPoint(world);
            return new MahjongWallPoint2(local.x + _coordinateRoot.rect.width * .5f,
                _coordinateRoot.rect.height * .5f - local.y);
        }

        private static void PositionLine(RectTransform rect, MahjongWallPoint2 start,
            MahjongWallPoint2 end)
        {
            Vector2 delta = new Vector2(end.X - start.X, start.Y - end.Y);
            rect.anchoredPosition = new Vector2((start.X + end.X) * .5f,
                -(start.Y + end.Y) * .5f);
            rect.sizeDelta = new Vector2(delta.magnitude, 3f);
            rect.localEulerAngles = new Vector3(0f, 0f,
                Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }

        private static void PositionAt(RectTransform rect, MahjongWallPoint2 point)
        {
            rect.anchoredPosition = new Vector2(point.X, -point.Y);
        }

        private void LoadDefaultLayoutOnce()
        {
            if (_loaded) return;
            _loaded = true;
            string path = StartupLayoutPath(Environment.GetCommandLineArgs(), DefaultLayoutPath);
            if (!File.Exists(path)) return;
            try
            {
                LoadLayoutFile(path);
                Debug.Log("[MahjongWallLayoutEditor] Loaded layout " + path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[MahjongWallLayoutEditor] Could not load saved layout: " + exception.Message);
            }
        }

        private static string StartupLayoutPath(string[] args, string fallback)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--prototype-layout")
                    return i + 1 < args.Length ? args[i + 1] : string.Empty;
            return fallback;
        }

        private void SaveLayout()
        {
            if (!IsWindows())
            {
                Debug.LogWarning("[MahjongWallLayoutEditor] The layout file picker is available in Windows builds only.");
                return;
            }

            Debug.Log("[MahjongWallLayoutEditor] Opening save dialog");
            string path;
            _fileDialogOpen = true;
            try
            {
                path = WindowsLayoutFileDialog.PickSaveJson(PreferredLayoutPath);
            }
            catch (Exception exception)
            {
                Debug.LogError("[MahjongWallLayoutEditor] Could not open save dialog: " + exception);
                return;
            }
            finally
            {
                _fileDialogOpen = false;
            }
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                bool restorePreview = _previewA;
                if (restorePreview) ToggleComparison();
                LayoutDocument document = CaptureDocument();
                if (restorePreview) ToggleComparison();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(document, true));
                RememberPath(path);
                _savedLayoutPath = path;
                Debug.Log("[MahjongWallLayoutEditor] Saved layout " + path);
            }
            catch (Exception exception)
            {
                Debug.LogError("[MahjongWallLayoutEditor] Could not save layout: " + exception.Message);
            }
        }

        private LayoutDocument CaptureDocument()
        {
            LayoutEntry[] entries = new LayoutEntry[_walls.Count];
            for (int i = 0; i < _walls.Count; i++) entries[i] = ToEntry(_walls[i]);
            return new LayoutDocument
            {
                version = 2,
                walls = entries,
                deletedWallIds = new List<string>(_deletedWallIds).ToArray()
            };
        }

        private void LoadLayoutFromDialog()
        {
            if (!IsWindows())
            {
                Debug.LogWarning("[MahjongWallLayoutEditor] The layout file picker is available in Windows builds only.");
                return;
            }

            Debug.Log("[MahjongWallLayoutEditor] Opening load dialog");
            string path;
            _fileDialogOpen = true;
            try
            {
                path = WindowsLayoutFileDialog.PickOpenJson(DefaultLayoutPath);
            }
            catch (Exception exception)
            {
                Debug.LogError("[MahjongWallLayoutEditor] Could not open load dialog: " + exception);
                return;
            }
            finally
            {
                _fileDialogOpen = false;
            }
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                LoadLayoutFile(path);
                RememberPath(path);
                Debug.Log("[MahjongWallLayoutEditor] Loaded layout " + path);
            }
            catch (Exception exception)
            {
                Debug.LogError("[MahjongWallLayoutEditor] Could not load layout: " + exception.Message);
            }
        }

        private void LoadLayoutFile(string path)
        {
            LayoutDocument document = JsonUtility.FromJson<LayoutDocument>(File.ReadAllText(path));
            if (document == null || document.walls == null) throw new InvalidDataException("Layout has no wall entries.");
            foreach (LayoutEntry entry in document.walls)
            {
                if (entry == null || !IsValid(entry)) throw new InvalidDataException("Invalid wall entry.");
                WallState template = _walls.Find(item => item.Seat == (MahjongWallSeat)entry.seat && !item.IsRuntimeCopy);
                if (entry.hasPlacement && template != null
                    && !PlacementValid(template, entry.axisLength, entry.faceWidthOverride, entry.extraGap))
                    throw new InvalidDataException("Invalid placement for " + entry.wallId);
            }
            ApplyDocument(document);
            _loadedLayoutPath = Path.GetFullPath(path);
            _previewA = false; _aWallId = null; _undo.Clear(); _redo.Clear();
            _fieldName = null; _fieldText = null; _fieldError = null;
            _textUndoField = null; _textUndoWallId = null; _textUndo.Clear();
            _draggingHandle = -1; _movingByKeyboard = false;
            _isolate = false; _viewMode = 2; _viewPosition = 1;
            CaptureBaseline(); RefreshView(); RefreshOverlay();
        }

        private void ApplyDocument(LayoutDocument document)
        {
            if (document == null || document.walls == null) throw new InvalidDataException("Layout has no wall entries.");
            _serializedLayout = document;
            _hasSerializedLayout = true;
            _serializedLayoutIsLegacy = document.version <= 0;
            if (!_serializedLayoutIsLegacy)
            {
                ApplyVersionedDocument(document);
                RefreshOverlay();
                return;
            }

            for (int i = 0; i < document.walls.Length; i++)
            {
                LayoutEntry entry = document.walls[i];
                if (entry == null || !IsValid(entry)) continue;
                MahjongWallSeat seat = (MahjongWallSeat)entry.seat;
                MahjongWallAxisFrame frame = FromEntry(entry);
                _loadedFrames[seat] = frame;
                for (int j = 0; j < _walls.Count; j++)
                {
                    if (_walls[j].Seat != seat) continue;
                    ApplyFrame(_walls[j], frame);
                    if (entry.hasWidthShrinkPercent)
                        SetWidthShrinkPercent(_walls[j], entry.widthShrinkPercent);
                    if (entry.hasWidthFalloffPerScreenY)
                        SetWidthFalloffPerScreenY(_walls[j], entry.widthFalloffPerScreenY);
                    break;
                }
                if (entry.hasWidthShrinkPercent)
                    _loadedWidthShrinkPercents[seat] = Mathf.Clamp(entry.widthShrinkPercent, 0f, 100f);
                if (entry.hasWidthFalloffPerScreenY)
                    _loadedWidthFalloffsPerScreenY[seat] = Mathf.Clamp(entry.widthFalloffPerScreenY, 0f, .05f);
            }
            RefreshOverlay();
        }

        private void ApplyVersionedDocument(LayoutDocument document)
        {
            _loadedFrames.Clear();
            _loadedWidthShrinkPercents.Clear();
            _loadedWidthFalloffsPerScreenY.Clear();
            _deletedWallIds.Clear();
            _restoredSerializedCopyIds.Clear();
            if (document.deletedWallIds != null)
                for (int i = 0; i < document.deletedWallIds.Length; i++)
                    if (!string.IsNullOrEmpty(document.deletedWallIds[i]))
                        _deletedWallIds.Add(document.deletedWallIds[i]);

            for (int i = 0; i < document.walls.Length; i++)
            {
                LayoutEntry entry = document.walls[i];
                if (entry == null || entry.isRuntimeCopy || !IsValid(entry)) continue;
                MahjongWallSeat seat = (MahjongWallSeat)entry.seat;
                _loadedFrames[seat] = FromEntry(entry);
                if (entry.hasWidthShrinkPercent)
                    _loadedWidthShrinkPercents[seat] = Mathf.Clamp(entry.widthShrinkPercent, 0f, 100f);
                if (entry.hasWidthFalloffPerScreenY)
                    _loadedWidthFalloffsPerScreenY[seat] = Mathf.Clamp(entry.widthFalloffPerScreenY, 0f, .05f);
            }

            for (int i = _walls.Count - 1; i >= 0; i--)
            {
                WallState wall = _walls[i];
                if (wall.IsRuntimeCopy || _deletedWallIds.Contains(wall.WallId))
                {
                    DestroyWallObjects(wall);
                    _walls.RemoveAt(i);
                    continue;
                }
                ApplySerializedEntryIfPresent(wall);
            }
            _selectedWall = -1;
            _draggingHandle = -1;

            // Copies are a snapshot: remove current ones and recreate precisely those in the file.
            foreach (KeyValuePair<MahjongWallSeat, Action<MahjongWallAxisFrame, float, float, string, bool>> factory
                in new List<KeyValuePair<MahjongWallSeat, Action<MahjongWallAxisFrame, float, float, string, bool>>>(_wallFactoriesBySeat))
            {
                bool hasBase = false;
                for (int i = 0; i < _walls.Count; i++)
                    if (!_walls[i].IsRuntimeCopy && _walls[i].Seat == factory.Key) { hasBase = true; break; }
                LayoutEntry baseEntry = FindEntry(document, BaseWallId(factory.Key));
                if (!hasBase && factory.Value != null && baseEntry != null
                    && !_deletedWallIds.Contains(BaseWallId(factory.Key)))
                    factory.Value(FromEntry(baseEntry), baseEntry.widthShrinkPercent,
                        baseEntry.widthFalloffPerScreenY, BaseWallId(factory.Key), false);
            }

            for (int i = 0; i < document.walls.Length; i++)
            {
                LayoutEntry entry = document.walls[i];
                if (entry == null || !entry.isRuntimeCopy || !IsValid(entry)
                    || string.IsNullOrEmpty(entry.wallId) || _deletedWallIds.Contains(entry.wallId)) continue;
                RestoreSerializedCopy(entry);
            }
        }

        private void ApplySerializedEntryIfPresent(WallState wall)
        {
            if (!_hasSerializedLayout || _serializedLayoutIsLegacy || wall.IsRuntimeCopy) return;
            LayoutEntry entry = FindEntry(_serializedLayout, wall.WallId);
            if (entry == null || !IsValid(entry)) return;
            if (entry.hasPlacement && !PlacementValid(wall, entry.axisLength, entry.faceWidthOverride, entry.extraGap))
            {
                Debug.LogWarning("[MahjongWallLayoutEditor] Invalid placement for " + wall.WallId);
                return;
            }
            ApplyFrame(wall, FromEntry(entry));
            if (entry.hasWidthShrinkPercent) SetWidthShrinkPercent(wall, entry.widthShrinkPercent);
            if (entry.hasWidthFalloffPerScreenY) SetWidthFalloffPerScreenY(wall, entry.widthFalloffPerScreenY);
            SetPlacement(wall, entry.hasPlacement ? entry.faceWidthOverride : wall.DefaultFaceWidthOverride,
                entry.hasPlacement ? entry.extraGap : wall.DefaultExtraGap);
            RefreshSkeleton();
        }

        private static LayoutEntry FindEntry(LayoutDocument document, string wallId)
        {
            if (document == null || document.walls == null) return null;
            for (int i = 0; i < document.walls.Length; i++)
            {
                LayoutEntry entry = document.walls[i];
                if (entry == null) continue;
                string candidateId = string.IsNullOrEmpty(entry.wallId) && !entry.isRuntimeCopy
                    ? BaseWallId((MahjongWallSeat)entry.seat) : entry.wallId;
                if (string.Equals(candidateId, wallId, StringComparison.Ordinal)) return entry;
            }
            return null;
        }

        private void RestoreSerializedCopiesForSeat(MahjongWallSeat seat)
        {
            if (!_hasSerializedLayout || _serializedLayoutIsLegacy || !_wallFactoriesBySeat.ContainsKey(seat)) return;
            for (int i = 0; i < _serializedLayout.walls.Length; i++)
            {
                LayoutEntry entry = _serializedLayout.walls[i];
                if (entry == null || !entry.isRuntimeCopy || entry.seat != (int)seat
                    || !IsValid(entry) || string.IsNullOrEmpty(entry.wallId)) continue;
                RestoreSerializedCopy(entry);
            }
        }

        private void RestoreSerializedCopy(LayoutEntry entry)
        {
            if (_restoredSerializedCopyIds.Contains(entry.wallId)) return;
            if (!_wallFactoriesBySeat.TryGetValue((MahjongWallSeat)entry.seat,
                out Action<MahjongWallAxisFrame, float, float, string, bool> factory)) return;
            if (factory == null) return;
            WallState template = _walls.Find(item => item.Seat == (MahjongWallSeat)entry.seat && !item.IsRuntimeCopy);
            if (entry.hasPlacement && template != null
                && !PlacementValid(template, entry.axisLength, entry.faceWidthOverride, entry.extraGap))
            {
                Debug.LogWarning("[MahjongWallLayoutEditor] Invalid placement for " + entry.wallId);
                return;
            }
            _restoredSerializedCopyIds.Add(entry.wallId);
            factory(FromEntry(entry), entry.widthShrinkPercent, entry.widthFalloffPerScreenY,
                entry.wallId, true);
            WallState copy = _walls.Find(item => item.WallId == entry.wallId);
            if (copy != null && entry.hasPlacement) { SetPlacement(copy, entry.faceWidthOverride, entry.extraGap); RefreshSkeleton(); }
        }

        private static bool IsValid(LayoutEntry entry) =>
            entry.seat >= (int)MahjongWallSeat.North && entry.seat <= (int)MahjongWallSeat.EastDiscard
            && IsFinite(entry.axis1X) && IsFinite(entry.axis1Y)
            && IsFinite(entry.axis2X) && IsFinite(entry.axis2Y)
            && IsFinite(entry.axis3X) && IsFinite(entry.axis3Y)
            && IsFinite(entry.axisLength) && entry.axisLength >= 40f
            && IsFinite(entry.axisSlopeDegrees) && IsFinite(entry.faceWidthAxisOffsetDegrees)
            && IsFinite(entry.faceUvRotationDegrees)
            && (!entry.hasWidthShrinkPercent || IsFinite(entry.widthShrinkPercent))
            && (!entry.hasWidthFalloffPerScreenY || IsFinite(entry.widthFalloffPerScreenY))
            && (!entry.hasPlacement || (IsFinite(entry.faceWidthOverride) && entry.faceWidthOverride >= 0f
                && IsFinite(entry.extraGap) && entry.extraGap >= 0f && entry.extraGap < entry.axisLength));

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static LayoutEntry ToEntry(WallState wall) => new LayoutEntry
        {
            wallId = wall.WallId,
            isRuntimeCopy = wall.IsRuntimeCopy,
            seat = (int)wall.Seat,
            axis1X = wall.Frame.Axis1Start.X,
            axis1Y = wall.Frame.Axis1Start.Y,
            axis2X = wall.Frame.Axis2Start.X,
            axis2Y = wall.Frame.Axis2Start.Y,
            axis3X = wall.Frame.Axis3Start.X,
            axis3Y = wall.Frame.Axis3Start.Y,
            axisLength = wall.Frame.AxisLength,
            axisSlopeDegrees = wall.Frame.AxisSlopeDegrees,
            faceWidthAxisOffsetDegrees = wall.Frame.FaceWidthAxisOffsetDegrees,
            faceUvRotationDegrees = wall.Frame.FaceUvRotationDegrees,
            widthShrinkPercent = wall.WidthShrinkPercent,
            hasWidthShrinkPercent = true,
            widthFalloffPerScreenY = wall.WidthFalloffPerScreenY,
            hasWidthFalloffPerScreenY = true,
            hasPlacement = HasPlacementOverride(wall, wall.FaceWidthOverride, wall.ExtraGap),
            faceWidthOverride = wall.FaceWidthOverride,
            extraGap = wall.ExtraGap,
            explicitTileGeometry = true
        };

        private static MahjongWallAxisFrame FromEntry(LayoutEntry entry)
        {
            var frame = new MahjongWallAxisFrame(
                new MahjongWallPoint2(entry.axis1X, entry.axis1Y),
                new MahjongWallPoint2(entry.axis2X, entry.axis2Y),
                new MahjongWallPoint2(entry.axis3X, entry.axis3Y),
                entry.axisLength, entry.axisSlopeDegrees, entry.faceWidthAxisOffsetDegrees,
                entry.faceUvRotationDegrees);
            return entry.explicitTileGeometry ? frame : NormalizeLoadedFrame((MahjongWallSeat)entry.seat, frame);
        }

        private static MahjongWallAxisFrame NormalizeLoadedFrame(MahjongWallSeat seat, MahjongWallAxisFrame frame)
        {
            if (seat != MahjongWallSeat.WestDiscard) return frame;
            if (Mathf.Abs(frame.FaceWidthAxisOffsetDegrees) < 1f
                || Mathf.Abs(Mathf.DeltaAngle(0f, frame.AxisSlopeDegrees + frame.FaceWidthAxisOffsetDegrees)) > .1f)
                return frame;
            MahjongWallPoint2 row = RowUnit(frame);
            MahjongWallPoint2 rise = frame.Axis2Start - frame.Axis1Start;
            float length = Mathf.Sqrt(rise.X * rise.X + rise.Y * rise.Y);
            if (length < .001f || Mathf.Abs(row.X * rise.Y - row.Y * rise.X) >= length * .3f)
                return frame;
            MahjongWallPoint2 depth = frame.Axis3Start - frame.Axis1Start;
            float thickness = Mathf.Sqrt(depth.X * depth.X + depth.Y * depth.Y);
            // Old side-discard frames used a separate face-width axis; the explicit renderer uses the row axis.
            return new MahjongWallAxisFrame(frame.Axis1Start,
                frame.Axis1Start + new MahjongWallPoint2(-row.Y * length, row.X * length),
                frame.Axis1Start - row * thickness, frame.AxisLength, frame.AxisSlopeDegrees,
                frame.FaceWidthAxisOffsetDegrees, frame.FaceUvRotationDegrees);
        }

        private bool IsWindows() => Application.platform == RuntimePlatform.WindowsPlayer
            || Application.platform == RuntimePlatform.WindowsEditor;

        private string DefaultLayoutPath => @"D:\majiang\default.json";
        private string PreferredLayoutPath
        {
            get
            {
                string lastPath = PlayerPrefs.GetString(LastLayoutPathKey, string.Empty);
                if (!string.IsNullOrEmpty(lastPath) && File.Exists(lastPath)) return lastPath;
                return Path.Combine(Application.persistentDataPath, DefaultLayoutFileName);
            }
        }

        private static void RememberPath(string path)
        {
            PlayerPrefs.SetString(LastLayoutPathKey, path);
            PlayerPrefs.Save();
        }

        private static class WindowsLayoutFileDialog
        {
            private const int FileMustExist = 0x00001000;
            private const int PathMustExist = 0x00000800;
            private const int NoChangeDirectory = 0x00000008;
            private const int Explorer = 0x00080000;
            private const int OverwritePrompt = 0x00000002;

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeOpenFileName
            {
                public int structSize;
                public IntPtr hwndOwner;
                public IntPtr hInstance;
                public IntPtr filter;
                public IntPtr customFilter;
                public int maxCustomFilter;
                public int filterIndex;
                public IntPtr file;
                public int maxFile;
                public IntPtr fileTitle;
                public int maxFileTitle;
                public IntPtr initialDirectory;
                public IntPtr title;
                public int flags;
                public short fileOffset;
                public short fileExtension;
                public IntPtr defaultExtension;
                public IntPtr customData;
                public IntPtr hook;
                public IntPtr templateName;
                public IntPtr reserved;
                public int reservedInt;
                public int flagsEx;
            }

            [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetOpenFileName(IntPtr dialog);

            [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetSaveFileName(IntPtr dialog);

            [DllImport("user32.dll")]
            private static extern IntPtr GetActiveWindow();

            public static string PickOpenJson(string initialPath) => ShowJsonDialog(initialPath,
                "Load Mahjong wall layout", FileMustExist | PathMustExist | NoChangeDirectory | Explorer, false);

            public static string PickSaveJson(string initialPath) => ShowJsonDialog(initialPath,
                "Save Mahjong wall layout", OverwritePrompt | PathMustExist | NoChangeDirectory | Explorer, true);

            private static string ShowJsonDialog(string initialPath, string title, int flags, bool save)
            {
                string directory = Path.GetDirectoryName(initialPath);
                if (string.IsNullOrEmpty(directory)) directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string filterText = "Mahjong wall layout (*.json)\0*.json\0JSON files (*.json)\0*.json\0\0";
                IntPtr filter = Marshal.AllocHGlobal(filterText.Length * sizeof(char));
                Marshal.Copy(filterText.ToCharArray(), 0, filter, filterText.Length);
                IntPtr initialDirectory = Marshal.StringToHGlobalUni(directory);
                IntPtr nativeTitle = Marshal.StringToHGlobalUni(title);
                IntPtr extension = Marshal.StringToHGlobalUni("json");
                IntPtr file = Marshal.AllocHGlobal(1024 * sizeof(char));
                IntPtr fileTitle = Marshal.AllocHGlobal(260 * sizeof(char));
                IntPtr dialogPointer = IntPtr.Zero;
                try
                {
                    Marshal.Copy(new char[1024], 0, file, 1024);
                    Marshal.Copy(new char[260], 0, fileTitle, 260);
                    if (save)
                    {
                        char[] initialName = (Path.GetFileName(initialPath) + "\0").ToCharArray();
                        Marshal.Copy(initialName, 0, file, initialName.Length);
                    }

                    NativeOpenFileName dialog = new NativeOpenFileName
                    {
                        structSize = Marshal.SizeOf(typeof(NativeOpenFileName)),
                        hwndOwner = GetActiveWindow(),
                        filter = filter,
                        filterIndex = 1,
                        file = file,
                        maxFile = 1024,
                        fileTitle = fileTitle,
                        maxFileTitle = 260,
                        initialDirectory = initialDirectory,
                        title = nativeTitle,
                        flags = flags,
                        defaultExtension = extension
                    };
                    dialogPointer = Marshal.AllocHGlobal(dialog.structSize);
                    Marshal.StructureToPtr(dialog, dialogPointer, false);
                    bool confirmed = save ? GetSaveFileName(dialogPointer) : GetOpenFileName(dialogPointer);
                    return confirmed ? Marshal.PtrToStringUni(file) : string.Empty;
                }
                finally
                {
                    if (dialogPointer != IntPtr.Zero) Marshal.FreeHGlobal(dialogPointer);
                    Marshal.FreeHGlobal(fileTitle);
                    Marshal.FreeHGlobal(file);
                    Marshal.FreeHGlobal(extension);
                    Marshal.FreeHGlobal(nativeTitle);
                    Marshal.FreeHGlobal(initialDirectory);
                    Marshal.FreeHGlobal(filter);
                }
            }
        }
    }
}

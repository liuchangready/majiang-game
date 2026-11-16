using System;
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
            return _loadedFrames.TryGetValue(seat, out MahjongWallAxisFrame frame) ? frame : fallback;
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
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
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
                    AdjustSelectedWidthShrink(1f);
                }
                if (keyboard.pKey.wasReleasedThisFrame) _increaseDepthKeyHeld = false;
                if (keyboard.mKey.wasPressedThisFrame && !_decreaseDepthKeyHeld)
                {
                    _decreaseDepthKeyHeld = true;
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
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _coordinateRoot, screen, null, out Vector2 local)) return;
            MahjongWallPoint2 pointer = new MahjongWallPoint2(
                local.x + _coordinateRoot.rect.width * .5f,
                _coordinateRoot.rect.height * .5f - local.y);

            if (Mouse.current.leftButton.wasPressedThisFrame)
                BeginPointerAction(pointer);
            if (_draggingHandle >= 0 && Mouse.current.leftButton.isPressed)
                DragHandle(pointer);
            if (Mouse.current.leftButton.wasReleasedThisFrame)
                _draggingHandle = -1;
        }

        private void OnGUI()
        {
            if (_coordinateRoot == null || Event.current == null) return;
            if (Event.current.type == EventType.KeyUp)
            {
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
                AdjustSelectedWidthShrink(1f);
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.M && !_decreaseDepthKeyHeld)
            {
                _decreaseDepthKeyHeld = true;
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

        private void DeleteSelectedWall()
        {
            if (_selectedWall < 0 || _selectedWall >= _walls.Count) return;
            WallState wall = _walls[_selectedWall];
            if (!wall.IsRuntimeCopy) _deletedWallIds.Add(wall.WallId);
            DestroyWallObjects(wall);
            _walls.RemoveAt(_selectedWall);
            _selectedWall = -1;
            _draggingHandle = -1;
            RefreshSkeleton();
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
            _runtimeCopyCount++;
            RefreshSkeleton();
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
            wall.PerTileFrontRenderer
            && (wall.Seat == MahjongWallSeat.East || wall.Seat == MahjongWallSeat.EastDiscard);

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
                SetOuterControlPoints(wall, ref frame, outer[0] + delta, outer[1], outer[2], outer[3]);
            }
            else if (_draggingHandle == 1)
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                SetOuterControlPoints(wall, ref frame, outer[0], outer[1] + delta, outer[2], outer[3]);
            }
            else if (_draggingHandle == 2)
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                SetOuterControlPoints(wall, ref frame, outer[0], outer[1], outer[2] + delta, outer[3]);
            }
            else if (_draggingHandle == 3)
            {
                MahjongWallPoint2[] outer = GetControlPoints(wall);
                SetOuterControlPoints(wall, ref frame, outer[0], outer[1], outer[2], wallPointer);
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
            if (Math.Abs(x) < .001f && Math.Abs(y) < .001f) return;
            float speed = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? 600f : 160f;
            WallState wall = _walls[_selectedWall];
            MahjongWallPoint2 center = new MahjongWallPoint2(
                _coordinateRoot.rect.width * .5f, _coordinateRoot.rect.height * .5f);
            MahjongWallPoint2 localShift = ToWallPoint(wall, center + new MahjongWallPoint2(
                x * speed * Time.unscaledDeltaTime, y * speed * Time.unscaledDeltaTime))
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

        private static void SetOuterControlPoints(WallState wall, ref MahjongWallAxisFrame frame,
            MahjongWallPoint2 a1, MahjongWallPoint2 b1, MahjongWallPoint2 c1, MahjongWallPoint2 a2)
        {
            MahjongWallPoint2 depth = a2 - a1;
            float extent = Length(depth);
            if (extent < 40f) return;
            frame.AxisSlopeDegrees = (float)(Math.Atan2(depth.Y, depth.X) * 180f / Math.PI);
            frame.AxisLength = AxisLengthForRenderedExtent(wall, extent);
            MahjongWallPoint2 row = RowUnit(frame);
            GetRenderedAxisOffsets(wall, frame, out float first, out _);
            MahjongWallPoint2 shift = row * first;
            frame.Axis1Start = a1 - shift;
            frame.Axis2Start = b1 - shift;
            frame.Axis3Start = c1 - shift;
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
                bool visible = _showSkeleton;
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
                    MahjongWallSurface[] outerFaces = MahjongExplicitFrontWallGraphic.GetOuterSurfaces(
                        explicitTiles[tile], wall.OuterPoint, tile, explicitTiles.Length);
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

        private void AddTileFrameFace(WallState wall, MahjongWallPoint2[] face, ref int lineIndex)
        {
            for (int edge = 0; edge < face.Length; edge++)
            {
                Image line;
                if (lineIndex == wall.TileFrameLines.Count)
                    wall.TileFrameLines.Add(CreateOverlayImage(
                        "Wall tile frame " + wall.Seat + " " + lineIndex,
                        TileFrameColor, new Vector2(1f, 1.5f)));
                line = wall.TileFrameLines[lineIndex++];
                line.color = TileFrameColor;
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
            string path = DefaultLayoutPath;
            if (!File.Exists(path)) return;
            try
            {
                ApplyDocument(JsonUtility.FromJson<LayoutDocument>(File.ReadAllText(path)));
                Debug.Log("[MahjongWallLayoutEditor] Loaded layout " + path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[MahjongWallLayoutEditor] Could not load saved layout: " + exception.Message);
            }
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
                LayoutDocument document = CaptureDocument();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(document, true));
                RememberPath(path);
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
                ApplyDocument(JsonUtility.FromJson<LayoutDocument>(File.ReadAllText(path)));
                RememberPath(path);
                Debug.Log("[MahjongWallLayoutEditor] Loaded layout " + path);
            }
            catch (Exception exception)
            {
                Debug.LogError("[MahjongWallLayoutEditor] Could not load layout: " + exception.Message);
            }
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
            ApplyFrame(wall, FromEntry(entry));
            if (entry.hasWidthShrinkPercent) SetWidthShrinkPercent(wall, entry.widthShrinkPercent);
            if (entry.hasWidthFalloffPerScreenY) SetWidthFalloffPerScreenY(wall, entry.widthFalloffPerScreenY);
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
            _restoredSerializedCopyIds.Add(entry.wallId);
            factory(FromEntry(entry), entry.widthShrinkPercent, entry.widthFalloffPerScreenY,
                entry.wallId, true);
        }

        private static bool IsValid(LayoutEntry entry) =>
            entry.seat >= (int)MahjongWallSeat.North && entry.seat <= (int)MahjongWallSeat.EastDiscard
            && IsFinite(entry.axis1X) && IsFinite(entry.axis1Y)
            && IsFinite(entry.axis2X) && IsFinite(entry.axis2Y)
            && IsFinite(entry.axis3X) && IsFinite(entry.axis3Y)
            && IsFinite(entry.axisLength) && entry.axisLength >= 40f
            && IsFinite(entry.axisSlopeDegrees);

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
            hasWidthFalloffPerScreenY = true
        };

        private static MahjongWallAxisFrame FromEntry(LayoutEntry entry) => new MahjongWallAxisFrame(
            new MahjongWallPoint2(entry.axis1X, entry.axis1Y),
            new MahjongWallPoint2(entry.axis2X, entry.axis2Y),
            new MahjongWallPoint2(entry.axis3X, entry.axis3Y),
            entry.axisLength, entry.axisSlopeDegrees, entry.faceWidthAxisOffsetDegrees,
            entry.faceUvRotationDegrees);

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

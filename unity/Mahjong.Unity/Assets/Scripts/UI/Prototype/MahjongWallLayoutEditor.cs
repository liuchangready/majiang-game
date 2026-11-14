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

        [Serializable]
        private sealed class LayoutDocument
        {
            public LayoutEntry[] walls = Array.Empty<LayoutEntry>();
        }

        [Serializable]
        private sealed class LayoutEntry
        {
            public int seat;
            public float axis1X, axis1Y;
            public float axis2X, axis2Y;
            public float axis3X, axis3Y;
            public float axisLength;
            public float axisSlopeDegrees;
        }

        private sealed class WallState
        {
            public MahjongWallSeat Seat;
            public MahjongWallRowGraphic Graphic;
            public MahjongWallAxisFrame Frame;
            public MahjongWallTextureMode TextureMode;
            public float FaceWidthOverride;
            public int GapAfterTileIndex;
            public float ExtraGap;
            public readonly List<MahjongWallFaceRevealGraphic> FaceGraphics =
                new List<MahjongWallFaceRevealGraphic>();
        }

        private readonly List<WallState> _walls = new List<WallState>(3);
        private readonly Dictionary<MahjongWallSeat, MahjongWallAxisFrame> _loadedFrames =
            new Dictionary<MahjongWallSeat, MahjongWallAxisFrame>();
        private readonly Image[] _handles = new Image[4];
        private readonly Image[] _edges = new Image[4];
        private RectTransform _coordinateRoot;
        private int _selectedWall = -1;
        private int _draggingHandle = -1;
        private int _lastShortcutFrame = -1;
        private MahjongWallPoint2 _lastPointer;
        private bool _loaded;
        private bool _saveKeyHeld;
        private bool _loadKeyHeld;
        private bool _fileDialogOpen;

        public void Configure(RectTransform coordinateRoot)
        {
            _coordinateRoot = coordinateRoot;
        }

        public MahjongWallAxisFrame ResolveFrame(MahjongWallSeat seat, MahjongWallAxisFrame fallback)
        {
            LoadDefaultLayoutOnce();
            return _loadedFrames.TryGetValue(seat, out MahjongWallAxisFrame frame) ? frame : fallback;
        }

        public void Register(MahjongWallSeat seat, MahjongWallRowGraphic graphic,
            MahjongWallAxisFrame frame, MahjongWallTextureMode textureMode,
            float faceWidthOverride, int gapAfterTileIndex, float extraGap,
            IReadOnlyList<MahjongWallFaceRevealGraphic> faceGraphics)
        {
            WallState state = null;
            for (int i = 0; i < _walls.Count; i++)
            {
                if (_walls[i].Seat != seat) continue;
                state = _walls[i];
                break;
            }

            if (state == null)
            {
                state = new WallState { Seat = seat };
                _walls.Add(state);
            }
            state.Graphic = graphic;
            state.Frame = frame;
            state.TextureMode = textureMode;
            state.FaceWidthOverride = faceWidthOverride;
            state.GapAfterTileIndex = gapAfterTileIndex;
            state.ExtraGap = extraGap;
            state.FaceGraphics.Clear();
            if (faceGraphics != null) state.FaceGraphics.AddRange(faceGraphics);
            EnsureOverlay();
        }

        private void Update()
        {
            if (_coordinateRoot == null) return;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
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
                if (Event.current.keyCode == KeyCode.S) _saveKeyHeld = false;
                if (Event.current.keyCode == KeyCode.L) _loadKeyHeld = false;
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
            if (_selectedWall >= 0)
            {
                MahjongWallPoint2[] selectedCorners = GetVisibleCorners(_walls[_selectedWall]);
                for (int handle = 0; handle < selectedCorners.Length; handle++)
                {
                    if (Distance(pointer, selectedCorners[handle]) > HandleHitRadius) continue;
                    _draggingHandle = handle;
                    _lastPointer = pointer;
                    return;
                }
            }

            for (int i = _walls.Count - 1; i >= 0; i--)
            {
                if (!Contains(GetVisibleCorners(_walls[i]), pointer)) continue;
                _selectedWall = i;
                _draggingHandle = -1;
                RefreshOverlay();
                return;
            }

            _selectedWall = -1;
            _draggingHandle = -1;
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

            if (_draggingHandle == 0)
            {
                frame.Axis1Start += delta;
                frame.Axis2Start += delta;
                frame.Axis3Start += delta;
            }
            else if (_draggingHandle == 1 || _draggingHandle == 2)
            {
                MahjongWallPoint2 origin = _draggingHandle == 1
                    ? VisibleStart(wall) : VisibleStart(wall) + (frame.Axis2Start - frame.Axis1Start);
                MahjongWallPoint2 span = wallPointer - origin;
                float length = Length(span);
                if (length < 40f) return;
                frame.AxisLength = AxisLengthForWall(wall, length);
                frame.AxisSlopeDegrees = (float)(Math.Atan2(span.Y, span.X) * 180d / Math.PI);
            }
            else
            {
                MahjongWallPoint2 rise = wallPointer - VisibleStart(wall);
                if (Length(rise) < 20f) return;
                frame.Axis2Start = frame.Axis1Start + rise;
            }

            ApplyFrame(wall, frame);
            RefreshOverlay();
        }

        private static void ApplyFrame(WallState wall, MahjongWallAxisFrame frame)
        {
            wall.Frame = frame;
            wall.Graphic.SetAxisFrame(frame);
            for (int i = 0; i < wall.FaceGraphics.Count; i++)
                wall.FaceGraphics[i].SetAxisFrame(frame);
        }

        private static float AxisLengthForWall(WallState wall, float wallExtent)
        {
            if (wall.FaceWidthOverride <= 0f)
                return MahjongWallGeometry.AxisLengthForWallExtent(wall.Seat, wallExtent);
            int count = MahjongWallGeometry.TileCount(wall.Seat);
            float pitch = (wallExtent - wall.FaceWidthOverride) / Mathf.Max(1, count - 1);
            if (wall.GapAfterTileIndex >= 0 && wall.GapAfterTileIndex < count - 1)
                pitch = (wallExtent - wall.FaceWidthOverride - wall.ExtraGap) / Mathf.Max(1, count - 1);
            return Mathf.Max(40f, pitch * (count - 1) +
                (wall.GapAfterTileIndex >= 0 && wall.GapAfterTileIndex < count - 1 ? wall.ExtraGap : 0f));
        }

        private static MahjongWallPoint2 VisibleStart(WallState wall) =>
            wall.Seat == MahjongWallSeat.North || wall.TextureMode == MahjongWallTextureMode.FaceUp
                ? wall.Frame.Axis1Start : wall.Frame.Axis3Start;

        private MahjongWallPoint2 ToWallPoint(WallState wall, MahjongWallPoint2 editorPoint)
        {
            Vector3 editorLocal = new Vector3(
                editorPoint.X - _coordinateRoot.rect.width * .5f,
                _coordinateRoot.rect.height * .5f - editorPoint.Y, 0f);
            Vector3 world = _coordinateRoot.TransformPoint(editorLocal);
            Vector3 local = wall.Graphic.rectTransform.InverseTransformPoint(world);
            return new MahjongWallPoint2(local.x, -local.y);
        }

        private MahjongWallPoint2[] GetVisibleCorners(WallState wall)
        {
            MahjongWallAxisFrame frame = wall.Frame;
            MahjongWallPoint2 origin = VisibleStart(wall);
            double radians = frame.AxisSlopeDegrees * Math.PI / 180d;
            MahjongWallPoint2 span = new MahjongWallPoint2(
                (float)Math.Cos(radians) * frame.AxisLength,
                (float)Math.Sin(radians) * frame.AxisLength);
            float faceWidth = wall.FaceWidthOverride > 0f
                ? wall.FaceWidthOverride
                : MahjongWallGeometry.ReferenceRenderedFaceWidth(wall.Seat, frame.AxisLength);
            span *= 1f + faceWidth / frame.AxisLength;
            MahjongWallPoint2 rise = frame.Axis2Start - frame.Axis1Start;
            MahjongWallPoint2 end = origin + span;
            var localCorners = new[] { origin, end, end + rise, origin + rise };
            for (int i = 0; i < localCorners.Length; i++)
            {
                Vector3 world = wall.Graphic.rectTransform.TransformPoint(
                    new Vector3(localCorners[i].X, -localCorners[i].Y, 0f));
                Vector3 editorLocal = _coordinateRoot.InverseTransformPoint(world);
                localCorners[i] = new MahjongWallPoint2(editorLocal.x + _coordinateRoot.rect.width * .5f,
                    _coordinateRoot.rect.height * .5f - editorLocal.y);
            }
            return localCorners;
        }

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
            }
            RefreshOverlay();
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

        private void RefreshOverlay()
        {
            bool visible = _selectedWall >= 0 && _selectedWall < _walls.Count;
            for (int i = 0; i < 4; i++)
            {
                if (_handles[i] != null) _handles[i].gameObject.SetActive(visible);
                if (_edges[i] != null) _edges[i].gameObject.SetActive(visible);
            }
            if (!visible) return;

            MahjongWallPoint2[] corners = GetVisibleCorners(_walls[_selectedWall]);
            for (int i = 0; i < 4; i++)
            {
                _edges[i].transform.SetAsLastSibling();
                _handles[i].transform.SetAsLastSibling();
                PositionAt(_handles[i].rectTransform, corners[i]);
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

        private static void PositionAt(RectTransform rect, MahjongWallPoint2 point)
        {
            rect.anchoredPosition = new Vector2(point.X, -point.Y);
        }

        private void LoadDefaultLayoutOnce()
        {
            if (_loaded) return;
            _loaded = true;
            string path = PlayerPrefs.GetString(LastLayoutPathKey, DefaultLayoutPath);
            if (!File.Exists(path)) path = DefaultLayoutPath;
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
                LayoutEntry[] entries = new LayoutEntry[_walls.Count];
                for (int i = 0; i < _walls.Count; i++) entries[i] = ToEntry(_walls[i]);
                LayoutDocument document = new LayoutDocument { walls = entries };
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
                path = WindowsLayoutFileDialog.PickOpenJson(PreferredLayoutPath);
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
                    break;
                }
            }
            RefreshOverlay();
        }

        private static bool IsValid(LayoutEntry entry) =>
            entry.seat >= (int)MahjongWallSeat.North && entry.seat <= (int)MahjongWallSeat.South
            && IsFinite(entry.axis1X) && IsFinite(entry.axis1Y)
            && IsFinite(entry.axis2X) && IsFinite(entry.axis2Y)
            && IsFinite(entry.axis3X) && IsFinite(entry.axis3Y)
            && IsFinite(entry.axisLength) && entry.axisLength >= 40f
            && IsFinite(entry.axisSlopeDegrees);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static LayoutEntry ToEntry(WallState wall) => new LayoutEntry
        {
            seat = (int)wall.Seat,
            axis1X = wall.Frame.Axis1Start.X,
            axis1Y = wall.Frame.Axis1Start.Y,
            axis2X = wall.Frame.Axis2Start.X,
            axis2Y = wall.Frame.Axis2Start.Y,
            axis3X = wall.Frame.Axis3Start.X,
            axis3Y = wall.Frame.Axis3Start.Y,
            axisLength = wall.Frame.AxisLength,
            axisSlopeDegrees = wall.Frame.AxisSlopeDegrees
        };

        private static MahjongWallAxisFrame FromEntry(LayoutEntry entry) => new MahjongWallAxisFrame(
            new MahjongWallPoint2(entry.axis1X, entry.axis1Y),
            new MahjongWallPoint2(entry.axis2X, entry.axis2Y),
            new MahjongWallPoint2(entry.axis3X, entry.axis3Y),
            entry.axisLength, entry.axisSlopeDegrees);

        private bool IsWindows() => Application.platform == RuntimePlatform.WindowsPlayer
            || Application.platform == RuntimePlatform.WindowsEditor;

        private string DefaultLayoutPath => Path.Combine(Application.persistentDataPath, "mahjong-wall-layout.json");
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

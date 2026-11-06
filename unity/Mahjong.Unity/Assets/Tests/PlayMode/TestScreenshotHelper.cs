#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public static class TestScreenshotHelper
    {
        public static string GetScreenshotDirectory()
        {
            string? envDir = Environment.GetEnvironmentVariable("P44_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(envDir))
            {
                Directory.CreateDirectory(envDir);
                return envDir;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            string dir = Path.Combine(projectRoot, "build", "p44", "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void ApplyViewport(Canvas canvas, float screenWidth, float screenHeight, Rect safeArea)
        {
            var scaler = canvas.GetComponent<CanvasScaler>();
            float scale = DesignViewport.CalculateScale(screenWidth, screenHeight);
            if (scaler != null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = scale;
            }
            var safeRoot = canvas.transform.Find("SafeAreaRoot") as RectTransform;
            var designRoot = safeRoot != null ? safeRoot.Find("DesignRoot") as RectTransform : null;
            if (designRoot != null)
            {
                designRoot.sizeDelta = new Vector2(DesignViewport.DesignWidth, DesignViewport.DesignHeight);
                designRoot.anchorMin = new Vector2(0.5f, 0.5f);
                designRoot.anchorMax = new Vector2(0.5f, 0.5f);
                designRoot.pivot = new Vector2(0.5f, 0.5f);
            }
            Rect safeDesign = DesignViewport.CalculateSafeDesignRect(screenWidth, screenHeight, safeArea);
            if (safeRoot != null)
            {
                safeRoot.anchorMin = Vector2.zero;
                safeRoot.anchorMax = Vector2.one;
                safeRoot.offsetMin = new Vector2(safeDesign.xMin, safeDesign.yMin);
                safeRoot.offsetMax = new Vector2(
                    -(DesignViewport.DesignWidth - safeDesign.xMax),
                    -(DesignViewport.DesignHeight - safeDesign.yMax));
            }
            Canvas.ForceUpdateCanvases();
        }

        public static void AssertAllActiveTextCornersAndHeightsFitViewports(Canvas canvas)
        {
            var failures = new List<string>();
            var cam = Camera.main;
            Assert.IsNotNull(cam, "Camera.main must exist");
            var previousMode = canvas.renderMode;
            var previousCamera = canvas.worldCamera;
            var previousTarget = cam!.targetTexture;
            try
            {
                foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(2560, 1080) })
                {
                    var rt = new RenderTexture(size.x, size.y, 24);
                    try
                    {
                        cam.targetTexture = rt;
                        canvas.renderMode = RenderMode.ScreenSpaceCamera;
                        canvas.worldCamera = cam;
                        canvas.planeDistance = 10;
                        var safe = new Rect(size.x == 1280 ? 0 : 140, 0, size.x == 1280 ? 1280 : 2420, size.y);
                        ApplyViewport(canvas, size.x, size.y, safe);
                        Canvas.ForceUpdateCanvases();
                        cam.Render();
                        int count = 0;
                        foreach (var t in canvas.GetComponentsInChildren<TMP_Text>())
                        {
                            if (string.IsNullOrEmpty(t.text)) continue;
                            count++;
                            t.ForceMeshUpdate();
                            var c = new Vector3[4];
                            t.rectTransform.GetWorldCorners(c);
                            foreach (var p in c)
                            {
                                var v = cam.WorldToViewportPoint(p);
                                var screen = new Vector2(v.x * size.x, v.y * size.y);
                                if (screen.x < safe.xMin - 1 || screen.x > safe.xMax + 1)
                                    failures.Add($"{size} {t.name} horizontal {screen}");
                                if (screen.y < safe.yMin - 1 || screen.y > safe.yMax + 1)
                                    failures.Add($"{size} {t.name} vertical {screen}");
                            }
                            if (t.preferredHeight > t.rectTransform.rect.height + 0.5f)
                                failures.Add($"{size} {t.name} text height {t.preferredHeight} > {t.rectTransform.rect.height}");
                        }
                        Assert.Greater(count, 10, $"Must verify populated text controls (found {count})");
                    }
                    finally
                    {
                        cam.targetTexture = previousTarget;
                        UnityEngine.Object.DestroyImmediate(rt);
                    }
                }
            }
            finally
            {
                canvas.renderMode = previousMode;
                canvas.worldCamera = previousCamera;
                cam.targetTexture = previousTarget;
                Canvas.ForceUpdateCanvases();
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        public static void CaptureFrame(string filename, int width = 1280, int height = 720)
        {
            string outDir = GetScreenshotDirectory();
            string fullPath = Path.Combine(outDir, filename);

            Camera? cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (cam == null)
            {
                var camGo = new GameObject("TestScreenshotCamera");
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
                cam.cullingMask = -1;
            }

            // Assign default font to any TMP_Text that lacks a font
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            if (fonts != null && fonts.Length > 0)
            {
                var font = fonts[0];
                foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None))
                {
                    if (text.font == null)
                    {
                        text.font = font;
                    }
                }
            }

            Canvas? canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            var prevMode = canvas != null ? canvas.renderMode : RenderMode.ScreenSpaceOverlay;
            var prevCam = canvas != null ? canvas.worldCamera : null;
            var prevPlaneDist = canvas != null ? canvas.planeDistance : 100f;

            var scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            var prevScaleMode = scaler != null ? scaler.uiScaleMode : CanvasScaler.ScaleMode.ScaleWithScreenSize;
            var prevFactor = scaler != null ? scaler.scaleFactor : 1f;

            int widthToUse = width;
            int heightToUse = height;
            var rt = new RenderTexture(widthToUse, heightToUse, 24);
            var prevActive = RenderTexture.active;
            Texture2D tex = new Texture2D(widthToUse, heightToUse, TextureFormat.RGB24, false);

            try
            {
                if (canvas != null && cam != null)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = 10f;
                    Rect screenSafeArea = widthToUse > 1920 ? new Rect(140f, 0f, widthToUse - 140f, heightToUse) : new Rect(0f, 0f, widthToUse, heightToUse);
                    ApplyViewport(canvas, widthToUse, heightToUse, screenSafeArea);
                }

                var prevTarget = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, widthToUse, heightToUse), 0, 0);
                tex.Apply();
                byte[] bytes = ImageConversion.EncodeToPNG(tex);
                File.WriteAllBytes(fullPath, bytes);
                Debug.Log($"SCREENSHOT_CAPTURED: {fullPath} ({bytes.Length} bytes, {widthToUse}x{heightToUse})");
            }
            finally
            {
                if (scaler != null)
                {
                    scaler.uiScaleMode = prevScaleMode;
                    scaler.scaleFactor = prevFactor;
                }

                if (canvas != null)
                {
                    canvas.renderMode = prevMode;
                    canvas.worldCamera = prevCam;
                    canvas.planeDistance = prevPlaneDist;
                    Canvas.ForceUpdateCanvases();
                }

                RenderTexture.active = prevActive;
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}

#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Mahjong.Unity.EditorTools
{
    [Serializable]
    public sealed class UiProtoBuildReport
    {
        public string result = string.Empty;
        public string platform = string.Empty;
        public int totalErrors;
        public int totalWarnings;
        public ulong totalSizeBytes;
        public double totalTimeSeconds;
        public string output = string.Empty;
    }

    public static class UiProtoWindowsBuild
    {
        public static void BuildWindowsDemo()
        {
            string outputPath = Environment.GetEnvironmentVariable("UI_PROTO_OUTPUT_PATH") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidOperationException("Set UI_PROTO_OUTPUT_PATH to the requested ui_proto.exe path.");
            }

            V5ArtPrototypeBuilder.RebuildV5ArtPrototype();
            string scenePath = V5ArtPrototypeBuilder.ScenePath;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
            {
                throw new FileNotFoundException("The ui_proto scene is missing: " + scenePath);
            }

            V5ArtPrototypeBuilder.ConfigurePrototypeSprites();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.StrictMode
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            string reportPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(outputPath) ?? string.Empty, "..", "build-report.json"));
            File.WriteAllText(reportPath, JsonUtility.ToJson(new UiProtoBuildReport
            {
                result = summary.result.ToString(),
                platform = summary.platform.ToString(),
                totalErrors = summary.totalErrors,
                totalWarnings = summary.totalWarnings,
                totalSizeBytes = summary.totalSize,
                totalTimeSeconds = summary.totalTime.TotalSeconds,
                output = outputPath
            }, true));
            Debug.Log("UI_PROTO_WINDOWS_BUILD " + summary.result
                + " errors=" + summary.totalErrors
                + " warnings=" + summary.totalWarnings
                + " output=" + outputPath);

            EditorApplication.Exit(summary.result == BuildResult.Succeeded && summary.totalErrors == 0 ? 0 : 1);
        }
    }
}

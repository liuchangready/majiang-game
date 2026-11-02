using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Editor-only C04-G3 Windows Player build entry point. It never changes Player
// settings, scenes or assets; the runner supplies isolated output/report paths.
public static class C04PlayerCheatBuild
{
    private static readonly string[] RequiredScenes =
    {
        "Assets/Scenes/Boot.unity",
        "Assets/Scenes/Menu.unity",
        "Assets/Scenes/Lobby.unity",
        "Assets/Scenes/Table.unity",
        "Assets/Scenes/Spectator.unity",
        "Assets/Scenes/Settlement.unity"
    };

    private static readonly string[] RequiredPlugins =
    {
        "Mahjong.Domain.dll",
        "Mahjong.Contracts.dll",
        "Mahjong.Contracts.Wire.dll",
        "Mahjong.TableRuntime.dll",
        "Mahjong.Client.dll",
        "Mahjong.LocalHost.dll",
        "Mahjong.Ai.dll",
        "Google.Protobuf.dll"
    };

    public static void Build()
    {
        string output = Arg("-c04BuildOutput");
        string report = Arg("-c04BuildReport");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(report))
        {
            throw new ArgumentException("C04 requires -c04BuildOutput and -c04BuildReport.");
        }

        string outputFull = Path.GetFullPath(output);
        string reportFull = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFull) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(reportFull) ?? ".");

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        string[] missingScenes = RequiredScenes
            .Except(scenes, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string pluginDir = Path.GetFullPath("Assets/Plugins/Mahjong");
        string[] missingPlugins = RequiredPlugins
            .Where(name => !File.Exists(Path.Combine(pluginDir, name)))
            .ToArray();
        if (missingScenes.Length > 0)
        {
            throw new InvalidOperationException(
                "C04 preflight missing enabled scenes: " + string.Join(",", missingScenes));
        }

        if (missingPlugins.Length > 0)
        {
            throw new InvalidOperationException(
                "C04 preflight missing shared plugins: " + string.Join(",", missingPlugins));
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputFull,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        DateTime started = DateTime.UtcNow;
        BuildReport build = BuildPipeline.BuildPlayer(options);
        DateTime finished = DateTime.UtcNow;
        BuildSummary summary = build.summary;
        string json =
            "{\"schema\":1" +
            ",\"result\":\"" + Escape(summary.result.ToString()) + "\"" +
            ",\"errors\":" + summary.totalErrors +
            ",\"warnings\":" + summary.totalWarnings +
            ",\"target\":\"" + Escape(summary.platform.ToString()) + "\"" +
            ",\"output\":\"" + Escape(outputFull) + "\"" +
            ",\"bytes\":" + summary.totalSize +
            ",\"startedUtc\":\"" + started.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"" +
            ",\"finishedUtc\":\"" + finished.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"" +
            ",\"durationSeconds\":" +
            summary.totalTime.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"unityVersion\":\"" + Escape(Application.unityVersion) + "\"" +
            ",\"productName\":\"" + Escape(PlayerSettings.productName) + "\"" +
            ",\"scenes\":[\"" + string.Join("\",\"", scenes.Select(Escape)) + "\"]" +
            ",\"plugins\":[" + string.Join(",", RequiredPlugins.Select(
                name => "{\"name\":\"" + Escape(name) + "\",\"bytes\":" +
                        new FileInfo(Path.Combine(pluginDir, name)).Length + "}")) + "]" +
            "}";
        File.WriteAllText(reportFull, json, new UTF8Encoding(false));
        Debug.Log("C04_BUILD_REPORT: " + json);

        if (summary.result != BuildResult.Succeeded ||

            !File.Exists(outputFull))
        {
            throw new InvalidOperationException(
                "C04 BuildReport rejected: result=" + summary.result +
                " errors=" + summary.totalErrors +
                " outputExists=" + File.Exists(outputFull));
        }
    }

    private static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }
}

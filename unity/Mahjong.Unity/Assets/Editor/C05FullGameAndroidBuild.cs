using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Android;
using UnityEngine;

// Editor-only entry point. It builds the actual enabled production scenes and
// writes a marker only after checking the BuildReport and Android settings.
public static class C05FullGameAndroidBuild
{
    public static void Build()
    {
        string output = Arg("-c05Output");
        string marker = Arg("-c05Marker");
        string log = Arg("-c05EditorLog");
        if (String.IsNullOrWhiteSpace(output) || String.IsNullOrWhiteSpace(marker) || String.IsNullOrWhiteSpace(log))
            throw new ArgumentException("C05 requires -c05Output, -c05Marker, and -c05EditorLog.");

        var settings = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
        var architecture = PlayerSettings.Android.targetArchitectures;
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        string[] required = { "Assets/Scenes/Boot.unity", "Assets/Scenes/Menu.unity", "Assets/Scenes/Lobby.unity",
            "Assets/Scenes/Table.unity", "Assets/Scenes/Spectator.unity", "Assets/Scenes/Settlement.unity" };
        string[] missing = required.Except(scenes, StringComparer.OrdinalIgnoreCase).ToArray();
        string package = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        string[] plugins = { "Mahjong.Domain.dll", "Mahjong.Contracts.dll", "Mahjong.Contracts.Wire.dll",
            "Mahjong.TableRuntime.dll", "Mahjong.Client.dll", "Mahjong.LocalHost.dll", "Mahjong.Ai.dll", "Google.Protobuf.dll" };
        string pluginDir = Path.GetFullPath("Assets/Plugins/Mahjong");
        string[] missingPlugins = plugins.Where(p => !File.Exists(Path.Combine(pluginDir, p))).ToArray();
        bool tmp = AssetDatabase.FindAssets("t:TMP_Settings").Length > 0 && AssetDatabase.FindAssets("t:TMP_FontAsset").Length > 0;
        string projectSettings = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "ProjectSettings/ProjectSettings.asset"));
        int minSdk = Setting(projectSettings, "AndroidMinSdkVersion");
        int targetSdkSetting = Setting(projectSettings, "AndroidTargetSdkVersion");
        bool landscapeOnly = (Setting(projectSettings, "defaultScreenOrientation") == 5 ||
            Setting(projectSettings, "defaultScreenOrientation") == 3 || Setting(projectSettings, "defaultScreenOrientation") == 4) &&
            Setting(projectSettings, "allowedAutorotateToPortrait") == 0 &&
            Setting(projectSettings, "allowedAutorotateToPortraitUpsideDown") == 0 &&
            Setting(projectSettings, "allowedAutorotateToLandscapeRight") == 1 &&
            Setting(projectSettings, "allowedAutorotateToLandscapeLeft") == 1;
        bool fullscreen = Setting(projectSettings, "androidStartInFullscreen") == 1;
        bool outsideSafeArea = Setting(projectSettings, "androidRenderOutsideSafeArea") == 1;

        var failures = new System.Collections.Generic.List<string>();
        if (missing.Length > 0) failures.Add("missing-enabled-scenes=" + String.Join(",", missing));
        if (settings != ScriptingImplementation.IL2CPP) failures.Add("scripting-backend-not-IL2CPP=" + settings);
        if ((architecture & AndroidArchitecture.ARM64) == 0 || architecture != AndroidArchitecture.ARM64) failures.Add("architectures-not-ARM64-only=" + architecture);
        if (String.IsNullOrWhiteSpace(package) || package == "com.DefaultCompany.UnityProject") failures.Add("invalid-package-id=" + package);
        if (missingPlugins.Length > 0) failures.Add("missing-shared-plugins=" + String.Join(",", missingPlugins));
        if (!tmp) failures.Add("TMP-settings-or-font-assets-missing");
        if (minSdk < 21) failures.Add("Android-minSdk-below-21=" + minSdk);
        if (!landscapeOnly) failures.Add("Android-orientation-not-landscape-only");
        if (!fullscreen || !outsideSafeArea) failures.Add("Android-fullscreen-or-safe-area-settings-invalid");
        if (failures.Count > 0) throw new InvalidOperationException("C05 preflight failed: " + String.Join(";", failures));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(marker)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log)));
        var options = new BuildPlayerOptions { scenes = scenes, locationPathName = output,
            target = BuildTarget.Android, options = BuildOptions.CleanBuildCache };
        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        string json = "{\"schema\":1,\"result\":\"" + summary.result + "\",\"errors\":" + summary.totalErrors +
            ",\"warnings\":" + summary.totalWarnings + ",\"target\":\"" + summary.platform + "\",\"output\":\"" +
            Escape(Path.GetFullPath(output)) + "\",\"bytes\":" + summary.totalSize + ",\"durationSeconds\":" +
            summary.totalTime.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"backend\":\"" + settings + "\",\"architectures\":\"" + architecture + "\",\"packageId\":\"" +
            Escape(package) + "\",\"minSdkVersion\":" + minSdk + ",\"targetSdkSetting\":" + targetSdkSetting +
            ",\"landscapeOnly\":" + landscapeOnly.ToString().ToLowerInvariant() + ",\"fullscreen\":" +
            fullscreen.ToString().ToLowerInvariant() + ",\"outsideSafeArea\":" + outsideSafeArea.ToString().ToLowerInvariant() +
            ",\"scenes\":[\"" + String.Join("\",\"", scenes.Select(Escape)) + "\"],\"tmpResources\":" +
            tmp.ToString().ToLowerInvariant() + "}";
        File.WriteAllText(log, json, new UTF8Encoding(false));
        Debug.Log("C05_BUILD_REPORT: " + json);
        if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0 || !File.Exists(output))
            throw new InvalidOperationException("C05 BuildReport rejected: result=" + summary.result + " errors=" + summary.totalErrors);
        File.WriteAllText(marker, json, new UTF8Encoding(false));
    }

    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    static int Setting(string yaml, string key)
    {
        var match = Regex.Match(yaml, "(?m)^\\s*" + Regex.Escape(key) + ":\\s*(-?\\d+)\\s*$");
        if (!match.Success) throw new InvalidOperationException("C05 missing Android PlayerSetting=" + key);
        return Int32.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
    static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }
}

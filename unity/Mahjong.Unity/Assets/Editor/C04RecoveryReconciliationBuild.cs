using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Editor-only build entry for the C04 Player recovery probe. The runner invokes this
// exact method so the Player, the probe and the shared plugins are bound in one build.
public static class C04RecoveryReconciliationBuild
{
    public static void Build()
    {
        string output = Arg("-c04Output");
        string marker = Arg("-c04Marker");
        string log = Arg("-c04Log");
        string sourceHead = Arg("-c04SourceHead");
        string buildCommit = Arg("-c04BuildCommit");
        if (String.IsNullOrWhiteSpace(output) || String.IsNullOrWhiteSpace(marker) ||
            String.IsNullOrWhiteSpace(log) || String.IsNullOrWhiteSpace(sourceHead) ||
            String.IsNullOrWhiteSpace(buildCommit))
        {
            throw new ArgumentException(
                "C04 build requires -c04Output, -c04Marker, -c04Log, -c04SourceHead and -c04BuildCommit.");
        }

        string[] requiredScenes =
        {
            "Assets/Scenes/Boot.unity",
            "Assets/Scenes/Menu.unity",
            "Assets/Scenes/Lobby.unity",
            "Assets/Scenes/Table.unity",
            "Assets/Scenes/Spectator.unity",
            "Assets/Scenes/Settlement.unity",
        };
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        string[] missingScenes = requiredScenes.Except(scenes, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] plugins =
        {
            "Mahjong.Domain.dll",
            "Mahjong.Contracts.dll",
            "Mahjong.Contracts.Wire.dll",
            "Mahjong.TableRuntime.dll",
            "Mahjong.Client.dll",
            "Mahjong.LocalHost.dll",
            "Mahjong.Ai.dll",
            "Google.Protobuf.dll",
        };
        string pluginDirectory = Path.GetFullPath("Assets/Plugins/Mahjong");
        string[] missingPlugins = plugins.Where(p => !File.Exists(Path.Combine(pluginDirectory, p))).ToArray();
        if (missingScenes.Length > 0)
        {
            throw new InvalidOperationException("C04 missing enabled scenes: " + String.Join(",", missingScenes));
        }

        if (missingPlugins.Length > 0)
        {
            throw new InvalidOperationException("C04 missing shared plugins: " + String.Join(",", missingPlugins));
        }

        if (!File.Exists(Path.Combine(pluginDirectory, "Mahjong.LocalHost.dll")))
        {
            throw new InvalidOperationException("C04 LocalHost plugin is missing.");
        }

        string outputExe = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(marker))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log))!);
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputExe,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        var pluginHashes = new List<string>();
        foreach (string plugin in plugins.OrderBy(p => p, StringComparer.Ordinal))
        {
            string path = Path.Combine(pluginDirectory, plugin);
            pluginHashes.Add("\"" + Escape(plugin) + "\":\"" + Sha256File(path) + "\"");
        }

        string playerSha = Sha256File(outputExe);
        string json = "{\"schemaVersion\":1,\"kind\":\"c04-recovery-player-build\"," +
            "\"result\":\"" + summary.result + "\",\"errors\":" + summary.totalErrors +
            ",\"warnings\":" + summary.totalWarnings + ",\"target\":\"" + summary.platform + "\"" +
            ",\"output\":\"" + Escape(outputExe) + "\",\"playerSha256\":\"" + playerSha + "\",\"bytes\":" + summary.totalSize +
            ",\"durationSeconds\":" + summary.totalTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
            ",\"sourceHead\":\"" + Escape(sourceHead) + "\",\"buildCommit\":\"" + Escape(buildCommit) + "\"" +
            ",\"scenes\":[\"" + String.Join("\",\"", scenes.Select(Escape)) + "\"]" +
            ",\"plugins\":{" + String.Join(",", pluginHashes) + "}}";
        File.WriteAllText(log, json, new UTF8Encoding(false));
        Debug.Log("C04_BUILD_REPORT: " + json);
        if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0 || !File.Exists(outputExe))
        {
            throw new InvalidOperationException(
                "C04 BuildReport rejected: result=" + summary.result + " errors=" + summary.totalErrors);
        }

        File.WriteAllText(marker, json, new UTF8Encoding(false));
    }

    private static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : String.Empty;
    }

    private static string Sha256File(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    private static string Escape(string value)
    {
        return (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

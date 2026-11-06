using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class C04PrivacyBuild
{
    public static void Build()
    {
        string output = Arg("-c04PrivacyOutput");
        string marker = Arg("-c04PrivacyMarker");
        string log = Arg("-c04PrivacyBuildLog");
        string sourceHead = Arg("-c04PrivacySourceHead");
        string buildCommit = Arg("-c04PrivacyBuildCommit");
        if (String.IsNullOrWhiteSpace(output) || String.IsNullOrWhiteSpace(marker) ||
            String.IsNullOrWhiteSpace(log) || String.IsNullOrWhiteSpace(sourceHead) || String.IsNullOrWhiteSpace(buildCommit))
            throw new ArgumentException("C04 privacy build requires output, marker, log, source head and build commit arguments.");

        string[] requiredScenes = { "Assets/Scenes/Boot.unity", "Assets/Scenes/Menu.unity", "Assets/Scenes/Lobby.unity", "Assets/Scenes/Table.unity", "Assets/Scenes/Spectator.unity", "Assets/Scenes/Settlement.unity" };
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        string[] missingScenes = requiredScenes.Except(scenes, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingScenes.Length != 0) throw new InvalidOperationException("C04 privacy required scenes are missing: " + String.Join(",", missingScenes));
        string pluginRoot = Path.GetFullPath("Assets/Plugins/Mahjong");
        string[] pluginNames = { "Mahjong.Domain.dll", "Mahjong.Contracts.dll", "Mahjong.Contracts.Wire.dll", "Mahjong.TableRuntime.dll", "Mahjong.Client.dll", "Mahjong.LocalHost.dll", "Mahjong.Ai.dll", "Google.Protobuf.dll" };
        string[] missingPlugins = pluginNames.Where(name => !File.Exists(Path.Combine(pluginRoot, name))).ToArray();
        if (missingPlugins.Length != 0) throw new InvalidOperationException("C04 privacy plugins are missing: " + String.Join(",", missingPlugins));

        string fullOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(marker))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log))!);
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = fullOutput,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        BuildSummary summary = report.summary;
        string[] errorMessages = report.steps
            .SelectMany(step => step.messages)
            .Where(message => String.Equals(message.type.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.content ?? String.Empty)
            .Where(message => !String.IsNullOrWhiteSpace(message))
            .ToArray();
        string playerSha = File.Exists(fullOutput) ? Sha256(fullOutput) : String.Empty;
        long playerBytes = File.Exists(fullOutput) ? new FileInfo(fullOutput).Length : 0L;
        string outputDirectory = Path.GetDirectoryName(fullOutput)!;
        string packageManifestPath = Path.Combine(outputDirectory, "player-package.sha256");
        string[] packageFiles = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !String.Equals(Path.GetFullPath(path), Path.GetFullPath(packageManifestPath), StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        long packageBytes = 0L;
        var manifestBuilder = new StringBuilder();
        foreach (string packageFile in packageFiles)
        {
            FileInfo info = new FileInfo(packageFile);
            packageBytes += info.Length;
            string relative = packageFile.Substring(outputDirectory.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
            manifestBuilder.Append(Sha256(packageFile)).Append("  ").Append(relative).Append('\n');
        }
        File.WriteAllText(packageManifestPath, manifestBuilder.ToString(), new UTF8Encoding(false));
        string packageManifestSha = Sha256(packageManifestPath);
        string[] pluginEntries = pluginNames.OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => "\"" + Escape(value) + "\":\"" + Sha256(Path.Combine(pluginRoot, value)) + "\"").ToArray();
        string errorMessagesJson = errorMessages.Length == 0
            ? String.Empty
            : "\"" + String.Join("\",\"", errorMessages.Select(Escape)) + "\"";
        string json = "{\"schemaVersion\":1,\"kind\":\"c04-privacy-player-build\",\"result\":\"" + summary.result +
            "\",\"errors\":" + summary.totalErrors + ",\"warnings\":" + summary.totalWarnings +
            ",\"target\":\"" + summary.platform + "\",\"output\":\"" + Escape(fullOutput) +
            "\",\"playerSha256\":\"" + playerSha + "\",\"playerBytes\":" + playerBytes +
            ",\"packageFileCount\":" + packageFiles.Length + ",\"packageBytes\":" + packageBytes +
            ",\"packageManifest\":\"" + Escape(packageManifestPath) + "\",\"packageManifestSha256\":\"" + packageManifestSha +
            "\",\"bytes\":" + summary.totalSize +
            ",\"sourceHead\":\"" + Escape(sourceHead) + "\",\"buildCommit\":\"" + Escape(buildCommit) +
            "\",\"errorMessages\":[" + errorMessagesJson + "],\"scenes\":[\"" + String.Join("\",\"", scenes.Select(Escape)) + "\"],\"plugins\":{" + String.Join(",", pluginEntries) + "}}";
        File.WriteAllText(log, json, new UTF8Encoding(false));
        Debug.Log("C04_PRIVACY_BUILD: " + json);
        if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0 || !File.Exists(fullOutput))
            throw new InvalidOperationException("C04 privacy BuildReport rejected: result=" + summary.result + " errors=" + summary.totalErrors);
        File.WriteAllText(marker, json, new UTF8Encoding(false));
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : String.Empty;
    }

    private static string Sha256(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
    }

    private static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string Escape(string value) => (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

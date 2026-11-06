#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;

/// <summary>Fresh Windows Player build entry point for the isolated C04 runner.</summary>
public static class C04PlayerActionBuild
{
    public static void Build()
    {
        string output = Arg("-c04-player-output");
        string marker = Arg("-c04-player-marker");
        if (String.IsNullOrWhiteSpace(output) || String.IsNullOrWhiteSpace(marker))
            throw new ArgumentException("C04 requires -c04-player-output and -c04-player-marker.");
        output = Path.GetFullPath(output);
        marker = Path.GetFullPath(marker);
        if (File.Exists(output) || Directory.Exists(output) || File.Exists(marker))
            throw new IOException("C04 build output and marker must be new owned paths.");
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (!scenes.Any(scene => scene.EndsWith("Boot.unity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The production Boot scene is not enabled.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0 || !File.Exists(output))
            throw new InvalidOperationException("C04 Windows Player build failed: result=" + report.summary.result + " errors=" + report.summary.totalErrors);
        string outputSha256;
        using (SHA256 sha = SHA256.Create())
        using (FileStream input = File.OpenRead(output))
            outputSha256 = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", String.Empty).ToLowerInvariant();
        string target = BuildTarget.StandaloneWindows64.ToString();
        string buildIdentityInput = target + "\n" + outputSha256 + "\n" + String.Join("\n", scenes);
        string buildIdentity;
        using (SHA256 sha = SHA256.Create())
            buildIdentity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(buildIdentityInput))).Replace("-", String.Empty).ToLowerInvariant();
        string record = "{\"result\":\"" + report.summary.result + "\",\"target\":\"" + target +
            "\",\"errors\":" + report.summary.totalErrors + ",\"bytes\":" + new FileInfo(output).Length +
            ",\"output\":\"" + Path.GetFileName(output) + "\",\"outputSha256\":\"" + outputSha256 +
            "\",\"buildIdentity\":\"" + buildIdentity + "\",\"scenes\":[\"" + String.Join("\",\"", scenes) + "\"]}";
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(record + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
    }

    private static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : String.Empty;
    }
}
#endif

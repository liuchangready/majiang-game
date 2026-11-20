#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Builds the lite product profile without changing the project's normal Player settings.</summary>
public static class G2LocalLitePlayerBuild
{
    public static void Build()
    {
        string output = Arg("-g2-local-lite-output");
        string marker = Arg("-g2-local-lite-marker");
        string source = Arg("-g2-local-lite-source");
        string diff = Arg("-g2-local-lite-diff-sha256");
        if (String.IsNullOrWhiteSpace(output) || String.IsNullOrWhiteSpace(marker) ||
            String.IsNullOrWhiteSpace(source) || source.Length != 40 || diff.Length != 64)
            throw new ArgumentException("Lite build requires output, marker, source HEAD and worktree diff SHA-256.");

        output = Path.GetFullPath(output);
        marker = Path.GetFullPath(marker);
        string project = Directory.GetParent(Application.dataPath)!.FullName;
        string streaming = Path.Combine(project, "Assets", "StreamingAssets");
        bool streamingExisted = Directory.Exists(streaming);
        string identityPath = Path.Combine(streaming, "G2LocalLiteBuild.json");
        if (File.Exists(output) || Directory.Exists(output) || File.Exists(marker) || File.Exists(identityPath))
            throw new IOException("Lite build output, marker and temporary identity must be new paths.");

        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (!scenes.Any(scene => scene.EndsWith("Boot.unity", StringComparison.OrdinalIgnoreCase)) || scenes.Length != 6)
            throw new InvalidOperationException("The six production scenes, including Boot, must be enabled.");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        Directory.CreateDirectory(streaming);
        string identity = "{\"product\":\"G2-Local-Lite\",\"sourceHead\":\"" + source +
            "\",\"worktreeDiffSha256\":\"" + diff + "\",\"unityVersion\":\"" + Application.unityVersion + "\"}";
        File.WriteAllText(identityPath, identity + "\n", new UTF8Encoding(false));
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
                extraScriptingDefines = new[] { "G2_LOCAL_LITE" }
            });
            string[] buildErrors = report.steps
                .SelectMany(step => step.messages)
                .Where(message => message.type == LogType.Error || message.type == LogType.Exception)
                .Select(message => message.type + ": " + message.content)
                .ToArray();
            Debug.Log("LITE_BUILD_REPORT result=" + report.summary.result + " errors=" + report.summary.totalErrors +
                " warnings=" + report.summary.totalWarnings + " errorMessages=" + String.Join(" || ", buildErrors));
            if (report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0 || !File.Exists(output))
                throw new InvalidOperationException("Lite Player build failed: result=" + report.summary.result +
                    " errors=" + report.summary.totalErrors + " details=" + report.SummarizeErrors());

            string exeSha = Sha256(output);
            string buildIdentity = Sha256(Encoding.UTF8.GetBytes(identity + "\n" + exeSha + "\n" + String.Join("\n", scenes)));
            string record = "{\"result\":\"" + report.summary.result + "\",\"target\":\"StandaloneWindows64\",\"errors\":" +
                report.summary.totalErrors + ",\"bytes\":" + new FileInfo(output).Length + ",\"outputSha256\":\"" + exeSha +
                "\",\"buildIdentity\":\"" + buildIdentity + "\",\"sourceHead\":\"" + source + "\",\"worktreeDiffSha256\":\"" +
                diff + "\",\"scenes\":[\"" + String.Join("\",\"", scenes) + "\"]}";
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(record + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
        finally
        {
            if (File.Exists(identityPath)) File.Delete(identityPath);
            if (File.Exists(identityPath + ".meta")) File.Delete(identityPath + ".meta");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (!streamingExisted && Directory.Exists(streaming) && !Directory.EnumerateFileSystemEntries(streaming).Any())
                Directory.Delete(streaming);
            string streamingMeta = streaming + ".meta";
            if (!streamingExisted && File.Exists(streamingMeta)) File.Delete(streamingMeta);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }

    private static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : String.Empty;
    }

    private static string Sha256(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream input = File.OpenRead(path)) return Hex(sha.ComputeHash(input));
    }

    private static string Sha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", String.Empty).ToLowerInvariant();
}
#endif

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

// Editor-only C04-G3 Windows Player build entry point. It never changes Player
// settings, scenes or assets; the runner supplies an isolated staged project and
// output/report paths. The report is written on success and on failure so that
// BuildReport step-level Error/Exception messages are preserved for diagnosis.
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

    private sealed class BuildObservation
    {
        public string Result = "NotStarted";
        public int? Errors;
        public int? Warnings;
        public string Target = string.Empty;
        public string Output = string.Empty;
        public long OutputBytes;
        public string OutputSha256 = string.Empty;
        public bool OutputExists;
        public string StartedUtc = string.Empty;
        public string FinishedUtc = string.Empty;
        public double DurationSeconds;
        public string UnityVersion = string.Empty;
        public string ProductName = string.Empty;
        public string[] Scenes = new string[0];
        public string Exception = string.Empty;
        public int StepErrorCount;
        public int StepExceptionCount;
        public int StepWarningCount;
        public int StepCount;
        public readonly List<string> StepDiagnostics = new List<string>();
        public readonly List<StepRecord> Steps = new List<StepRecord>();
    }

    private sealed class StepRecord
    {
        public string Name = string.Empty;
        public int Depth;
        public double DurationSeconds;
        public readonly List<MessageRecord> Messages = new List<MessageRecord>();
    }

    private sealed class MessageRecord
    {
        public string Type = string.Empty;
        public string Content = string.Empty;
    }

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

        var observation = new BuildObservation
        {
            Target = BuildTarget.StandaloneWindows64.ToString(),
            Output = outputFull,
            UnityVersion = Application.unityVersion,
            ProductName = PlayerSettings.productName,
            StartedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        DateTime started = DateTime.UtcNow;
        try
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            observation.Scenes = scenes;

            string[] missingScenes = RequiredScenes
                .Except(scenes, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingScenes.Length > 0)
            {
                throw new InvalidOperationException(
                    "C04 preflight missing enabled scenes: " + string.Join(",", missingScenes));
            }

            string pluginDir = Path.GetFullPath("Assets/Plugins/Mahjong");
            string[] missingPlugins = RequiredPlugins
                .Where(name => !File.Exists(Path.Combine(pluginDir, name)))
                .ToArray();
            if (missingPlugins.Length > 0)
            {
                throw new InvalidOperationException(
                    "C04 preflight missing shared plugins: " + string.Join(",", missingPlugins));
            }

            RepairPackageImportOrder();

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputFull,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            BuildReport build = BuildPipeline.BuildPlayer(options);
            DateTime finished = DateTime.UtcNow;
            observation.FinishedUtc = finished.ToString("O", CultureInfo.InvariantCulture);
            observation.DurationSeconds = Math.Round((finished - started).TotalSeconds, 3);
            if (build == null)
            {
                throw new InvalidOperationException("C04 BuildPipeline returned a null BuildReport.");
            }

            BuildSummary summary = build.summary;
            observation.Result = summary.result.ToString();
            observation.Errors = checked((int)summary.totalErrors);
            observation.Warnings = checked((int)summary.totalWarnings);
            observation.Target = summary.platform.ToString();
            observation.Output = string.IsNullOrWhiteSpace(summary.outputPath)
                ? outputFull
                : Path.GetFullPath(summary.outputPath);
            observation.OutputExists = File.Exists(observation.Output);
            if (observation.OutputExists)
            {
                var outputInfo = new FileInfo(observation.Output);
                observation.OutputBytes = outputInfo.Length;
                observation.OutputSha256 = HashFile(observation.Output);
            }

            CaptureSteps(build, observation);
        }
        catch (Exception exception)
        {
            observation.Exception = exception.ToString();
            observation.FinishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            observation.DurationSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 3);
            WriteReport(reportFull, observation, pluginDir: null);
            Debug.LogError("C04_BUILD_FAILED: " + exception);
            throw;
        }

        string finalPluginDir = Path.GetFullPath("Assets/Plugins/Mahjong");
        WriteReport(reportFull, observation, finalPluginDir);
        Debug.Log("C04_BUILD_REPORT: " + reportFull);

        // Fail closed on any BuildReport error. The real count and every step
        // message stay in the report; they are never filtered or renumbered.
        if (!string.Equals(observation.Result, "Succeeded", StringComparison.Ordinal) ||
            observation.Errors.GetValueOrDefault() != 0 ||
            observation.StepErrorCount != 0 ||
            observation.StepExceptionCount != 0 ||
            !observation.OutputExists)
        {
            throw new InvalidOperationException(
                "C04 BuildReport rejected: result=" + observation.Result +
                " errors=" + (observation.Errors.HasValue ? observation.Errors.Value.ToString(CultureInfo.InvariantCulture) : "unknown") +
                " stepErrors=" + observation.StepErrorCount +
                " stepExceptions=" + observation.StepExceptionCount +
                " outputExists=" + observation.OutputExists);
        }
    }

    private static void RepairPackageImportOrder()
    {
        // The URP/SRP global-settings postprocessor can observe these package
        // assets as "Host type is not matching any asset type" during a cold
        // batchmode import. Force a synchronous reimport before the build so the
        // importer has a completed artifact instead of the partially imported
        // package resource. This only touches Unity's ignored Library import
        // cache, not tracked project assets or package sources.
        string[] packageAssets =
        {
            "Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph",
            "Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader"
        };
        foreach (string assetPath in packageAssets)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) == null)
            {
                continue;
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }
    private static void CaptureSteps(BuildReport build, BuildObservation observation)
    {
        if (build.steps == null)
        {
            return;
        }

        foreach (BuildStep step in build.steps)
        {
            var record = new StepRecord
            {
                Name = step.name ?? string.Empty,
                Depth = step.depth,
                DurationSeconds = Math.Round(step.duration.TotalSeconds, 3)
            };
            if (step.messages != null)
            {
                foreach (BuildStepMessage message in step.messages)
                {
                    string type = message.type.ToString();
                    string content = message.content ?? string.Empty;
                    record.Messages.Add(new MessageRecord { Type = type, Content = content });
                    if (string.Equals(type, "Error", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "Exception", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "Assert", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(type, "Error", StringComparison.OrdinalIgnoreCase))
                        {
                            observation.StepErrorCount++;
                        }
                        else if (string.Equals(type, "Exception", StringComparison.OrdinalIgnoreCase))
                        {
                            observation.StepExceptionCount++;
                        }
                        else
                        {
                            observation.StepErrorCount++;
                        }

                        observation.StepDiagnostics.Add(
                            "step=" + record.Name + " depth=" + record.Depth + " type=" + type + " message=" + content);
                    }
                    else if (string.Equals(type, "Warning", StringComparison.OrdinalIgnoreCase))
                    {
                        observation.StepWarningCount++;
                    }
                }
            }

            observation.Steps.Add(record);
        }

        observation.StepCount = observation.Steps.Count;
    }

    private static void WriteReport(string path, BuildObservation observation, string pluginDir)
    {
        var json = new StringBuilder(8192);
        json.Append('{');
        AppendProperty(json, "schema", 1, first: true);
        AppendProperty(json, "result", observation.Result);
        AppendNullableNumber(json, "errors", observation.Errors);
        AppendNullableNumber(json, "warnings", observation.Warnings);
        AppendProperty(json, "target", observation.Target);
        AppendProperty(json, "output", observation.Output);
        AppendProperty(json, "bytes", observation.OutputBytes);
        AppendProperty(json, "outputSha256", observation.OutputSha256);
        AppendProperty(json, "outputExists", observation.OutputExists);
        AppendProperty(json, "startedUtc", observation.StartedUtc);
        AppendProperty(json, "finishedUtc", observation.FinishedUtc);
        AppendProperty(json, "durationSeconds", observation.DurationSeconds);
        AppendProperty(json, "unityVersion", observation.UnityVersion);
        AppendProperty(json, "productName", observation.ProductName);
        AppendProperty(json, "exception", observation.Exception);

        json.Append(",\"scenes\":[");
        for (int index = 0; index < observation.Scenes.Length; index++)
        {
            if (index > 0) { json.Append(','); }
            AppendString(json, observation.Scenes[index]);
        }
        json.Append(']');

        json.Append(",\"plugins\":[");
        if (!string.IsNullOrWhiteSpace(pluginDir) && Directory.Exists(pluginDir))
        {
            string[] names = Directory.GetFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (int index = 0; index < names.Length; index++)
            {
                if (index > 0) { json.Append(','); }
                string full = Path.Combine(pluginDir, names[index]);
                json.Append('{');
                AppendProperty(json, "name", names[index], first: true);
                AppendProperty(json, "bytes", new FileInfo(full).Length);
                AppendProperty(json, "sha256", HashFile(full));
                json.Append('}');
            }
        }
        json.Append(']');

        json.Append(",\"stepCount\":").Append(observation.StepCount);
        json.Append(",\"stepErrorCount\":").Append(observation.StepErrorCount);
        json.Append(",\"stepExceptionCount\":").Append(observation.StepExceptionCount);
        json.Append(",\"stepWarningCount\":").Append(observation.StepWarningCount);

        json.Append(",\"stepDiagnostics\":[");
        for (int index = 0; index < observation.StepDiagnostics.Count; index++)
        {
            if (index > 0) { json.Append(','); }
            AppendString(json, observation.StepDiagnostics[index]);
        }
        json.Append(']');

        json.Append(",\"steps\":[");
        for (int index = 0; index < observation.Steps.Count; index++)
        {
            StepRecord step = observation.Steps[index];
            if (index > 0) { json.Append(','); }
            json.Append('{');
            AppendProperty(json, "name", step.Name, first: true);
            AppendProperty(json, "depth", step.Depth);
            AppendProperty(json, "durationSeconds", step.DurationSeconds);
            json.Append(",\"messages\":[");
            for (int messageIndex = 0; messageIndex < step.Messages.Count; messageIndex++)
            {
                if (messageIndex > 0) { json.Append(','); }
                MessageRecord message = step.Messages[messageIndex];
                json.Append('{');
                AppendProperty(json, "type", message.Type, first: true);
                AppendProperty(json, "content", message.Content);
                json.Append('}');
            }
            json.Append("]}");
        }
        json.Append(']');

        json.Append('}');
        WriteAllTextAtomic(path, json.ToString());
    }

    private static void AppendProperty(StringBuilder json, string name, string value, bool first = false)
    {
        if (!first) { json.Append(','); }
        AppendString(json, name);
        json.Append(':');
        AppendString(json, value ?? string.Empty);
    }

    private static void AppendProperty(StringBuilder json, string name, long value, bool first = false)
    {
        if (!first) { json.Append(','); }
        AppendString(json, name);
        json.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendProperty(StringBuilder json, string name, double value, bool first = false)
    {
        if (!first) { json.Append(','); }
        AppendString(json, name);
        json.Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void AppendProperty(StringBuilder json, string name, bool value, bool first = false)
    {
        if (!first) { json.Append(','); }
        AppendString(json, name);
        json.Append(':').Append(value ? "true" : "false");
    }

    private static void AppendNullableNumber(StringBuilder json, string name, int? value)
    {
        json.Append(',');
        AppendString(json, name);
        json.Append(':');
        json.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
    }

    private static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        if (value != null)
        {
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            json.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            json.Append(character);
                        }
                        break;
                }
            }
        }
        json.Append('"');
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try
            {
                File.Replace(temp, path, null);
            }
            catch
            {
                File.Delete(path);
                File.Move(temp, path);
            }
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static string HashFile(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            var text = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }
    }

    private static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
    }
}

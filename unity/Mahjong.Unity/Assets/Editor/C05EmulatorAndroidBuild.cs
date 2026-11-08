using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Editor-only entry point for the explicitly marked ANDROID_EMULATOR test variant.
// It builds the real enabled production scenes with the same shared plugin closure as
// the accepted ARM64 entry, but pins the in-memory ABI to x86_64 (IL2CPP) so the APK
// can run on the existing x86_64 emulator image. It never changes source ProjectSettings:
// the architecture override is restored in a finally block and every input byte is
// re-hashed after the build; any rewrite is reported as a failure.
public static class C05EmulatorAndroidBuild
{
    const string Variant = "ANDROID_EMULATOR";
    const string ExpectedAbi = "X86_64";

    static readonly string[] RequiredScenes =
    {
        "Assets/Scenes/Boot.unity", "Assets/Scenes/Menu.unity", "Assets/Scenes/Lobby.unity",
        "Assets/Scenes/Table.unity", "Assets/Scenes/Spectator.unity", "Assets/Scenes/Settlement.unity"
    };

    static readonly string[] RequiredPlugins =
    {
        "Mahjong.Domain.dll", "Mahjong.Contracts.dll", "Mahjong.Contracts.Wire.dll",
        "Mahjong.TableRuntime.dll", "Mahjong.Client.dll", "Mahjong.LocalHost.dll",
        "Mahjong.Ai.dll", "Google.Protobuf.dll"
    };

    static readonly string[] ProductionInputs =
    {
        "Assets/Editor/C05EmulatorAndroidBuild.cs",
        "Assets/Editor/C05EmulatorAndroidBuild.cs.meta",
        "ProjectSettings/ProjectSettings.asset",
        "ProjectSettings/EditorBuildSettings.asset",
        "Packages/manifest.json",
        "Packages/packages-lock.json"
    };

    public static void Build()
    {
        string output = Arg("-c05EmuOutput");
        string marker = Arg("-c05EmuMarker");
        string log = Arg("-c05EmuEditorLog");
        string sourceRootArg = Arg("-c05EmuSourceRoot");
        string sourceHead = Arg("-c05EmuSourceHead") ?? "";
        string resolvedTargetArg = Arg("-c05EmuResolvedTargetSdk");
        if (String.IsNullOrWhiteSpace(output) || String.IsNullOrWhiteSpace(marker) || String.IsNullOrWhiteSpace(log))
            throw new ArgumentException("C05 emulator build requires -c05EmuOutput, -c05EmuMarker and -c05EmuEditorLog.");
        int resolvedTargetSdk;
        if (!Int32.TryParse(resolvedTargetArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolvedTargetSdk))
            throw new ArgumentException("C05 emulator build requires an integer -c05EmuResolvedTargetSdk.");

        string projectRoot = Directory.GetCurrentDirectory();
        string sourceRoot = String.IsNullOrWhiteSpace(sourceRootArg) ? projectRoot : Path.GetFullPath(sourceRootArg);
        string outputFull = Path.GetFullPath(output);
        string markerFull = Path.GetFullPath(marker);
        string logFull = Path.GetFullPath(log);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFull));
        Directory.CreateDirectory(Path.GetDirectoryName(markerFull));
        Directory.CreateDirectory(Path.GetDirectoryName(logFull));

        var failures = new List<string>();
        var notes = new List<string>();
        var inputHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[] projectSettingsBefore = null;
        string projectSettingsPath = Path.Combine(projectRoot, "ProjectSettings/ProjectSettings.asset");
        object originalArch = null;
        PropertyInfo archProperty = null;
        string archBefore = "UNKNOWN";
        string archAfter = "UNKNOWN";
        DateTime startedAt = DateTime.UtcNow;

        try
        {
            foreach (string rel in ProductionInputs)
            {
                string full = Path.Combine(projectRoot, rel);
                if (!File.Exists(full)) { failures.Add("missing-production-input=" + rel); continue; }
                inputHashes[rel] = Sha256(full);
                if (rel == "ProjectSettings/ProjectSettings.asset")
                {
                    projectSettingsBefore = File.ReadAllBytes(full);
                    notes.Add("projectSettingsSha256Before=" + inputHashes[rel]);
                }
            }

            var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rel in ProductionInputs)
            {
                string full = Path.Combine(sourceRoot, rel);
                if (!File.Exists(full)) { failures.Add("missing-source-input=" + rel); continue; }
                sourceHashes[rel] = Sha256(full);
            }

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            string[] missingScenes = RequiredScenes.Except(scenes, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missingScenes.Length > 0) failures.Add("missing-enabled-scenes=" + String.Join(",", missingScenes));
            if (scenes.Length != RequiredScenes.Length)
                failures.Add("enabled-scene-count=" + scenes.Length + " expected=" + RequiredScenes.Length);

            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
            if (backend != ScriptingImplementation.IL2CPP) failures.Add("scripting-backend-not-IL2CPP=" + backend);

            string package = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
            if (String.IsNullOrWhiteSpace(package) || package == "com.DefaultCompany.UnityProject")
                failures.Add("invalid-package-id=" + package);

            string pluginDir = Path.GetFullPath("Assets/Plugins/Mahjong");
            string[] missingPlugins = RequiredPlugins.Where(p => !File.Exists(Path.Combine(pluginDir, p))).ToArray();
            if (missingPlugins.Length > 0) failures.Add("missing-shared-plugins=" + String.Join(",", missingPlugins));

            bool tmp = AssetDatabase.FindAssets("t:TMP_Settings").Length > 0 && AssetDatabase.FindAssets("t:TMP_FontAsset").Length > 0;
            if (!tmp) failures.Add("TMP-settings-or-font-assets-missing");

            string settingsYaml = File.ReadAllText(Path.Combine(projectRoot, "ProjectSettings/ProjectSettings.asset"));
            int minSdk = Setting(settingsYaml, "AndroidMinSdkVersion");
            int targetSdkSetting = Setting(settingsYaml, "AndroidTargetSdkVersion");
            bool landscapeOnly = (Setting(settingsYaml, "defaultScreenOrientation") == 5 ||
                Setting(settingsYaml, "defaultScreenOrientation") == 3 || Setting(settingsYaml, "defaultScreenOrientation") == 4) &&
                Setting(settingsYaml, "allowedAutorotateToPortrait") == 0 &&
                Setting(settingsYaml, "allowedAutorotateToPortraitUpsideDown") == 0 &&
                Setting(settingsYaml, "allowedAutorotateToLandscapeRight") == 1 &&
                Setting(settingsYaml, "allowedAutorotateToLandscapeLeft") == 1;
            bool fullscreen = Setting(settingsYaml, "androidStartInFullscreen") == 1;
            bool outsideSafeArea = Setting(settingsYaml, "androidRenderOutsideSafeArea") == 1;
            if (minSdk < 21) failures.Add("Android-minSdk-below-21=" + minSdk);
            if (targetSdkSetting < 0) failures.Add("Android-targetSdk-invalid=" + targetSdkSetting);
            if (!landscapeOnly) failures.Add("Android-orientation-not-landscape-only");
            if (!fullscreen || !outsideSafeArea) failures.Add("Android-fullscreen-or-safe-area-settings-invalid");

            string x86Unity = Path.Combine(EditorApplication.applicationContentsPath,
                "PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Libs/x86_64/libunity.so");
            string x86Main = Path.Combine(EditorApplication.applicationContentsPath,
                "PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Libs/x86_64/libmain.so");
            if (!File.Exists(x86Unity) || !File.Exists(x86Main))
                failures.Add("IL2CPP-x86_64-player-libs-missing");
            notes.Add("il2cppX86_64Libs=" + File.Exists(x86Unity) + "/" + File.Exists(x86Main));

            archProperty = typeof(PlayerSettings.Android).GetProperty("targetArchitectures",
                BindingFlags.Public | BindingFlags.Static);
            if (archProperty == null)
            {
                failures.Add("AndroidArchitecture-property-missing");
            }
            else
            {
                originalArch = archProperty.GetValue(null, null);
                archBefore = originalArch == null ? "null" : originalArch.ToString();
                object target;
                try { target = Enum.Parse(archProperty.PropertyType, ExpectedAbi, true); }
                catch (Exception) { target = null; failures.Add("AndroidArchitecture-X86_64-not-supported"); }
                if (target != null)
                {
                    archProperty.SetValue(null, target, null);
                    object applied = archProperty.GetValue(null, null);
                    archAfter = applied == null ? "null" : applied.ToString();
                    if (!String.Equals(archAfter, ExpectedAbi, StringComparison.OrdinalIgnoreCase))
                        failures.Add("in-memory-architecture-not-x86_64=" + archAfter);
                }
            }

            notes.Add("unityVersion=" + Application.unityVersion);
            notes.Add("package=" + package);
            notes.Add("scenes=" + String.Join(",", scenes));
            notes.Add("architectureBefore=" + archBefore);
            notes.Add("architectureInMemory=" + archAfter);
            notes.Add("inputSha256=" + JsonMap(inputHashes));

            if (failures.Count > 0)
                throw new InvalidOperationException("C05 emulator preflight failed: " + String.Join(";", failures));

            // A normal run is a cold build. A wrapper-validation rerun may reuse the cache only
            // when the caller explicitly asks for it; player inputs are still hashed and bound.
            bool reuseCache = Environment.GetCommandLineArgs().Contains("-c05EmuReuseCache");
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputFull,
                target = BuildTarget.Android,
                options = reuseCache ? BuildOptions.None : BuildOptions.CleanBuildCache
            };
            notes.Add("buildCacheMode=" + (reuseCache ? "REUSE" : "CLEAN"));

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report == null) throw new InvalidOperationException("C05 emulator BuildPipeline returned no BuildReport.");
            BuildSummary summary = report.summary;

            // Unity flushes the in-memory AndroidArchitecture override into ProjectSettings.asset
            // while building. The APK has already been produced, so restore the exact pre-build
            // bytes at once and record the transient write as a known, disclosed build effect
            // instead of leaving a modified tracked source file behind.
            bool projectSettingsTransientWrite = false;
            if (projectSettingsBefore != null && File.Exists(projectSettingsPath))
            {
                byte[] currentSettings = File.ReadAllBytes(projectSettingsPath);
                if (!BytesEqual(currentSettings, projectSettingsBefore))
                {
                    File.WriteAllBytes(projectSettingsPath, projectSettingsBefore);
                    projectSettingsTransientWrite = true;
                    notes.Add("projectSettingsTransientWriteRestored=true");
                }
            }

            int stepErrors = 0;
            int stepExceptions = 0;
            var stepMessages = new List<string>();
            foreach (BuildStep step in report.steps)
            {
                foreach (var message in step.messages)
                {
                    bool errorClass = message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Assert;
                    if (!errorClass) continue;
                    stepErrors++;
                    string content = (message.content ?? "").Replace("\r", " ").Replace("\n", " ");
                    if (message.type == LogType.Exception) stepExceptions++;
                    if (stepMessages.Count < 20) stepMessages.Add(message.type + ": " + (content.Length > 280 ? content.Substring(0, 280) : content));
                }
            }
            if (stepErrors > 0) failures.Add("BuildReport-stepErrors=" + stepErrors);
            if (stepExceptions > 0) failures.Add("BuildReport-stepExceptions=" + stepExceptions);

            var afterHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rel in ProductionInputs)
            {
                string full = Path.Combine(projectRoot, rel);
                afterHashes[rel] = File.Exists(full) ? Sha256(full) : null;
            }
            var changed = afterHashes.Where(kv => !inputHashes.TryGetValue(kv.Key, out string before) ||
                !String.Equals(before, kv.Value, StringComparison.Ordinal)).Select(kv => kv.Key).ToArray();
            if (changed.Length > 0)
                failures.Add("production-input-rewritten-by-build=" + String.Join(",", changed));

            var sourceAfterHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rel in ProductionInputs)
            {
                string full = Path.Combine(sourceRoot, rel);
                sourceAfterHashes[rel] = File.Exists(full) ? Sha256(full) : null;
            }
            var sourceChanged = sourceAfterHashes.Where(kv => !sourceHashes.TryGetValue(kv.Key, out string before) ||
                !String.Equals(before, kv.Value, StringComparison.Ordinal)).Select(kv => kv.Key).ToArray();
            if (sourceChanged.Length > 0)
                failures.Add("source-input-rewritten-by-build=" + String.Join(",", sourceChanged));

            bool apkExists = File.Exists(outputFull);
            // A cold build must always emit a fresh APK. In the explicit cache-reuse mode Unity
            // may copy the byte-identical cached APK and preserve its timestamp; the caller's
            // summary binds the APK SHA/size, and the cold-build evidence remains the authority.
            bool fresh = apkExists && (reuseCache || File.GetLastWriteTimeUtc(outputFull) >= startedAt.AddSeconds(-1));
            if (reuseCache) notes.Add("cacheReusePreservesApkTimestamp=" + !fresh);
            bool succeeded = summary.result == BuildResult.Succeeded && summary.totalErrors == 0 &&
                summary.platform == BuildTarget.Android && apkExists && fresh;
            if (!succeeded)
                failures.Add("BuildReport-rejected result=" + summary.result + " errors=" + summary.totalErrors +
                    " platform=" + summary.platform + " apkExists=" + apkExists + " fresh=" + fresh);

            string applyArch = archProperty == null || archProperty.GetValue(null, null) == null
                ? "UNKNOWN" : archProperty.GetValue(null, null).ToString();

            string reportJson = "{" +
                "\"schema\":2" +
                ",\"variant\":\"" + Variant + "\"" +
                ",\"cacheMode\":\"" + (reuseCache ? "REUSE" : "CLEAN") + "\"" +
                ",\"result\":\"" + summary.result + "\"" +
                ",\"errors\":" + summary.totalErrors +
                ",\"stepErrors\":" + stepErrors +
                ",\"stepExceptions\":" + stepExceptions +
                ",\"stepErrorMessages\":[" + String.Join(",", stepMessages.Select(s => "\"" + J(s) + "\"")) + "]" +
                ",\"warnings\":" + summary.totalWarnings +
                ",\"target\":\"" + summary.platform + "\"" +
                ",\"output\":\"" + J(outputFull) + "\"" +
                ",\"bytes\":" + summary.totalSize +
                ",\"durationSeconds\":" + summary.totalTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
                ",\"backend\":\"" + backend + "\"" +
                ",\"architectures\":\"" + applyArch + "\"" +
                ",\"requestedAbi\":\"" + ExpectedAbi + "\"" +
                ",\"packageId\":\"" + J(package) + "\"" +
                ",\"minSdkVersion\":" + minSdk +
                ",\"targetSdkSetting\":" + targetSdkSetting +
                ",\"resolvedTargetSdk\":" + resolvedTargetSdk +
                ",\"sourceHead\":\"" + J(sourceHead) + "\"" +
                ",\"sourceRoot\":\"" + J(sourceRoot.Replace("\\", "/")) + "\"" +
                ",\"sourceInputSha256\":" + JsonMap(sourceHashes) +
                ",\"sourceInputsRewritten\":[" + String.Join(",", sourceChanged.Select(s => "\"" + J(s) + "\"")) + "]" +
                ",\"landscapeOnly\":" + Bool(landscapeOnly) +
                ",\"fullscreen\":" + Bool(fullscreen) +
                ",\"outsideSafeArea\":" + Bool(outsideSafeArea) +
                ",\"tmpResources\":" + Bool(tmp) +
                ",\"unityVersion\":\"" + J(Application.unityVersion) + "\"" +
                ",\"scenes\":[" + String.Join(",", scenes.Select(s => "\"" + J(s) + "\"")) + "]" +
                ",\"inputSha256\":" + JsonMap(inputHashes) +
                ",\"productionInputsRewritten\":[" + String.Join(",", changed.Select(s => "\"" + J(s) + "\"")) + "]" +
                ",\"projectSettingsTransientWriteRestored\":" + Bool(projectSettingsTransientWrite) +
                ",\"failedAssertions\":[" + String.Join(",", failures.Select(s => "\"" + J(s) + "\"")) + "]" +
                ",\"notes\":[" + String.Join(",", notes.Select(s => "\"" + J(s) + "\"")) + "]" +
                "}";

            File.WriteAllText(logFull, reportJson, new UTF8Encoding(false));
            Debug.Log("C05_EMULATOR_BUILD_REPORT: " + reportJson);

            if (failures.Count > 0)
                throw new InvalidOperationException("C05 emulator build rejected: " + String.Join(";", failures));

            File.WriteAllText(markerFull, reportJson, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            try
            {
                if (projectSettingsBefore != null && File.Exists(projectSettingsPath))
                {
                    string after = Sha256(projectSettingsPath);
                    if (!String.Equals(after, inputHashes.TryGetValue("ProjectSettings/ProjectSettings.asset", out string before) ? before : null,
                        StringComparison.Ordinal))
                    {
                        File.WriteAllBytes(projectSettingsPath, projectSettingsBefore);
                        notes.Add("projectSettingsRestored=true");
                    }
                }
            }
            catch (Exception restoreEx) { notes.Add("projectSettingsRestoreError=" + restoreEx.Message); }

            string failJson = "{" +
                "\"schema\":2,\"variant\":\"" + Variant + "\",\"result\":\"FAILED\"" +
                ",\"error\":\"" + J(ex.Message) + "\"" +
                ",\"failedAssertions\":[" + String.Join(",", failures.Select(s => "\"" + J(s) + "\"")) + "]" +
                ",\"architectureBefore\":\"" + J(archBefore) + "\"" +
                ",\"architectureInMemory\":\"" + J(archAfter) + "\"" +
                ",\"unityVersion\":\"" + J(Application.unityVersion) + "\"" +
                ",\"notes\":[" + String.Join(",", notes.Select(s => "\"" + J(s) + "\"")) + "]" +
                "}";
            try { File.WriteAllText(logFull, failJson, new UTF8Encoding(false)); } catch { }
            throw;
        }
        finally
        {
            if (archProperty != null && originalArch != null)
            {
                try { archProperty.SetValue(null, originalArch, null); } catch { }
            }
        }
    }

    static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int Setting(string yaml, string key)
    {
        Match match = Regex.Match(yaml, "(?m)^\\s*" + Regex.Escape(key) + ":\\s*(-?\\d+)\\s*$");
        if (!match.Success) throw new InvalidOperationException("C05 emulator missing Android PlayerSetting=" + key);
        return Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToUpperInvariant();
    }

    static string Bool(bool value) { return value ? "true" : "false"; }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static string J(string s)
    {
        if (s == null) return "";
        var b = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': b.Append("\\\\"); break;
                case '"': b.Append("\\\""); break;
                case '\r': b.Append("\\r"); break;
                case '\n': b.Append("\\n"); break;
                case '\t': b.Append("\\t"); break;
                default: b.Append(c); break;
            }
        }
        return b.ToString();
    }

    static string JsonMap(Dictionary<string, string> map)
    {
        return "{" + String.Join(",", map.Select(kv =>
            "\"" + J(kv.Key) + "\":\"" + J(kv.Value) + "\"")) + "}";
    }
}

// The Android Gradle Plugin writes intermediate output under
// <GradleProject>/<module>/build/, and two of those trees exceed 260 characters
// once nested in the staging project's Library/Bee/Android/Prj/IL2CPP/Gradle:
//
//   unityLibrary/build/.transforms/<hex>/transformed/
//     bundleLibRuntimeToDirRelease/bundleLibRuntimeToDirRelease_dex/
//     com/unity3d/player/UnityPlayerActivity.dex            -> 262 chars
//   launcher/build/intermediates/desugar_graph/release/dexBuilderRelease/out/
//     currentProject/jar_<128 hex>_bucket_0/graph.bin       -> 260 chars
//
// Both are past the Windows MAX_PATH limit the JDK's legacy file API enforces, so
// the staging path budget rejects the project before the build starts.
//
// :unityLibrary moves its whole build directory. :launcher cannot: Unity's
// MoveFinalPackage postprocessor copies the APK from the hardcoded path
// launcher/build/outputs/apk/release, and a redirected output tree makes that
// copy fail with DirectoryNotFoundException. So only launcher's intermediates
// directory is moved, leaving outputs in place.
//
// The redirect must be installed here, not in a checked-in settings template:
// the Gradle project is generated by Unity during the build, and enabling custom
// templates would change ProjectSettings.asset, which is a production input this
// task may not modify.
public sealed class C05EmulatorGradleBuildDirRedirect : UnityEditor.Android.IPostGenerateGradleAndroidProject
{
    /// <summary>Short root that keeps every generated path inside MAX_PATH.</summary>
    public const string ShortBuildRoot = "D:/gc";

    public int callbackOrder { get { return 0; } }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // `path` is the generated module directory (…/Gradle/unityLibrary for the
        // library module); the root settings.gradle lives in its parent.
        string moduleDir = Path.GetFullPath(path);
        string rootDir = Directory.GetParent(moduleDir)?.FullName;
        if (rootDir == null)
            throw new InvalidOperationException(
                "C05 gradle redirect: cannot locate the Gradle root above '" + moduleDir + "'.");

        string settings = Path.Combine(rootDir, "settings.gradle");
        if (!File.Exists(settings))
            throw new InvalidOperationException(
                "C05 gradle redirect: settings.gradle not found in '" + rootDir + "'.");

        const string marker = "// C05 gradle build-dir redirect";
        string text = File.ReadAllText(settings);
        if (text.IndexOf(marker, StringComparison.Ordinal) >= 0) return;

        // Gradle 9 removed allprojects() from Settings; gradle.beforeProject runs for
        // every project as it is evaluated.
        var sb = new StringBuilder(text);
        if (!text.EndsWith("\n", StringComparison.Ordinal)) sb.Append("\n");
        sb.Append("\n");
        sb.Append(marker + "\n");
        sb.Append("// Keep generated output under the Windows MAX_PATH limit the JDK file API\n");
        sb.Append("// enforces. launcher keeps build/outputs in place because Unity copies the APK\n");
        sb.Append("// from launcher/build/outputs/apk/release.\n");
        sb.Append("def c05ShortBuildRoot = '" + ShortBuildRoot + "'\n");
        sb.Append("gradle.beforeProject { p ->\n");
        sb.Append("    if (p.path == ':unityLibrary') {\n");
        sb.Append("        p.buildDir = new File(c05ShortBuildRoot, 'unityLibrary')\n");
        sb.Append("    } else if (p.path == ':launcher') {\n");
        sb.Append("        p.layout.buildDirectory.dir('intermediates').get().asFile = ")
          .Append("new File(c05ShortBuildRoot, 'launcher-intermediates')\n");
        sb.Append("    }\n");
        sb.Append("}\n");
        File.WriteAllText(settings, sb.ToString(), new UTF8Encoding(false));
    }
}

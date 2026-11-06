#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mahjong.Unity.Editor
{
    /// <summary>
    /// Editor-only fail-closed gate for the shared C# plugin closure.
    ///
    /// A Unity Editor may otherwise silently compile/play against the stale
    /// binary DLLs committed under Assets/Plugins/Mahjong when the developer
    /// forgot to run tools/unity/build-shared.ps1 after changing dotnet/src.
    /// This guard invokes the same production verifier used from the command
    /// line (tools/unity/check-shared-plugins.ps1) before:
    ///   * any BuildPipeline / -buildWindows64Player / -executeMethod build, via
    ///     IPreprocessBuildWithReport; and
    ///   * entering Play mode, via playModeStateChanged(ExitingEditMode).
    ///
    /// It never runs in a player build, never reads repository sources at
    /// runtime, and never changes machine/editor policy. On failure it throws
    /// BuildFailedException (build) or cancels the play-mode transition (play).
    /// </summary>
    [InitializeOnLoad]
    public sealed class SharedPluginProvenanceGuard : IPreprocessBuildWithReport
    {
        public const string Marker = "SHARED_PLUGIN_PROVENANCE";
        private const string CheckerRelativePath = @"tools\unity\check-shared-plugins.ps1";
        private const int ValidationTimeoutMilliseconds = 180000;

        public int callbackOrder
        {
            get { return 0; }
        }

        /// <summary>Null until the play gate has run once in this domain.</summary>
        public static bool? LastPlayGateBlocked { get; private set; }

        /// <summary>Human-readable outcome of the most recent play gate.</summary>
        public static string LastPlayGateDetail { get; private set; }

        static SharedPluginProvenanceGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>Forces the static constructor (and play subscription) to run.</summary>
        public static void EnsureInitialized()
        {
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            string detail;
            if (!TryValidate(out detail))
            {
                string message = Marker + "_BUILD_BLOCKED: " + detail +
                    " Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\\unity\\build-shared.ps1 -Configuration Release";
                Debug.LogError(message);
                throw new BuildFailedException(message);
            }

            Debug.Log(Marker + "_BUILD_OK: shared plugin provenance verified before building " +
                report.summary.platform + ".");
        }

        public static bool TryValidate(out string detail)
        {
            try
            {
                string repositoryRoot = FindRepositoryRoot();
                if (string.IsNullOrEmpty(repositoryRoot))
                {
                    detail = "could not locate " + CheckerRelativePath + " in any ancestor of Application.dataPath (" +
                        Application.dataPath + "); the Unity project cannot prove that Assets/Plugins/Mahjong is synced.";
                    return false;
                }

                string checker = Path.Combine(repositoryRoot, CheckerRelativePath);
                if (!File.Exists(checker))
                {
                    detail = "provenance checker not found at " + checker +
                        "; the Unity project cannot prove that Assets/Plugins/Mahjong is synced.";
                    return false;
                }

                string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string powershell = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershell))
                {
                    detail = "Windows PowerShell 5.1 not found at " + powershell +
                        "; refusing to skip the shared-plugin provenance gate.";
                    return false;
                }

                string arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    QuoteArgument(checker) + " -ProjectRoot " + QuoteArgument(repositoryRoot) + " -Quiet";

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var startInfo = new ProcessStartInfo(powershell, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = repositoryRoot
                };

                using (var process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) { stdout.AppendLine(args.Data); }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) { stderr.AppendLine(args.Data); }
                    };

                    if (!process.Start())
                    {
                        detail = "failed to start the provenance checker process.";
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(ValidationTimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(5000); } catch { }
                        detail = "provenance checker timed out after " +
                            (ValidationTimeoutMilliseconds / 1000) + "s; output: " + CombineOutput(stdout, stderr);
                        return false;
                    }

                    // Flush the asynchronous readers before reading ExitCode/output.
                    process.WaitForExit();
                    int exitCode = process.ExitCode;
                    if (exitCode != 0)
                    {
                        detail = "provenance checker exited " + exitCode + "; " + CombineOutput(stdout, stderr);
                        return false;
                    }
                }

                detail = "ok";
                return true;
            }
            catch (Exception exception)
            {
                detail = "provenance guard exception " + exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }

            string detail;
            if (TryValidate(out detail))
            {
                LastPlayGateBlocked = false;
                LastPlayGateDetail = "ok";
            }
            else
            {
                LastPlayGateBlocked = true;
                LastPlayGateDetail = detail;
                // Setting isPlaying=false during ExitingEditMode cancels the transition.
                EditorApplication.isPlaying = false;
                Debug.LogError(Marker + "_PLAY_BLOCKED: " + detail +
                    " Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\\unity\\build-shared.ps1 -Configuration Release");
            }
        }

        private static string FindRepositoryRoot()
        {
            // Application.dataPath = <repo>/unity/Mahjong.Unity/Assets. Walk up
            // until the production checker is actually found instead of trusting
            // a fixed parent count: a wrong root previously made the gate fail
            // for the wrong reason (missing checker) instead of validating the
            // plugin closure. If no ancestor contains the checker, fail closed.
            DirectoryInfo current = new DirectoryInfo(Application.dataPath);
            for (int depth = 0; depth < 8 && current != null; depth++)
            {
                string candidate = Path.Combine(current.FullName, CheckerRelativePath);
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            return null;
        }

        private static string CombineOutput(StringBuilder stdout, StringBuilder stderr)
        {
            string combined = (stdout.ToString() + stderr.ToString()).Trim();
            if (combined.Length == 0)
            {
                return "(no checker output)";
            }
            const int limit = 6000;
            if (combined.Length > limit)
            {
                combined = "..." + combined.Substring(combined.Length - limit);
            }
            return combined.Replace("\r", " ").Replace("\n", " | ");
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }
                builder.Append(character);
            }
            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }
            builder.Append('"');
            return builder.ToString();
        }
    }
}
#endif

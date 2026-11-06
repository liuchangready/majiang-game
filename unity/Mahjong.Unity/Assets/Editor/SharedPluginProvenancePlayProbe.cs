#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Mahjong.Unity.Editor
{
    /// <summary>
    /// Test/automation probe for the Editor Play gate. It is Editor-only and is
    /// never compiled into a player. Run it in a non-batch Editor with:
    ///
    ///   Unity.exe -projectPath unity/Mahjong.Unity
    ///     -executeMethod Mahjong.Unity.Editor.SharedPluginProvenancePlayProbe.Run
    ///     -provenancePlayProbeResult &lt;json path&gt;
    ///     -logFile &lt;log path&gt;
    ///
    /// It requests Play mode and records whether the real
    /// SharedPluginProvenanceGuard allowed or cancelled the transition:
    ///   exit 0  = Play mode actually entered (valid provenance)
    ///   exit 42 = Play mode was cancelled by the guard (invalid provenance)
    ///   exit 1  = Play mode did not enter for an unrelated reason
    ///   exit 3  = probe timeout
    /// </summary>
    public static class SharedPluginProvenancePlayProbe
    {
        private const string ResultArgument = "-provenancePlayProbeResult";
        private const double TimeoutSeconds = 180.0;
        private const double BlockedSettleSeconds = 5.0;
        private const double DiagnosticIntervalSeconds = 15.0;
        private const double FirstRequestDelaySeconds = 2.0;
        private const double RetryRequestAfterSeconds = 20.0;
        private const int MaxPlayRequests = 3;

        private static string _resultPath;
        private static double _startedAt;
        private static double _playRequestedAt = -1.0;
        private static double _lastDiagnosticAt = -1.0;
        private static int _playRequestCount;
        private static bool _enteredPlayMode;
        private static bool _completed;

        public static void Run()
        {
            _resultPath = ReadArgument(ResultArgument);
            if (string.IsNullOrEmpty(_resultPath))
            {
                Debug.LogError("PLAY_PROBE_ERROR: missing " + ResultArgument);
                EditorApplication.Exit(2);
                return;
            }

            SharedPluginProvenanceGuard.EnsureInitialized();
            _startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnUpdate;
            EditorApplication.delayCall += RequestPlay;
        }

        /// <summary>
        /// Requests Play mode once. delayCall can be dropped while Unity is still
        /// performing its first asset import, so OnUpdate also invokes this after
        /// a short delay and retries a request that was silently ignored.
        /// </summary>
        private static void RequestPlay()
        {
            if (_completed || _playRequestCount >= MaxPlayRequests)
            {
                return;
            }

            _playRequestCount++;
            Debug.Log("PLAY_PROBE_REQUESTED attempt=" + _playRequestCount);
            EditorApplication.isPlaying = true;
            _playRequestedAt = EditorApplication.timeSinceStartup;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (_completed)
            {
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _enteredPlayMode = true;
                Debug.Log("PLAY_PROBE_ENTERED_PLAY");
                EditorApplication.delayCall += delegate
                {
                    if (!_completed)
                    {
                        EditorApplication.isPlaying = false;
                    }
                };
                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode)
            {
                if (_enteredPlayMode)
                {
                    Finish(true, SharedPluginProvenanceGuard.LastPlayGateBlocked, 0);
                }
                else
                {
                    bool blocked = SharedPluginProvenanceGuard.LastPlayGateBlocked == true;
                    Finish(false, blocked, blocked ? 42 : 1);
                }
            }
        }

        private static void OnUpdate()
        {
            if (_completed)
            {
                return;
            }

            if (_playRequestedAt <= 0.0)
            {
                if (EditorApplication.timeSinceStartup - _startedAt > FirstRequestDelaySeconds)
                {
                    RequestPlay();
                }
            }
            else if (!_enteredPlayMode &&
                     SharedPluginProvenanceGuard.LastPlayGateBlocked != true &&
                     _playRequestCount < MaxPlayRequests &&
                     !EditorApplication.isPlayingOrWillChangePlaymode &&
                     EditorApplication.timeSinceStartup - _playRequestedAt > RetryRequestAfterSeconds)
            {
                Debug.Log("PLAY_PROBE_RETRY: previous Play request was not observed");
                RequestPlay();
            }

            double elapsedSinceRequest = (_playRequestedAt > 0.0)
                ? EditorApplication.timeSinceStartup - _playRequestedAt
                : 0.0;

            // Once the real guard has reported a block, EnteredPlayMode can never
            // arrive. Finish after a short settle period even if Unity leaves the
            // "will change play mode" flag set, which otherwise hangs a headless
            // probe until the wrapper timeout. This still asserts the negative:
            // no EnteredPlayMode event and the guard's own blocked result.
            if (!_enteredPlayMode &&
                _playRequestedAt > 0.0 &&
                SharedPluginProvenanceGuard.LastPlayGateBlocked == true &&
                elapsedSinceRequest > BlockedSettleSeconds)
            {
                Debug.Log("PLAY_PROBE_BLOCKED_CANCELLED: enteredPlay=False isPlaying=" +
                    EditorApplication.isPlaying + " willChange=" +
                    EditorApplication.isPlayingOrWillChangePlaymode);
                Finish(false, true, 42);
                return;
            }

            // Unity does not always deliver an EnteredEditMode event when the
            // ExitingEditMode transition is cancelled; observe the stable
            // "still in edit mode, play never started" state instead.
            if (!_enteredPlayMode &&
                _playRequestedAt > 0.0 &&
                SharedPluginProvenanceGuard.LastPlayGateBlocked == true &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                elapsedSinceRequest > 2.0)
            {
                Debug.Log("PLAY_PROBE_BLOCKED_CANCELLED");
                Finish(false, true, 42);
                return;
            }

            if (_enteredPlayMode || _playRequestedAt > 0.0)
            {
                if (_lastDiagnosticAt < 0.0 ||
                    EditorApplication.timeSinceStartup - _lastDiagnosticAt > DiagnosticIntervalSeconds)
                {
                    _lastDiagnosticAt = EditorApplication.timeSinceStartup;
                    Debug.Log("PLAY_PROBE_STATE: enteredPlay=" + _enteredPlayMode +
                        " isPlaying=" + EditorApplication.isPlaying +
                        " willChange=" + EditorApplication.isPlayingOrWillChangePlaymode +
                        " gateBlocked=" + SharedPluginProvenanceGuard.LastPlayGateBlocked);
                }
            }

            if (EditorApplication.timeSinceStartup - _startedAt > TimeoutSeconds)
            {
                Finish(_enteredPlayMode, SharedPluginProvenanceGuard.LastPlayGateBlocked, 3);
            }
        }

        private static void Finish(bool enteredPlay, bool? blocked, int exitCode)
        {
            if (_completed)
            {
                return;
            }
            _completed = true;

            string detail = SharedPluginProvenanceGuard.LastPlayGateDetail;
            if (detail == null)
            {
                detail = string.Empty;
            }
            string json = "{\"enteredPlay\":" + (enteredPlay ? "true" : "false") +
                ",\"blocked\":" + (blocked == true ? "true" : "false") +
                ",\"gateResultKnown\":" + (blocked.HasValue ? "true" : "false") +
                ",\"detail\":\"" + EscapeJson(detail) + "\"}";

            try
            {
                string directory = Path.GetDirectoryName(_resultPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(_resultPath, json, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Debug.LogError("PLAY_PROBE_WRITE_ERROR: " + exception.Message);
                exitCode = 2;
            }

            Debug.Log("PLAY_PROBE_RESULT: " + json);
            EditorApplication.Exit(exitCode);
        }

        private static string ReadArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length - 1; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index + 1];
                }
            }
            return null;
        }

        private static string EscapeJson(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(character); break;
                }
            }
            return builder.ToString();
        }
    }
}
#endif

#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    /// <summary>Run identity shared by the UI and app composition without crossing assembly boundaries.</summary>
    public static class LocalLiteRunContext
    {
        [Serializable]
        private sealed class RunRecord
        {
            public string product = "G2-Local-Lite";
            public string version = string.Empty;
            public string buildGuid = string.Empty;
            public string runId = string.Empty;
            public string startedUtc = string.Empty;
            public string buildIdentity = string.Empty;
            public string characterId = "player";
        }

        [Serializable]
        private sealed class StageRecord
        {
            public string utc = string.Empty;
            public string caseId = string.Empty;
            public string stage = string.Empty;
            public string characterId = "player";
        }

        public static bool Enabled { get; private set; }
        public static string? RunRoot { get; private set; }
        public static string CurrentCaseId { get; private set; } = "random";
        public static string CurrentStage { get; private set; } = "boot";
        public static string CurrentCharacterId { get; private set; } = "player";
        public static string? LastFailure { get; private set; }
        public static string? LastPublicError { get; private set; }

        public static void SetPublicError(string? error) => LastPublicError = error;

        public static void ResetForTest()
        {
            Application.logMessageReceived -= CapturePublicDiagnostic;
            Enabled = false;
            RunRoot = null;
            CurrentCaseId = "random";
            CurrentStage = "boot";
            CurrentCharacterId = "player";
            _record = null;
            LastFailure = null;
            LastPublicError = null;
        }

        public static void Initialize(string runRoot, string version, string buildGuid, string buildIdentity)
        {
            RunRoot = Path.GetFullPath(runRoot);
            Enabled = true;
            try
            {
                string runId = Path.GetFileName(RunRoot);
                var record = new RunRecord
                {
                    version = version,
                    buildGuid = buildGuid,
                    runId = runId,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    buildIdentity = buildIdentity,
                    characterId = CurrentCharacterId
                };
                _record = record;
                Directory.CreateDirectory(Path.Combine(RunRoot, "logs"));
                File.WriteAllText(Path.Combine(RunRoot, "run.json"), JsonUtility.ToJson(record) + "\n");
                LastFailure = null;
                CurrentCaseId = "random";
                CurrentStage = "boot";
                Application.logMessageReceived -= CapturePublicDiagnostic;
                Application.logMessageReceived += CapturePublicDiagnostic;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LastFailure = "LOCAL_LITE_DIAGNOSTICS_UNAVAILABLE";
            }
        }

        private static RunRecord? _record;

        public static void SetCharacter(string characterId)
        {
            CurrentCharacterId = characterId == "octopus" ? "octopus" : "player";
            if (_record == null || string.IsNullOrWhiteSpace(RunRoot)) return;
            _record.characterId = CurrentCharacterId;
            try { File.WriteAllText(Path.Combine(RunRoot, "run.json"), JsonUtility.ToJson(_record) + "\n"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { LastFailure = "LOCAL_LITE_DIAGNOSTICS_UNAVAILABLE"; }
        }

        public static bool RecordStage(string? caseId, string stage)
        {
            string? root = RunRoot;
            if (!Enabled || string.IsNullOrWhiteSpace(root)) return true;
            CurrentCaseId = string.IsNullOrWhiteSpace(caseId) ? "random" : caseId;
            CurrentStage = stage;
            try
            {
                string line = JsonUtility.ToJson(new StageRecord
                {
                    utc = DateTime.UtcNow.ToString("o"),
                    caseId = CurrentCaseId,
                    stage = stage,
                    characterId = CurrentCharacterId
                }) + "\n";
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                File.AppendAllText(Path.Combine(root, "logs", "cases.jsonl"), line);
                File.WriteAllText(Path.Combine(root, "current-case.json"), line);
                LastFailure = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LastFailure = "LOCAL_LITE_DIAGNOSTICS_UNAVAILABLE";
                return false;
            }
        }

        private static void CapturePublicDiagnostic(string condition, string stackTrace, LogType type)
        {
            string? root = RunRoot;
            if (!Enabled || string.IsNullOrWhiteSpace(root) ||
                !(condition.StartsWith("LOCAL_LITE_", StringComparison.Ordinal) ||
                  condition.StartsWith("NAV_BLOCKED:", StringComparison.Ordinal) ||
                  condition.StartsWith("LOCAL_HOST_START_FAILED:", StringComparison.Ordinal))) return;
            try
            {
                string line = DateTime.UtcNow.ToString("o") + "|" + type + "|" + condition + "\n";
                File.AppendAllText(Path.Combine(root, "logs", "player-public.log"), line);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LastFailure = "LOCAL_LITE_DIAGNOSTICS_UNAVAILABLE";
            }
        }
    }
}

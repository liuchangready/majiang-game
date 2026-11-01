#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Mahjong.LocalHost;
using Mahjong.Unity.UI;
using Process = System.Diagnostics.Process;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Opt-in Player recovery exporter. It never runs unless the process is started with
    /// <c>-p45-recovery-export &lt;dir&gt;</c>. It opens the real production local table through
    /// the normal menu route, then asks the already-open LocalHost runtime for one read-only,
    /// committed snapshot. It submits no action and writes only the requested evidence files.
    /// </summary>
    public sealed class P45RecoveryExportProbe : MonoBehaviour
    {
        private const string ExportFlag = "-p45-recovery-export";

        private ProbeArguments _arguments = new ProbeArguments();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ProbeArguments arguments = ProbeArguments.Parse(Environment.GetCommandLineArgs());
            if (!arguments.Enabled)
            {
                return;
            }

            var gameObject = new GameObject("P45RecoveryExportProbe");
            DontDestroyOnLoad(gameObject);
            var probe = gameObject.AddComponent<P45RecoveryExportProbe>();
            probe._arguments = arguments;
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            if (!_arguments.IsComplete(out string argumentError))
            {
                Fail("ARGUMENT_INVALID", argumentError);
                yield break;
            }

            Directory.CreateDirectory(_arguments.ExportDirectory);
            Record("PROBE_START export=" + _arguments.ExportDirectory + " phase=" + _arguments.Phase);

            float menuDeadline = Time.realtimeSinceStartup + 60f;
            AppRoot? root = null;
            while (Time.realtimeSinceStartup < menuDeadline)
            {
                root = AppRoot.Instance;
                if (root != null && root.Router.CurrentScene == SceneId.Menu)
                {
                    break;
                }

                yield return null;
            }

            if (root == null || root.Router.CurrentScene != SceneId.Menu)
            {
                Fail("APP_ROOT_OR_MENU_TIMEOUT", "The production menu route did not become ready.");
                yield break;
            }

            try
            {
                LocalTableHost.PrepareRecoveryCaptureOnNextOpen(_arguments.DataDirectory);
            }
            catch (Exception exception)
            {
                Fail("CAPTURE_PREPARE_FAILED", exception.GetType().Name + ": " + exception.Message);
                yield break;
            }

            Task<LocalGameStartResult> start = root.Router.StartLocalGameAsync();
            while (!start.IsCompleted)
            {
                yield return null;
            }

            if (start.IsFaulted)
            {
                Fail("LOCAL_START_EXCEPTION", start.Exception?.GetBaseException().Message ?? "unknown");
                yield break;
            }

            LocalGameStartResult result = start.Result;
            Record("LOCAL_START status=" + result.Status + " code=" + result.StableErrorCode);
            if (result.Status != LocalGameStartStatus.Started)
            {
                Fail("LOCAL_START_FAILED", result.StableErrorCode);
                yield break;
            }

            float tableDeadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < tableDeadline && root.Router.CurrentScene != SceneId.Table)
            {
                yield return null;
            }

            if (root.Router.CurrentScene != SceneId.Table)
            {
                Fail("TABLE_SCENE_TIMEOUT", "The local table scene did not load.");
                yield break;
            }

            // The runtime captured its first committed baseline before starting the autonomous
            // driver. Read that exact capture rather than sampling a later AI-advanced version.
            // No command or synthetic input is submitted by this probe.
            Task<LocalTableRecoveryCapture> captureTask;
            try
            {
                captureTask = LocalTableHost.TakePreparedRecoveryCaptureAsync(_arguments.DataDirectory, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Fail("CAPTURE_START_FAILED", exception.GetType().Name + ": " + exception.Message);
                yield break;
            }

            float captureDeadline = Time.realtimeSinceStartup + 30f;
            while (!captureTask.IsCompleted && Time.realtimeSinceStartup < captureDeadline)
            {
                yield return null;
            }

            if (!captureTask.IsCompleted)
            {
                Fail("CAPTURE_TIMEOUT", "The committed authority snapshot was not stable within 30 seconds.");
                yield break;
            }

            if (captureTask.IsFaulted)
            {
                Exception exception = captureTask.Exception?.GetBaseException() ?? new InvalidOperationException("unknown");
                Fail("CAPTURE_FAILED", exception.GetType().Name + ": " + exception.Message);
                yield break;
            }

            try
            {
                WriteEvidence(captureTask.Result);
            }
            catch (Exception exception)
            {
                Fail("EXPORT_WRITE_FAILED", exception.GetType().Name + ": " + exception.Message);
                yield break;
            }

            Record("EXPORT_DONE phase=" + _arguments.Phase + " stateVersion=" + captureTask.Result.Facts.StateVersion);
            if (_arguments.ExitAfterExport)
            {
                Application.Quit(0);
            }
        }

        private void WriteEvidence(LocalTableRecoveryCapture capture)
        {
            LocalTableRecoveryFacts facts = capture.Facts;
            string authorityPath = Path.Combine(_arguments.ExportDirectory, "authority.save");
            string cheatPath = Path.Combine(_arguments.ExportDirectory, "cheat-state.bin");
            string exportPath = Path.Combine(_arguments.ExportDirectory, "export.json");
            string donePath = Path.Combine(_arguments.ExportDirectory, "export.done");
            if (File.Exists(authorityPath) || File.Exists(cheatPath) || File.Exists(exportPath) || File.Exists(donePath))
            {
                throw new IOException("The one-time export directory already contains evidence.");
            }

            string authoritySha = Sha256Hex(capture.AuthoritySaveBytes);
            if (!string.Equals(authoritySha, facts.AuthoritySaveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Captured authority bytes do not match their decoded digest.");
            }

            string cheatSha = Sha256Hex(capture.CheatStatePayload);
            if (!string.Equals(cheatSha, facts.CheatStateSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Captured cheat aggregate bytes do not match their decoded digest.");
            }

            string processPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                throw new InvalidOperationException("The exporter cannot read its own process path.");
            }

            processPath = Path.GetFullPath(processPath);
            string playerSha = Sha256File(processPath);
            if (!string.Equals(playerSha, _arguments.PlayerSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The running Player executable hash does not match the launch binding.");
            }

            string processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);
            string capturedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

            WriteBytesAtomic(authorityPath, capture.AuthoritySaveBytes);
            WriteBytesAtomic(cheatPath, capture.CheatStatePayload);
            string exportJson = BuildExportJson(
                facts,
                processPath,
                playerSha,
                processStart,
                capturedAt,
                authoritySha,
                capture.AuthoritySaveBytes.Length,
                cheatSha,
                capture.CheatStatePayload.Length);
            WriteTextAtomic(exportPath, exportJson);
            WriteTextAtomic(donePath, Sha256Hex(Encoding.UTF8.GetBytes(exportJson)) + "  export.json" + Environment.NewLine);
        }

        private string BuildExportJson(
            LocalTableRecoveryFacts facts,
            string processPath,
            string playerSha,
            string processStart,
            string capturedAt,
            string authoritySha,
            int authorityBytes,
            string cheatSha,
            int cheatBytes)
        {
            var builder = new StringBuilder(16384);
            builder.Append('{');
            Number(builder, "schemaVersion", 1); builder.Append(',');
            String(builder, "kind", "p45-player-structured-export"); builder.Append(',');
            String(builder, "exporterVersion", "p45-player-structured-export/1"); builder.Append(',');
            String(builder, "phase", _arguments.Phase); builder.Append(',');
            String(builder, "runId", _arguments.RunId); builder.Append(',');
            String(builder, "attempt", _arguments.Attempt); builder.Append(',');
            String(builder, "challenge", _arguments.Challenge); builder.Append(',');
            Number(builder, "pid", Process.GetCurrentProcess().Id); builder.Append(',');
            String(builder, "processStartUtc", processStart); builder.Append(',');
            String(builder, "processPath", processPath); builder.Append(',');
            String(builder, "playerSha256", playerSha); builder.Append(',');
            String(builder, "packageManifestSha256", _arguments.PackageManifestSha256); builder.Append(',');
            Number(builder, "packageFileCount", _arguments.PackageFileCount); builder.Append(',');
            String(builder, "sourceHead", _arguments.SourceHead); builder.Append(',');
            String(builder, "buildCommit", _arguments.BuildCommit); builder.Append(',');
            String(builder, "buildProvenance", _arguments.BuildProvenance); builder.Append(',');
            Number(builder, "rulesetVersion", _arguments.RulesetVersion); builder.Append(',');
            String(builder, "capturedAt", capturedAt); builder.Append(',');
            String(builder, "authorityFile", "authority.save"); builder.Append(',');
            Number(builder, "authorityBytes", authorityBytes); builder.Append(',');
            String(builder, "authoritySha256", authoritySha); builder.Append(',');
            String(builder, "cheatStateFile", "cheat-state.bin"); builder.Append(',');
            Number(builder, "cheatStateBytes", cheatBytes); builder.Append(',');
            String(builder, "cheatStateSha256", cheatSha); builder.Append(',');
            builder.Append("\"facts\":");
            AppendFacts(builder, facts);
            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendFacts(StringBuilder builder, LocalTableRecoveryFacts facts)
        {
            builder.Append('{');
            String(builder, "authoritySaveSha256", facts.AuthoritySaveSha256); builder.Append(',');
            Number(builder, "authoritySaveBytes", facts.AuthoritySaveBytes); builder.Append(',');
            String(builder, "roomId", facts.RoomId); builder.Append(',');
            String(builder, "roundId", facts.RoundId); builder.Append(',');
            Number(builder, "rulesetVersion", facts.RulesetVersion); builder.Append(',');
            String(builder, "stateVersion", facts.StateVersion); builder.Append(',');
            String(builder, "eventSeq", facts.EventSeq); builder.Append(',');
            String(builder, "stateFingerprint", facts.StateFingerprint); builder.Append(',');
            String(builder, "stateHash", facts.StateHash); builder.Append(',');
            String(builder, "controlVersion", facts.ControlVersion); builder.Append(',');
            String(builder, "cheatVersion", facts.CheatVersion); builder.Append(',');
            String(builder, "cheatFingerprint", facts.CheatFingerprint); builder.Append(',');
            String(builder, "cheatStateSha256", facts.CheatStateSha256); builder.Append(',');
            builder.Append("\"controlEpochs\":[");
            for (int i = 0; i < facts.ControlEpochs.Count; i++)
            {
                if (i != 0) { builder.Append(','); }
                builder.Append(facts.ControlEpochs[i].ToString(CultureInfo.InvariantCulture));
            }

            builder.Append("],");

            builder.Append("\"requests\":[");
            for (int i = 0; i < facts.Requests.Count; i++)
            {
                if (i != 0) { builder.Append(','); }
                LocalTableRecoveryRequestFact request = facts.Requests[i];
                builder.Append('{');
                String(builder, "accountId", request.AccountId); builder.Append(',');
                String(builder, "scopeId", request.ScopeId); builder.Append(',');
                String(builder, "requestId", request.RequestId); builder.Append(',');
                String(builder, "receiptId", request.ReceiptId); builder.Append(',');
                Bool(builder, "confirmed", request.Confirmed); builder.Append(',');
                String(builder, "status", request.Status); builder.Append(',');
                String(builder, "payloadSha256", request.PayloadSha256); builder.Append(',');
                String(builder, "evaluatedStateVersion", request.EvaluatedStateVersion); builder.Append(',');
                StringOrNull(builder, "commandVersion", request.CommandVersion); builder.Append(',');
                StringOrNull(builder, "eventSeq", request.EventSeq);
                builder.Append('}');
            }

            builder.Append("],");

            builder.Append("\"controlRequests\":[");
            for (int i = 0; i < facts.ControlRequests.Count; i++)
            {
                if (i != 0) { builder.Append(','); }
                LocalTableRecoveryControlFact control = facts.ControlRequests[i];
                builder.Append('{');
                String(builder, "accountId", control.AccountId); builder.Append(',');
                String(builder, "scopeId", control.ScopeId); builder.Append(',');
                String(builder, "requestId", control.RequestId); builder.Append(',');
                String(builder, "digest", control.Digest); builder.Append(',');
                String(builder, "kind", control.Kind); builder.Append(',');
                String(builder, "evaluatedStateVersion", control.EvaluatedStateVersion);
                builder.Append('}');
            }

            builder.Append("],");

            builder.Append("\"ledger\":{");
            Number(builder, "points", facts.Ledger.Points); builder.Append(',');
            Number(builder, "initialPoints", facts.Ledger.InitialPoints); builder.Append(',');
            builder.Append("\"balances\":[");
            for (int i = 0; i < facts.Ledger.Balances.Count; i++)
            {
                if (i != 0) { builder.Append(','); }
                builder.Append(facts.Ledger.Balances[i].ToString(CultureInfo.InvariantCulture));
            }

            builder.Append("],");
            Number(builder, "entryCount", facts.Ledger.EntryCount); builder.Append(',');
            String(builder, "entriesSha256", facts.Ledger.EntriesSha256); builder.Append(',');
            String(builder, "balancesSha256", facts.Ledger.BalancesSha256); builder.Append(',');
            Number(builder, "totalVersion", facts.Ledger.TotalVersion); builder.Append(',');
            Number(builder, "checkpointVersion", facts.Ledger.CheckpointVersion); builder.Append(',');
            String(builder, "source", facts.Ledger.Source);
            builder.Append("},");

            builder.Append("\"physical\":{");
            Number(builder, "tileCount", facts.Physical.TileCount); builder.Append(',');
            Number(builder, "uniqueCount", facts.Physical.UniqueCount); builder.Append(',');
            Number(builder, "wallCount", facts.Physical.WallCount); builder.Append(',');
            Number(builder, "deadWallCount", facts.Physical.DeadWallCount); builder.Append(',');
            String(builder, "tilesSha256", facts.Physical.TilesSha256);
            builder.Append("}}");
        }

        private void Fail(string code, string detail)
        {
            Record("EXPORT_FAIL " + code + " " + detail);
            try
            {
                var builder = new StringBuilder();
                builder.Append('{');
                String(builder, "kind", "p45-player-structured-export-error");
                builder.Append(',');
                String(builder, "phase", _arguments.Phase);
                builder.Append(',');
                String(builder, "code", code);
                builder.Append(',');
                String(builder, "detail", detail);
                builder.Append(',');
                String(builder, "capturedAt", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
                builder.Append('}');
                WriteTextAtomic(Path.Combine(_arguments.ExportDirectory, "export.error.json"), builder.ToString());
            }
            catch
            {
                // Debug.Log remains the process log.
            }

            UnityEngine.Debug.LogError("P45_RECOVERY_EXPORT_FAIL " + code + ": " + detail);
            Application.Quit(1);
        }

        private static void Record(string line)
        {
            UnityEngine.Debug.Log("P45_RECOVERY_EXPORT " + line);
        }

        private static void WriteBytesAtomic(string path, byte[] bytes)
        {
            string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path);
        }

        private static void WriteTextAtomic(string path, string text)
        {
            string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path);
        }

        private static string Sha256File(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void String(StringBuilder builder, string name, string value)
        {
            builder.Append('"').Append(name).Append("\":\"");
            AppendEscaped(builder, value);
            builder.Append('"');
        }

        private static void StringOrNull(StringBuilder builder, string name, string? value)
        {
            builder.Append('"').Append(name).Append("\":");
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            builder.Append('"');
            AppendEscaped(builder, value);
            builder.Append('"');
        }

        private static void Number(StringBuilder builder, string name, long value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Bool(StringBuilder builder, string name, bool value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
        }

        private static void AppendEscaped(StringBuilder builder, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }
        }

        private sealed class ProbeArguments
        {
            public bool Enabled { get; private set; }
            public string ExportDirectory { get; private set; } = string.Empty;
            public string Phase { get; private set; } = string.Empty;
            public string Challenge { get; private set; } = string.Empty;
            public string RunId { get; private set; } = string.Empty;
            public string Attempt { get; private set; } = string.Empty;
            public string DataDirectory { get; private set; } = string.Empty;
            public string SmokeDataRoot { get; private set; } = string.Empty;
            public string PlayerSha256 { get; private set; } = string.Empty;
            public string PackageManifestSha256 { get; private set; } = string.Empty;
            public int PackageFileCount { get; private set; }
            public string SourceHead { get; private set; } = string.Empty;
            public string BuildCommit { get; private set; } = string.Empty;
            public string BuildProvenance { get; private set; } = string.Empty;
            public int RulesetVersion { get; private set; }
            public bool ExitAfterExport { get; private set; }

            public static ProbeArguments Parse(string[] args)
            {
                var result = new ProbeArguments();
                result.ExportDirectory = Value(args, ExportFlag);
                if (string.IsNullOrWhiteSpace(result.ExportDirectory))
                {
                    return result;
                }

                result.Enabled = true;
                result.Phase = Value(args, "-p45-recovery-phase");
                result.Challenge = Value(args, "-p45-recovery-challenge");
                result.RunId = Value(args, "-p45-recovery-run");
                result.Attempt = Value(args, "-p45-recovery-attempt");
                result.DataDirectory = Value(args, "-p45-recovery-data-dir");
                result.SmokeDataRoot = Value(args, "-smoke-data");
                result.PlayerSha256 = Value(args, "-p45-recovery-player-sha");
                result.PackageManifestSha256 = Value(args, "-p45-recovery-package-sha");
                result.SourceHead = Value(args, "-p45-recovery-source-head");
                result.BuildCommit = Value(args, "-p45-recovery-build-commit");
                result.BuildProvenance = Value(args, "-p45-recovery-build-provenance");
                result.ExitAfterExport = HasFlag(args, "-p45-recovery-exit-after-export");
                int.TryParse(Value(args, "-p45-recovery-package-count"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int fileCount);
                result.PackageFileCount = fileCount;
                int.TryParse(Value(args, "-p45-recovery-ruleset"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int ruleset);
                result.RulesetVersion = ruleset;
                return result;
            }

            public bool IsComplete(out string error)
            {
                if (Phase != "BEFORE_RECOVERY" && Phase != "AFTER_RECOVERY")
                {
                    error = "phase must be exactly BEFORE_RECOVERY or AFTER_RECOVERY";
                    return false;
                }

                if (Challenge.Length != 64 || !IsHex(Challenge))
                {
                    error = "challenge must be 64 hex characters";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(RunId) || string.IsNullOrWhiteSpace(Attempt) ||
                    string.IsNullOrWhiteSpace(DataDirectory) || string.IsNullOrWhiteSpace(SmokeDataRoot) ||
                    string.IsNullOrWhiteSpace(PlayerSha256) || string.IsNullOrWhiteSpace(PackageManifestSha256) ||
                    string.IsNullOrWhiteSpace(SourceHead) || string.IsNullOrWhiteSpace(BuildCommit) ||
                    string.IsNullOrWhiteSpace(BuildProvenance) || PackageFileCount <= 0 || RulesetVersion <= 0)
                {
                    error = "one or more recovery binding fields are empty";
                    return false;
                }

                string export = Path.GetFullPath(ExportDirectory);
                string data = Path.GetFullPath(DataDirectory);
                string smoke = Path.GetFullPath(SmokeDataRoot);
                if (!export.StartsWith(smoke.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    error = "export directory escapes the -smoke-data root";
                    return false;
                }

                if (!string.Equals(data, Path.Combine(smoke, "local"), StringComparison.OrdinalIgnoreCase))
                {
                    error = "data directory is not the production local save directory under -smoke-data";
                    return false;
                }

                error = string.Empty;
                return true;
            }

            private static string Value(string[] args, string flag)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], flag, StringComparison.Ordinal))
                    {
                        return args[i + 1];
                    }
                }

                return string.Empty;
            }

            private static bool HasFlag(string[] args, string flag)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], flag, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool IsHex(string value)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!hex)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}

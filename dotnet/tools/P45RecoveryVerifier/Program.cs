using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.LocalHost;

namespace P45RecoveryVerifier
{
    /// <summary>
    /// Independent read-only verifier for one P4.5 Player authority snapshot. It decodes the
    /// persisted bytes and the separately captured current cheat aggregate in a separate process;
    /// it never opens a LocalHost runtime, acquires a lease, submits a command or writes a save.
    /// </summary>
    internal static class Program
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.Error.WriteLine("usage: P45RecoveryVerifier <authority-save-path> [current-cheat-state-path]");
                return 2;
            }

            try
            {
                string path = Path.GetFullPath(args[0]);
                byte[] bytes = File.ReadAllBytes(path);
                byte[]? cheat = null;
                if (args.Length == 2)
                {
                    if (string.IsNullOrWhiteSpace(args[1]))
                    {
                        throw new ArgumentException("The current cheat-state path is empty.");
                    }

                    cheat = File.ReadAllBytes(Path.GetFullPath(args[1]));
                }

                LocalTableRecoveryFacts facts = LocalTableRecoveryFactsReader.ReadPersistedFacts(bytes, cheat);
                Console.Out.Write(JsonSerializer.Serialize(new VerifierResult(true, facts, null), Options));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Out.Write(JsonSerializer.Serialize(
                    new VerifierResult(false, null, exception.GetType().Name + ": " + exception.Message),
                    Options));
                return 2;
            }
        }

        private sealed class VerifierResult
        {
            public VerifierResult(bool ok, LocalTableRecoveryFacts? facts, string? error)
            {
                Ok = ok;
                Facts = facts;
                Error = error;
            }

            public bool Ok { get; }
            public LocalTableRecoveryFacts? Facts { get; }
            public string? Error { get; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    public static class Program
    {
        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitBlocked = 2;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("usage: <suite|crash-client|recover-client|https-health> --key value ...");
                    return ExitBlocked;
                }

                string mode = args[0];
                Options options = Options.Parse(args.Skip(1).ToArray());
                switch (mode)
                {
                    case "https-health":
                        return await RunHttpsHealthAsync(options).ConfigureAwait(false);
                    case "suite":
                        return await SuiteRunner.RunAsync(options).ConfigureAwait(false);
                    case "crash-client":
                        return await CrashClient.RunAsync(options).ConfigureAwait(false);
                    case "recover-client":
                        return await RecoverClient.RunAsync(options).ConfigureAwait(false);
                    default:
                        Console.Error.WriteLine("unknown mode: " + mode);
                        return ExitBlocked;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("b1-worker failed: " + ex.GetType().Name + ": " + Redact(ex.Message));
                return ExitFailed;
            }
        }

        private static async Task<int> RunHttpsHealthAsync(Options options)
        {
            string url = options.Require("url");
            string caRoot = options.Require("ca-root");
            string expect = options.Require("expect");
            using var root = new X509Certificate2(caRoot);
            using var client = new B1LobbyClient(new Uri(url), root);
            B1LobbyClient.LobbyHttpResult result = await client.GetAsync(string.Empty, CancellationToken.None)
                .ConfigureAwait(false);
            if (!string.Equals(result.StatusCode.ToString(CultureInfo.InvariantCulture), expect, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "health expected " + expect + " but observed " + result.StatusCode + ".");
                return ExitFailed;
            }

            Console.WriteLine("B1-HEALTH-OK status=" + result.StatusCode);
            return ExitOk;
        }

        public static string Sha256Hex(string value)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        public static string Sha256File(string path)
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }

        public static void WriteJson(string path, object value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        public static T ReadJson<T>(string path)
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException("Could not deserialize " + path + ".");
        }

        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            // A diagnostic whitelist, not a blacklist and not a sanitizer.
            // Arbitrary exception text, JSON, escaped keys, tokens and any
            // unknown structure are not echoed at all. Only exact stable codes,
            // status words, exception type names, HTTP/stage forms and simple
            // key=value diagnostics with whitelisted keys survive.
            string value = message.Trim();
            if (IsSafeDiagnostic(value))
            {
                return value;
            }

            return "[redacted]";
        }

        private static readonly HashSet<string> SafeCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "PASS", "FAIL", "BLOCKED", "APPLIED", "REJECTED", "RECORDED", "UNKNOWN", "NOT_RUN",
            "CLIENT_RECEIPT_SCOPE_MISSING", "REQUEST_ID_CONFLICT", "SCOPE_MISMATCH",
            "SCOPE_ROLE_NOT_AUTHORIZED", "INVALID_TOKEN", "PROTOCOL_VIOLATION",
            "WS_CLOSE_4401", "WS_CLOSE_4403", "WS_CLOSE_4404", "WS_CLOSE_4503"
        };

        private static readonly HashSet<string> SafeExceptionTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ArgumentException", "InvalidOperationException", "IOException", "TimeoutException",
            "OperationCanceledException", "HttpRequestException", "WebSocketException",
            "TaskCanceledException", "ObjectDisposedException", "ProbeRejectedException"
        };

        private static bool IsSafeDiagnostic(string value)
        {
            if (value.Length == 0 || value.Length > 200)
            {
                return false;
            }

            // Any structure, quoting, escaping, whitespace-bearing free text or
            // credential keyword is untrusted by default.
            if (value.IndexOfAny(new[] { '{', '}', '[', ']', '"', '\'', '\\', '\r', '\n', '\t' }) >= 0)
            {
                return false;
            }

            if (Regex.IsMatch(value, @"(?i)\b(token|secret|password|authorization|session|bootstrap|credential|private[_-]?key)\b"))
            {
                return false;
            }

            if (SafeCodes.Contains(value) || SafeExceptionTypes.Contains(value))
            {
                return true;
            }

            if (Regex.IsMatch(value, @"^HTTP [0-9]{3}$") ||
                Regex.IsMatch(value, @"^stage [a-z0-9-]+$"))
            {
                return true;
            }

            // Structured diagnostic fields we explicitly emit ourselves.
            if (Regex.IsMatch(value, @"^(code|status|stage|reason|state|room|seat|role|version|count)=[A-Za-z0-9_.:/-]{0,80}$"))
            {
                return true;
            }

            return Regex.IsMatch(value, @"^(code|status|stage)=[A-Za-z0-9_.:/-]{0,80}(;(code|status|stage)=[A-Za-z0-9_.:/-]{0,80}){0,3}$");
        }

        public sealed class Options
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string key = args[i];
                    if (!key.StartsWith("--", StringComparison.Ordinal) || key.Length <= 2)
                    {
                        throw new ArgumentException("Expected --key, got: " + key);
                    }

                    string name = key.Substring(2);
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options._values[name] = args[++i];
                    }
                    else
                    {
                        options._values[name] = "true";
                    }
                }

                return options;
            }

            public string Require(string name)
            {
                if (!_values.TryGetValue(name, out string? value) || string.IsNullOrEmpty(value))
                {
                    throw new ArgumentException("Missing --" + name + ".");
                }

                return value;
            }

            public string? Optional(string name)
            {
                return _values.TryGetValue(name, out string? value) ? value : null;
            }

            public bool Has(string name)
            {
                return _values.ContainsKey(name);
            }
        }
    }
}

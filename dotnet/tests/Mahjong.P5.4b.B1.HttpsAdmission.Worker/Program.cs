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

            // Diagnostic output is a whitelist, not a sanitizer that tries to keep
            // a credential with a changed prefix. Any token/PEM/credential-shaped
            // input is replaced as a whole; ordinary short status text is kept.
            string value = message!;
            if (LooksSensitive(value))
            {
                return "[redacted-sensitive]";
            }

            return value.Length > 300 ? value.Substring(0, 300) : value;
        }

        private static bool LooksSensitive(string value)
        {
            return Regex.IsMatch(value, @"-----BEGIN [^-]+-----", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(value, @"(?i)\bBearer\s+\S+")
                   || Regex.IsMatch(value, @"[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")
                   || Regex.IsMatch(value, @"(?i)[""']?(sessionToken|bootstrapSecret|authorization|private[_-]?key|password|secret|token|credential)[""']?\s*[:=]")
                   || Regex.IsMatch(value, @"eyJ[A-Za-z0-9_-]{8,}")
                   || Regex.IsMatch(value, @"[A-Za-z0-9+/]{20,}={0,2}")
                   || Regex.IsMatch(value, @"[A-Za-z0-9_-]{16,}")
                   || Regex.IsMatch(value, @"[A-Fa-f0-9]{40,}");
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

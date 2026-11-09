using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.TestSupport
{
    /// <summary>Checks frozen V4 source identities without requiring old sources or Git history.</summary>
    public static class FrozenGoldenProvenance
    {
        private const string PinsSha256 = "b6b36fd35b76be74846093305e17ff357d849d9861a1c876ea9069c6968eaf67";

        public static void AssertFixture(string fixturePath) => AssertFixture(fixturePath, ResolveDefaultFixture(fixturePath));

        public static void AssertFixture(string fixturePath, string actualFixturePath)
        {
            bool mutationProbe = String.Equals(Environment.GetEnvironmentVariable("MAHJONG_GOLDEN_PROBE"), "1", StringComparison.Ordinal);

            string fullFixturePath = Path.GetFullPath(actualFixturePath);
            string root = FindRootOrAssemblyRoot(Path.GetDirectoryName(fullFixturePath)!);
            string pinsPath = Path.Combine(root, "tests", "v5-golden", "source-provenance-v1.json");
            byte[] pinsBytes = File.ReadAllBytes(pinsPath);
            if (!String.Equals(Sha256(pinsBytes), PinsSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Frozen source-pin manifest hash mismatch.");

            string relative = fixturePath.Replace('\\', '/');
            using JsonDocument pins = JsonDocument.Parse(pinsBytes);
            JsonElement manifest = pins.RootElement;
            if (manifest.GetProperty("schema").GetString() != "v5-frozen-v4-source-pins-v1")
                throw new InvalidDataException("Unexpected frozen source-pin schema.");

            JsonElement fixture = manifest.GetProperty("fixtures").EnumerateArray()
                .SingleOrDefault(row => row.GetProperty("path").GetString() == relative);
            if (fixture.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Golden fixture is not frozen: " + relative);

            byte[] actualBytes = File.ReadAllBytes(fullFixturePath);
            if (!mutationProbe)
            {
                byte[] canonicalBytes = NormalizeLf(actualBytes);
                string actualFixtureHash = Sha256(canonicalBytes);
                if (!String.Equals(actualFixtureHash, fixture.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                    throw new InvalidDataException("Golden fixture bytes differ from their frozen pin: " + relative);
            }

            HashSet<string> expectedPaths = manifest.GetProperty("sources").EnumerateArray()
                .Where(row => row.GetProperty("fixture").GetString() == relative)
                .Select(row =>
                {
                    string commit = row.GetProperty("commit").GetString() ?? String.Empty;
                    string blob = row.GetProperty("gitBlobSha1").GetString() ?? String.Empty;
                    string path = row.GetProperty("path").GetString() ?? String.Empty;
                    if (commit.Length != 40 || blob.Length != 40 || !IsHex(commit) || !IsHex(blob))
                        throw new InvalidDataException("Malformed historical Git source pin: " + path);
                    return path;
                }).ToHashSet(StringComparer.Ordinal);

            using JsonDocument golden = JsonDocument.Parse(actualBytes);
            AssertPinnedGeneratorMetadata(golden.RootElement, fixture);
            HashSet<string> observedPaths = new HashSet<string>(StringComparer.Ordinal);
            CollectHistoricalPaths(golden.RootElement, observedPaths);
            if (!observedPaths.SetEquals(expectedPaths))
                throw new InvalidDataException("Golden historical source paths differ from frozen pins: " + relative);
            AssertPinnedHistoricalHashes(golden.RootElement, manifest, fixture, relative);
        }

        public static void AssertHistoricalSourceHash(string fixturePath, string actualFixturePath, string historicalSourcePath, string actualSha256)
        {
            string root = FindRootOrAssemblyRoot(Path.GetDirectoryName(Path.GetFullPath(actualFixturePath))!);
            string pinsPath = Path.Combine(root, "tests", "v5-golden", "source-provenance-v1.json");
            byte[] pinsBytes = File.ReadAllBytes(pinsPath);
            if (!String.Equals(Sha256(pinsBytes), PinsSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Frozen source-pin manifest hash mismatch.");
            using JsonDocument pins = JsonDocument.Parse(pinsBytes);
            JsonElement pin = pins.RootElement.GetProperty("sources").EnumerateArray().SingleOrDefault(row =>
                row.GetProperty("fixture").GetString() == fixturePath &&
                row.GetProperty("path").GetString() == historicalSourcePath);
            if (pin.ValueKind == JsonValueKind.Undefined ||
                !String.Equals(pin.GetProperty("sha256LfNormalized").GetString(), actualSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Golden historical source hash differs from frozen source pin: " + historicalSourcePath);
        }

        private static void CollectHistoricalPaths(JsonElement value, HashSet<string> paths)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                    CollectHistoricalPaths(property.Value, paths);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                    CollectHistoricalPaths(item, paths);
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? String.Empty;
                foreach (Match match in Regex.Matches(text, "engine/src/[A-Za-z0-9_./-]+\\.kt"))
                    paths.Add(match.Value);
            }
        }

        private static void AssertPinnedGeneratorMetadata(JsonElement golden, JsonElement fixturePin)
        {
            if (fixturePin.TryGetProperty("generator", out JsonElement generator) && generator.ValueKind == JsonValueKind.String)
            {
                string actual = golden.TryGetProperty("generator", out JsonElement value) ? value.GetString() ?? String.Empty : String.Empty;
                if (!String.Equals(actual, generator.GetString(), StringComparison.Ordinal))
                    throw new InvalidDataException("Golden generator path differs from frozen identity.");
            }
            if (fixturePin.TryGetProperty("generatedByCommand", out JsonElement command) && command.ValueKind == JsonValueKind.String)
            {
                string actual = golden.TryGetProperty("generatedByCommand", out JsonElement value) ? value.GetString() ?? String.Empty : String.Empty;
                string[] accepted = fixturePin.TryGetProperty("acceptedGeneratorCommands", out JsonElement values)
                    ? values.EnumerateArray().Select(item => item.GetString() ?? String.Empty).ToArray()
                    : new[] { command.GetString() ?? String.Empty };
                if (!accepted.Contains(actual, StringComparer.Ordinal))
                    throw new InvalidDataException("Golden exporter command differs from frozen identity.");
            }
        }

        private static void AssertPinnedHistoricalHashes(JsonElement golden, JsonElement manifest, JsonElement fixturePin, string fixturePath)
        {
            JsonElement[] pins = manifest.GetProperty("sources").EnumerateArray()
                .Where(row => row.GetProperty("fixture").GetString() == fixturePath).ToArray();
            Dictionary<string, string> expected = pins
                .Where(row => row.TryGetProperty("sha256LfNormalized", out _))
                .ToDictionary(row => row.GetProperty("path").GetString()!, row => row.GetProperty("sha256LfNormalized").GetString()!, StringComparer.Ordinal);
            Dictionary<string, string> observed = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectHistoricalHashes(golden, observed);
            Dictionary<string, JsonElement> expectedMetadata = pins
                .Where(row => row.TryGetProperty("sourceMetadata", out _))
                .ToDictionary(row => row.GetProperty("path").GetString()!, row => row.GetProperty("sourceMetadata"), StringComparer.Ordinal);
            Dictionary<string, JsonElement> observedMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            CollectHistoricalSourceMetadata(golden, observedMetadata);
            if (golden.TryGetProperty("generator", out JsonElement generator) && generator.ValueKind == JsonValueKind.String &&
                golden.TryGetProperty("exporterSourceSha256", out JsonElement exporterHash) && exporterHash.ValueKind == JsonValueKind.String)
                observed[generator.GetString()!] = exporterHash.GetString()!;
            foreach (KeyValuePair<string, string> pin in expected)
            {
                bool required = fixturePin.TryGetProperty("sourceHashesRequired", out JsonElement requiredElement) && requiredElement.ValueKind == JsonValueKind.True;
                if (!observed.TryGetValue(pin.Key, out string? actual) && !required) continue;
                if (actual == null || !String.Equals(actual, pin.Value, StringComparison.Ordinal))
                    throw new InvalidDataException("Golden historical source hash differs from frozen pin: " + pin.Key);
            }
            foreach (KeyValuePair<string, JsonElement> pin in expectedMetadata)
            {
                if (!observedMetadata.TryGetValue(pin.Key, out JsonElement actual))
                    throw new InvalidDataException("Golden historical source metadata is missing: " + pin.Key);
                JsonElement expectedMeta = pin.Value;
                if (actual.GetProperty("role").GetString() != expectedMeta.GetProperty("role").GetString() ||
                    actual.GetProperty("sha256Raw").GetString() != expectedMeta.GetProperty("sha256Raw").GetString() ||
                    actual.GetProperty("bytesRaw").GetInt64() != expectedMeta.GetProperty("bytesRaw").GetInt64() ||
                    actual.GetProperty("bytesLfNormalized").GetInt64() != expectedMeta.GetProperty("bytesLfNormalized").GetInt64() ||
                    actual.GetProperty("lineEnding").GetString() != expectedMeta.GetProperty("lineEnding").GetString() ||
                    actual.GetProperty("rawEqualsLfNormalized").GetBoolean() != expectedMeta.GetProperty("rawEqualsLfNormalized").GetBoolean())
                    throw new InvalidDataException("Golden historical source metadata differs from frozen pin: " + pin.Key);
            }
        }

        private static void CollectHistoricalSourceMetadata(JsonElement value, Dictionary<string, JsonElement> metadata)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("role", out JsonElement role) && role.ValueKind == JsonValueKind.String &&
                    value.TryGetProperty("path", out JsonElement path) && path.ValueKind == JsonValueKind.String &&
                    value.TryGetProperty("sha256Raw", out JsonElement rawHash) && rawHash.ValueKind == JsonValueKind.String &&
                    value.TryGetProperty("sha256LfNormalized", out _))
                    metadata[path.GetString()!] = value;
                foreach (JsonProperty property in value.EnumerateObject())
                    CollectHistoricalSourceMetadata(property.Value, metadata);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray()) CollectHistoricalSourceMetadata(item, metadata);
            }
        }

        private static void CollectHistoricalHashes(JsonElement value, Dictionary<string, string> hashes)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                string? path = value.TryGetProperty("file", out JsonElement file) ? file.GetString() :
                    value.TryGetProperty("path", out JsonElement itemPath) ? itemPath.GetString() : null;
                JsonElement hash = value.TryGetProperty("sha256LfNormalized", out JsonElement lfHash) ? lfHash :
                    value.TryGetProperty("sha256", out JsonElement rawHash) ? rawHash : default;
                if (path != null && path.StartsWith("engine/src/", StringComparison.Ordinal) && path.EndsWith(".kt", StringComparison.Ordinal) && hash.ValueKind == JsonValueKind.String)
                    hashes[path] = hash.GetString() ?? String.Empty;
                foreach (JsonProperty property in value.EnumerateObject())
                    CollectHistoricalHashes(property.Value, hashes);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray()) CollectHistoricalHashes(item, hashes);
            }
        }

        private static string ResolveDefaultFixture(string relative)
        {
            string normalized = relative.Replace('\\', '/');
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                string candidate = Path.Combine(current.FullName, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                current = current.Parent;
            }
            throw new FileNotFoundException("Frozen golden fixture was not found: " + relative);
        }

        private static string FindRoot(string start)
        {
            DirectoryInfo? current = new DirectoryInfo(start);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "tests", "v5-golden", "source-provenance-v1.json")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Repository root with frozen V5 golden provenance was not found.");
        }

        private static string FindRootOrAssemblyRoot(string start)
        {
            try { return FindRoot(start); }
            catch (DirectoryNotFoundException) { return FindRoot(AppContext.BaseDirectory); }
        }

        private static bool IsHex(string value) => value.All(Uri.IsHexDigit);

        private static string Sha256(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static byte[] NormalizeLf(byte[] bytes)
        {
            string text = new System.Text.UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n");
            return new System.Text.UTF8Encoding(false).GetBytes(text);
        }
    }
}

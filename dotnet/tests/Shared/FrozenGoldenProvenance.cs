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
        private const string PinsSha256 = "fda67b11efc3b00275d992b6b8c8b01f6ea926f75c8e155243fe73d5b66d10c9";

        public static void AssertFixture(string fixturePath)
        {
            string root = FindRoot();
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

            string actualFixtureHash = Sha256(File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
            if (!String.Equals(actualFixtureHash, fixture.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("Golden fixture bytes differ from their frozen pin: " + relative);

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

            using JsonDocument golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
            HashSet<string> observedPaths = new HashSet<string>(StringComparer.Ordinal);
            CollectHistoricalPaths(golden.RootElement, observedPaths);
            if (!observedPaths.SetEquals(expectedPaths))
                throw new InvalidDataException("Golden historical source paths differ from frozen pins: " + relative);
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

        private static string FindRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "tests", "v5-golden", "source-provenance-v1.json")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Repository root with frozen V5 golden provenance was not found.");
        }

        private static bool IsHex(string value) => value.All(Uri.IsHexDigit);

        private static string Sha256(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", String.Empty).ToLowerInvariant();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    public sealed class AssetMetadataTests
    {
        private static readonly string[] Scripts =
        {
            "PlayerTableViewState.cs",
            "PlayerTableProjectionMapper.cs",
            "TableInteractionState.cs",
            "TableCommandIntentFactory.cs",
            "ConfirmedAnimationQueue.cs"
        };

        [Fact]
        public void FiveTableScriptsHaveStableUniqueMetaGuids()
        {
            string root = FindRepositoryRoot();
            string tableDir = Path.Combine(root, "unity", "Mahjong.Unity", "Assets", "Scripts", "Table");
            var guids = new HashSet<string>(StringComparer.Ordinal);

            foreach (string script in Scripts)
            {
                string scriptPath = Path.Combine(tableDir, script);
                string metaPath = scriptPath + ".meta";
                Assert.True(File.Exists(scriptPath), "Missing script " + script);
                Assert.True(File.Exists(metaPath), "Missing meta " + metaPath);

                string text = File.ReadAllText(metaPath);
                Match match = Regex.Match(text, @"(?m)^guid:\s*([0-9a-f]{32})\s*$");
                Assert.True(match.Success, "Invalid or missing guid in " + metaPath);
                Assert.True(guids.Add(match.Groups[1].Value), "Duplicate guid in " + metaPath);
                Assert.Contains("fileFormatVersion: 2", text, StringComparison.Ordinal);
                Assert.Contains("MonoImporter:", text, StringComparison.Ordinal);
            }

            // Prove the five GUIDs are unique in the whole Unity asset tree, not
            // merely within this directory.
            string assets = Path.Combine(root, "unity", "Mahjong.Unity", "Assets");
            foreach (string meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
            {
                if (Array.IndexOf(Scripts, Path.GetFileName(meta).Replace(".meta", string.Empty)) >= 0 &&
                    string.Equals(Path.GetDirectoryName(meta), tableDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = Regex.Match(File.ReadAllText(meta), @"(?m)^guid:\s*([0-9a-f]{32})\s*$");
                if (match.Success)
                {
                    Assert.DoesNotContain(match.Groups[1].Value, guids);
                }
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "unity", "Mahjong.Unity")) &&
                    Directory.Exists(Path.Combine(current.FullName, "dotnet")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
        }
    }
}

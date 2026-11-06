using System;
using System.IO;
using UnityEngine;

namespace Mahjong.Unity.Platform
{
    public sealed class UnityAppPaths
    {
        public UnityAppPaths(string? persistentRoot = null)
        {
            PersistentRoot = string.IsNullOrEmpty(persistentRoot)
                ? Application.persistentDataPath
                : Path.GetFullPath(persistentRoot);

            OutboxDirectory = Path.Combine(PersistentRoot, "client", "outbox");
            LocalSaveDirectory = Path.Combine(PersistentRoot, "local");
            LogDirectory = Path.Combine(PersistentRoot, "logs");
        }

        public string PersistentRoot { get; }
        public string OutboxDirectory { get; }
        public string LocalSaveDirectory { get; }
        public string LogDirectory { get; }

        public void EnsureDirectoriesCreated()
        {
            EnsureUnderRoot(OutboxDirectory);
            EnsureUnderRoot(LocalSaveDirectory);
            EnsureUnderRoot(LogDirectory);

            Directory.CreateDirectory(OutboxDirectory);
            Directory.CreateDirectory(LocalSaveDirectory);
            Directory.CreateDirectory(LogDirectory);
        }

        public string ValidateAndNormalize(string relativeOrSubPath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrSubPath))
            {
                throw new ArgumentException("Path cannot be empty or whitespace.", nameof(relativeOrSubPath));
            }

            string combined = Path.IsPathRooted(relativeOrSubPath)
                ? relativeOrSubPath
                : Path.Combine(PersistentRoot, relativeOrSubPath);

            string full = Path.GetFullPath(combined);
            EnsureUnderRoot(full);
            return full;
        }

        private void EnsureUnderRoot(string path)
        {
            string rootWithSep = PersistentRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? PersistentRoot
                : PersistentRoot + Path.DirectorySeparatorChar;

            if (!path.StartsWith(rootWithSep, StringComparison.Ordinal) &&
                !string.Equals(path, PersistentRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Path '{path}' escapes persistent root '{PersistentRoot}'.");
            }
        }
    }
}

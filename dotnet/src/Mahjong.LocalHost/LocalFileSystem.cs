using System;
using System.IO;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Narrow file primitive seam. Production uses <see cref="SystemLocalFileSystem"/>; tests may
    /// install a faulting implementation to prove that a failure before the commit point never
    /// installs state and that a post-commit failure preserves the persisted facts.
    /// </summary>
    internal interface ILocalFileSystem
    {
        bool FileExists(string path);

        void CreateDirectory(string path);

        Stream CreateNew(string path);

        Stream OpenRead(string path);

        Stream OpenExclusive(string path);

        void FlushToDisk(Stream stream);

        void Move(string source, string destination);

        void Replace(string source, string destination, string backup);

        void Copy(string source, string destination, bool overwrite);

        void Delete(string path);

        byte[] ReadAllBytes(string path);
    }

    internal sealed class SystemLocalFileSystem : ILocalFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public Stream CreateNew(string path) =>
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);

        public Stream OpenRead(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

        public Stream OpenExclusive(string path) =>
            new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);

        public void FlushToDisk(Stream stream)
        {
            stream.Flush();
            if (stream is FileStream file)
            {
                file.Flush(true);
            }
        }

        public void Move(string source, string destination) => File.Move(source, destination);

        public void Replace(string source, string destination, string backup) =>
            File.Replace(source, destination, backup, true);

        public void Copy(string source, string destination, bool overwrite) =>
            File.Copy(source, destination, overwrite);

        public void Delete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    }

    /// <summary>Stable failure categories for the atomic writer.</summary>
    internal enum LocalWriteVerdict : byte
    {
        Committed = 0,
        Aborted = 1,
        OutcomeUnknown = 2
    }

    internal sealed class LocalWriteResult
    {
        private LocalWriteResult(LocalWriteVerdict verdict, string detail, bool backupNeedsRepair)
        {
            Verdict = verdict;
            Detail = detail ?? string.Empty;
            BackupNeedsRepair = backupNeedsRepair;
        }

        public LocalWriteVerdict Verdict { get; }

        public string Detail { get; }

        public bool BackupNeedsRepair { get; }

        public static LocalWriteResult Committed(bool backupNeedsRepair, string detail) =>
            new LocalWriteResult(LocalWriteVerdict.Committed, detail, backupNeedsRepair);

        public static LocalWriteResult Aborted(string detail) =>
            new LocalWriteResult(LocalWriteVerdict.Aborted, detail, false);

        public static LocalWriteResult Unknown(string detail) =>
            new LocalWriteResult(LocalWriteVerdict.OutcomeUnknown, detail, false);
    }
}

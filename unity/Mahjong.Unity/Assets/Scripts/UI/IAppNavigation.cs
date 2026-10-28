using System.Threading.Tasks;

namespace Mahjong.Unity.UI
{
    public enum LocalGameStartStatus
    {
        Started = 0,
        Busy = 1,
        Cancelled = 2,
        Failed = 3,
        /// <summary>
        /// Stable terminal state (quarantine, close fault or bounded shutdown
        /// timeout). The composition is not reusable in this process and the UI
        /// must not offer a retry.
        /// </summary>
        Terminal = 4
    }

    /// <summary>
    /// Result of one "start local game" attempt. Carries only a stable error code;
    /// never exception text, tickets, paths, seeds or other private data.
    /// </summary>
    public sealed class LocalGameStartResult
    {
        public const string LocalHostUnavailableCode = "LOCAL_HOST_UNAVAILABLE";
        public const string LocalHostStartFailedCode = "LOCAL_HOST_START_FAILED";
        public const string LocalHostTerminalCode = "LOCAL_HOST_TERMINAL";
        public const string TableSceneUnavailableCode = "TABLE_SCENE_UNAVAILABLE";
        public const string UnexpectedFailureCode = "LOCAL_GAME_START_FAILED";

        private static readonly LocalGameStartResult StartedResult = new LocalGameStartResult(LocalGameStartStatus.Started, null);
        private static readonly LocalGameStartResult BusyResult = new LocalGameStartResult(LocalGameStartStatus.Busy, null);
        private static readonly LocalGameStartResult CancelledResult = new LocalGameStartResult(LocalGameStartStatus.Cancelled, null);

        private LocalGameStartResult(LocalGameStartStatus status, string? stableErrorCode)
        {
            Status = status;
            StableErrorCode = stableErrorCode;
        }

        public LocalGameStartStatus Status { get; }

        public string? StableErrorCode { get; }

        /// <summary>True only for the stable terminal status.</summary>
        public bool IsTerminal => Status == LocalGameStartStatus.Terminal;

        public static LocalGameStartResult Started() => StartedResult;

        public static LocalGameStartResult Busy() => BusyResult;

        public static LocalGameStartResult Cancelled() => CancelledResult;

        public static LocalGameStartResult Failed(string stableErrorCode) =>
            new LocalGameStartResult(LocalGameStartStatus.Failed, stableErrorCode);

        public static LocalGameStartResult Terminal(string stableErrorCode) =>
            new LocalGameStartResult(LocalGameStartStatus.Terminal, stableErrorCode);
    }

    public interface IAppNavigation
    {
        void OpenLobby();
        void BackToMenu();
        void RequestQuit();

        /// <summary>
        /// Whether this navigation can start a real local table session at all.
        /// False while the production composition only wires the explicit
        /// unavailable factory; not a promise that any individual start succeeds.
        /// </summary>
        bool IsLocalGameStartAvailable { get; }

        /// <summary>
        /// Opens the local session and navigates to the Table scene, or returns a
        /// stable failure/cancel/busy result. Never throws to the UI caller.
        /// </summary>
        Task<LocalGameStartResult> StartLocalGameAsync();
    }
}

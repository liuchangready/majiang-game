using System;
using System.Collections.Generic;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    public sealed class GuestState
    {
        public string AccountId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string SessionToken { get; set; } = string.Empty;

        public string DeviceId { get; set; } = string.Empty;

        public string BootstrapSecret { get; set; } = string.Empty;

        public string? RoomId { get; set; }

        public string? AssignmentId { get; set; }

        public string Role { get; set; } = "PLAYER";

        public int Seat { get; set; } = -1;
    }

    public sealed class CrashClientEvidence
    {
        public int Pid { get; set; }

        public string BootId { get; set; } = string.Empty;

        public string ProcessStartedUtc { get; set; } = string.Empty;

        public string AccountId { get; set; } = string.Empty;

        public string AssignmentId { get; set; } = string.Empty;

        public int Seat { get; set; }

        public string RoomId { get; set; } = string.Empty;

        public string RequestIdHash { get; set; } = string.Empty;

        public string RequestId { get; set; } = string.Empty;

        public string ScopeIdHash { get; set; } = string.Empty;

        public long BaselineStateVersion { get; set; }

        public ulong ExpectedStateVersion { get; set; }

        public string OriginalReceiptStatus { get; set; } = string.Empty;

        public string ReplayReceiptStatus { get; set; } = string.Empty;

        public bool ReplayReplayed { get; set; }

        public ulong OriginalCommandVersion { get; set; }

        public ulong ReplayCommandVersion { get; set; }

        public uint DiscardTileId { get; set; }

        public string RoundId { get; set; } = string.Empty;

        public int CapturedCommandFrames { get; set; }

        public int DroppedReceiptCount { get; set; }

        public string OutboxDirectory { get; set; } = string.Empty;

        public string OutboxRawSha256 { get; set; } = string.Empty;

        public int PendingBeforeKill { get; set; }

        public bool HoldArmed { get; set; }
    }

    public sealed class RecoverClientEvidence
    {
        public int NewPid { get; set; }

        public string NewBootId { get; set; } = string.Empty;

        public string ProcessStartedUtc { get; set; } = string.Empty;

        public string AccountId { get; set; } = string.Empty;

        public string AssignmentId { get; set; } = string.Empty;

        public int BaselineCount { get; set; }

        public long BaselineStateVersion { get; set; }

        public bool BaselineBeforeQuery { get; set; }

        public int PendingBeforeQuery { get; set; }

        public bool DeliveryUnknownBeforeQuery { get; set; }

        public bool NeedsReconciliationBeforeQuery { get; set; }

        public int PendingAfterQuery { get; set; }

        public string ResolvedStatus { get; set; } = string.Empty;
        public string ResolvedReason { get; set; } = string.Empty;

        public bool ResolvedFromLookup { get; set; }

        public ulong ResolvedCommandVersion { get; set; }

        public string QueryDigestHash { get; set; } = string.Empty;

        public int OutboxEntriesAfter { get; set; }

        public string OutboxDirectory { get; set; } = string.Empty;

        public long RecoveredStateVersion { get; set; }

        public bool RecoveredCrashActionVisible { get; set; }

        public uint RecoveredPublicDiscardTileId { get; set; }

        public int RecoveredPublicDiscardSeat { get; set; }

        public int RecoveredPublicPlayerCount { get; set; }

        public string RecoveredSessionIdHash { get; set; } = string.Empty;

        public string RecoveredRole { get; set; } = string.Empty;

        public int RecoveredSeat { get; set; }
    }

    public sealed class SuiteEvidence
    {
        public string RoomId { get; set; } = string.Empty;

        public int PlayerCount { get; set; }

        public int SpectatorCount { get; set; }

        public int AdmittedPlayers { get; set; }

        public bool SpectatorAdmitted { get; set; }

        public bool BaselineFullForAll { get; set; }

        public bool PrivateStreamsIsolated { get; set; }

        public int NormalActionSeat { get; set; } = -1;

        public ulong NormalActionStateVersion { get; set; }

        public string NormalRequestId { get; set; } = string.Empty;

        public ulong NormalReplayCommandVersion { get; set; }

        public bool NormalReplaySameReceipt { get; set; }

        public int NormalReplayObservedReceipts { get; set; }

        public bool AllClientsSawNormalAction { get; set; }

        public int CrashPid { get; set; }

        public int CrashCommandVersion { get; set; }

        public string CrashRequestId { get; set; } = string.Empty;

        public bool RecoveredProjectionConfirmed { get; set; }

        public int RecoverPid { get; set; }

        public bool RecoveryFound { get; set; }

        public int RecoveryPendingAfter { get; set; }

        public bool AllClientsSawCrashAction { get; set; }

        public List<string> NegativeResults { get; set; } = new List<string>();

        public List<NegativeFact> NegativeFacts { get; set; } = new List<NegativeFact>();

        public bool HttpIdempotencyCreateReplay { get; set; }

        public bool HttpIdempotencyCreateConflict { get; set; }

        public bool HttpIdempotencyJoinReplay { get; set; }

        public bool HttpIdempotencyJoinConflict { get; set; }

        public bool HttpDifferentKindConflict { get; set; }

        public bool HttpTicketReplay { get; set; }

        public bool HttpTicketConflict { get; set; }

        public bool TlsHttpsUntrustedRootRejected { get; set; }

        public bool TlsHttpsWrongNameRejected { get; set; }

        public bool TlsHttpsExpiredRejected { get; set; }

        public bool TlsWssUntrustedRootRejected { get; set; }

        public bool TlsWssWrongNameRejected { get; set; }

        public bool TlsWssExpiredRejected { get; set; }

        public bool CrossAccountReceiptQueryRejected { get; set; }

        public bool ForeignRoomTicketRejected { get; set; }

        public bool ExpiredTicketRejected { get; set; }

        public bool TamperedTicketRejected { get; set; }

        public bool SpectatorPlayerCommandRejected { get; set; }

        public bool SameKeyDifferentBytesPreservedOriginal { get; set; }

        public bool ReceiptDigestDomainMismatch { get; set; }

        public string FirstReceiptCanonicalDigestHex { get; set; } = string.Empty;

        public string LookupCanonicalDigestHex { get; set; } = string.Empty;

        public bool FirstReceiptDigestVerified { get; set; }

        public bool LookupReceiptDigestAvailable { get; set; }

        public bool LookupReceiptDigestMatchesExpected { get; set; }

        public bool CanonicalReceiptIdentityVerified { get; set; }

        public bool Blocked { get; set; }

        public string BlockerCode { get; set; } = string.Empty;

        public string BlockerDetail { get; set; } = string.Empty;

        public bool ServerReceiptObserved { get; set; }

        public string ServerReceiptStatus { get; set; } = string.Empty;

        public ulong ServerReceiptCommandVersion { get; set; }

        public bool ServerReceiptScopePresent { get; set; }

        public int ClientPendingAfterServerReceipt { get; set; }

        public bool RawReceiptQueryFound { get; set; }

        public ulong RawReceiptQueryCommandVersion { get; set; }

        public string RawReceiptQueryDetail { get; set; } = string.Empty;

        public bool ImmediateReissueRejected { get; set; }

        public string ImmediateReissueDetail { get; set; } = string.Empty;

        public List<string> Assertions { get; set; } = new List<string>();
    }

    public sealed class NegativeFact
    {
        public string Label { get; set; } = string.Empty;

        public string RoomId { get; set; } = string.Empty;

        public string RequestId { get; set; } = string.Empty;

        public int ExpectedReceiptCount { get; set; }

        public string ExpectedStatus { get; set; } = string.Empty;

        public string ExpectedCanonicalDigestHex { get; set; } = string.Empty;

        public string AccountId { get; set; } = string.Empty;

        public string ScopeId { get; set; } = string.Empty;

        public string Api { get; set; } = string.Empty;

        public int ExpectedJournalCount { get; set; }
    }
}

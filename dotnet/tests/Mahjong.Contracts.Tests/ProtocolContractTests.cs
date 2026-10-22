using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.Protocol.Tests
{
    public sealed class ProtocolContractTests
    {
        [Fact]
        public void FrozenProtocolVersionsAreV1()
        {
            Assert.Equal(1u, ProtocolConstants.Major);
            Assert.Equal(0u, ProtocolConstants.Minor);
            Assert.Equal(1u, ProtocolConstants.RulesetVersion);
            Assert.Equal(1u, ProtocolConstants.ProjectionVersion);
            Assert.Equal(1u, ProtocolConstants.EventSchemaVersion);
            Assert.Equal(1u, ProtocolConstants.CommandPayloadSchemaVersion);
            Assert.Equal(ProtocolVersion.Current, new ProtocolVersion(1, 0));
            Assert.Equal("1.0", ProtocolVersion.Current.ToString());
        }

        [Fact]
        public void MajorMismatchIsRejectedWithoutDowngradeOrFallback()
        {
            var client = new VersionHandshake(new ProtocolVersion(2, 0), 1, 1, false, false);
            CompatibilityDecision decision = ProtocolCompatibility.EvaluateCurrent(client);

            Assert.False(decision.Allowed);
            Assert.Equal(CompatibilityStatus.MajorUnsupported, decision.Status);
            Assert.Equal(ProtocolErrorCode.ProtocolMajorUnsupported, decision.Code);
            Assert.Contains("not server major", decision.Detail);
        }

        [Fact]
        public void V4BusinessVersionAndMagicAreRejectedAsLegacy()
        {
            var v4 = new VersionHandshake(new ProtocolVersion(8, 0), 1, 1, false, true);
            CompatibilityDecision decision = ProtocolCompatibility.EvaluateCurrent(v4);

            Assert.False(decision.Allowed);
            Assert.Equal(CompatibilityStatus.LegacyV4Rejected, decision.Status);
            Assert.Equal(ProtocolErrorCode.LegacyV4Rejected, decision.Code);

            Assert.True(ProtocolCompatibility.HasLegacyV4Magic(new byte[] { (byte)'M', (byte)'J', (byte)'T', (byte)'C', 0 }));
            Assert.False(ProtocolCompatibility.HasLegacyV4Magic(new byte[] { 0, 0, 0, 0 }));
        }

        [Fact]
        public void HigherMinorIsAllowedForOptionalOnlyFeaturesAndRejectedWhenRequired()
        {
            var optional = new VersionHandshake(new ProtocolVersion(1, 1), 1, 1, false, false);
            CompatibilityDecision optionalDecision = ProtocolCompatibility.EvaluateCurrent(optional);
            Assert.True(optionalDecision.Allowed);
            Assert.Equal(CompatibilityStatus.Compatible, optionalDecision.Status);

            var required = new VersionHandshake(new ProtocolVersion(1, 1), 1, 1, true, false);
            CompatibilityDecision requiredDecision = ProtocolCompatibility.EvaluateCurrent(required);
            Assert.False(requiredDecision.Allowed);
            Assert.Equal(CompatibilityStatus.MinorUnsupported, requiredDecision.Status);
            Assert.Equal(ProtocolErrorCode.ProtocolMinorUnsupported, requiredDecision.Code);
        }

        [Fact]
        public void LowerMinorIsAllowed()
        {
            var client = new VersionHandshake(new ProtocolVersion(1, 0), 1, 1, false, false);
            CompatibilityDecision decision = ProtocolCompatibility.EvaluateCurrent(client);

            Assert.True(decision.Allowed);
        }

        [Fact]
        public void RulesetAndProjectionMismatchesHaveDistinctStableCodes()
        {
            var ruleset = new VersionHandshake(new ProtocolVersion(1, 0), 2, 1, false, false);
            var projection = new VersionHandshake(new ProtocolVersion(1, 0), 1, 2, false, false);

            CompatibilityDecision rulesetDecision = ProtocolCompatibility.EvaluateCurrent(ruleset);
            CompatibilityDecision projectionDecision = ProtocolCompatibility.EvaluateCurrent(projection);

            Assert.False(rulesetDecision.Allowed);
            Assert.Equal(ProtocolErrorCode.RulesetVersionUnsupported, rulesetDecision.Code);
            Assert.False(projectionDecision.Allowed);
            Assert.Equal(ProtocolErrorCode.ProjectionVersionUnsupported, projectionDecision.Code);
            Assert.NotEqual(rulesetDecision.Code, projectionDecision.Code);
        }

        [Fact]
        public void CompatibilityMatrixIsFrozenAndCoversAllTenRows()
        {
            IReadOnlyList<CompatibilityRule> matrix = ProtocolCompatibility.Matrix;

            Assert.Equal(10, matrix.Count);
            Assert.Contains(matrix, row => row.Code == ProtocolErrorCode.ProtocolMajorUnsupported);
            Assert.Contains(matrix, row => row.Code == ProtocolErrorCode.LegacyV4Rejected);
            Assert.Contains(matrix, row => row.Code == ProtocolErrorCode.ProtocolMinorUnsupported);
            Assert.Contains(matrix, row => row.Code == ProtocolErrorCode.StreamGap);
            Assert.Contains(matrix, row => row.Code == ProtocolErrorCode.MessageTooLarge);
        }

        [Fact]
        public void StreamVersionMayRepeatWhileProjectionSeqAdvances()
        {
            var current = new StreamCursor(new StreamVersion(7), new ProjectionSeq(11));
            var from = current;
            var incoming = new StreamCursor(new StreamVersion(7), new ProjectionSeq(12));

            Assert.Equal(CursorRelation.Refresh, StreamCursor.Classify(current, from, incoming, false));
        }

        [Fact]
        public void StreamVersionMayJumpWhenOtherSeatsHadPrivateCommands()
        {
            var current = new StreamCursor(new StreamVersion(5), new ProjectionSeq(10));
            var incoming = new StreamCursor(new StreamVersion(7), new ProjectionSeq(11));

            Assert.Equal(CursorRelation.Advance, StreamCursor.Classify(current, current, incoming, false));
        }

        [Fact]
        public void FromCursorMismatchIsAGapEvenWhenProjectionSeqLooksSequential()
        {
            var current = new StreamCursor(new StreamVersion(5), new ProjectionSeq(10));
            var wrongFrom = new StreamCursor(new StreamVersion(6), new ProjectionSeq(10));
            var incoming = new StreamCursor(new StreamVersion(7), new ProjectionSeq(11));

            Assert.Equal(CursorRelation.Gap, StreamCursor.Classify(current, wrongFrom, incoming, false));
        }

        [Fact]
        public void ProjectionSequenceJumpIsAGap()
        {
            var current = new StreamCursor(new StreamVersion(5), new ProjectionSeq(10));
            var incoming = new StreamCursor(new StreamVersion(6), new ProjectionSeq(12));

            Assert.Equal(CursorRelation.Gap, StreamCursor.Classify(current, current, incoming, false));
        }

        [Fact]
        public void OlderOrDuplicateProjectionIsStale()
        {
            var current = new StreamCursor(new StreamVersion(5), new ProjectionSeq(10));
            var duplicate = new StreamCursor(new StreamVersion(5), new ProjectionSeq(10));
            var older = new StreamCursor(new StreamVersion(4), new ProjectionSeq(9));

            Assert.Equal(CursorRelation.Stale, StreamCursor.Classify(current, current, duplicate, false));
            Assert.Equal(CursorRelation.Stale, StreamCursor.Classify(current, current, older, false));
        }

        [Fact]
        public void FullSnapshotAlwaysResetsTheCursor()
        {
            var current = new StreamCursor(new StreamVersion(5), new ProjectionSeq(10));
            var incoming = new StreamCursor(new StreamVersion(1), new ProjectionSeq(1));

            Assert.Equal(CursorRelation.Baseline, StreamCursor.Classify(current, default, incoming, true));
        }

        [Fact]
        public void RequestScopeBindsAccountRoleRoomAndLifetime()
        {
            var scope = new RequestScope(
                new RequestScopeId("scope-1"),
                "account-1",
                ProtocolRole.Player,
                new RoomId("room-1"),
                1000,
                2000);

            Assert.False(scope.IsExpiredAt(1999));
            Assert.True(scope.IsExpiredAt(2000));
            Assert.Equal(ProtocolRole.Player, scope.Role);
            Assert.Throws<ArgumentException>(() => new RequestScope(
                new RequestScopeId("scope-1"), "account-1", ProtocolRole.Unspecified, new RoomId("room-1"), 1, 2));
            Assert.Throws<ArgumentException>(() => new RequestScope(
                new RequestScopeId("scope-1"), "account-1", ProtocolRole.Player, new RoomId("room-1"), 2, 2));
        }

        [Fact]
        public void AppliedReceiptRequiresCommandVersionAndEventSeq()
        {
            Assert.Throws<ArgumentException>(() => new Receipt(
                ReceiptStatus.Applied, null, new StateVersion(3), null, new EventSeq(9), null, null));
            Assert.Throws<ArgumentException>(() => new Receipt(
                ReceiptStatus.Applied, null, new StateVersion(3), new StateVersion(4), null, null, null));
            Assert.Throws<ArgumentException>(() => new Receipt(
                ReceiptStatus.Applied, null, new StateVersion(3), new StateVersion(5), new EventSeq(9), null, null));

            Receipt receipt = Receipt.Applied(new StateVersion(3), new StateVersion(4), new EventSeq(9));
            Assert.True(receipt.IsDefinitive);
            Assert.Equal(new StateVersion(4), receipt.CommandVersion);
            Assert.Equal(new EventSeq(9), receipt.EventSeq);
        }

        [Fact]
        public void RejectedAndUnknownReceiptsDoNotCarryDefinitiveVersions()
        {
            Receipt rejected = Receipt.Rejected(new StateVersion(3), ProtocolErrorCode.WrongPhase);
            Assert.True(rejected.IsDefinitive);
            Assert.Null(rejected.CommandVersion);
            Assert.Null(rejected.EventSeq);

            Receipt unknown = Receipt.Unknown();
            Assert.False(unknown.IsDefinitive);
            Assert.Null(unknown.CommandVersion);
            Assert.Throws<InvalidOperationException>(() => unknown.AsReplay());
        }

        [Fact]
        public void IdempotentReplayKeepsOriginalFieldsAndOnlyChangesTheDeliveryFlag()
        {
            Receipt original = Receipt.Recorded(
                new StateVersion(7),
                new StateVersion(8),
                new EventSeq(103),
                committedAtMs: 1234,
                digest: CanonicalPayloadDigest.Compute(CommandPayloadFactory.SubmitAction(
                    "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 5 })));

            Receipt replay = original.AsReplay();

            Assert.True(replay.Replayed);
            Assert.False(original.Replayed);
            Assert.Equal(original.Status, replay.Status);
            Assert.Equal(original.EvaluatedStateVersion, replay.EvaluatedStateVersion);
            Assert.Equal(original.CommandVersion, replay.CommandVersion);
            Assert.Equal(original.EventSeq, replay.EventSeq);
            Assert.Equal(original.CommittedAtMs, replay.CommittedAtMs);
            Assert.Equal(original.CanonicalPayloadDigest, replay.CanonicalPayloadDigest);
        }

        [Fact]
        public void ReceiptLookupUnknownAndNotFoundNeverProveNonCommit()
        {
            ReceiptLookupResult unknown = ReceiptLookupResult.Unknown();
            ReceiptLookupResult notFound = ReceiptLookupResult.NotFound();

            Assert.False(unknown.ProvesNotCommitted);
            Assert.False(notFound.ProvesNotCommitted);
            Assert.True(unknown.RequiresReconciliation);
            Assert.True(notFound.RequiresReconciliation);
            Assert.False(unknown.IsDefinitive);
        }

        [Fact]
        public void IdempotencyDistinguishesNewReplayAndConflict()
        {
            PayloadDigest digest = CanonicalPayloadDigest.Compute(CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 5 }));
            PayloadDigest other = CanonicalPayloadDigest.Compute(CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 6 }));

            Assert.Equal(IdempotencyDecision.New, ReceiptIdempotency.Classify(null, digest));
            Assert.Equal(IdempotencyDecision.Replay, ReceiptIdempotency.Classify(digest, digest));
            Assert.Equal(IdempotencyDecision.Conflict, ReceiptIdempotency.Classify(digest, other));
        }

        [Fact]
        public void ErrorVocabularyIsStableWellFormedAndHasNoDuplicates()
        {
            Assert.Equal(ProtocolErrorCode.AllProtocolCodes.Count, ProtocolErrorCode.AllProtocolCodes.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ProtocolErrorCode.AllReasonCodes.Count, ProtocolErrorCode.AllReasonCodes.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ProtocolErrorCode.AllCloseCodes.Count, ProtocolErrorCode.AllCloseCodes.Distinct(StringComparer.Ordinal).Count());

            foreach (string code in ProtocolErrorCode.AllProtocolCodes
                .Concat(ProtocolErrorCode.AllReasonCodes)
                .Concat(ProtocolErrorCode.AllCloseCodes))
            {
                Assert.True(ProtocolErrorCode.IsWellFormed(code), code);
            }

            Assert.True(ProtocolErrorCode.IsKnownProtocolCode(ProtocolErrorCode.MessageTooLarge));
            Assert.True(ProtocolErrorCode.IsKnownReasonCode(ProtocolErrorCode.WrongPhase));
            Assert.True(ProtocolErrorCode.IsKnownCloseCode(ProtocolErrorCode.SlowConsumer));
            Assert.False(ProtocolErrorCode.IsKnownProtocolCode("NOT_A_REAL_CODE"));
            Assert.True(ProtocolErrorCode.IsWellFormed("NEW_UNKNOWN_CODE"));
        }

        [Fact]
        public void ApplicationCloseCodesMapToStableWebSocketCodes()
        {
            Assert.Equal(1000u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.LocalClosed));
            Assert.Equal(4400u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.ProtocolMajorUnsupported));
            Assert.Equal(4400u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.ProtocolViolation));
            Assert.Equal(4401u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.InvalidToken));
            Assert.Equal(4403u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.DisplacedByNewConnection));
            Assert.Equal(4409u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.RequestIdConflict));
            Assert.Equal(1009u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.MessageTooLarge));
            Assert.Equal(4429u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.SlowConsumer));
            Assert.Equal(4503u, ProtocolErrorCode.ToWebSocketCloseCode(ProtocolErrorCode.LeaseLost));
            Assert.Equal(1011u, ProtocolErrorCode.ToWebSocketCloseCode("UNMAPPED_NEW_CLOSE_CODE"));
            Assert.True(ProtocolErrorCode.IsPermanentClose(ProtocolErrorCode.ProtocolMajorUnsupported));
            Assert.False(ProtocolErrorCode.IsPermanentClose(ProtocolErrorCode.ServerBusy));
        }

        [Fact]
        public void EveryV4ApiErrorMapsToADistinctStableV5Code()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INVALID_PROTOCOL_VERSION"] = ProtocolErrorCode.ProtocolMajorUnsupported,
                ["MALFORMED_REQUEST"] = ProtocolErrorCode.MalformedMessage,
                ["ALREADY_BOUND"] = ProtocolErrorCode.AlreadyBound,
                ["ROOM_NOT_FOUND"] = ProtocolErrorCode.RoomNotFound,
                ["ROOM_FULL"] = ProtocolErrorCode.RoomFull,
                ["SERVER_FULL"] = ProtocolErrorCode.ServerFull,
                ["MISSING_ROOM_ID"] = ProtocolErrorCode.MalformedMessage,
                ["MISSING_ROOM_OR_TOKEN"] = ProtocolErrorCode.MalformedMessage,
                ["MISSING_ACTION_CHOICE"] = ProtocolErrorCode.MalformedMessage,
                ["MISSING_SHUFFLE_PLAN"] = ProtocolErrorCode.MalformedMessage,
                ["UNAUTHENTICATED_SESSION"] = ProtocolErrorCode.Unauthenticated,
                ["INVALID_TOKEN"] = ProtocolErrorCode.InvalidToken,
                ["UNKNOWN_REQUEST_TYPE"] = ProtocolErrorCode.UnknownMessageType,
                ["CONFLICT"] = ProtocolErrorCode.RequestIdConflict,
                ["SERVER_BUSY"] = ProtocolErrorCode.ServerBusy,
                ["INTERNAL_ERROR"] = ProtocolErrorCode.InternalError,
                ["REQUEST_SCOPE_EXPIRED"] = ProtocolErrorCode.RequestScopeExpired
            };

            Assert.Equal(17, map.Count);
            foreach (KeyValuePair<string, string> pair in map)
            {
                Assert.True(ProtocolErrorCode.TryMapLegacyV4Code(pair.Key, out string mapped), pair.Key);
                Assert.Equal(pair.Value, mapped);
            }

            Assert.False(ProtocolErrorCode.TryMapLegacyV4Code("SOMETHING_ELSE", out _));
        }

        [Fact]
        public void CanonicalEncodingRejectsDuplicateAndDescendingFields()
        {
            byte[] duplicate = TestCanonical.Craft((ushort)CommandKind.SubmitAction,
                (1, (byte)CanonicalValueType.String, TestCanonical.Utf8("r")),
                (1, (byte)CanonicalValueType.String, TestCanonical.Utf8("r")));
            byte[] descending = TestCanonical.Craft((ushort)CommandKind.SubmitAction,
                (2, (byte)CanonicalValueType.String, TestCanonical.Utf8("d")),
                (1, (byte)CanonicalValueType.String, TestCanonical.Utf8("r")));

            Assert.Throws<CanonicalEncodingException>(() => CanonicalPayloadParser.Parse(duplicate));
            Assert.Throws<CanonicalEncodingException>(() => CanonicalPayloadParser.Parse(descending));
        }

        [Fact]
        public void CanonicalEncodingRejectsTrailingBytesWrongMagicAndWrongVersion()
        {
            BusinessPayload valid = CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 5 });
            byte[] bytes = valid.CopyBytes();

            byte[] trailing = new byte[bytes.Length + 1];
            Array.Copy(bytes, trailing, bytes.Length);
            trailing[bytes.Length] = 0xFF;

            byte[] wrongMagic = (byte[])bytes.Clone();
            wrongMagic[0] = (byte)'X';

            byte[] wrongVersion = (byte[])bytes.Clone();
            wrongVersion[4] = 2;

            Assert.Throws<CanonicalEncodingException>(() => CanonicalPayloadParser.Parse(trailing));
            Assert.Throws<CanonicalEncodingException>(() => CanonicalPayloadParser.Parse(wrongMagic));
            Assert.Throws<CanonicalEncodingException>(() => CanonicalPayloadParser.Parse(wrongVersion));
        }

        [Fact]
        public void DigestIsLowercaseHexAndChangesWithBusinessBytes()
        {
            BusinessPayload first = CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 5 });
            BusinessPayload second = CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 6 });

            PayloadDigest digest = first.Digest;
            string hex = digest.ToHex();

            Assert.Equal(64, hex.Length);
            Assert.Equal(hex.ToLowerInvariant(), hex);
            Assert.Equal(digest, CanonicalPayloadDigest.Compute(first));
            Assert.NotEqual(digest, second.Digest);
            Assert.True(CanonicalPayloadDigest.Verify(first.AsSpan(), digest));
        }

        [Fact]
        public void RetainedBusinessBytesAreDefensivelyCopiedAndRetriesReuseThem()
        {
            byte[] original = CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 5 }).CopyBytes();
            BusinessPayload retained = BusinessPayload.Retain(original);
            byte[] before = retained.CopyBytes();

            original[0] = 0xFF;

            Assert.Equal(before, retained.CopyBytes());
            Assert.Equal(CanonicalPayloadDigest.Compute(before), retained.Digest);

            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                retained,
                new RequestId("request-1"),
                new RequestScopeId("scope-1"),
                new StateVersion(3));
            ProtocolCommand retry = command.Retry();

            Assert.Same(command.Payload, retry.Payload);
            Assert.Equal(command.CanonicalDigest, retry.CanonicalDigest);
            Assert.Equal(before, retry.Payload.CopyBytes());
        }

        [Fact]
        public void CommandValidatorRequiresKindSpecificFields()
        {
            BusinessPayload missingRound = new CanonicalPayloadWriter((ushort)CommandKind.SubmitAction)
                .WriteString(CommandPayloadFields.DecisionIdField, "decision-1")
                .WriteUInt64(CommandPayloadFields.ControlEpochField, 1)
                .WriteUInt32(CommandPayloadFields.ActionType, (uint)ProtocolActionType.DiscardTile)
                .Build();

            CommandValidationException ex = Assert.Throws<CommandValidationException>(() =>
                CommandPayloadValidator.Validate(CommandKind.SubmitAction, missingRound));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);

            BusinessPayload wrongKind = CommandPayloadFactory.SubmitSleeveBloom("round-1", "decision-1", 1);
            Assert.Throws<CommandValidationException>(() =>
                CommandPayloadValidator.Validate(CommandKind.SubmitBeamSwap, wrongKind));
        }

        [Fact]
        public void PublicProjectionGuardRejectsPrivateAndUnknownEvents()
        {
            ProtocolEventRecord publicDiscard = ProtocolEventRecord.Known(
                ProtocolEventType.TileDiscarded,
                ProtocolEventVisibility.Public,
                1,
                new EventSeq(1),
                new StateVersion(1),
                0,
                new byte[] { 1, 2, 3 });
            ProtocolEventRecord privateDraw = ProtocolEventRecord.Known(
                ProtocolEventType.TileDrawn,
                ProtocolEventVisibility.Private,
                1,
                new EventSeq(2),
                new StateVersion(2),
                0,
                new byte[] { 4, 5, 6 });
            ProtocolEventRecord unknown = ProtocolEventRecord.UnknownEvent(
                new UnknownEventRecord(999, 1, new byte[] { 7 }));

            PublicProjectionGuard.ValidateEvent(publicDiscard);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, Assert.Throws<PublicProjectionException>(
                () => PublicProjectionGuard.ValidateEvent(privateDraw)).Code);
            Assert.Equal(ProtocolErrorCode.StreamGap, Assert.Throws<PublicProjectionException>(
                () => PublicProjectionGuard.ValidateEvent(unknown)).Code);
            Assert.False(unknown.IsDomainFact);
            Assert.True(unknown.Unknown!.RequiresFullBaseline);
        }

        [Fact]
        public void PublicSnapshotCannotBeConstructedWithPrivateEvents()
        {
            var privateEvent = ProtocolEventRecord.Known(
                ProtocolEventType.CheatPointsChanged,
                ProtocolEventVisibility.Public,
                1,
                new EventSeq(9),
                new StateVersion(9),
                0,
                new byte[] { 1 });

            var session = new PublicSessionSummary(false, string.Empty, Array.Empty<ScoreView>(), Array.Empty<int>());

            PublicProjectionException ex = Assert.Throws<PublicProjectionException>(() => new PublicMatchView(
                null,
                session,
                Array.Empty<ClockView>(),
                false,
                string.Empty,
                new[] { privateEvent }));
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, ex.Code);
        }

        [Fact]
        public void PlayerSnapshotPrivatePartMustBelongToTheSameSeat()
        {
            var session = new PublicSessionSummary(false, string.Empty, Array.Empty<ScoreView>(), Array.Empty<int>());
            var publicView = new PublicMatchView(null, session, Array.Empty<ClockView>(), false, string.Empty, Array.Empty<ProtocolEventRecord>());
            var privateView = new PrivatePlayerView(1, new uint[] { 1, 2 }, null, false, Array.Empty<uint>(), null, null);

            Assert.Throws<ArgumentException>(() => new PlayerSnapshotView(
                new SessionId("session-1"),
                null,
                0,
                StreamCursor.Empty,
                new StateVersion(0),
                new EventSeq(0),
                new SessionVersion(0),
                new RoundVersion(0),
                publicView,
                privateView));
        }
    }

    internal static class TestCanonical
    {
        public static byte[] Utf8(string value)
        {
            return System.Text.Encoding.UTF8.GetBytes(value);
        }

        public static byte[] Craft(ushort commandKind, params (ushort Field, byte Type, byte[] Raw)[] fields)
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(new byte[] { (byte)'M', (byte)'J', (byte)'C', (byte)'1' }, 0, 4);
                WriteUInt16(stream, 1);
                WriteUInt16(stream, commandKind);
                for (int i = 0; i < fields.Length; i++)
                {
                    WriteUInt16(stream, fields[i].Field);
                    stream.WriteByte(fields[i].Type);
                    WriteUInt32(stream, (uint)fields[i].Raw.Length);
                    stream.Write(fields[i].Raw, 0, fields[i].Raw.Length);
                }

                return stream.ToArray();
            }
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}

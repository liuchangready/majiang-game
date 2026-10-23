using System;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ContractRole = Mahjong.Contracts.ProtocolRole;
using ContractScope = Mahjong.Contracts.RequestScope;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WireRole = Mahjong.Contracts.Wire.Role;
using WireScope = Mahjong.Contracts.Wire.RequestScope;

namespace Mahjong.Client
{
    /// <summary>
    /// Narrow mapping between the typed contract entities and the frozen wire
    /// envelope. No business payload is ever rebuilt here.
    /// </summary>
    internal static class ClientWireMapper
    {
        public static Envelope NewEnvelope(MessageType messageType, string? correlationId)
        {
            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = messageType
            };

            if (!string.IsNullOrEmpty(correlationId))
            {
                envelope.CorrelationId = correlationId!;
            }

            return envelope;
        }

        public static Envelope NewHello(
            string ticket,
            string clientBuild,
            bool resumeRequested,
            string? resumeSessionId,
            ulong bindingEpoch,
            string correlationId)
        {
            Envelope envelope = NewEnvelope(MessageType.Hello, correlationId);
            envelope.BindingEpoch = bindingEpoch;
            envelope.Hello = new Hello
            {
                Ticket = ticket ?? string.Empty,
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                ClientBuild = clientBuild ?? string.Empty,
                ResumeRequested = resumeRequested
            };

            if (resumeRequested && !string.IsNullOrEmpty(resumeSessionId))
            {
                envelope.Hello.ResumeSessionId = resumeSessionId!;
            }

            return envelope;
        }

        public static ContractScope ToContract(WireScope scope)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (string.IsNullOrEmpty(scope.ScopeId))
            {
                throw new ArgumentException("Scope id is missing.", nameof(scope));
            }

            if (string.IsNullOrEmpty(scope.AccountId))
            {
                throw new ArgumentException("Scope account id is missing.", nameof(scope));
            }

            if (string.IsNullOrEmpty(scope.RoomId))
            {
                throw new ArgumentException("Scope room id is missing.", nameof(scope));
            }

            return new ContractScope(
                new RequestScopeId(scope.ScopeId),
                scope.AccountId,
                ToContractRole(scope.Role),
                new RoomId(scope.RoomId),
                scope.IssuedAtMs,
                scope.ExpiresAtMs);
        }

        public static WireScope ToWire(ContractScope scope)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            return new WireScope
            {
                ScopeId = scope.ScopeId.Value,
                AccountId = scope.AccountId,
                Role = ToWireRole(scope.Role),
                RoomId = scope.RoomId.Value,
                IssuedAtMs = scope.IssuedAtMs,
                ExpiresAtMs = scope.ExpiresAtMs
            };
        }

        public static ContractRole ToContractRole(WireRole role)
        {
            switch (role)
            {
                case WireRole.Player:
                    return ContractRole.Player;
                case WireRole.Spectator:
                    return ContractRole.Spectator;
                case WireRole.Lobby:
                    return ContractRole.Lobby;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Role is unspecified or unknown.");
            }
        }

        public static WireRole ToWireRole(ContractRole role)
        {
            switch (role)
            {
                case ContractRole.Player:
                    return WireRole.Player;
                case ContractRole.Spectator:
                    return WireRole.Spectator;
                case ContractRole.Lobby:
                    return WireRole.Lobby;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Role is unspecified or unknown.");
            }
        }

        public static ContractCursor ToContract(WireCursor cursor)
        {
            if (cursor == null)
            {
                throw new ArgumentNullException(nameof(cursor));
            }

            return new ContractCursor(
                new StreamVersion(cursor.StreamVersion),
                new ProjectionSeq(cursor.ProjectionSeq));
        }

        public static WireCursor ToWire(ContractCursor cursor)
        {
            return new WireCursor
            {
                StreamVersion = cursor.StreamVersion.Value,
                ProjectionSeq = cursor.ProjectionSeq.Value
            };
        }

        public static SyncTarget ToSyncTarget(ContractRole role)
        {
            switch (role)
            {
                case ContractRole.Player:
                    return SyncTarget.PlayerStream;
                case ContractRole.Spectator:
                    return SyncTarget.PublicStream;
                case ContractRole.Lobby:
                    return SyncTarget.Room;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Role is unspecified or unknown.");
            }
        }

        public static ReceiptLookupStatus ToContractLookupStatus(LookupStatus status)
        {
            switch (status)
            {
                case LookupStatus.Found:
                    return ReceiptLookupStatus.Found;
                case LookupStatus.Unknown:
                    return ReceiptLookupStatus.Unknown;
                case LookupStatus.NotFound:
                    return ReceiptLookupStatus.NotFound;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Lookup status is unspecified or unknown.");
            }
        }
    }
}

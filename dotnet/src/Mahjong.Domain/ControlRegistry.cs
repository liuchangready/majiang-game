using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    public enum SeatControllerKind : byte
    {
        LocalHuman = 0,
        RemoteHuman = 1,
        Computer = 2,
        Takeover = 3
    }

    public enum ConnectionStatus : byte
    {
        Connected = 0,
        Disconnected = 1
    }

    public enum DisconnectMode : byte
    {
        ImmediateAi = 0,
        GraceThenAi = 1,
        TimeoutOnly = 2
    }

    public sealed class DisconnectPolicy
    {
        public DisconnectPolicy(
            DisconnectMode mode = DisconnectMode.GraceThenAi,
            long graceMillis = 10000L,
            string? aiProfileId = null)
        {
            if (graceMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(graceMillis), graceMillis, "Grace must be non-negative.");
            }

            Mode = mode;
            GraceMillis = graceMillis;
            AiProfileId = aiProfileId;
        }

        public DisconnectMode Mode { get; }

        public long GraceMillis { get; }

        public string? AiProfileId { get; }
    }

    public sealed class SeatControlState
    {
        internal SeatControlState(
            int seat,
            ConnectionStatus status,
            SeatControllerKind kind,
            long controlEpoch,
            long? disconnectedAtMillis,
            long? graceDeadlineMillis,
            string? aiProfileId)
        {
            Seat = seat;
            Status = status;
            Kind = kind;
            ControlEpoch = controlEpoch;
            DisconnectedAtMillis = disconnectedAtMillis;
            GraceDeadlineMillis = graceDeadlineMillis;
            AiProfileId = aiProfileId;
        }

        public int Seat { get; }

        public ConnectionStatus Status { get; }

        public SeatControllerKind Kind { get; }

        public long ControlEpoch { get; }

        public long? DisconnectedAtMillis { get; }

        public long? GraceDeadlineMillis { get; }

        public string? AiProfileId { get; }

        public bool IsConnected => Status == ConnectionStatus.Connected;

        public bool IsDisconnected => Status == ConnectionStatus.Disconnected;

        public bool IsHuman => Kind == SeatControllerKind.LocalHuman || Kind == SeatControllerKind.RemoteHuman;

        public bool IsAutomated => Kind == SeatControllerKind.Computer || Kind == SeatControllerKind.Takeover;

        public bool IsTakenOver => Kind == SeatControllerKind.Takeover;

        public override string ToString() =>
            "seat" + Seat + "/" + Status + "/" + Kind + "/epoch" + ControlEpoch;
    }

    public enum ControlValidationCode : byte
    {
        None = 0,
        InvalidSeat = 1,
        EpochMismatch = 2,
        NotAutomated = 3,
        NotHuman = 4
    }

    public sealed class ControlValidation
    {
        private ControlValidation(bool isValid, ControlValidationCode code, string message)
        {
            IsValid = isValid;
            Code = code;
            Message = message;
        }

        public bool IsValid { get; }

        public ControlValidationCode Code { get; }

        public string Message { get; }

        internal static ControlValidation Ok() => new ControlValidation(true, ControlValidationCode.None, string.Empty);

        internal static ControlValidation Fail(ControlValidationCode code, string message) =>
            new ControlValidation(false, code, message);
    }

    /// <summary>
    /// Seat controller + connection control authority (V4 <c>ControllerRegistry</c> merged with
    /// the domain-visible half of server <c>ConnectionControl</c>).
    ///
    /// Rules preserved:
    /// - every controller change/reinitialization increments that seat's controlEpoch;
    /// - a reconnect never reclaims automatically; only an explicit ReclaimControl does, and
    ///   that increments the epoch so in-flight takeover decisions become stale;
    /// - grace expiry is evaluated against an injected monotonic time and an optional expected
    ///   epoch, so a late timer cannot take over a newer controller;
    /// - this class only validates epochs; it never mutates round state.
    /// </summary>
    public sealed class ControlRegistry
    {
        private readonly SeatControlState[] _states = new SeatControlState[4];
        private readonly SeatControllerKind[] _preferredHumanKind = new SeatControllerKind[4];

        public ControlRegistry(
            DisconnectPolicy? policy = null,
            IReadOnlyDictionary<int, SeatControllerKind>? initialKinds = null,
            int seatCount = 4)
        {
            if (seatCount != 4)
            {
                throw new ArgumentOutOfRangeException(nameof(seatCount), seatCount, "V5 supports exactly four seats.");
            }

            Policy = policy ?? new DisconnectPolicy();
            for (int seat = 0; seat < 4; seat++)
            {
                SeatControllerKind kind = seat == 0 ? SeatControllerKind.LocalHuman : SeatControllerKind.Computer;
                if (initialKinds != null && initialKinds.TryGetValue(seat, out SeatControllerKind configured))
                {
                    kind = configured;
                }

                _preferredHumanKind[seat] = kind == SeatControllerKind.RemoteHuman
                    ? SeatControllerKind.RemoteHuman
                    : SeatControllerKind.LocalHuman;
                _states[seat] = new SeatControlState(
                    seat,
                    ConnectionStatus.Connected,
                    kind,
                    0L,
                    null,
                    null,
                    null);
            }
        }

        public DisconnectPolicy Policy { get; }

        public SeatControlState GetState(int seat)
        {
            CheckSeat(seat);
            return _states[seat];
        }

        public IReadOnlyList<SeatControlState> AllStates()
        {
            var copy = new SeatControlState[4];
            Array.Copy(_states, copy, 4);
            return new ReadOnlyCollection<SeatControlState>(copy);
        }

        public SeatControllerKind GetController(int seat)
        {
            return GetState(seat).Kind;
        }

        public long GetEpoch(int seat)
        {
            return GetState(seat).ControlEpoch;
        }

        /// <summary>V4 ControllerRegistry semantics: every call increments the seat epoch.</summary>
        public SeatControlState SetController(int seat, SeatControllerKind kind)
        {
            CheckSeat(seat);
            SeatControlState current = _states[seat];
            if (kind == SeatControllerKind.LocalHuman || kind == SeatControllerKind.RemoteHuman)
            {
                _preferredHumanKind[seat] = kind;
            }

            _states[seat] = Copy(
                current,
                current.Status,
                kind,
                checked(current.ControlEpoch + 1L),
                current.DisconnectedAtMillis,
                current.GraceDeadlineMillis,
                current.AiProfileId);
            return _states[seat];
        }

        public IReadOnlyList<SeatControlState> Initialize(IReadOnlyList<SeatControllerKind> kinds)
        {
            if (kinds == null)
            {
                throw new ArgumentNullException(nameof(kinds));
            }

            if (kinds.Count != 4)
            {
                throw new ArgumentException("Controllers list must contain exactly four entries.", nameof(kinds));
            }

            for (int seat = 0; seat < 4; seat++)
            {
                SeatControlState current = _states[seat];
                if (kinds[seat] == SeatControllerKind.LocalHuman || kinds[seat] == SeatControllerKind.RemoteHuman)
                {
                    _preferredHumanKind[seat] = kinds[seat];
                }

                _states[seat] = Copy(
                    current,
                    current.Status,
                    kinds[seat],
                    checked(current.ControlEpoch + 1L),
                    current.DisconnectedAtMillis,
                    current.GraceDeadlineMillis,
                    current.AiProfileId);
            }

            return AllStates();
        }

        public bool IsHuman(int seat) => GetState(seat).IsHuman;

        public bool IsAutomated(int seat) => GetState(seat).IsAutomated;

        public SeatControlState Disconnect(int seat, long atMillis)
        {
            CheckSeat(seat);
            CheckTime(atMillis);
            SeatControlState current = _states[seat];
            if (current.IsDisconnected)
            {
                return current;
            }

            SeatControlState updated;
            switch (Policy.Mode)
            {
                case DisconnectMode.ImmediateAi:
                    updated = Copy(
                        current,
                        ConnectionStatus.Disconnected,
                        SeatControllerKind.Takeover,
                        checked(current.ControlEpoch + 1L),
                        atMillis,
                        null,
                        Policy.AiProfileId);
                    break;
                case DisconnectMode.GraceThenAi:
                    if (current.IsHuman)
                    {
                        updated = Copy(
                            current,
                            ConnectionStatus.Disconnected,
                            current.Kind,
                            current.ControlEpoch,
                            atMillis,
                            checked(atMillis + Policy.GraceMillis),
                            Policy.AiProfileId);
                    }
                    else
                    {
                        updated = Copy(
                            current,
                            ConnectionStatus.Disconnected,
                            current.Kind,
                            current.ControlEpoch,
                            atMillis,
                            null,
                            current.AiProfileId ?? Policy.AiProfileId);
                    }

                    break;
                case DisconnectMode.TimeoutOnly:
                    updated = Copy(
                        current,
                        ConnectionStatus.Disconnected,
                        current.Kind,
                        current.ControlEpoch,
                        atMillis,
                        null,
                        null);
                    break;
                default:
                    throw new InvalidOperationException("Unknown disconnect mode " + Policy.Mode + ".");
            }

            _states[seat] = updated;
            return updated;
        }

        public SeatControlState Reconnect(int seat, long atMillis, bool reclaim = false)
        {
            CheckSeat(seat);
            CheckTime(atMillis);
            SeatControlState current = _states[seat];
            bool shouldReclaim = reclaim && !current.IsHuman;
            if (current.IsConnected && !shouldReclaim)
            {
                return current;
            }

            SeatControllerKind kind = shouldReclaim ? _preferredHumanKind[seat] : current.Kind;
            long epoch = shouldReclaim ? checked(current.ControlEpoch + 1L) : current.ControlEpoch;
            SeatControlState updated = Copy(
                current,
                ConnectionStatus.Connected,
                kind,
                epoch,
                null,
                null,
                shouldReclaim ? null : current.AiProfileId);
            _states[seat] = updated;
            return updated;
        }

        /// <summary>Explicit human reclaim. Reconnecting alone never reclaims control.</summary>
        public SeatControlState ReclaimControl(int seat)
        {
            CheckSeat(seat);
            SeatControlState current = _states[seat];
            if (current.IsHuman)
            {
                return current;
            }

            SeatControlState updated = Copy(
                current,
                current.Status,
                _preferredHumanKind[seat],
                checked(current.ControlEpoch + 1L),
                current.DisconnectedAtMillis,
                current.GraceDeadlineMillis,
                null);
            _states[seat] = updated;
            return updated;
        }

        public SeatControlState RequestTakeover(int seat)
        {
            CheckSeat(seat);
            SeatControlState current = _states[seat];
            if (current.IsTakenOver)
            {
                return current;
            }

            SeatControlState updated = Copy(
                current,
                current.Status,
                SeatControllerKind.Takeover,
                checked(current.ControlEpoch + 1L),
                current.DisconnectedAtMillis,
                current.GraceDeadlineMillis,
                Policy.AiProfileId);
            _states[seat] = updated;
            return updated;
        }

        public bool IsTakeoverNeeded(int seat, long nowMillis, long? expectedEpoch = null)
        {
            CheckSeat(seat);
            CheckTime(nowMillis);
            SeatControlState current = _states[seat];
            if (expectedEpoch.HasValue && current.ControlEpoch != expectedEpoch.Value)
            {
                return false;
            }

            return current.IsDisconnected &&
                   current.IsHuman &&
                   current.GraceDeadlineMillis.HasValue &&
                   nowMillis >= current.GraceDeadlineMillis.Value;
        }

        /// <summary>
        /// Applies a due grace takeover. A stale expectedEpoch is a zero-side-effect false,
        /// which is the guard against a late timer fired after an explicit reclaim.
        /// </summary>
        public bool CheckGraceExpiration(int seat, long nowMillis, long? expectedEpoch = null)
        {
            CheckSeat(seat);
            CheckTime(nowMillis);
            SeatControlState current = _states[seat];
            if (expectedEpoch.HasValue && current.ControlEpoch != expectedEpoch.Value)
            {
                return false;
            }

            if (!current.IsDisconnected ||
                !current.IsHuman ||
                !current.GraceDeadlineMillis.HasValue ||
                nowMillis < current.GraceDeadlineMillis.Value)
            {
                return false;
            }

            _states[seat] = Copy(
                current,
                current.Status,
                SeatControllerKind.Takeover,
                checked(current.ControlEpoch + 1L),
                current.DisconnectedAtMillis,
                null,
                Policy.AiProfileId);
            return true;
        }

        public IReadOnlyList<int> PollPendingTakeovers(long nowMillis)
        {
            CheckTime(nowMillis);
            var seats = new List<int>();
            for (int seat = 0; seat < 4; seat++)
            {
                if (CheckGraceExpiration(seat, nowMillis))
                {
                    seats.Add(seat);
                }
            }

            return new ReadOnlyCollection<int>(seats);
        }

        /// <summary>Read-only epoch check used before any player/AI decision is applied.</summary>
        public ControlValidation ValidateEpoch(int seat, long expectedEpoch)
        {
            if (seat < 0 || seat > 3)
            {
                return ControlValidation.Fail(ControlValidationCode.InvalidSeat, "Seat must be 0..3.");
            }

            SeatControlState current = _states[seat];
            if (current.ControlEpoch != expectedEpoch)
            {
                return ControlValidation.Fail(
                    ControlValidationCode.EpochMismatch,
                    "Seat " + seat + " controlEpoch is " + current.ControlEpoch + ", got " + expectedEpoch + ".");
            }

            return ControlValidation.Ok();
        }

        /// <summary>AI/takeover submission guard: epoch must match and the seat must be automated.</summary>
        public ControlValidation ValidateAutomatedAction(int seat, long expectedEpoch)
        {
            ControlValidation epoch = ValidateEpoch(seat, expectedEpoch);
            if (!epoch.IsValid)
            {
                return epoch;
            }

            if (!_states[seat].IsAutomated)
            {
                return ControlValidation.Fail(
                    ControlValidationCode.NotAutomated,
                    "Seat " + seat + " is not automated.");
            }

            return ControlValidation.Ok();
        }

        public ControlValidation ValidateHumanAction(int seat, long expectedEpoch)
        {
            ControlValidation epoch = ValidateEpoch(seat, expectedEpoch);
            if (!epoch.IsValid)
            {
                return epoch;
            }

            if (!_states[seat].IsHuman)
            {
                return ControlValidation.Fail(
                    ControlValidationCode.NotHuman,
                    "Seat " + seat + " is not human-controlled.");
            }

            return ControlValidation.Ok();
        }

        private static SeatControlState Copy(
            SeatControlState source,
            ConnectionStatus status,
            SeatControllerKind kind,
            long epoch,
            long? disconnectedAt,
            long? graceDeadline,
            string? aiProfileId)
        {
            return new SeatControlState(
                source.Seat,
                status,
                kind,
                epoch,
                disconnectedAt,
                graceDeadline,
                aiProfileId);
        }

        private static void CheckSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }
        }

        private static void CheckTime(long millis)
        {
            if (millis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(millis), millis, "Time must be non-negative.");
            }
        }
    }
}

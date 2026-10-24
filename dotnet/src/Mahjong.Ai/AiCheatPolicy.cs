using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mahjong.Ai
{
    /// <summary>
    /// AI cheat-technique policy and budget configuration (ENG-019 port of V4
    /// <c>engine/src/main/kotlin/com/majiang/ai/AiCheatPolicy.kt</c>).
    ///
    /// This type is pure configuration: it describes what an AI seat would be allowed to
    /// do. It never spends points, never reads authoritative state, never touches the
    /// ledger and cannot enable anything by itself — the room authority keeps the final
    /// say by intersecting this policy with its own (V4 <c>TableAuthority</c> calls
    /// <c>config.aiCheatPolicy.restrictWith(strategyPolicy)</c>). A caller-side policy
    /// therefore can never decide a charge or bypass the master switch.
    ///
    /// Default is disabled, which keeps existing AI behaviour unchanged: no technique is
    /// ever offered while <see cref="Enabled"/> is false, no matter how the capability
    /// toggles are set. A takeover seat additionally requires an explicit
    /// <see cref="TakeoverMaySpend"/>; the V4 <c>ShuffleConfig.aiMaySpend</c> legacy
    /// projection (<c>aiMaySpend &amp;&amp; !enabled</c>, legacy budget 60) lives in the
    /// table runtime wiring, not here.
    ///
    /// The type is immutable and defensively copies every dictionary it is given or
    /// returns. V4 kept the toggles in a <c>LinkedHashMap</c> that shared references with
    /// the caller of <c>copy()</c> until it explicitly copied the map
    /// (<c>TableAuthority</c> has to do exactly that by hand); here the copy happens once
    /// in the constructor and no writable dictionary ever escapes, so the safe copy the
    /// V4 call sites had to remember is now structural.
    ///
    /// Semantics are ported from the V4 source behaviour, not its comments:
    /// validation rejects negative budgets/reserve, NaN or out-of-range
    /// <see cref="RiskTolerance"/>, candidate limits outside 1..64 and blank capability
    /// ids; unknown capability ids default to enabled; <see cref="RestrictWith"/>
    /// intersects (any side forbidding forbids, stricter budget/candidate/risk wins,
    /// higher reserve wins, takeover requires both) and unions the toggle keys so unknown
    /// stable ids survive; ordinal, case-sensitive id comparison matches Kotlin
    /// <c>String</c> map keys; equality is the Kotlin data-class equality, including the
    /// bitwise <c>Double.equals</c> comparison that distinguishes <c>-0.0</c> from
    /// <c>0.0</c>.
    /// </summary>
    public sealed class AiCheatPolicy : IEquatable<AiCheatPolicy>
    {
        private readonly string[] _keys;
        private readonly Dictionary<string, bool> _values;

        /// <summary>
        /// Creates a policy. Every parameter defaults to the V4 value, so the parameterless
        /// policy is exactly V4's <c>AiCheatPolicy()</c>: disabled, 60 points per round,
        /// no reserve, risk tolerance 0.5, up to 6 candidates per capability, takeover
        /// never spends and no per-capability override.
        /// </summary>
        public AiCheatPolicy(
            bool enabled = false,
            int maxSpendPerRound = 60,
            int reservePoints = 0,
            double riskTolerance = 0.5,
            int candidateLimit = 6,
            bool takeoverMaySpend = false,
            IReadOnlyDictionary<string, bool>? capabilityToggles = null)
        {
            // Validation order mirrors the V4 init block: the first violated rule wins.
            if (maxSpendPerRound < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxSpendPerRound),
                    maxSpendPerRound,
                    "maxSpendPerRound must be non-negative.");
            }

            if (reservePoints < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reservePoints),
                    reservePoints,
                    "reservePoints must be non-negative.");
            }

            // NaN fails both comparisons below, so it must be rejected explicitly — the V4
            // range check `riskTolerance in 0.0..1.0` is false for NaN for the same reason.
            if (double.IsNaN(riskTolerance) || riskTolerance < 0.0 || riskTolerance > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(riskTolerance),
                    riskTolerance,
                    "riskTolerance must be in 0.0..1.0.");
            }

            if (candidateLimit < 1 || candidateLimit > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(candidateLimit),
                    candidateLimit,
                    "candidateLimit must be in 1..64.");
            }

            if (capabilityToggles == null)
            {
                _keys = Array.Empty<string>();
                _values = new Dictionary<string, bool>(StringComparer.Ordinal);
            }
            else
            {
                // Defensive copy in the caller's iteration order (V4 copies into a
                // LinkedHashMap the same way). Add() fails loudly on a duplicate key,
                // which a well-formed map cannot contain.
                var keys = new List<string>(capabilityToggles.Count);
                var values = new Dictionary<string, bool>(capabilityToggles.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, bool> entry in capabilityToggles)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key))
                    {
                        throw new ArgumentException(
                            "capability toggle id cannot be blank.",
                            nameof(capabilityToggles));
                    }

                    keys.Add(entry.Key);
                    values.Add(entry.Key, entry.Value);
                }

                _keys = keys.ToArray();
                _values = values;
            }

            Enabled = enabled;
            MaxSpendPerRound = maxSpendPerRound;
            ReservePoints = reservePoints;
            RiskTolerance = riskTolerance;
            CandidateLimit = candidateLimit;
            TakeoverMaySpend = takeoverMaySpend;
            CapabilityToggles = new OrderedToggles(_keys, _values);
        }

        /// <summary>Room/policy level master switch. False disables every capability.</summary>
        public bool Enabled { get; }

        /// <summary>Maximum cheat points a seat may spend per round.</summary>
        public int MaxSpendPerRound { get; }

        /// <summary>Points that must stay unspent.</summary>
        public int ReservePoints { get; }

        /// <summary>
        /// Risk tolerance in [0, 1]; the higher the value the more exposure risk the AI
        /// accepts. Strategy configuration only — this number never reaches the domain
        /// authority hash, a command intent's canonical bytes or a persisted charge.
        /// </summary>
        public double RiskTolerance { get; }

        /// <summary>Per-capability candidate cap, 1..64.</summary>
        public int CandidateLimit { get; }

        /// <summary>
        /// Whether a takeover seat may actively spend points. Defaults to false: takeover
        /// must opt in explicitly, and the room policy must allow it too.
        /// </summary>
        public bool TakeoverMaySpend { get; }

        /// <summary>
        /// Per-capability toggles by stable capability id; absent ids mean enabled.
        /// The view is read-only, iterates in insertion order (the V4 LinkedHashMap
        /// behaviour) and is not affected by later mutations of any source dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, bool> CapabilityToggles { get; }

        /// <summary>
        /// The single capability toggle, ignoring the master switch. Unknown ids (and
        /// therefore a blank id, which cannot be a key) default to enabled — V4 has no
        /// validation on this path.
        /// </summary>
        public bool IsCapabilityToggled(string capabilityId)
        {
            if (capabilityId == null)
            {
                throw new ArgumentNullException(nameof(capabilityId));
            }

            return _values.TryGetValue(capabilityId, out bool allowed) ? allowed : true;
        }

        /// <summary>Master switch and single toggle must both allow the capability.</summary>
        public bool Allows(string capabilityId)
        {
            return Enabled && IsCapabilityToggled(capabilityId);
        }

        /// <summary>
        /// Returns a copy with <paramref name="capabilityId"/> set to
        /// <paramref name="allowed"/>. The original instance and its dictionary are never
        /// modified; an existing key keeps its position and a new key is appended, exactly
        /// like V4's <c>LinkedHashMap</c> copy. Blank ids are rejected.
        /// </summary>
        public AiCheatPolicy WithCapability(string capabilityId, bool allowed)
        {
            // Kotlin separates null (intrinsic null check → NPE) from blank
            // (require → IllegalArgumentException); keep both distinct here.
            if (capabilityId == null)
            {
                throw new ArgumentNullException(nameof(capabilityId));
            }

            AiGuard.RequireText(capabilityId, nameof(capabilityId));

            var keys = new List<string>(_keys.Length + 1);
            var values = new Dictionary<string, bool>(_keys.Length + 1, StringComparer.Ordinal);
            bool replaced = false;
            for (int i = 0; i < _keys.Length; i++)
            {
                string existing = _keys[i];
                if (existing == capabilityId)
                {
                    keys.Add(existing);
                    values.Add(existing, allowed);
                    replaced = true;
                }
                else
                {
                    keys.Add(existing);
                    values.Add(existing, _values[existing]);
                }
            }

            if (!replaced)
            {
                keys.Add(capabilityId);
                values.Add(capabilityId, allowed);
            }

            return new AiCheatPolicy(
                enabled: Enabled,
                maxSpendPerRound: MaxSpendPerRound,
                reservePoints: ReservePoints,
                riskTolerance: RiskTolerance,
                candidateLimit: CandidateLimit,
                takeoverMaySpend: TakeoverMaySpend,
                capabilityToggles: values);
        }

        /// <summary>
        /// Intersects this policy with a strategy-level policy: any side forbidding a
        /// capability forbids it, budget and candidate limit take the stricter (smaller)
        /// value, the reserved points take the higher value, risk tolerance takes the
        /// smaller value and takeover requires both sides. A null <paramref name="other"/>
        /// means "no extra constraint" and returns this instance unchanged (the same
        /// reference, like V4). The toggle map is the union of both key sets — unknown
        /// stable ids survive — where each side's missing keys count as enabled.
        /// </summary>
        public AiCheatPolicy RestrictWith(AiCheatPolicy? other)
        {
            if (other == null)
            {
                return this;
            }

            var values = new Dictionary<string, bool>(_keys.Length + other._keys.Length, StringComparer.Ordinal);
            var keys = new List<string>(_keys.Length + other._keys.Length);
            for (int i = 0; i < _keys.Length; i++)
            {
                AddUnionToggle(keys, values, _keys[i], other);
            }

            for (int i = 0; i < other._keys.Length; i++)
            {
                AddUnionToggle(keys, values, other._keys[i], other);
            }

            return new AiCheatPolicy(
                enabled: Enabled && other.Enabled,
                maxSpendPerRound: MinOf(MaxSpendPerRound, other.MaxSpendPerRound),
                reservePoints: MaxOf(ReservePoints, other.ReservePoints),
                riskTolerance: MinOf(RiskTolerance, other.RiskTolerance),
                candidateLimit: MinOf(CandidateLimit, other.CandidateLimit),
                takeoverMaySpend: TakeoverMaySpend && other.TakeoverMaySpend,
                capabilityToggles: values);
        }

        private void AddUnionToggle(
            List<string> keys,
            Dictionary<string, bool> values,
            string key,
            AiCheatPolicy other)
        {
            if (values.ContainsKey(key))
            {
                return;
            }

            keys.Add(key);
            values.Add(key, IsCapabilityToggled(key) && other.IsCapabilityToggled(key));
        }

        /// <summary>
        /// Kotlin <c>minOf(a, b)</c> returns the left operand on ties (including the
        /// ±0.0 pair, which compares equal); <see cref="Math.Min(double, double)"/> does
        /// not guarantee that, so the tie rule is spelled out.
        /// </summary>
        private static double MinOf(double left, double right)
        {
            return left <= right ? left : right;
        }

        private static int MinOf(int left, int right)
        {
            return left <= right ? left : right;
        }

        private static int MaxOf(int left, int right)
        {
            return left >= right ? left : right;
        }

        /// <summary>
        /// Kotlin data-class equality. The risk tolerance is compared on its bits like
        /// Java's <c>Double.equals</c> (so -0.0 and 0.0 differ), while the toggle map
        /// compares by content regardless of iteration order (Kotlin <c>Map.equals</c>),
        /// with ordinal, case-sensitive ids.
        /// </summary>
        public bool Equals(AiCheatPolicy? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null)
            {
                return false;
            }

            return Enabled == other.Enabled &&
                   MaxSpendPerRound == other.MaxSpendPerRound &&
                   ReservePoints == other.ReservePoints &&
                   BitConverter.DoubleToInt64Bits(RiskTolerance) ==
                   BitConverter.DoubleToInt64Bits(other.RiskTolerance) &&
                   CandidateLimit == other.CandidateLimit &&
                   TakeoverMaySpend == other.TakeoverMaySpend &&
                   TogglesEqual(other._values);
        }

        public override bool Equals(object? obj)
        {
            return obj is AiCheatPolicy other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Enabled);
            hash.Add(MaxSpendPerRound);
            hash.Add(ReservePoints);
            hash.Add(BitConverter.DoubleToInt64Bits(RiskTolerance));
            hash.Add(CandidateLimit);
            hash.Add(TakeoverMaySpend);
            int toggles = 0;
            for (int i = 0; i < _keys.Length; i++)
            {
                string key = _keys[i];
                toggles ^= (key.GetHashCode(StringComparison.Ordinal) * 397) ^ (_values[key] ? 1 : 0);
            }

            hash.Add(toggles);
            return hash.ToHashCode();
        }

        public static bool operator ==(AiCheatPolicy? left, AiCheatPolicy? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        public static bool operator !=(AiCheatPolicy? left, AiCheatPolicy? right)
        {
            return !(left == right);
        }

        private bool TogglesEqual(Dictionary<string, bool> otherValues)
        {
            if (_values.Count != otherValues.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, bool> entry in _values)
            {
                if (!otherValues.TryGetValue(entry.Key, out bool value) || value != entry.Value)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Kotlin data-class <c>toString</c> shape (lowercase booleans, invariant risk
        /// formatting, toggles in insertion order). Purely diagnostic.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiCheatPolicy(enabled=");
            builder.Append(Enabled ? "true" : "false");
            builder.Append(", maxSpendPerRound=").Append(MaxSpendPerRound);
            builder.Append(", reservePoints=").Append(ReservePoints);
            builder.Append(", riskTolerance=").Append(RiskTolerance.ToString(CultureInfo.InvariantCulture));
            builder.Append(", candidateLimit=").Append(CandidateLimit);
            builder.Append(", takeoverMaySpend=").Append(TakeoverMaySpend ? "true" : "false");
            builder.Append(", capabilityToggles={");
            for (int i = 0; i < _keys.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(_keys[i]).Append('=').Append(_values[_keys[i]] ? "true" : "false");
            }

            builder.Append("})");
            return builder.ToString();
        }

        /// <summary>
        /// Read-only insertion-ordered view over the constructor's defensive copy. It is
        /// not a <c>Dictionary</c>, so no caller can cast its way back to a writable map.
        /// </summary>
        private sealed class OrderedToggles : IReadOnlyDictionary<string, bool>
        {
            private readonly string[] _keys;
            private readonly Dictionary<string, bool> _values;

            internal OrderedToggles(string[] keys, Dictionary<string, bool> values)
            {
                _keys = keys;
                _values = values;
            }

            public int Count => _keys.Length;

            public IEnumerable<string> Keys
            {
                get
                {
                    foreach (string key in _keys)
                    {
                        yield return key;
                    }
                }
            }

            public IEnumerable<bool> Values
            {
                get
                {
                    foreach (string key in _keys)
                    {
                        yield return _values[key];
                    }
                }
            }

            public bool this[string key] => _values[key];

            public bool ContainsKey(string key)
            {
                return _values.ContainsKey(key);
            }

            public bool TryGetValue(string key, out bool value)
            {
                return _values.TryGetValue(key, out value);
            }

            public IEnumerator<KeyValuePair<string, bool>> GetEnumerator()
            {
                foreach (string key in _keys)
                {
                    yield return new KeyValuePair<string, bool>(key, _values[key]);
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}

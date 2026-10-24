using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mahjong.Ai
{
    /// <summary>
    /// ENG-017 port of the pure candidate value models of V4
    /// <c>engine/src/main/kotlin/com/majiang/ai/AiDecisionModels.kt</c> (lines 21..202).
    ///
    /// Seven value types, all immutable with defensive copies at every boundary, so an
    /// instance can be frozen and read from any thread without synchronization — the same
    /// guarantee V4 relies on when a snapshot crosses into the asynchronous AI lane:
    /// <see cref="AiPhase"/>, <see cref="AiRisk"/>, <see cref="AiEffect"/>,
    /// <see cref="AiVisibleParams"/>, <see cref="AiCandidate"/>,
    /// <see cref="AiDecisionStamp"/> and <see cref="AiCandidateLimits"/>.
    ///
    /// Deliberately out of scope here (ENG-018 factory/registry wiring and later ports):
    /// V4's <c>AiCheatView</c>, <c>AiSeatSnapshot</c>, <c>AiDecisionContext</c>,
    /// <c>AiDecisionStrategy</c>, <c>AiCandidateFactory</c>, <c>AiCandidateScorer</c>
    /// and the cheat capability registry. No PlayerView, GameState or authority
    /// projection is referenced; the Double values here are heuristic ordering
    /// metadata only and never enter an authority hash, a canonical command byte
    /// sequence or a persisted charge fact (those remain SHA-256 over
    /// <c>CommandIntent</c> identity).
    ///
    /// Every rule below is ported from observed V4 behaviour, verified by running the
    /// real compiled V4 classes (engine build at D:/majiang/engine/build/classes/kotlin/
    /// main, sources byte-identical between worktree baseline 7844194 and remote head
    /// 3c2e7f5) through a reflection probe; the probe log and its SHA-256 are recorded
    /// in doc/v5-重构/V5-P16-AI-CANDIDATE-MODELS.md. Kotlin semantics that differ from
    /// .NET are implemented explicitly in <see cref="KotlinSemantics"/>: the Kotlin
    /// blank/whitespace set, Kotlin <c>String.trim()</c>, Kotlin
    /// <c>String.toIntOrNull()</c> (which accepts Unicode decimal digits such as
    /// fullwidth ４ and Arabic-Indic ٤ but never trims surrounding whitespace) and the
    /// Java-parity string/double hashes that make the Kotlin data-class equality
    /// contracts reproducible instead of process-random.
    ///
    /// Exception conventions follow the accepted AiCheatPolicy port: Kotlin intrinsic
    /// parameter null checks throw <see cref="ArgumentNullException"/>, Kotlin
    /// <c>require(...)</c> on text throws <see cref="ArgumentException"/>, Kotlin
    /// <c>require(...)</c> on numeric ranges throws
    /// <see cref="ArgumentOutOfRangeException"/>, and validation order mirrors the V4
    /// init blocks so the first violated rule wins.
    /// </summary>

    /// <summary>
    /// AI decision window phase. V4 name mapping (ordinals verified by probe):
    /// <c>Idle</c>=IDLE(0), <c>ShufflePreparation</c>=SHUFFLE_PREPARATION(1),
    /// <c>OrdinaryAction</c>=ORDINARY_ACTION(2), <c>OfferChoice</c>=OFFER_CHOICE(3).
    ///
    /// The phase is part of the decision stamp: results computed for one window never
    /// apply to another even for the same seat and decision id.
    /// </summary>
    public enum AiPhase
    {
        /// <summary>V4 <c>IDLE</c>: no decision window is open.</summary>
        Idle = 0,

        /// <summary>V4 <c>SHUFFLE_PREPARATION</c>: off-turn stack-preparation window.</summary>
        ShufflePreparation = 1,

        /// <summary>V4 <c>ORDINARY_ACTION</c>: the seat's normal action window.</summary>
        OrdinaryAction = 2,

        /// <summary>V4 <c>OFFER_CHOICE</c>: a cheat-technique offer selection window.</summary>
        OfferChoice = 3,
    }

    /// <summary>
    /// Heuristic risk estimate of a candidate (ENG-017 port of V4
    /// <c>AiRisk</c>). Ordering metadata only: never authoritative, never persisted as a
    /// fact, never hashed into authority identity.
    ///
    /// Kotlin data-class semantics: equality and the hash are bitwise on the doubles
    /// (Java <c>Double.equals</c>), so -0.0 and 0.0 are distinguishable values even
    /// though they compare equal numerically (probe: <c>AiRisk(0.0,0.0)</c> does not
    /// equal <c>AiRisk(-0.0,-0.0)</c>, and <c>penalty</c> of the latter is -0.0 with
    /// sign bit 0x8000000000000000). NaN and infinities are rejected at construction.
    /// </summary>
    public sealed class AiRisk : IEquatable<AiRisk?>
    {
        /// <summary>Probability-style exposure risk in [0, 1].</summary>
        public double Exposure { get; }

        /// <summary>Probability-style suspicion risk in [0, 1].</summary>
        public double Suspicion { get; }

        /// <summary>
        /// Unified risk penalty for strategy ordering: <c>exposure * 0.5 + suspicion *
        /// 0.5</c>. All-zero inputs yield -0.0 exactly like the JVM (probe raw bits
        /// 0x8000000000000000); the sign is irrelevant to ordering comparisons.
        /// </summary>
        public double Penalty => Exposure * 0.5 + Suspicion * 0.5;

        /// <summary>Creates a risk estimate; defaults mirror V4 <c>AiRisk()</c>.</summary>
        public AiRisk(double exposure = 0.0, double suspicion = 0.0)
        {
            // Validation order mirrors the V4 init block: exposure first.
            if (double.IsNaN(exposure) || exposure < 0.0 || exposure > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(exposure),
                    exposure,
                    "exposure must be in 0.0..1.0.");
            }

            if (double.IsNaN(suspicion) || suspicion < 0.0 || suspicion > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(suspicion),
                    suspicion,
                    "suspicion must be in 0.0..1.0.");
            }

            Exposure = exposure;
            Suspicion = suspicion;
        }

        /// <inheritdoc/>
        public bool Equals(AiRisk? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null &&
                   BitConverter.DoubleToInt64Bits(Exposure) ==
                   BitConverter.DoubleToInt64Bits(other.Exposure) &&
                   BitConverter.DoubleToInt64Bits(Suspicion) ==
                   BitConverter.DoubleToInt64Bits(other.Suspicion);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is AiRisk other && Equals(other);
        }

        /// <summary>
        /// Java-parity hash of the Kotlin data class: deterministic across processes
        /// (unlike <see cref="object.GetHashCode"/>), identical to the JVM value.
        /// Diagnostic/ordering use only — never an authority digest.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int result = KotlinSemantics.JavaDoubleHash(Exposure);
                result = 31 * result + KotlinSemantics.JavaDoubleHash(Suspicion);
                return result;
            }
        }

        /// <summary>Bitwise data-class equality; <see cref="AiRisk"/>(0.0) ≠ (−0.0).</summary>
        public static bool operator ==(AiRisk? left, AiRisk? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        /// <summary>Bitwise data-class inequality.</summary>
        public static bool operator !=(AiRisk? left, AiRisk? right)
        {
            return !(left == right);
        }

        /// <summary>Kotlin data-class <c>toString</c> shape; diagnostic only.</summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiRisk(exposure=");
            builder.Append(Exposure.ToString(CultureInfo.InvariantCulture));
            builder.Append(", suspicion=").Append(Suspicion.ToString(CultureInfo.InvariantCulture));
            builder.Append(')');
            return builder.ToString();
        }
    }

    /// <summary>
    /// Visible-effect estimate of a candidate (ENG-017 port of V4 <c>AiEffect</c>).
    /// Derived from the seat's own visible information only; never a promise of the real
    /// outcome and never an authoritative fact.
    ///
    /// The integer fields are unvalidated in V4 (probe: Int.MinValue/MaxValue/-1 all
    /// accepted); only <paramref name="note"/> is bounded to 120 UTF-16 code units —
    /// 60 surrogate pairs pass, 61 fail (probe). Kotlin data-class equality over all
    /// four fields.
    /// </summary>
    public sealed class AiEffect : IEquatable<AiEffect?>
    {
        private const int MaxNoteLength = 120;

        /// <summary>Estimated hand value.</summary>
        public int HandValue { get; }

        /// <summary>Estimated shanten improvement.</summary>
        public int ShantenImprovement { get; }

        /// <summary>Estimated retained value.</summary>
        public int RetainedValue { get; }

        /// <summary>Diagnostic note, at most 120 UTF-16 code units.</summary>
        public string Note { get; }

        /// <summary>Creates an effect estimate; defaults mirror V4 <c>AiEffect()</c>.</summary>
        public AiEffect(int handValue = 0, int shantenImprovement = 0, int retainedValue = 0, string note = "")
        {
            if (note is null)
            {
                throw new ArgumentNullException(nameof(note));
            }

            if (note.Length > MaxNoteLength)
            {
                throw new ArgumentException(
                    "effect note must be <= 120 chars.",
                    nameof(note));
            }

            HandValue = handValue;
            ShantenImprovement = shantenImprovement;
            RetainedValue = retainedValue;
            Note = note;
        }

        /// <inheritdoc/>
        public bool Equals(AiEffect? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null &&
                   HandValue == other.HandValue &&
                   ShantenImprovement == other.ShantenImprovement &&
                   RetainedValue == other.RetainedValue &&
                   Note == other.Note;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is AiEffect other && Equals(other);
        }

        /// <summary>
        /// Java-parity hash of the Kotlin data class; deterministic across processes.
        /// Diagnostic/ordering use only — never an authority digest.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int result = HandValue;
                result = 31 * result + ShantenImprovement;
                result = 31 * result + RetainedValue;
                result = 31 * result + KotlinSemantics.JavaStringHash(Note);
                return result;
            }
        }

        /// <summary>Data-class equality over all four fields (case-sensitive note).</summary>
        public static bool operator ==(AiEffect? left, AiEffect? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        /// <summary>Data-class inequality.</summary>
        public static bool operator !=(AiEffect? left, AiEffect? right)
        {
            return !(left == right);
        }

        /// <summary>Kotlin data-class <c>toString</c> shape; diagnostic only.</summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiEffect(handValue=");
            builder.Append(HandValue);
            builder.Append(", shantenImprovement=").Append(ShantenImprovement);
            builder.Append(", retainedValue=").Append(RetainedValue);
            builder.Append(", note=").Append(Note);
            builder.Append(')');
            return builder.ToString();
        }
    }

    /// <summary>
    /// String key/value parameters of a candidate (ENG-017 port of V4
    /// <c>AiVisibleParams</c>). Contains no hidden tiles and no mutable domain objects;
    /// capability adapters parse the strings themselves (ENG-018).
    ///
    /// V4 semantics, all probe-verified against the real engine classes:
    /// <list type="bullet">
    /// <item>the stored map is a defensive copy of an insertion-ordered map; later
    /// mutations of any source are invisible and the view cannot be cast back to a
    /// writable dictionary;</item>
    /// <item><c>Of</c> rejects Kotlin-blank keys (empty, ASCII space, NBSP U+00A0 and
    /// 0x001C..0x001F are blank; U+0085 is not) and applies duplicate keys last-value-
    /// wins while keeping the first insertion position;</item>
    /// <item><c>From</c> performs no validation at all — blank keys are legal through
    /// this path in V4 and remain legal here;</item>
    /// <item>map-content equality is iteration-order independent and case-sensitive;
    /// the hash is the Java map hash (sum of key^value), deterministic across
    /// processes (probe: <c>{a=1, b=" two"}</c> hashes to 1068702 in the JVM);</item>
    /// <item><c>Int</c> is Kotlin <c>toIntOrNull()</c>: optional ASCII sign, then
    /// decimal digits per UTF-16 unit — fullwidth ４２ and Arabic-Indic ٤٢ parse,
    /// whitespace is never trimmed, overflow returns null, a bare sign returns
    /// null;</item>
    /// <item><c>IntList</c> splits on the raw comma, Kotlin-trims each element, parses
    /// with the same rule and silently drops unparsable elements ("1, x ,,99999999999"
    /// keeps only what parsed);</item>
    /// <item><c>StringSet</c> splits on the raw comma, Kotlin-trims each element,
    /// drops empties and dedupes preserving first-occurrence order (NBSP U+00A0 and
    /// vertical tab are Kotlin-trimmable; U+0085 is not).</item>
    /// </list>
    /// </summary>
    public sealed class AiVisibleParams : IEquatable<AiVisibleParams?>
    {
        private readonly string[] _keys;
        private readonly Dictionary<string, string> _values;

        private AiVisibleParams()
        {
            _keys = Array.Empty<string>();
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            Values = new OrderedEntries(_keys, _values);
        }

        private AiVisibleParams(string[] keys, Dictionary<string, string> values)
        {
            _keys = keys;
            _values = values;
            Values = new OrderedEntries(_keys, _values);
        }

        /// <summary>V4 <c>AiVisibleParams.EMPTY</c>: the shared empty instance.</summary>
        public static AiVisibleParams Empty { get; } = new AiVisibleParams();

        /// <summary>
        /// V4 <c>of(vararg Pair)</c>. Keys must not be Kotlin-blank; duplicate keys keep
        /// their first position and take the last value. Values may be any string,
        /// including empty (probe: an empty value is stored verbatim).
        /// </summary>
        public static AiVisibleParams Of(params (string Key, string Value)[] entries)
        {
            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var keys = new List<string>(entries.Length);
            var values = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
            foreach ((string key, string value) in entries)
            {
                if (key is null)
                {
                    throw new ArgumentNullException(nameof(entries), "parameter key cannot be null.");
                }

                if (value is null)
                {
                    // V4's typed surface cannot express a null value (Pair<String, String>);
                    // only untyped JVM paths could inject one. Tightened deliberately.
                    throw new ArgumentNullException(nameof(entries), "parameter value cannot be null.");
                }

                if (KotlinSemantics.IsKotlinBlank(key))
                {
                    throw new ArgumentException("parameter key cannot be blank.", nameof(entries));
                }

                if (values.ContainsKey(key))
                {
                    values[key] = value;
                }
                else
                {
                    keys.Add(key);
                    values.Add(key, value);
                }
            }

            return new AiVisibleParams(keys.ToArray(), values);
        }

        /// <summary>
        /// V4 <c>from(map)</c>: no validation, defensive copy preserving the source's
        /// enumeration order. An unordered source (for example a Dictionary after
        /// removals) keeps whatever order it enumerates in — exactly like passing a
        /// HashMap in V4.
        /// </summary>
        public static AiVisibleParams From(IReadOnlyDictionary<string, string> map)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            var keys = new List<string>(map.Count);
            var values = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in map)
            {
                if (entry.Key is null)
                {
                    throw new ArgumentNullException(nameof(map), "parameter key cannot be null.");
                }

                if (entry.Value is null)
                {
                    // Same deliberate tightening as Of: V4's typed surface cannot
                    // produce a null value here either.
                    throw new ArgumentNullException(nameof(map), "parameter value cannot be null.");
                }

                // A null key cannot be stored in an Ordinal dictionary; V4's
                // LinkedHashMap would accept one, but its typed readers can never
                // query it back (the key parameter is intrinsically non-null).
                keys.Add(entry.Key);
                values.Add(entry.Key, entry.Value);
            }

            return new AiVisibleParams(keys.ToArray(), values);
        }

        /// <summary>
        /// Read-only, insertion-ordered view over the stored pairs; iterates in the
        /// order keys were first inserted and is unaffected by later mutations of any
        /// source dictionary (probe).
        /// </summary>
        public IReadOnlyDictionary<string, string> Values { get; }

        /// <summary>V4 <c>string(key)</c>: the raw value, or null when absent.</summary>
        public string? String(string key)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _values.TryGetValue(key, out string? value) ? value : null;
        }

        /// <summary>V4 <c>int(key)</c>: Kotlin <c>toIntOrNull()</c> of the raw value.</summary>
        public int? Int(string key)
        {
            string? raw = String(key);
            return raw is null ? (int?)null : KotlinSemantics.KotlinToIntOrNull(raw);
        }

        /// <summary>V4 <c>intList(key)</c>: comma split, Kotlin-trim, parse, drop failures.</summary>
        public IReadOnlyList<int> IntList(string key)
        {
            string? raw = String(key);
            if (raw is null)
            {
                return Array.Empty<int>();
            }

            string[] elements = raw.Split(',');
            var result = new List<int>(elements.Length);
            foreach (string element in elements)
            {
                int? parsed = KotlinSemantics.KotlinToIntOrNull(KotlinSemantics.KotlinTrim(element));
                if (parsed.HasValue)
                {
                    result.Add(parsed.Value);
                }
            }

            return result;
        }

        /// <summary>
        /// V4 <c>stringSet(key)</c>: comma split, Kotlin-trim, drop empties, dedupe
        /// preserving first-occurrence order. V4 returns a LinkedHashSet; netstandard2.1
        /// has no IReadOnlySet, so the same iteration order is exposed as a read-only
        /// list whose elements are guaranteed distinct.
        /// </summary>
        public IReadOnlyList<string> StringSet(string key)
        {
            string? raw = String(key);
            if (raw is null)
            {
                return Array.Empty<string>();
            }

            string[] elements = raw.Split(',');
            var seen = new Dictionary<string, bool>(elements.Length, StringComparer.Ordinal);
            var result = new List<string>(elements.Length);
            foreach (string element in elements)
            {
                string trimmed = KotlinSemantics.KotlinTrim(element);
                if (trimmed.Length == 0 || seen.ContainsKey(trimmed))
                {
                    continue;
                }

                seen.Add(trimmed, true);
                result.Add(trimmed);
            }

            return result;
        }

        /// <inheritdoc/>
        public bool Equals(AiVisibleParams? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null || _values.Count != other._values.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> entry in _values)
            {
                if (!other._values.TryGetValue(entry.Key, out string? value) || value != entry.Value)
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is AiVisibleParams other && Equals(other);
        }

        /// <summary>
        /// Java map hash (sum of key ^ value over all entries, Java string hashes);
        /// order-independent and deterministic across processes. Diagnostic use only —
        /// never an authority digest.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 0;
                foreach (string key in _keys)
                {
                    hash += KotlinSemantics.JavaStringHash(key) ^
                            KotlinSemantics.JavaStringHash(_values[key]);
                }

                return hash;
            }
        }

        /// <summary>Map-content equality; iteration order is irrelevant.</summary>
        public static bool operator ==(AiVisibleParams? left, AiVisibleParams? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        /// <summary>Map-content inequality.</summary>
        public static bool operator !=(AiVisibleParams? left, AiVisibleParams? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// V4 <c>"AiVisibleParams($values)"</c> shape with insertion order; entries are
        /// formatted like Kotlin's Map.toString. Diagnostic only.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiVisibleParams({");
            for (int i = 0; i < _keys.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(_keys[i]).Append('=').Append(_values[_keys[i]]);
            }

            builder.Append("})");
            return builder.ToString();
        }

        /// <summary>
        /// Read-only insertion-ordered view over the constructor's defensive copy; not a
        /// Dictionary, so no caller can cast its way back to a writable map (the
        /// OrderedToggles precedent from AiCheatPolicy).
        /// </summary>
        private sealed class OrderedEntries : IReadOnlyDictionary<string, string>
        {
            private readonly string[] _keys;
            private readonly Dictionary<string, string> _values;

            internal OrderedEntries(string[] keys, Dictionary<string, string> values)
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

            public IEnumerable<string> Values
            {
                get
                {
                    foreach (string key in _keys)
                    {
                        yield return _values[key];
                    }
                }
            }

            public string this[string key] => _values[key];

            public bool ContainsKey(string key)
            {
                return _values.ContainsKey(key);
            }

            public bool TryGetValue(string key, out string value)
            {
                return _values.TryGetValue(key, out value!);
            }

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                foreach (string key in _keys)
                {
                    yield return new KeyValuePair<string, string>(key, _values[key]);
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }

    /// <summary>
    /// Unified AI candidate (ENG-017 port of V4 <c>AiCandidate</c>). Both ordinary
    /// mahjong actions and cheat techniques share this contract; submission re-locates
    /// <see cref="OptionId"/> on a fresh authoritative projection, so nothing here is
    /// executed directly.
    ///
    /// Kotlin data-class semantics: equality covers all nine fields, with bitwise
    /// double comparison inside <see cref="Risk"/> (probe: -0.0 exposure distinguishes
    /// two otherwise-equal candidates) and order-independent map comparison inside
    /// <see cref="Parameters"/>. Validation mirrors the V4 init block order:
    /// optionId, capabilityId (Kotlin not-blank) then cost &gt;= 0.
    /// </summary>
    public sealed class AiCandidate : IEquatable<AiCandidate?>
    {
        /// <summary>Unique option id within the current decision.</summary>
        public string OptionId { get; }

        /// <summary><c>"mahjong"</c> or a stable cheat capability id.</summary>
        public string CapabilityId { get; }

        /// <summary>Decision window phase that produced this candidate.</summary>
        public AiPhase Phase { get; }

        /// <summary>Estimated cheat-point cost; ordinary actions are 0.</summary>
        public int Cost { get; }

        /// <summary>Exposure/suspicion risk estimate (ordering metadata only).</summary>
        public AiRisk Risk { get; }

        /// <summary>Visible-effect estimate (ordering metadata only).</summary>
        public AiEffect EstimatedEffect { get; }

        /// <summary>Whether executing the candidate ends the current decision window.</summary>
        public bool TerminalForWindow { get; }

        /// <summary>Visible parameters; defaults to <see cref="AiVisibleParams.Empty"/>.</summary>
        public AiVisibleParams Parameters { get; }

        /// <summary>Whether this is the window's safe fallback candidate.</summary>
        public bool IsSafeDefault { get; }

        /// <summary>Creates a candidate.</summary>
        public AiCandidate(
            string optionId,
            string capabilityId,
            AiPhase phase,
            int cost,
            AiRisk risk,
            AiEffect estimatedEffect,
            bool terminalForWindow,
            AiVisibleParams? parameters = null,
            bool isSafeDefault = false)
        {
            if (optionId is null)
            {
                throw new ArgumentNullException(nameof(optionId));
            }

            if (capabilityId is null)
            {
                throw new ArgumentNullException(nameof(capabilityId));
            }

            if (risk is null)
            {
                throw new ArgumentNullException(nameof(risk));
            }

            if (estimatedEffect is null)
            {
                throw new ArgumentNullException(nameof(estimatedEffect));
            }

            if (KotlinSemantics.IsKotlinBlank(optionId))
            {
                throw new ArgumentException("optionId cannot be blank.", nameof(optionId));
            }

            if (KotlinSemantics.IsKotlinBlank(capabilityId))
            {
                throw new ArgumentException("capabilityId cannot be blank.", nameof(capabilityId));
            }

            if (cost < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cost),
                    cost,
                    "cost must be non-negative.");
            }

            OptionId = optionId;
            CapabilityId = capabilityId;
            Phase = phase;
            Cost = cost;
            Risk = risk;
            EstimatedEffect = estimatedEffect;
            TerminalForWindow = terminalForWindow;
            Parameters = parameters ?? AiVisibleParams.Empty;
            IsSafeDefault = isSafeDefault;
        }

        /// <inheritdoc/>
        public bool Equals(AiCandidate? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null &&
                   OptionId == other.OptionId &&
                   CapabilityId == other.CapabilityId &&
                   Phase == other.Phase &&
                   Cost == other.Cost &&
                   Risk == other.Risk &&
                   EstimatedEffect == other.EstimatedEffect &&
                   TerminalForWindow == other.TerminalForWindow &&
                   Parameters == other.Parameters &&
                   IsSafeDefault == other.IsSafeDefault;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is AiCandidate other && Equals(other);
        }

        /// <summary>
        /// Java-parity hash of the Kotlin data class, with the V4 enum field hashed as
        /// its ordinal: the JVM hashes enum instances by identity, which is
        /// process-random, so the ordinal is substituted to keep the in-process
        /// "equal objects hash equal" contract both reproducible here and stable
        /// across processes. Diagnostic/ordering use only — never an authority digest.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int result = KotlinSemantics.JavaStringHash(OptionId);
                result = 31 * result + KotlinSemantics.JavaStringHash(CapabilityId);
                result = 31 * result + (int)Phase;
                result = 31 * result + Cost;
                result = 31 * result + Risk.GetHashCode();
                result = 31 * result + EstimatedEffect.GetHashCode();
                result = 31 * result + (TerminalForWindow ? 1231 : 1237);
                result = 31 * result + Parameters.GetHashCode();
                result = 31 * result + (IsSafeDefault ? 1231 : 1237);
                return result;
            }
        }

        /// <summary>Data-class equality over all nine fields.</summary>
        public static bool operator ==(AiCandidate? left, AiCandidate? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        /// <summary>Data-class inequality.</summary>
        public static bool operator !=(AiCandidate? left, AiCandidate? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Kotlin data-class <c>toString</c> shape with the V4 enum names
        /// (e.g. <c>ORDINARY_ACTION</c>) and lowercase booleans; diagnostic only.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiCandidate(optionId=");
            builder.Append(OptionId);
            builder.Append(", capabilityId=").Append(CapabilityId);
            builder.Append(", phase=").Append(KotlinSemantics.KotlinPhaseName(Phase));
            builder.Append(", cost=").Append(Cost);
            builder.Append(", risk=").Append(Risk);
            builder.Append(", estimatedEffect=").Append(EstimatedEffect);
            builder.Append(", terminalForWindow=").Append(TerminalForWindow ? "true" : "false");
            builder.Append(", parameters=").Append(Parameters);
            builder.Append(", isSafeDefault=").Append(IsSafeDefault ? "true" : "false");
            builder.Append(')');
            return builder.ToString();
        }
    }

    /// <summary>
    /// Frozen decision identity stamp (ENG-017 port of V4 <c>AiDecisionStamp</c>).
    /// Captured when an asynchronous AI computation starts; the authority compares
    /// every field when the result re-enters the serial queue, and any mismatch
    /// invalidates the stale result.
    ///
    /// Kotlin data-class semantics over all ten fields; a null and a set value always
    /// differ (probe: every field toggled independently). Only <c>sessionId</c> must be
    /// Kotlin-not-blank; the other identity strings are unvalidated in V4 and stay
    /// unvalidated here, so empty strings remain legal. All Kotlin numeric ranges are
    /// inclusive: seat 0..3, controlEpoch/stateVersion &gt;= 0, and
    /// <c>DeadlineMillis</c> &gt;= 0 only when set (IDLE stamps carry null).
    /// </summary>
    public sealed class AiDecisionStamp : IEquatable<AiDecisionStamp?>
    {
        /// <summary>Seat 0..3 the decision belongs to.</summary>
        public int Seat { get; }

        /// <summary>Session identity; Kotlin not-blank.</summary>
        public string SessionId { get; }

        /// <summary>Round identity; null before a round exists.</summary>
        public string? RoundId { get; }

        /// <summary>Decision identity; null when the window has no stable id.</summary>
        public string? DecisionId { get; }

        /// <summary>Authority control epoch at capture time.</summary>
        public long ControlEpoch { get; }

        /// <summary>Window phase at capture time.</summary>
        public AiPhase Phase { get; }

        /// <summary>Authority state version at capture time.</summary>
        public long StateVersion { get; }

        /// <summary>Cheat offer identity; null outside offer windows.</summary>
        public string? OfferId { get; }

        /// <summary>Decision window identity; null when there is none.</summary>
        public string? WindowId { get; }

        /// <summary>Deadline in unix millis; null when the window has no deadline.</summary>
        public long? DeadlineMillis { get; }

        /// <summary>Creates a stamp; every field participates in identity.</summary>
        public AiDecisionStamp(
            int seat,
            string sessionId,
            string? roundId,
            string? decisionId,
            long controlEpoch,
            AiPhase phase,
            long stateVersion,
            string? offerId,
            string? windowId,
            long? deadlineMillis)
        {
            if (sessionId is null)
            {
                throw new ArgumentNullException(nameof(sessionId));
            }

            // Validation order mirrors the V4 init block: seat, sessionId,
            // controlEpoch, stateVersion, then the conditional deadline check.
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(seat),
                    seat,
                    "seat must be in 0..3.");
            }

            if (KotlinSemantics.IsKotlinBlank(sessionId))
            {
                throw new ArgumentException("sessionId cannot be blank.", nameof(sessionId));
            }

            if (controlEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(controlEpoch),
                    controlEpoch,
                    "controlEpoch must be non-negative.");
            }

            if (stateVersion < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stateVersion),
                    stateVersion,
                    "stateVersion must be non-negative.");
            }

            if (deadlineMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deadlineMillis),
                    deadlineMillis,
                    "deadlineMillis must be non-negative.");
            }

            Seat = seat;
            SessionId = sessionId;
            RoundId = roundId;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            Phase = phase;
            StateVersion = stateVersion;
            OfferId = offerId;
            WindowId = windowId;
            DeadlineMillis = deadlineMillis;
        }

        /// <inheritdoc/>
        public bool Equals(AiDecisionStamp? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null &&
                   Seat == other.Seat &&
                   SessionId == other.SessionId &&
                   RoundId == other.RoundId &&
                   DecisionId == other.DecisionId &&
                   ControlEpoch == other.ControlEpoch &&
                   Phase == other.Phase &&
                   StateVersion == other.StateVersion &&
                   OfferId == other.OfferId &&
                   WindowId == other.WindowId &&
                   DeadlineMillis == other.DeadlineMillis;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is AiDecisionStamp other && Equals(other);
        }

        /// <summary>
        /// Java-parity hash of the Kotlin data class (null fields hash 0), with the V4
        /// enum field hashed as its ordinal because JVM enum hashes are
        /// process-random. Diagnostic use only — window matching uses full equality,
        /// and the authority digest remains the CommandIntent SHA-256.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int result = Seat;
                result = 31 * result + KotlinSemantics.JavaStringHash(SessionId);
                result = 31 * result + (RoundId is null ? 0 : KotlinSemantics.JavaStringHash(RoundId));
                result = 31 * result + (DecisionId is null ? 0 : KotlinSemantics.JavaStringHash(DecisionId));
                result = 31 * result + KotlinSemantics.JavaLongHash(ControlEpoch);
                result = 31 * result + (int)Phase;
                result = 31 * result + KotlinSemantics.JavaLongHash(StateVersion);
                result = 31 * result + (OfferId is null ? 0 : KotlinSemantics.JavaStringHash(OfferId));
                result = 31 * result + (WindowId is null ? 0 : KotlinSemantics.JavaStringHash(WindowId));
                result = 31 * result +
                         (DeadlineMillis is null ? 0 : KotlinSemantics.JavaLongHash(DeadlineMillis.Value));
                return result;
            }
        }

        /// <summary>Data-class equality over all ten fields (case-sensitive strings).</summary>
        public static bool operator ==(AiDecisionStamp? left, AiDecisionStamp? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        /// <summary>Data-class inequality.</summary>
        public static bool operator !=(AiDecisionStamp? left, AiDecisionStamp? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Kotlin data-class <c>toString</c> shape with the V4 enum name; nullable
        /// fields render as <c>null</c>. Diagnostic only.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiDecisionStamp(seat=");
            builder.Append(Seat);
            builder.Append(", sessionId=").Append(SessionId);
            builder.Append(", roundId=").Append(RoundId ?? "null");
            builder.Append(", decisionId=").Append(DecisionId ?? "null");
            builder.Append(", controlEpoch=").Append(ControlEpoch);
            builder.Append(", phase=").Append(KotlinSemantics.KotlinPhaseName(Phase));
            builder.Append(", stateVersion=").Append(StateVersion);
            builder.Append(", offerId=").Append(OfferId ?? "null");
            builder.Append(", windowId=").Append(WindowId ?? "null");
            builder.Append(", deadlineMillis=").Append(
                DeadlineMillis is null ? "null" : DeadlineMillis.Value.ToString(CultureInfo.InvariantCulture));
            builder.Append(')');
            return builder.ToString();
        }
    }

    /// <summary>
    /// Budget and filtering limits of one AI decision, computed once by the authority
    /// projection and then frozen (ENG-017 port of V4 <c>AiCandidateLimits</c>).
    ///
    /// V4 deliberately does NOT make this a data class, so instances are compared by
    /// identity and no equality/hash overrides exist here either (probe: two
    /// content-identical limit instances are not equal, mirroring the JVM default).
    /// <see cref="EnabledCapabilityIds"/> is a defensive insertion-ordered deduplicated
    /// copy that never aliases the source; netstandard2.1 has no IReadOnlySet, so the
    /// set iteration order is exposed as a read-only list while membership goes
    /// through <see cref="Allows"/>. <see cref="SpendablePoints"/> is
    /// <c>max(0, min(maxSpend, remaining - reserve))</c> with no overflow risk because
    /// all three inputs are validated non-negative.
    /// </summary>
    public sealed class AiCandidateLimits
    {
        private readonly string[] _keys;
        private readonly Dictionary<string, bool> _membership;

        /// <summary>Creates frozen limits; validation mirrors the V4 init block order.</summary>
        public AiCandidateLimits(
            IEnumerable<string> enabledCapabilityIds,
            int maxSpend,
            int remainingPoints,
            int reservePoints,
            int candidateLimit,
            double riskTolerance)
        {
            if (enabledCapabilityIds is null)
            {
                throw new ArgumentNullException(nameof(enabledCapabilityIds));
            }

            // Kotlin initializes the enabledCapabilityIds property before the init
            // block runs, so the defensive copy happens even when validation throws.
            var keys = new List<string>();
            var membership = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string id in enabledCapabilityIds)
            {
                if (id is null)
                {
                    throw new ArgumentNullException(
                        nameof(enabledCapabilityIds),
                        "enabled capability id cannot be null.");
                }

                if (membership.ContainsKey(id))
                {
                    continue; // LinkedHashSet semantics: first occurrence wins.
                }

                keys.Add(id);
                membership.Add(id, true);
            }

            if (maxSpend < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxSpend),
                    maxSpend,
                    "maxSpend must be non-negative.");
            }

            if (remainingPoints < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingPoints),
                    remainingPoints,
                    "remainingPoints must be non-negative.");
            }

            if (reservePoints < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reservePoints),
                    reservePoints,
                    "reservePoints must be non-negative.");
            }

            if (candidateLimit < 1 || candidateLimit > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(candidateLimit),
                    candidateLimit,
                    "candidateLimit must be in 1..64.");
            }

            // -0.0 passes exactly like the JVM (-0.0 >= 0.0 is true); NaN fails the
            // explicit check and every range comparison, mirroring `in 0.0..1.0`.
            if (double.IsNaN(riskTolerance) || riskTolerance < 0.0 || riskTolerance > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(riskTolerance),
                    riskTolerance,
                    "riskTolerance must be in 0.0..1.0.");
            }

            _keys = keys.ToArray();
            _membership = membership;
            EnabledCapabilityIds = new OrderedIds(_keys);
            MaxSpend = maxSpend;
            RemainingPoints = remainingPoints;
            ReservePoints = reservePoints;
            CandidateLimit = candidateLimit;
            RiskTolerance = riskTolerance;
        }

        /// <summary>
        /// Enabled capability ids as an insertion-ordered, deduplicated read-only view
        /// (V4: unmodifiable LinkedHashSet). Later mutations of any source are
        /// invisible, and the view cannot be cast back to a writable collection.
        /// </summary>
        public IReadOnlyList<string> EnabledCapabilityIds { get; }

        /// <summary>Maximum cheat points spendable this decision.</summary>
        public int MaxSpend { get; }

        /// <summary>Cheat points currently available to the seat.</summary>
        public int RemainingPoints { get; }

        /// <summary>Points that must stay reserved.</summary>
        public int ReservePoints { get; }

        /// <summary>Maximum candidates considered this decision, 1..64.</summary>
        public int CandidateLimit { get; }

        /// <summary>
        /// Risk tolerance in [0, 1] for strategy scoring. Ordering metadata only — this
        /// number never reaches the domain authority hash or a persisted charge.
        /// </summary>
        public double RiskTolerance { get; }

        /// <summary>Points this decision may actually spend (never negative).</summary>
        public int SpendablePoints
        {
            get
            {
                int afterReserve = RemainingPoints - ReservePoints;
                return Math.Max(0, Math.Min(MaxSpend, afterReserve));
            }
        }

        /// <summary>Whether <paramref name="capabilityId"/> is enabled (ordinal compare).</summary>
        public bool Allows(string capabilityId)
        {
            if (capabilityId is null)
            {
                throw new ArgumentNullException(nameof(capabilityId));
            }

            return _membership.ContainsKey(capabilityId);
        }

        /// <summary>Whether <paramref name="cost"/> is in 0..<see cref="SpendablePoints"/>.</summary>
        public bool CanSpend(int cost)
        {
            return cost >= 0 && cost <= SpendablePoints;
        }

        /// <summary>
        /// JVM-default diagnostic identity (V4 is not a data class, so no structured
        /// toString exists to port); content appears only for logs, never identity.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder("AiCandidateLimits@");
            builder.Append(GetHashCode().ToString("x8", CultureInfo.InvariantCulture));
            builder.Append("(enabled=[");
            for (int i = 0; i < _keys.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(_keys[i]);
            }

            builder.Append("], maxSpend=").Append(MaxSpend);
            builder.Append(", remainingPoints=").Append(RemainingPoints);
            builder.Append(", reservePoints=").Append(ReservePoints);
            builder.Append(", candidateLimit=").Append(CandidateLimit);
            builder.Append(", riskTolerance=").Append(RiskTolerance.ToString(CultureInfo.InvariantCulture));
            builder.Append(')');
            return builder.ToString();
        }

        /// <summary>
        /// Read-only insertion-ordered view over the constructor's defensive copy; not
        /// a List/array, so no caller can cast its way back to a writable collection
        /// (the OrderedToggles precedent from AiCheatPolicy).
        /// </summary>
        private sealed class OrderedIds : IReadOnlyList<string>
        {
            private readonly string[] _keys;

            internal OrderedIds(string[] keys)
            {
                _keys = keys;
            }

            public int Count => _keys.Length;

            public string this[int index] => _keys[index];

            public IEnumerator<string> GetEnumerator()
            {
                foreach (string key in _keys)
                {
                    yield return key;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }

    /// <summary>
    /// Kotlin string/number semantics that differ from .NET, implemented once for the
    /// candidate models. Every rule below was verified by driving the real compiled V4
    /// engine classes (probe log recorded in
    /// doc/v5-重构/V5-P16-AI-CANDIDATE-MODELS.md), not inferred from comments.
    /// </summary>
    internal static class KotlinSemantics
    {
        /// <summary>
        /// Kotlin <c>Char.isWhitespace()</c> = <c>Character.isWhitespace ||
        /// Character.isSpaceChar</c>. Differs from
        /// <see cref="char.IsWhiteSpace(char)"/> in exactly two BMP places: 0x001C..
        /// 0x001F are Java-only whitespace (rejected as blank by V4), while U+0085 is
        /// neither Java whitespace nor a space char (accepted by V4). NBSP U+00A0 is
        /// a space char and therefore Kotlin whitespace on both sides (probe: NBSP
        /// trims in stringSet and rejects blank keys/session ids).
        /// </summary>
        internal static bool IsKotlinWhitespace(char c)
        {
            int code = c;
            if (code >= 0x1C && code <= 0x1F)
            {
                return true; // Java file/group/record/unit separator: V4 whitespace, .NET is not
            }

            if (code == 0x85)
            {
                return false; // next line: .NET whitespace, but not V4 whitespace
            }

            return char.IsWhiteSpace(c);
        }

        /// <summary>Kotlin <c>CharSequence.isBlank()</c>: empty or all Kotlin-whitespace.</summary>
        internal static bool IsKotlinBlank(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (!IsKotlinWhitespace(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Kotlin <c>String.trim()</c>: strips Kotlin-whitespace from both ends. Probe
        /// parity: vertical tab trims, NBSP trims, ideographic space trims, U+0085
        /// does not.
        /// </summary>
        internal static string KotlinTrim(string value)
        {
            int start = 0;
            int end = value.Length;
            while (start < end && IsKotlinWhitespace(value[start]))
            {
                start++;
            }

            while (end > start && IsKotlinWhitespace(value[end - 1]))
            {
                end--;
            }

            return start == 0 && end == value.Length ? value : value.Substring(start, end - start);
        }

        /// <summary>
        /// Kotlin <c>String.toIntOrNull()</c> (radix 10): optional ASCII '+'/'-' sign,
        /// then decimal digits per UTF-16 unit, parsed with the Kotlin stdlib's
        /// negative-accumulation algorithm including its <c>limitBeforeMul</c>
        /// pre-check — the pre-check is what makes "2147483649" and "-2147483649"
        /// return null without relying on unchecked wrap-around (a naive accumulate-
        /// then-compare port lets the wrapped value slip past the limit test).
        /// Whitespace is never trimmed; a bare sign, an empty string, a non-digit
        /// anywhere and overflow all return null. Digits beyond ASCII come from
        /// <see cref="CharUnicodeInfo.GetDecimalDigitValue"/>, matching
        /// <c>Character.digit(c, 10)</c>: fullwidth ４ and Arabic-Indic ٤ parse;
        /// supplementary-plane digits fail per UTF-16 unit as lone surrogates, the
        /// same as the JVM. Probe-verified against the real V4 engine classes.
        /// </summary>
        internal static int? KotlinToIntOrNull(string value)
        {
            if (value.Length == 0)
            {
                return null;
            }

            bool negative = false;
            int index = 0;
            char firstChar = value[0];
            if (firstChar < '0')
            {
                // Anything below '0' that is not a sign is rejected (space, tab, the
                // Java-only separators, U+0085, punctuation), exactly like the stdlib.
                if (firstChar == '-')
                {
                    negative = true;
                }
                else if (firstChar != '+')
                {
                    return null;
                }

                if (value.Length == 1)
                {
                    return null; // bare sign
                }

                index++;
            }

            int limit = negative ? int.MinValue : -int.MaxValue;
            int limitBeforeMul = limit / 10;
            int result = 0;
            while (index < value.Length)
            {
                int digit = DecimalDigitValue(value[index]);
                if (digit < 0)
                {
                    return null;
                }

                if (result < limitBeforeMul)
                {
                    return null;
                }

                result *= 10;
                if (result < limit + digit)
                {
                    return null;
                }

                result -= digit;
                index++;
            }

            return negative ? result : -result;
        }

        private static int DecimalDigitValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            return CharUnicodeInfo.GetDecimalDigitValue(c);
        }

        /// <summary>Java <c>String.hashCode</c> over UTF-16 units; deterministic.</summary>
        internal static int JavaStringHash(string value)
        {
            unchecked
            {
                int hash = 0;
                for (int i = 0; i < value.Length; i++)
                {
                    hash = 31 * hash + value[i];
                }

                return hash;
            }
        }

        /// <summary>Java <c>Long.hashCode</c>: high and low 32-bit halves xor'ed.</summary>
        internal static int JavaLongHash(long value)
        {
            unchecked
            {
                return (int)(value ^ (long)((ulong)value >> 32));
            }
        }

        /// <summary>
        /// Java <c>Double.hashCode</c>: bits xor (bits &gt;&gt;&gt; 32) as int; NaN
        /// cannot occur (constructors reject it), but the canonical NaN bits are used
        /// anyway to mirror the JVM contract.
        /// </summary>
        internal static int JavaDoubleHash(double value)
        {
            long bits = double.IsNaN(value) ? 0x7ff8000000000000L : BitConverter.DoubleToInt64Bits(value);
            return JavaLongHash(bits);
        }

        /// <summary>The V4 enum constant name for a phase, for Kotlin-shaped toString.</summary>
        internal static string KotlinPhaseName(AiPhase phase)
        {
            switch (phase)
            {
                case AiPhase.Idle: return "IDLE";
                case AiPhase.ShufflePreparation: return "SHUFFLE_PREPARATION";
                case AiPhase.OrdinaryAction: return "ORDINARY_ACTION";
                case AiPhase.OfferChoice: return "OFFER_CHOICE";
                default: return ((int)phase).ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}

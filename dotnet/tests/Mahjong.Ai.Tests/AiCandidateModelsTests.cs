using System;
using System.Collections.Generic;
using Mahjong.Ai;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Behaviour matrix for the ENG-017 port of the V4 candidate value models
    /// (<c>AiDecisionModels.kt</c> lines 21..202: <see cref="AiPhase"/>,
    /// <see cref="AiRisk"/>, <see cref="AiEffect"/>, <see cref="AiVisibleParams"/>,
    /// <see cref="AiCandidate"/>, <see cref="AiDecisionStamp"/> and
    /// <see cref="AiCandidateLimits"/>).
    ///
    /// Every expectation below was pinned by a reflection probe that drove the REAL
    /// compiled V4 engine classes (D:/majiang/engine/build/classes/kotlin/main, whose
    /// AiDecisionModels.kt is byte-identical between worktree baseline 7844194 and
    /// remote head 3c2e7f5) under kotlin-stdlib-2.4.10; the probe log and its SHA-256
    /// are embedded in doc/v5-重构/V5-P16-AI-CANDIDATE-MODELS.md
    /// (log sha256 47fe64ae853a8c6ce75a8855c61cd2dc0d2507d1b300be668fa77c97d1c37c51,
    /// 170 checks: 168 behavioural agreements, 2 message-format-only artifacts).
    ///
    /// The matrix is not a property-readback mirror: each group exercises observable
    /// V4 behaviour — the Kotlin blank/whitespace rule (NBSP and 0x001C..0x001F are
    /// blank, U+0085 is not), Kotlin <c>toIntOrNull</c> (Unicode decimal digits in,
    /// whitespace never trimmed, overflow null), Kotlin <c>trim</c>, comma-list
    /// parsing that drops unparsable elements, insertion-ordered defensive copies,
    /// Kotlin data-class equality with bitwise <c>Double.equals</c> semantics (so
    /// -0.0 is distinct from 0.0), the V4 init-block validation order, and the
    /// identity-only equality of the deliberately non-data class
    /// <see cref="AiCandidateLimits"/>. JVM-exact hash values are asserted only where
    /// the V4 value is deterministic (doubles and strings); enum fields hash by JVM
    /// identity, so stamps and candidates assert only the equal-objects-hash-equal
    /// contract.
    /// </summary>
    public class AiCandidateModelsTests
    {
        // Unicode test inputs, built from code points so this file stays pure ASCII.
        private static readonly string Nbsp = ((char)0x00A0).ToString();
        private static readonly string Vt = ((char)0x000B).ToString();
        private static readonly string Fs = ((char)0x001C).ToString();
        private static readonly string Nel = ((char)0x0085).ToString();
        private static readonly string Ideo = ((char)0x3000).ToString();
        private static readonly string Fw42 = ((char)0xFF14).ToString() + ((char)0xFF12).ToString();
        private static readonly string Fw7 = ((char)0xFF17).ToString();
        private static readonly string Fw0 = ((char)0xFF10).ToString();
        private static readonly string FwPlus = ((char)0xFF0B).ToString();
        private static readonly string FwMinus = ((char)0xFF0D).ToString();
        private static readonly string Ar42 = ((char)0x0664).ToString() + ((char)0x0662).ToString();
        private static readonly string Ar4 = ((char)0x0664).ToString();
        private static readonly string Emoji = ((char)0xD83D).ToString() + ((char)0xDE00).ToString();

        private static double NextUp(double value)
        {
            return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + 1);
        }

        // ---------------------------------------------------------------------
        // AiPhase: the V4 enum mapping
        // ---------------------------------------------------------------------

        [Fact]
        public void AiPhaseOrdinalsMatchTheV4Enum()
        {
            // Probe: "0=IDLE 1=SHUFFLE_PREPARATION 2=ORDINARY_ACTION 3=OFFER_CHOICE".
            Assert.Equal(AiPhase.Idle, (AiPhase)0);
            Assert.Equal(AiPhase.ShufflePreparation, (AiPhase)1);
            Assert.Equal(AiPhase.OrdinaryAction, (AiPhase)2);
            Assert.Equal(AiPhase.OfferChoice, (AiPhase)3);
            Assert.Equal(4, Enum.GetValues(typeof(AiPhase)).Length);
        }

        // ---------------------------------------------------------------------
        // AiRisk
        // ---------------------------------------------------------------------

        [Fact]
        public void AiRiskDefaultsMatchTheV4NoArgConstructor()
        {
            AiRisk risk = new AiRisk();

            Assert.Equal(0.0, risk.Exposure);
            Assert.Equal(0.0, risk.Suspicion);
            Assert.Equal(0.0, risk.Penalty);
        }

        [Fact]
        public void AiRiskPenaltyIsTheUniformHalfAndHalfBlend()
        {
            Assert.Equal(0.375, new AiRisk(0.5, 0.25).Penalty);
            Assert.Equal(0.5, new AiRisk(1.0, 0.0).Penalty);
            Assert.Equal(0.5, new AiRisk(0.0, 1.0).Penalty);
            Assert.Equal(1.0, new AiRisk(1.0, 1.0).Penalty);
        }

        [Fact]
        public void AiRiskPenaltyKeepsTheNegativeZeroSignBitsLikeTheJvm()
        {
            // Probe: AiRisk(-0.0,-0.0).penalty raw bits 0x8000000000000000; the
            // all-positive-zero candidate keeps +0.0 (bits 0).
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(new AiRisk(-0.0, -0.0).Penalty));
            Assert.Equal(0L, BitConverter.DoubleToInt64Bits(new AiRisk(0.0, 0.0).Penalty));
            // IEEE addition rules match the JVM: -0.0 + +0.0 = +0.0.
            Assert.Equal(0L, BitConverter.DoubleToInt64Bits(new AiRisk(-0.0, 0.0).Penalty));
        }

        [Fact]
        public void AiRiskRejectsNaNBothInfinitiesAndEveryOutOfRangeValue()
        {
            // Probe: each case throws IllegalArgumentException naming the first bad field.
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(double.NaN, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(0.0, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(double.PositiveInfinity, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(0.0, double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(double.NegativeInfinity, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(0.0, double.NegativeInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(NextUp(1.0), 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(-double.Epsilon, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiRisk(0.0, -1e-12));
        }

        [Fact]
        public void AiRiskAcceptsTheInclusiveBoundariesIncludingNegativeZero()
        {
            // Probe: AiRisk(1.0,1.0) constructs; -0.0 passes because -0.0 >= 0.0.
            AiRisk boundary = new AiRisk(1.0, 1.0);
            Assert.Equal(1.0, boundary.Exposure);
            Assert.Equal(1.0, boundary.Suspicion);

            AiRisk negativeZero = new AiRisk(-0.0, -0.0);
            Assert.True(BitConverter.DoubleToInt64Bits(negativeZero.Exposure) == long.MinValue);
            Assert.True(BitConverter.DoubleToInt64Bits(negativeZero.Suspicion) == long.MinValue);
        }

        [Fact]
        public void AiRiskValidatesExposureBeforeSuspicionLikeTheV4InitBlock()
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => new AiRisk(double.NaN, double.NaN));

            Assert.Equal("exposure", exception.ParamName);
        }

        [Fact]
        public void AiRiskEqualityIsBitwiseSoNegativeZeroDiffersFromZero()
        {
            AiRisk zero = new AiRisk(0.0, 0.0);
            AiRisk negativeZero = new AiRisk(-0.0, -0.0);

            // Probe: AiRisk(0.0,0.0).equals(AiRisk(-0.0,-0.0)) is false despite
            // numerically equal components (Java Double.equals bit patterns).
            Assert.False(zero.Equals(negativeZero));
            Assert.True(zero != negativeZero);
            Assert.True(zero.Equals(new AiRisk(0.0, 0.0)));
            Assert.True(zero == new AiRisk(0.0, 0.0));
            Assert.False(zero.Equals(new AiRisk(0.5, 0.0)));
            Assert.False(zero.Equals(new AiRisk(0.0, 0.5)));
            Assert.False(zero.Equals(null));
            Assert.False(zero.Equals("not a risk"));
        }

        [Fact]
        public void AiRiskJavaParityHashIsDeterministicAndMatchesTheJvmValue()
        {
            // Probe: both AiRisk(0.0,0.0) instances hash to 0 in the JVM; the port
            // reproduces the exact value because doubles hash from their bits.
            Assert.Equal(0, new AiRisk(0.0, 0.0).GetHashCode());
            Assert.Equal(new AiRisk(0.5, 0.25).GetHashCode(), new AiRisk(0.5, 0.25).GetHashCode());
            Assert.NotEqual(new AiRisk(0.0, 0.0).GetHashCode(), new AiRisk(1.0, 0.0).GetHashCode());
        }

        [Fact]
        public void AiRiskToStringRendersTheDataClassShape()
        {
            // Diagnostic formatting is .NET-invariant ("0", not the JVM "0.0").
            Assert.Equal("AiRisk(exposure=0, suspicion=0)", new AiRisk().ToString());
            Assert.Equal("AiRisk(exposure=0.5, suspicion=0.25)", new AiRisk(0.5, 0.25).ToString());
        }

        // ---------------------------------------------------------------------
        // AiEffect
        // ---------------------------------------------------------------------

        [Fact]
        public void AiEffectDefaultsMatchTheV4NoArgConstructor()
        {
            AiEffect effect = new AiEffect();

            Assert.Equal(0, effect.HandValue);
            Assert.Equal(0, effect.ShantenImprovement);
            Assert.Equal(0, effect.RetainedValue);
            Assert.Equal(string.Empty, effect.Note);
        }

        [Fact]
        public void AiEffectNoteBoundaryIsOneHundredTwentyUtf16Units()
        {
            // Probe: exactly 120 chars construct; 121 throw; 60 surrogate pairs (120
            // UTF-16 units) construct; 61 pairs (122 units) throw — the bound counts
            // UTF-16 code units, not Unicode code points.
            Assert.Equal(120, new string('x', 120).Length);
            Assert.Equal(120, new AiEffect(note: new string('x', 120)).Note.Length);
            Assert.Throws<ArgumentException>(() => new AiEffect(note: new string('x', 121)));

            string sixtyPairs = Repeat(Emoji, 60);
            Assert.Equal(120, sixtyPairs.Length);
            Assert.Equal(sixtyPairs, new AiEffect(note: sixtyPairs).Note);

            string sixtyOnePairs = Repeat(Emoji, 61);
            Assert.Equal(122, sixtyOnePairs.Length);
            Assert.Throws<ArgumentException>(() => new AiEffect(note: sixtyOnePairs));
        }

        [Fact]
        public void AiEffectIntegersAreUnvalidatedLikeV4()
        {
            // Probe: Int.MinValue/Int.MaxValue/-1 all construct untouched.
            AiEffect effect = new AiEffect(int.MinValue, int.MaxValue, -1, "e");

            Assert.Equal(int.MinValue, effect.HandValue);
            Assert.Equal(int.MaxValue, effect.ShantenImprovement);
            Assert.Equal(-1, effect.RetainedValue);
        }

        [Fact]
        public void AiEffectRejectsNullNoteLikeTheKotlinIntrinsicCheck()
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => new AiEffect(note: null!));

            Assert.Equal("note", exception.ParamName);
        }

        [Fact]
        public void AiEffectEqualityCoversEveryFieldCaseSensitively()
        {
            AiEffect effect = new AiEffect(1, 2, 3, "n");

            Assert.True(effect.Equals(new AiEffect(1, 2, 3, "n")));
            Assert.False(effect.Equals(new AiEffect(2, 2, 3, "n")));
            Assert.False(effect.Equals(new AiEffect(1, 3, 3, "n")));
            Assert.False(effect.Equals(new AiEffect(1, 2, 4, "n")));
            Assert.False(effect.Equals(new AiEffect(1, 2, 3, "N")));
            Assert.False(effect.Equals(new AiEffect(1, 2, 3, "m")));
            Assert.False(effect.Equals(new AiEffect(1, 2, 3)));
            Assert.False(effect.Equals(null));
            Assert.Equal(effect.GetHashCode(), new AiEffect(1, 2, 3, "n").GetHashCode());
        }

        [Fact]
        public void AiEffectOperatorsTrackEqualsIncludingNulls()
        {
            AiEffect effect = new AiEffect(1, 2, 3, "n");

            Assert.True(effect == new AiEffect(1, 2, 3, "n"));
            Assert.True(effect != new AiEffect(1, 2, 3, "other"));
            Assert.True((AiEffect?)null == (AiEffect?)null);
            Assert.False(effect == null);
            Assert.False(null == effect);
        }

        // ---------------------------------------------------------------------
        // AiVisibleParams: construction, order, defence
        // ---------------------------------------------------------------------

        [Fact]
        public void OfPreservesInsertionOrderLikeALinkedHashMap()
        {
            // Probe: of(b=2,a=1) iterates [b, a].
            AiVisibleParams parameters = AiVisibleParams.Of(("b", "2"), ("a", "1"));

            Assert.Equal(new[] { "b", "a" }, Keys(parameters));
        }

        [Fact]
        public void FromPreservesTheSourceIterationOrder()
        {
            // Probe: from(LinkedHashMap b=2,a=1) iterates [b, a]; the port copies the
            // source's enumeration order the same way.
            var source = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
            AiVisibleParams parameters = AiVisibleParams.From(source);

            Assert.Equal(new[] { "b", "a" }, Keys(parameters));
        }

        [Fact]
        public void OfDuplicateKeysKeepFirstPositionAndTakeLastValue()
        {
            // Probe: duplicate key last-wins and collapses to a single entry.
            AiVisibleParams parameters = AiVisibleParams.Of(("k", "1"), ("a", "x"), ("k", "2"));

            Assert.Equal(2, parameters.Values.Count);
            Assert.Equal("2", parameters.String("k"));
            Assert.Equal(new[] { "k", "a" }, Keys(parameters));
        }

        [Fact]
        public void EmptyIsTheSharedEmptyInstanceAndBehavesLikeV4()
        {
            // Probe: EMPTY is empty, equals from(emptyMap), hashes 0 and prints the
            // Kotlin Map.toString shape.
            Assert.Empty(AiVisibleParams.Empty.Values);
            Assert.True(AiVisibleParams.Empty.Equals(AiVisibleParams.From(new Dictionary<string, string>())));
            Assert.Equal(0, AiVisibleParams.Empty.GetHashCode());
            Assert.Equal("AiVisibleParams({})", AiVisibleParams.Empty.ToString());
        }

        [Fact]
        public void FromPerformsNoValidationLikeV4()
        {
            // Probe: from() never checks keys — blank keys are legal through this path
            // and stay legal in the port.
            var source = new Dictionary<string, string> { [""] = "empty key", [Nbsp] = "nbsp key" };
            AiVisibleParams parameters = AiVisibleParams.From(source);

            Assert.Equal("empty key", parameters.String(""));
            Assert.Equal("nbsp key", parameters.String(Nbsp));
        }

        [Fact]
        public void OfRejectsKotlinBlankKeysWithTheExactV4BlankSet()
        {
            // Probe: "", " ", NBSP and the Java-only 0x001C are blank; U+0085 is not.
            Assert.Throws<ArgumentException>(() => AiVisibleParams.Of(("", "v")));
            Assert.Throws<ArgumentException>(() => AiVisibleParams.Of((" ", "v")));
            Assert.Throws<ArgumentException>(() => AiVisibleParams.Of((Nbsp, "v")));
            Assert.Throws<ArgumentException>(() => AiVisibleParams.Of((Fs, "v")));
            Assert.Throws<ArgumentException>(() => AiVisibleParams.Of(("\t", "v")));
            Assert.NotNull(AiVisibleParams.Of((Nel, "v")));
        }

        [Fact]
        public void OfRejectsNullKeysAndNullValues()
        {
            // Deliberate tightening: V4's typed surface cannot express a null value,
            // and a null key can never be read back through the typed API.
            Assert.Throws<ArgumentNullException>(() => AiVisibleParams.Of((null!, "v")));
            Assert.Throws<ArgumentNullException>(() => AiVisibleParams.Of(("k", null!)));
        }

        [Fact]
        public void ConstructorCopiesTheSourceMap()
        {
            var source = new Dictionary<string, string> { ["k"] = "v" };
            AiVisibleParams parameters = AiVisibleParams.From(source);

            // Mutate the caller's map afterwards: the params must not notice.
            source["k"] = "mutated";
            source["k2"] = "v2";

            Assert.Equal("v", parameters.String("k"));
            Assert.Null(parameters.String("k2"));
            Assert.Single(parameters.Values);
        }

        [Fact]
        public void ValuesViewCannotBeCastBackToAWritableDictionary()
        {
            AiVisibleParams parameters = AiVisibleParams.Of(("a", "1"));

            Assert.Null(parameters.Values as IDictionary<string, string>);
            Assert.Null(parameters.Values as System.Collections.IDictionary);
            Assert.Throws<InvalidCastException>(
                () => (Dictionary<string, string>)parameters.Values);

            // The read-only surface still answers lookups and enumeration.
            Assert.True(parameters.Values.ContainsKey("a"));
            Assert.True(parameters.Values.TryGetValue("a", out string? value) && value == "1");
            Assert.Equal("1", parameters.Values["a"]);
            Assert.Equal(new[] { "a" }, parameters.Values.Keys);
            Assert.Equal(new[] { "1" }, parameters.Values.Values);
        }

        [Fact]
        public void StringReturnsRawValuesAndNullForMissingKeys()
        {
            AiVisibleParams parameters = AiVisibleParams.Of(("b", "2"));

            Assert.Equal("2", parameters.String("b"));
            Assert.Null(parameters.String("missing"));
            Assert.Throws<ArgumentNullException>(() => parameters.String(null!));
        }

        // ---------------------------------------------------------------------
        // AiVisibleParams: Kotlin toIntOrNull semantics (probe matrix)
        // ---------------------------------------------------------------------

        [Fact]
        public void IntParsesSignedAsciiDecimalsWithoutTrimming()
        {
            AiVisibleParams p = AiVisibleParams.Of(
                ("n", "42"), ("n2", " 42"), ("n3", "42 "), ("n4", "+42"), ("n5", "-42"),
                ("n6", ""), ("n7", "+"), ("n8", "-"), ("n18", "\t42"), ("n21", "-0"));

            Assert.Equal(42, p.Int("n"));
            Assert.Null(p.Int("n2")); // whitespace never trimmed
            Assert.Null(p.Int("n3"));
            Assert.Equal(42, p.Int("n4"));
            Assert.Equal(-42, p.Int("n5"));
            Assert.Null(p.Int("n6"));
            Assert.Null(p.Int("n7")); // bare sign
            Assert.Null(p.Int("n8"));
            Assert.Null(p.Int("n18"));
            Assert.Equal(0, p.Int("n21"));
        }

        [Fact]
        public void IntHandlesIntegerExtremesAndOverflowByReturningNull()
        {
            AiVisibleParams p = AiVisibleParams.Of(
                ("n9", "2147483647"), ("n10", "2147483648"),
                ("n11", "-2147483648"), ("n12", "-2147483649"));

            Assert.Equal(int.MaxValue, p.Int("n9"));
            Assert.Null(p.Int("n10"));
            Assert.Equal(int.MinValue, p.Int("n11"));
            // The Kotlin stdlib's limitBeforeMul pre-check rejects the wrapped
            // accumulation; a naive accumulate-then-compare port would not.
            Assert.Null(p.Int("n12"));
        }

        [Fact]
        public void IntAcceptsUnicodeDecimalDigitsLikeCharacterDotDigit()
        {
            AiVisibleParams p = AiVisibleParams.Of(
                ("n13", Fw42), ("n14", Ar42), ("n19", FwPlus + Fw42), ("n20", "-" + Fw0),
                ("n23", Ar4));

            Assert.Equal(42, p.Int("n13")); // fullwidth digits are Nd
            Assert.Equal(42, p.Int("n14")); // Arabic-Indic digits are Nd
            Assert.Null(p.Int("n19")); // fullwidth '+' is punctuation, not a sign
            Assert.Equal(0, p.Int("n20")); // ASCII sign with fullwidth zero parses
            Assert.Equal(4, p.Int("n23"));
        }

        [Fact]
        public void IntRejectsEverythingThatIsNotAKotlinIntegerLiteral()
        {
            AiVisibleParams p = AiVisibleParams.Of(
                ("n15", "42.0"), ("n16", "0x10"), ("n17", "1_000"), ("n22", Vt + "42" + Vt));

            Assert.Null(p.Int("n15"));
            Assert.Null(p.Int("n16"));
            Assert.Null(p.Int("n17"));
            Assert.Null(p.Int("n22")); // no trim on the int() path
        }

        [Fact]
        public void IntReturnsNullForMissingKeys()
        {
            Assert.Null(AiVisibleParams.Empty.Int("missing"));
        }

        // ---------------------------------------------------------------------
        // AiVisibleParams: intList / stringSet (probe matrix)
        // ---------------------------------------------------------------------

        [Fact]
        public void IntListSplitsTrimsParsesAndDropsUnparsableElements()
        {
            // Probe: '1, 2 ,x,3.5,+4,<fullwidth-minus>5' keeps [1, 2, 4]; the
            // fullwidth-minus element fails to parse and is dropped silently.
            AiVisibleParams lst = AiVisibleParams.Of(
                ("l1", "1, 2 ,x,3.5,+4," + FwMinus + "5"),
                ("l2", Fw42 + "," + Ar42 + ", " + Fw7 + " "),
                ("l3", ""), ("l4", ","), ("l5", "1,,2"), ("l6", "99999999999,5"),
                ("l7", "-2147483648, +2147483647"));

            Assert.Equal(new[] { 1, 2, 4 }, lst.IntList("l1"));
            Assert.Equal(new[] { 42, 42, 7 }, lst.IntList("l2"));
            Assert.Empty(lst.IntList("l3"));
            Assert.Empty(lst.IntList("l4"));
            Assert.Equal(new[] { 1, 2 }, lst.IntList("l5"));
            Assert.Equal(new[] { 5 }, lst.IntList("l6")); // overflow drops only itself
            Assert.Equal(new[] { int.MinValue, int.MaxValue }, lst.IntList("l7"));
        }

        [Fact]
        public void IntListReturnsEmptyForMissingKeys()
        {
            Assert.Empty(AiVisibleParams.Empty.IntList("missing"));
        }

        [Fact]
        public void StringSetTrimsDropsEmptiesAndDedupesInFirstOccurrenceOrder()
        {
            // Probe: NBSP is Kotlin-trimmable, so "<NBSP>a" joins the "a" bucket and
            // the set iterates [b, a]; the ideographic space element trims to empty
            // and disappears.
            AiVisibleParams sets = AiVisibleParams.Of(
                ("s1", "b, a ,b,," + Nbsp + "a"),
                ("s2", Ideo + "," + Nel + "x, x"),
                ("s3", "a,b,a"),
                ("s4", ""), ("s5", ","), ("s6", Vt + "z" + Vt));

            Assert.Equal(new[] { "b", "a" }, sets.StringSet("s1"));
            Assert.Equal(new[] { Nel + "x", "x" }, sets.StringSet("s2")); // U+0085 not trimmed
            Assert.Equal(new[] { "a", "b" }, sets.StringSet("s3"));
            Assert.Empty(sets.StringSet("s4"));
            Assert.Empty(sets.StringSet("s5"));
            Assert.Equal(new[] { "z" }, sets.StringSet("s6")); // vertical tab trims
        }

        [Fact]
        public void StringSetReturnsEmptyForMissingKeys()
        {
            Assert.Empty(AiVisibleParams.Empty.StringSet("missing"));
        }

        [Fact]
        public void EmptyStringValuesAreKeptVerbatim()
        {
            // Probe: of(k="") stores the empty string and string(k) returns it.
            AiVisibleParams parameters = AiVisibleParams.Of(("k", ""));

            Assert.Equal(string.Empty, parameters.String("k"));
            Assert.Single(parameters.Values);
        }

        // ---------------------------------------------------------------------
        // AiVisibleParams: Kotlin data-class equality and diagnostics
        // ---------------------------------------------------------------------

        [Fact]
        public void ParamsEqualityIsMapContentAndIgnoresIterationOrder()
        {
            AiVisibleParams first = AiVisibleParams.Of(("a", "1"), ("b", " two"));
            AiVisibleParams second = AiVisibleParams.Of(("b", " two"), ("a", "1"));

            Assert.True(first.Equals(second));
            Assert.True(first == second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
            Assert.False(first == AiVisibleParams.Of(("a", "1"), ("b", "2")));
            Assert.False(first == AiVisibleParams.Of(("a", "1")));
            Assert.False(first == AiVisibleParams.Of(("A", "1"), ("b", " two"))); // case-sensitive
            Assert.True(AiVisibleParams.Empty == AiVisibleParams.From(new Dictionary<string, string>()));
            Assert.True((AiVisibleParams?)null == (AiVisibleParams?)null);
            Assert.False(first == null);
            Assert.False(first.Equals("not params"));
        }

        [Fact]
        public void ParamsJavaParityHashMatchesTheJvmValues()
        {
            // Probe: from JVM String/Map hashing, {a=1, b=" two"} hashes to 1068702
            // and the empty map to 0; the port reproduces both exactly.
            Assert.Equal(1068702, AiVisibleParams.Of(("a", "1"), ("b", " two")).GetHashCode());
            Assert.Equal(0, AiVisibleParams.Empty.GetHashCode());
        }

        [Fact]
        public void ParamsToStringRendersTheKotlinMapShape()
        {
            // Probe: AiVisibleParams({a=1, b= two}) — Kotlin Map.toString format in
            // insertion order.
            Assert.Equal("AiVisibleParams({a=1, b= two})", AiVisibleParams.Of(("a", "1"), ("b", " two")).ToString());
        }

        // ---------------------------------------------------------------------
        // AiCandidate
        // ---------------------------------------------------------------------

        private static AiRisk SampleRisk() => new AiRisk(0.25, 0.5);

        private static AiEffect SampleEffect() => new AiEffect(10, 1, 2, "note");

        private static AiCandidate SampleCandidate(
            string optionId = "opt",
            string capabilityId = "mahjong",
            AiPhase phase = AiPhase.OrdinaryAction,
            int cost = 0,
            AiRisk? risk = null,
            AiEffect? effect = null,
            bool terminal = true,
            AiVisibleParams? parameters = null,
            bool safeDefault = false)
        {
            return new AiCandidate(
                optionId, capabilityId, phase, cost,
                risk ?? SampleRisk(), effect ?? SampleEffect(),
                terminal, parameters, safeDefault);
        }

        [Fact]
        public void CandidateDefaultsMatchTheV4OptionalParameters()
        {
            AiCandidate candidate = SampleCandidate(parameters: null, safeDefault: false);

            Assert.Same(AiVisibleParams.Empty, candidate.Parameters);
            Assert.False(candidate.IsSafeDefault);
            Assert.Equal(AiPhase.OrdinaryAction, candidate.Phase);
        }

        [Fact]
        public void CandidateRejectsKotlinBlankOptionAndCapabilityIds()
        {
            // Probe: "", " " and NBSP all throw; the same blank set as keys/stamps.
            Assert.Throws<ArgumentException>(() => SampleCandidate(optionId: ""));
            Assert.Throws<ArgumentException>(() => SampleCandidate(optionId: " "));
            Assert.Throws<ArgumentException>(() => SampleCandidate(optionId: Nbsp));
            Assert.Throws<ArgumentException>(() => SampleCandidate(optionId: Fs));
            Assert.NotNull(SampleCandidate(optionId: Nel));

            Assert.Throws<ArgumentException>(() => SampleCandidate(capabilityId: ""));
            Assert.Throws<ArgumentException>(() => SampleCandidate(capabilityId: " "));
            Assert.Throws<ArgumentException>(() => SampleCandidate(capabilityId: Nbsp));
        }

        [Fact]
        public void CandidateRejectsNegativeCostAndAcceptsIntMaxValue()
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleCandidate(cost: -1));

            Assert.Equal("cost", exception.ParamName);
            Assert.Equal(int.MaxValue, SampleCandidate(cost: int.MaxValue).Cost);
        }

        [Fact]
        public void CandidateValidatesInTheV4InitOrder()
        {
            // optionId is required before capabilityId, capabilityId before cost.
            ArgumentException first = Assert.Throws<ArgumentException>(
                () => SampleCandidate(optionId: "", capabilityId: "", cost: -1));
            Assert.Equal("optionId", first.ParamName);

            ArgumentException second = Assert.Throws<ArgumentException>(
                () => SampleCandidate(optionId: "opt", capabilityId: "", cost: -1));
            Assert.Equal("capabilityId", second.ParamName);
        }

        [Fact]
        public void CandidateRejectsNullComponentsLikeTheKotlinIntrinsicChecks()
        {
            Assert.Throws<ArgumentNullException>(() => SampleCandidate(optionId: null!));
            Assert.Throws<ArgumentNullException>(() => SampleCandidate(capabilityId: null!));
            // risk/effect have no default in the V4 constructor: pass them straight
            // through so the intrinsic null check is what fires.
            Assert.Throws<ArgumentNullException>(
                () => new AiCandidate("opt", "cap", AiPhase.Idle, 0, null!, new AiEffect(), true));
            Assert.Throws<ArgumentNullException>(
                () => new AiCandidate("opt", "cap", AiPhase.Idle, 0, new AiRisk(), null!, true));
        }

        [Fact]
        public void CandidateEqualityCoversAllNineFields()
        {
            AiCandidate baseline = SampleCandidate();

            Assert.True(baseline.Equals(SampleCandidate()));
            Assert.False(baseline.Equals(SampleCandidate(optionId: "opt2")));
            Assert.False(baseline.Equals(SampleCandidate(capabilityId: "mahjong2")));
            Assert.False(baseline.Equals(SampleCandidate(phase: AiPhase.OfferChoice)));
            Assert.False(baseline.Equals(SampleCandidate(cost: 5)));
            // Probe: bitwise risk equality inside the candidate distinguishes -0.0.
            Assert.False(baseline.Equals(SampleCandidate(risk: new AiRisk(-0.0, 0.5))));
            Assert.False(baseline.Equals(SampleCandidate(effect: new AiEffect(10, 1, 2, "note2"))));
            Assert.False(baseline.Equals(SampleCandidate(terminal: false)));
            Assert.False(baseline.Equals(SampleCandidate(parameters: AiVisibleParams.Of(("a", "1")))));
            Assert.False(baseline.Equals(SampleCandidate(safeDefault: true)));
            Assert.False(baseline.Equals(null));
            Assert.False(baseline.Equals("not a candidate"));
        }

        [Fact]
        public void CandidateParameterEqualityIgnoresMapIterationOrder()
        {
            AiCandidate first = SampleCandidate(
                parameters: AiVisibleParams.Of(("a", "1"), ("b", "2")));
            AiCandidate second = SampleCandidate(
                effect: new AiEffect(10, 1, 2, "note"),
                parameters: AiVisibleParams.Of(("b", "2"), ("a", "1")));

            Assert.True(first.Equals(second));
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
        }

        [Fact]
        public void CandidateOperatorsTrackEqualsIncludingNulls()
        {
            AiCandidate candidate = SampleCandidate();

            Assert.True(candidate == SampleCandidate());
            Assert.True(candidate != SampleCandidate(cost: 1));
            Assert.True((AiCandidate?)null == (AiCandidate?)null);
            Assert.False(candidate == null);
        }

        // ---------------------------------------------------------------------
        // AiDecisionStamp
        // ---------------------------------------------------------------------

        private static AiDecisionStamp SampleStamp(
            int seat = 1,
            string sessionId = "session",
            string? roundId = "round",
            string? decisionId = "decision",
            long controlEpoch = 5,
            AiPhase phase = AiPhase.OrdinaryAction,
            long stateVersion = 7,
            string? offerId = "offer",
            string? windowId = "window",
            long? deadlineMillis = 123)
        {
            return new AiDecisionStamp(
                seat, sessionId, roundId, decisionId, controlEpoch,
                phase, stateVersion, offerId, windowId, deadlineMillis);
        }

        [Fact]
        public void StampAcceptsAllNullIdentityFieldsLikeV4()
        {
            // Probe: a fully anonymous IDLE stamp constructs; only sessionId is
            // validated, so the remaining identity strings may be null or empty.
            AiDecisionStamp stamp = SampleStamp(
                seat: 0, roundId: null, decisionId: null, offerId: null,
                windowId: null, deadlineMillis: null, controlEpoch: 0,
                phase: AiPhase.Idle, stateVersion: 0);

            Assert.Null(stamp.RoundId);
            Assert.Null(stamp.DecisionId);
            Assert.Null(stamp.OfferId);
            Assert.Null(stamp.WindowId);
            Assert.Null(stamp.DeadlineMillis);
        }

        [Fact]
        public void StampLeavesNonSessionIdentityStringsUnvalidated()
        {
            // Probe: blank roundId is accepted — V4 validates only sessionId.
            Assert.NotNull(SampleStamp(roundId: ""));
            Assert.NotNull(SampleStamp(decisionId: ""));
            Assert.NotNull(SampleStamp(offerId: ""));
            Assert.NotNull(SampleStamp(windowId: ""));
        }

        [Fact]
        public void StampRejectsSeatsOutsideZeroToThree()
        {
            ArgumentOutOfRangeException low = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(seat: -1));
            Assert.Equal("seat", low.ParamName);

            Assert.Throws<ArgumentOutOfRangeException>(() => SampleStamp(seat: 4));
            for (int seat = 0; seat <= 3; seat++)
            {
                Assert.Equal(seat, SampleStamp(seat: seat).Seat);
            }
        }

        [Fact]
        public void StampAppliesTheKotlinNotBlankRuleToSessionId()
        {
            // Probe: "", " ", NBSP and 0x001C are blank and rejected; U+0085 is a
            // legal session id because it is not Kotlin whitespace.
            Assert.Throws<ArgumentException>(() => SampleStamp(sessionId: ""));
            Assert.Throws<ArgumentException>(() => SampleStamp(sessionId: " "));
            Assert.Throws<ArgumentException>(() => SampleStamp(sessionId: Nbsp));
            Assert.Throws<ArgumentException>(() => SampleStamp(sessionId: Fs));
            ArgumentException tabbed = Assert.Throws<ArgumentException>(
                () => SampleStamp(sessionId: "\t"));
            Assert.Equal("sessionId", tabbed.ParamName);
            Assert.NotNull(SampleStamp(sessionId: Nel));
        }

        [Fact]
        public void StampRejectsNegativeEpochsVersionsAndDeadlines()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(controlEpoch: -1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(stateVersion: -1));
            ArgumentOutOfRangeException deadline = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(deadlineMillis: -1));
            Assert.Equal("deadlineMillis", deadline.ParamName);

            // Probe: zero values and Long.MaxValue all construct.
            Assert.NotNull(SampleStamp(controlEpoch: 0, stateVersion: 0, deadlineMillis: 0));
            Assert.NotNull(SampleStamp(
                controlEpoch: long.MaxValue, stateVersion: long.MaxValue,
                deadlineMillis: long.MaxValue));
        }

        [Fact]
        public void StampValidatesInTheV4InitOrder()
        {
            // seat, then sessionId, then controlEpoch, then stateVersion, then deadline.
            ArgumentOutOfRangeException seatFirst = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(seat: 9, sessionId: ""));
            Assert.Equal("seat", seatFirst.ParamName);

            ArgumentException sessionSecond = Assert.Throws<ArgumentException>(
                () => SampleStamp(sessionId: " ", controlEpoch: -1));
            Assert.Equal("sessionId", sessionSecond.ParamName);

            ArgumentOutOfRangeException epochThird = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(controlEpoch: -1, stateVersion: -1));
            Assert.Equal("controlEpoch", epochThird.ParamName);

            ArgumentOutOfRangeException versionFourth = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleStamp(stateVersion: -1, deadlineMillis: -1));
            Assert.Equal("stateVersion", versionFourth.ParamName);
        }

        [Fact]
        public void StampEqualityCoversAllTenFieldsIncludingNullVersusSet()
        {
            AiDecisionStamp baseline = SampleStamp();

            Assert.True(baseline.Equals(SampleStamp()));
            Assert.False(baseline.Equals(SampleStamp(seat: 2)));
            Assert.False(baseline.Equals(SampleStamp(sessionId: "session2")));
            Assert.False(baseline.Equals(SampleStamp(roundId: null)));
            Assert.False(baseline.Equals(SampleStamp(decisionId: "decision2")));
            Assert.False(baseline.Equals(SampleStamp(controlEpoch: 6)));
            Assert.False(baseline.Equals(SampleStamp(phase: AiPhase.OfferChoice)));
            Assert.False(baseline.Equals(SampleStamp(stateVersion: 8)));
            Assert.False(baseline.Equals(SampleStamp(offerId: "offer2")));
            Assert.False(baseline.Equals(SampleStamp(windowId: "window2")));
            Assert.False(baseline.Equals(SampleStamp(deadlineMillis: 124)));
            Assert.False(baseline.Equals(SampleStamp(offerId: null)));
            Assert.False(baseline.Equals(SampleStamp(windowId: null)));
            Assert.False(baseline.Equals(SampleStamp(deadlineMillis: null)));
            Assert.False(baseline.Equals(null));
        }

        [Fact]
        public void StampIdentityIsCaseSensitiveAndHashConsistent()
        {
            AiDecisionStamp stamp = SampleStamp();

            Assert.False(stamp.Equals(SampleStamp(sessionId: "Session")));
            Assert.Equal(stamp.GetHashCode(), SampleStamp().GetHashCode());
            Assert.True(stamp == SampleStamp());
            Assert.True(stamp != SampleStamp(seat: 0));
        }

        // ---------------------------------------------------------------------
        // AiCandidateLimits
        // ---------------------------------------------------------------------

        private static AiCandidateLimits SampleLimits(
            IEnumerable<string>? ids = null,
            int maxSpend = 5,
            int remaining = 3,
            int reserve = 2,
            int candidateLimit = 6,
            double riskTolerance = 0.5)
        {
            return new AiCandidateLimits(
                ids ?? new[] { "b", "a" }, maxSpend, remaining, reserve,
                candidateLimit, riskTolerance);
        }

        [Fact]
        public void LimitsCopyDedupeAndOrderEnabledIdsLikeALinkedHashSet()
        {
            // Probe: source [b, a, b] stores [b, a] in first-occurrence order.
            AiCandidateLimits limits = SampleLimits(ids: new[] { "b", "a", "b" });

            Assert.Equal(new[] { "b", "a" }, limits.EnabledCapabilityIds);
            Assert.Equal(2, limits.EnabledCapabilityIds.Count);
        }

        [Fact]
        public void LimitsEnabledIdsAreDefensivelyCopied()
        {
            var source = new List<string> { "k" };
            AiCandidateLimits limits = SampleLimits(ids: source);

            source.Add("post");

            Assert.Equal(new[] { "k" }, limits.EnabledCapabilityIds);
            Assert.False(limits.Allows("post"));
        }

        [Fact]
        public void LimitsEnabledIdsViewCannotBeCastBackToAWritableCollection()
        {
            AiCandidateLimits limits = SampleLimits(ids: new[] { "a" });

            Assert.Null(limits.EnabledCapabilityIds as ICollection<string>);
            Assert.Null(limits.EnabledCapabilityIds as IList<string>);
            Assert.Throws<InvalidCastException>(
                () => (string[])limits.EnabledCapabilityIds);

            // The read-only surface still answers indexing and enumeration.
            Assert.Equal("a", limits.EnabledCapabilityIds[0]);
        }

        [Fact]
        public void AllowsUsesOrdinalCaseSensitiveMembership()
        {
            AiCandidateLimits limits = SampleLimits(ids: new[] { "tile_mark" });

            Assert.True(limits.Allows("tile_mark"));
            Assert.False(limits.Allows("TILE_MARK"));
            Assert.False(limits.Allows("beam_swap"));
            Assert.Throws<ArgumentNullException>(() => limits.Allows(null!));
        }

        [Fact]
        public void EmptyCapabilitySetAllowsNothing()
        {
            // Probe: all-zero budgets with no enabled ids allow nothing and spend 0.
            AiCandidateLimits limits = SampleLimits(
                ids: Array.Empty<string>(), maxSpend: 0, remaining: 0, reserve: 0);

            Assert.Empty(limits.EnabledCapabilityIds);
            Assert.False(limits.Allows("mahjong"));
            Assert.Equal(0, limits.SpendablePoints);
        }

        [Fact]
        public void SpendablePointsIsMaxOfZeroAndMinOfMaxSpendAndRemainingMinusReserve()
        {
            // Probe matrix, including the integer extremes.
            Assert.Equal(1, SampleLimits(maxSpend: 5, remaining: 3, reserve: 2).SpendablePoints);
            Assert.Equal(0, SampleLimits(maxSpend: 5, remaining: 2, reserve: 5).SpendablePoints);
            Assert.Equal(0, SampleLimits(maxSpend: 0, remaining: 0, reserve: 0).SpendablePoints);
            Assert.Equal(
                int.MaxValue,
                SampleLimits(maxSpend: int.MaxValue, remaining: int.MaxValue, reserve: 0).SpendablePoints);
            Assert.Equal(
                0,
                SampleLimits(maxSpend: 5, remaining: int.MaxValue, reserve: int.MaxValue).SpendablePoints);
            Assert.Equal(7, SampleLimits(maxSpend: 10, remaining: 7, reserve: 0).SpendablePoints);
        }

        [Fact]
        public void CanSpendRequiresNonNegativeCostWithinSpendablePoints()
        {
            AiCandidateLimits empty = SampleLimits(maxSpend: 5, remaining: 2, reserve: 5);
            AiCandidateLimits one = SampleLimits(maxSpend: 5, remaining: 3, reserve: 2);

            Assert.True(empty.CanSpend(0)); // probe: cost 0 is always affordable
            Assert.False(empty.CanSpend(-1));
            Assert.False(empty.CanSpend(1));
            Assert.True(one.CanSpend(1));
            Assert.False(one.CanSpend(2));
        }

        [Fact]
        public void LimitsAreIdentityComparedLikeTheNonDataClassTheyPort()
        {
            // Probe: two content-identical instances are NOT equal in V4 — the class
            // deliberately has no data-class equality. No overrides exist here either.
            AiCandidateLimits first = SampleLimits();
            AiCandidateLimits second = SampleLimits();

            Assert.False(first.Equals(second));
            Assert.False(first == second as object);
            Assert.True(first.Equals(first));
            Assert.Same(first, first);
        }

        [Fact]
        public void LimitsAcceptTheInclusiveRiskToleranceBoundariesIncludingNegativeZero()
        {
            Assert.Equal(1.0, SampleLimits(riskTolerance: 1.0).RiskTolerance);
            Assert.Equal(0.0, SampleLimits(riskTolerance: 0.0).RiskTolerance);

            // Probe: -0.0 constructs because -0.0 >= 0.0; the port stores the same
            // negative zero bit pattern the JVM field would hold.
            AiCandidateLimits negativeZero = SampleLimits(riskTolerance: -0.0);
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(negativeZero.RiskTolerance));
        }

        [Fact]
        public void LimitsRejectNaNInfinitiesAndOutOfRangeRiskTolerance()
        {
            // Probe: NaN and both infinities fail the range require; so does anything
            // beyond [0, 1].
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(riskTolerance: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(riskTolerance: double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(riskTolerance: double.NegativeInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(riskTolerance: -0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(riskTolerance: NextUp(1.0)));
        }

        [Fact]
        public void LimitsEnforceTheOneToSixtyFourCandidateRange()
        {
            Assert.Equal(1, SampleLimits(candidateLimit: 1).CandidateLimit);
            Assert.Equal(64, SampleLimits(candidateLimit: 64).CandidateLimit);
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(candidateLimit: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(candidateLimit: 65));
        }

        [Fact]
        public void LimitsRejectNegativeBudgets()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(maxSpend: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(remaining: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SampleLimits(reserve: -1));
        }

        [Fact]
        public void LimitsValidateInTheV4InitOrder()
        {
            // maxSpend, remainingPoints, reservePoints, candidateLimit, riskTolerance.
            ArgumentOutOfRangeException spend = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleLimits(maxSpend: -1, remaining: -1, reserve: -1, candidateLimit: 0, riskTolerance: double.NaN));
            Assert.Equal("maxSpend", spend.ParamName);

            ArgumentOutOfRangeException remaining = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleLimits(remaining: -1, reserve: -1, candidateLimit: 0, riskTolerance: double.NaN));
            Assert.Equal("remainingPoints", remaining.ParamName);

            ArgumentOutOfRangeException reserve = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleLimits(reserve: -1, candidateLimit: 0, riskTolerance: double.NaN));
            Assert.Equal("reservePoints", reserve.ParamName);

            ArgumentOutOfRangeException candidate = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleLimits(candidateLimit: 0, riskTolerance: double.NaN));
            Assert.Equal("candidateLimit", candidate.ParamName);

            ArgumentOutOfRangeException risk = Assert.Throws<ArgumentOutOfRangeException>(
                () => SampleLimits(riskTolerance: double.NaN));
            Assert.Equal("riskTolerance", risk.ParamName);
        }

        [Fact]
        public void LimitsRejectNullIdSource()
        {
            // Straight through to the constructor: the sample helper would coalesce
            // the null into a default id set.
            Assert.Throws<ArgumentNullException>(
                () => new AiCandidateLimits(null!, 5, 3, 2, 6, 0.5));
        }

        [Fact]
        public void LimitsToStringIsDiagnosticOnly()
        {
            // V4 has no data-class toString for limits; the port renders a JVM-style
            // identity header plus the frozen scalars.
            string text = SampleLimits(maxSpend: 5).ToString();

            Assert.StartsWith("AiCandidateLimits@", text);
            Assert.Contains("maxSpend=5", text);
            Assert.Contains("candidateLimit=6", text);
        }

        // ---------------------------------------------------------------------
        // Cross-model: the heuristic doubles never leak into identity surfaces
        // ---------------------------------------------------------------------

        [Fact]
        public void CandidateHashIsConsistentAcrossEqualInstancesAndProcesses()
        {
            // Equal candidates hash equally (in-process contract), and the Java-parity
            // hash is stable across processes because it never touches randomized
            // runtime hashing. It remains diagnostic metadata, never an authority
            // digest — the authoritative identity stays with CommandIntent SHA-256.
            AiCandidate candidate = SampleCandidate();

            Assert.Equal(candidate.GetHashCode(), SampleCandidate().GetHashCode());
            Assert.Equal(
                SampleCandidate(parameters: AiVisibleParams.Of(("a", "1"), ("b", "2"))).GetHashCode(),
                SampleCandidate(parameters: AiVisibleParams.Of(("b", "2"), ("a", "1"))).GetHashCode());
        }

        private static string[] Keys(AiVisibleParams parameters)
        {
            var keys = new List<string>();
            foreach (string key in parameters.Values.Keys)
            {
                keys.Add(key);
            }

            return keys.ToArray();
        }

        private static string Repeat(string value, int count)
        {
            var builder = new System.Text.StringBuilder(value.Length * count);
            for (int i = 0; i < count; i++)
            {
                builder.Append(value);
            }

            return builder.ToString();
        }
    }
}

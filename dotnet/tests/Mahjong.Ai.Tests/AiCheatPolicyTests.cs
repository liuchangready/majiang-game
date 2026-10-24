using System;
using System.Collections.Generic;
using Mahjong.Ai;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Behaviour matrix for the ENG-019 port of V4 <c>AiCheatPolicy.kt</c>.
    ///
    /// The matrix is not a property-readback mirror: every group exercises an observable
    /// behaviour the V4 source actually has — the master switch that no configuration can
    /// bypass, the intersection direction of every scalar in
    /// <see cref="AiCheatPolicy.RestrictWith"/> (including the java.lang.Math.min signed
    /// zero for the risk tolerance), the union-of-keys toggle merge, Kotlin
    /// <c>LinkedHashMap</c> iteration order, the V4 Kotlin blank-id rule on both entry
    /// points, the intrinsic null check on <c>allows</c>, Kotlin data-class equality
    /// (including the bitwise <c>Double.equals</c> treatment of -0.0) and the
    /// defensive-copy guarantee that V4 call sites had to maintain by hand when copying
    /// <c>LinkedHashMap</c> references.
    /// </summary>
    public class AiCheatPolicyTests
    {
        // ---------------------------------------------------------------------
        // Defaults and the master switch
        // ---------------------------------------------------------------------

        [Fact]
        public void ParameterlessPolicyMatchesTheV4Defaults()
        {
            AiCheatPolicy policy = new AiCheatPolicy();

            Assert.False(policy.Enabled);
            Assert.Equal(60, policy.MaxSpendPerRound);
            Assert.Equal(0, policy.ReservePoints);
            Assert.Equal(0.5, policy.RiskTolerance);
            Assert.Equal(6, policy.CandidateLimit);
            Assert.False(policy.TakeoverMaySpend);
            Assert.Empty(policy.CapabilityToggles);
        }

        [Fact]
        public void DisabledMasterSwitchGatesEveryCapabilityNoMatterHowTogglesAreSet()
        {
            // The bypass attempt: every per-capability toggle opened, a generous budget
            // and takeover opt-in still yield no allowed capability.
            AiCheatPolicy policy = new AiCheatPolicy(
                enabled: false,
                maxSpendPerRound: int.MaxValue,
                reservePoints: 0,
                riskTolerance: 1.0,
                candidateLimit: 64,
                takeoverMaySpend: true,
                capabilityToggles: new Dictionary<string, bool>
                {
                    ["TILE_MARK"] = true,
                    ["SLEEVE_BLOOM"] = true,
                    ["BEAM_SWAP"] = true,
                    ["SHUFFLE_PREPARATION"] = true
                });

            Assert.True(policy.IsCapabilityToggled("TILE_MARK"));
            Assert.False(policy.Allows("TILE_MARK"));
            Assert.False(policy.Allows("SLEEVE_BLOOM"));
            Assert.False(policy.Allows("BEAM_SWAP"));
            Assert.False(policy.Allows("SHUFFLE_PREPARATION"));
            // Even an id that was never mentioned stays gated by the master switch.
            Assert.False(policy.Allows("unknown_capability"));
        }

        [Fact]
        public void WithCapabilityCannotReviveADisabledMasterSwitch()
        {
            AiCheatPolicy disabled = new AiCheatPolicy(enabled: false);
            AiCheatPolicy opened = disabled.WithCapability("TILE_MARK", allowed: true);

            Assert.True(opened.IsCapabilityToggled("TILE_MARK"));
            Assert.False(opened.Allows("TILE_MARK"));
        }

        [Fact]
        public void RestrictWithCannotReviveADisabledMasterSwitchInEitherDirection()
        {
            AiCheatPolicy disabled = new AiCheatPolicy(enabled: false, maxSpendPerRound: 60);
            AiCheatPolicy fullyOpen = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: int.MaxValue,
                takeoverMaySpend: true,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = true });

            Assert.False(disabled.RestrictWith(fullyOpen).Allows("TILE_MARK"));
            Assert.False(fullyOpen.RestrictWith(disabled).Allows("TILE_MARK"));
            Assert.False(disabled.RestrictWith(fullyOpen).Enabled);
        }

        // ---------------------------------------------------------------------
        // Takeover budgeting
        // ---------------------------------------------------------------------

        [Fact]
        public void TakeoverSpendingIsForbiddenByDefault()
        {
            Assert.False(new AiCheatPolicy().TakeoverMaySpend);
            Assert.False(new AiCheatPolicy(enabled: true, maxSpendPerRound: 60).TakeoverMaySpend);
        }

        [Fact]
        public void TakeoverMaySpendRequiresBothSidesOfTheIntersection()
        {
            AiCheatPolicy optedIn = new AiCheatPolicy(enabled: true, takeoverMaySpend: true);
            AiCheatPolicy takeoverForbidden = new AiCheatPolicy(enabled: true);

            Assert.False(optedIn.RestrictWith(takeoverForbidden).TakeoverMaySpend);
            Assert.False(takeoverForbidden.RestrictWith(optedIn).TakeoverMaySpend);
            Assert.True(optedIn.RestrictWith(optedIn).TakeoverMaySpend);
        }

        // ---------------------------------------------------------------------
        // Validation (V4 init block order and boundaries)
        // ---------------------------------------------------------------------

        [Fact]
        public void RejectsNegativeMaxSpendPerRound()
        {
            ArgumentException exception = Assert.ThrowsAny<ArgumentException>(
                () => new AiCheatPolicy(maxSpendPerRound: -1));
            Assert.Equal("maxSpendPerRound", exception.ParamName);
        }

        [Fact]
        public void RejectsNegativeReservePoints()
        {
            ArgumentException exception = Assert.ThrowsAny<ArgumentException>(
                () => new AiCheatPolicy(reservePoints: -1));
            Assert.Equal("reservePoints", exception.ParamName);
        }

        [Fact]
        public void RejectsNaNAndBothInfinitiesAsRiskTolerance()
        {
            double[] rejected = { double.NaN, double.PositiveInfinity, double.NegativeInfinity };
            foreach (double risk in rejected)
            {
                ArgumentException exception = Assert.ThrowsAny<ArgumentException>(
                    () => new AiCheatPolicy(riskTolerance: risk));
                Assert.Equal("riskTolerance", exception.ParamName);
                ArgumentOutOfRangeException range = Assert.IsType<ArgumentOutOfRangeException>(exception);
                double actual = Assert.IsType<double>(range.ActualValue);
                Assert.True(double.IsNaN(actual) || double.IsInfinity(actual));
            }
        }

        [Fact]
        public void RejectsOutOfRangeRiskTolerance()
        {
            double[] rejected = { 1.0000000000000002, 2.0, -1e-9, -0.0001 };
            foreach (double risk in rejected)
            {
                ArgumentException exception = Assert.ThrowsAny<ArgumentException>(
                    () => new AiCheatPolicy(riskTolerance: risk));
                Assert.Equal("riskTolerance", exception.ParamName);
            }
        }

        [Fact]
        public void AcceptsInclusiveRiskToleranceBoundaries()
        {
            // 0.0 and 1.0 are legal in V4 (closed range), as is -0.0 because the range
            // check compares numerically and -0.0 < 0.0 is false.
            double[] accepted = { 0.0, 1.0, 0.25, 0.75, -0.0 };
            foreach (double risk in accepted)
            {
                Assert.True(new AiCheatPolicy(riskTolerance: risk).RiskTolerance.Equals(risk));
            }
        }

        [Fact]
        public void RejectsCandidateLimitZeroSixtyFiveAndBeyond()
        {
            int[] rejected = { 0, 65, 100, -3 };
            foreach (int limit in rejected)
            {
                ArgumentException exception = Assert.ThrowsAny<ArgumentException>(
                    () => new AiCheatPolicy(candidateLimit: limit));
                Assert.Equal("candidateLimit", exception.ParamName);
            }
        }

        [Fact]
        public void AcceptsCandidateLimitBoundariesOneAndSixtyFour()
        {
            Assert.Equal(1, new AiCheatPolicy(candidateLimit: 1).CandidateLimit);
            Assert.Equal(64, new AiCheatPolicy(candidateLimit: 64).CandidateLimit);
        }

        [Fact]
        public void RejectsBlankCapabilityToggleKeys()
        {
            string[] blank = { "", " ", "\t", " \t\n" };
            foreach (string key in blank)
            {
                ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                    capabilityToggles: new Dictionary<string, bool> { [key] = false }));
                Assert.Equal("capabilityToggles", exception.ParamName);
            }

            // A blank key must be rejected even when the toggle value would be true.
            Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                capabilityToggles: new Dictionary<string, bool> { [""] = true }));

            // Java-only whitespace 0x001C..0x001F is blank for V4 (Char.isWhitespace)
            // although .NET does not classify it as whitespace (caller D-1-03); both
            // entry points must reject it like any other blank id.
            for (int code = 0x1C; code <= 0x1F; code++)
            {
                string key = ((char)code).ToString();
                Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                    capabilityToggles: new Dictionary<string, bool> { [key] = false }));
                ArgumentException viaWith = Assert.ThrowsAny<ArgumentException>(
                    () => new AiCheatPolicy().WithCapability(key, allowed: true));
                Assert.Equal("capabilityId", viaWith.ParamName);
            }
        }

        [Fact]
        public void KotlinBlankRuleMatchesTheV4WhitespaceSetOnBothEntryPoints()
        {
            // Caller parity probe D-1-03: V4's isBlank() requires every char to satisfy
            // Char.isWhitespace() = Character.isWhitespace(c) || Character.isSpaceChar(c).
            // That set differs from .NET char.IsWhiteSpace in exactly two BMP places:
            // 0x001C..0x001F are Java-only whitespace (V4 blank, .NET not), while 0x0085
            // (next line) is neither Java whitespace nor a space char (V4 not blank,
            // .NET whitespace). NBSP U+00A0, figure space U+2007 and narrow no-break
            // space U+202F are isSpaceChar on both sides and stay blank/rejected.
            int[] rejectedCodes =
            {
                0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20,               // basic whitespace
                0x1C, 0x1D, 0x1E, 0x1F,                           // Java-only separators
                0xA0, 0x1680, 0x2000, 0x2007, 0x200A, 0x202F,     // space chars (isSpaceChar)
                0x2028, 0x2029, 0x205F, 0x3000                    // line/para/ideographic space
            };
            foreach (int code in rejectedCodes)
            {
                string key = ((char)code).ToString();
                Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                    capabilityToggles: new Dictionary<string, bool> { [key] = true }));
                ArgumentException viaWith = Assert.ThrowsAny<ArgumentException>(
                    () => new AiCheatPolicy().WithCapability(key, allowed: true));
                Assert.Equal("capabilityId", viaWith.ParamName);
            }

            // The whole U+2000..U+200A general-punctuation block is isSpaceChar for
            // Kotlin (the probe's blankBMP lists every member) — swept, not sampled.
            for (int code = 0x2000; code <= 0x200A; code++)
            {
                Assert.ThrowsAny<ArgumentException>(
                    () => new AiCheatPolicy().WithCapability(((char)code).ToString(), allowed: true));
            }

            // 0x0085 is accepted as a stable id on both entry points, even with value
            // false, and keeps behaving as an ordinary ordinal key afterwards.
            string nextLine = ((char)0x85).ToString();
            AiCheatPolicy fromCtor = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { [nextLine] = false });
            Assert.False(fromCtor.IsCapabilityToggled(nextLine));
            Assert.False(fromCtor.Allows(nextLine));
            AiCheatPolicy fromWithCapability = new AiCheatPolicy(enabled: true)
                .WithCapability(nextLine, allowed: false);
            Assert.False(fromWithCapability.Allows(nextLine));

            // A V4-blank id must be rejected even when the toggle value would be true.
            Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                capabilityToggles: new Dictionary<string, bool> { [((char)0x1C).ToString()] = true }));

            // Mixed strings follow the same per-char rule: a mix containing the
            // Java-only 0x001C is V4-blank even though .NET sees visible content, and a
            // mix containing 0x0085 is NOT V4-blank even though .NET sees whitespace.
            string javaOnlyMix = new string(new[] { (char)0x1C, (char)0x20, (char)0x1F });
            Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                capabilityToggles: new Dictionary<string, bool> { [javaOnlyMix] = true }));
            Assert.ThrowsAny<ArgumentException>(
                () => new AiCheatPolicy().WithCapability(javaOnlyMix, allowed: true));

            string nextLineMix = new string(new[] { (char)0x85, (char)0x20, (char)0x0A });
            AiCheatPolicy keptMix = new AiCheatPolicy(enabled: true)
                .WithCapability(nextLineMix, allowed: false);
            Assert.False(keptMix.IsCapabilityToggled(nextLineMix));

            // The 0x0085 id survives a RestrictWith union like any other stable id.
            AiCheatPolicy merged = fromCtor.RestrictWith(fromWithCapability);
            Assert.False(merged.IsCapabilityToggled(nextLine));
            Assert.Contains(nextLine, merged.CapabilityToggles.Keys);
            Assert.True(merged.IsCapabilityToggled("absent_id"));

            // Visible content is never blank on either side.
            foreach (string visible in new[] { "TILE_MARK", " cap", "cap ", "0", "." })
            {
                _ = new AiCheatPolicy(capabilityToggles: new Dictionary<string, bool> { [visible] = true });
                _ = new AiCheatPolicy().WithCapability(visible, allowed: true);
            }
        }

        [Fact]
        public void FirstInvalidRuleWinsInTheV4InitOrder()
        {
            // Kotlin validates maxSpendPerRound, reservePoints, riskTolerance,
            // candidateLimit, then toggle keys. The first violation wins even when later
            // ones are also present.
            ArgumentException first = Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                maxSpendPerRound: -1,
                candidateLimit: 0,
                capabilityToggles: new Dictionary<string, bool> { [""] = false }));
            Assert.Equal("maxSpendPerRound", first.ParamName);

            ArgumentException second = Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                reservePoints: -1,
                candidateLimit: 0));
            Assert.Equal("reservePoints", second.ParamName);

            ArgumentException third = Assert.ThrowsAny<ArgumentException>(() => new AiCheatPolicy(
                riskTolerance: double.NaN,
                candidateLimit: 0));
            Assert.Equal("riskTolerance", third.ParamName);
        }

        // ---------------------------------------------------------------------
        // Capability toggles and Allows
        // ---------------------------------------------------------------------

        [Fact]
        public void UnknownCapabilityIdsDefaultToEnabled()
        {
            AiCheatPolicy policy = new AiCheatPolicy(capabilityToggles: new Dictionary<string, bool>
            {
                ["TILE_MARK"] = false
            });

            // Absent ids are enabled even on a policy that disables another capability.
            Assert.True(policy.IsCapabilityToggled("SLEEVE_BLOOM"));
            Assert.True(policy.IsCapabilityToggled("never_registered_capability"));

            // V4 has no blank-check on this path: a blank id is simply not a key.
            Assert.True(policy.IsCapabilityToggled(""));

            // An absent id therefore behaves like an explicit true.
            AiCheatPolicy enabled = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["never_registered_capability"] = true });
            Assert.True(enabled.Allows("never_registered_capability"));
        }

        [Fact]
        public void ToggleIdsAreComparedOrdinallyAndCaseSensitively()
        {
            AiCheatPolicy policy = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["tilemark"] = false });

            Assert.False(policy.IsCapabilityToggled("tilemark"));
            Assert.True(policy.IsCapabilityToggled("TileMark"));
            Assert.True(policy.IsCapabilityToggled("TILEMARK"));
            Assert.True(policy.Allows("TileMark"));
            Assert.False(policy.Allows("tilemark"));

            // Key case also separates otherwise identical policies in equality.
            AiCheatPolicy upper = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["TileMark"] = false });
            Assert.NotEqual(policy, upper);
        }

        [Fact]
        public void NullCapabilityIdIsRejectedLikeTheKotlinIntrinsicNullCheck()
        {
            AiCheatPolicy policy = new AiCheatPolicy();
            Assert.Throws<ArgumentNullException>(() => policy.IsCapabilityToggled(null!));

            // Kotlin inserts the intrinsic parameter null check at the method entry,
            // BEFORE the body and therefore before the `enabled &&` short-circuit:
            // Allows(null) throws in BOTH master switch polarities (verified against V4
            // by compiling and running the real AiCheatPolicy.kt, caller D-1-02).
            Assert.Throws<ArgumentNullException>(() => new AiCheatPolicy(enabled: false).Allows(null!));
            Assert.Throws<ArgumentNullException>(() => new AiCheatPolicy(enabled: true).Allows(null!));

            // The short-circuit only governs valid ids on a disabled policy.
            Assert.False(new AiCheatPolicy(enabled: false).Allows("TILE_MARK"));
        }

        [Fact]
        public void AllowsRequiresMasterSwitchAndToggleTogether()
        {
            AiCheatPolicy enabledToggleOff = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = false });
            Assert.False(enabledToggleOff.Allows("TILE_MARK"));

            AiCheatPolicy enabledToggleOn = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = true });
            Assert.True(enabledToggleOn.Allows("TILE_MARK"));
        }

        // ---------------------------------------------------------------------
        // WithCapability
        // ---------------------------------------------------------------------

        [Fact]
        public void WithCapabilityReturnsANewInstanceAndNeverTouchesTheOriginal()
        {
            AiCheatPolicy original = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = true });

            AiCheatPolicy updated = original.WithCapability("SLEEVE_BLOOM", allowed: false);

            Assert.NotSame(original, updated);
            Assert.True(original.Allows("SLEEVE_BLOOM"));
            Assert.False(updated.Allows("SLEEVE_BLOOM"));
            Assert.True(updated.Allows("TILE_MARK"));
            Assert.Single(original.CapabilityToggles);
            Assert.Equal(2, updated.CapabilityToggles.Count);
            Assert.True(original.Equals(Copy(original)));
        }

        [Fact]
        public void WithCapabilityPreservesInsertionOrderLikeALinkedHashMap()
        {
            AiCheatPolicy original = new AiCheatPolicy(capabilityToggles: new Dictionary<string, bool>
            {
                ["a"] = true,
                ["b"] = false
            });

            // Replacing an existing key keeps its position (V4 LinkedHashMap.put).
            AiCheatPolicy replaced = original.WithCapability("a", allowed: false);
            Assert.Equal(new[] { "a", "b" }, CollectionKeys(replaced));
            Assert.False(replaced.IsCapabilityToggled("a"));

            // A new key is appended at the end.
            AiCheatPolicy appended = original.WithCapability("c", allowed: true);
            Assert.Equal(new[] { "a", "b", "c" }, CollectionKeys(appended));
        }

        [Fact]
        public void WithCapabilityChainsAccumulateAndKeepEveryOtherScalar()
        {
            AiCheatPolicy chained = new AiCheatPolicy(
                    enabled: true,
                    maxSpendPerRound: 30,
                    reservePoints: 5,
                    riskTolerance: 0.25,
                    candidateLimit: 9,
                    takeoverMaySpend: true)
                .WithCapability("TILE_MARK", allowed: false)
                .WithCapability("BEAM_SWAP", allowed: false);

            Assert.True(chained.Enabled);
            Assert.Equal(30, chained.MaxSpendPerRound);
            Assert.Equal(5, chained.ReservePoints);
            Assert.Equal(0.25, chained.RiskTolerance);
            Assert.Equal(9, chained.CandidateLimit);
            Assert.True(chained.TakeoverMaySpend);
            Assert.False(chained.Allows("TILE_MARK"));
            Assert.False(chained.Allows("BEAM_SWAP"));
            Assert.True(chained.Allows("SLEEVE_BLOOM"));
        }

        [Fact]
        public void WithCapabilityToTheSameValueYieldsAnEqualButDistinctInstance()
        {
            AiCheatPolicy original = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["a"] = true });

            AiCheatPolicy rewritten = original.WithCapability("a", allowed: true);

            // Kotlin copy() semantics: a new instance that still equals the original.
            Assert.NotSame(original, rewritten);
            Assert.True(original.Equals(rewritten));
            Assert.Equal(original.GetHashCode(), rewritten.GetHashCode());
        }

        [Fact]
        public void WithCapabilityRejectsBlankIds()
        {
            AiCheatPolicy policy = new AiCheatPolicy();

            Assert.Throws<ArgumentNullException>(() => policy.WithCapability(null!, allowed: true));
            ArgumentException empty = Assert.ThrowsAny<ArgumentException>(
                () => policy.WithCapability("", allowed: true));
            Assert.Equal("capabilityId", empty.ParamName);
            ArgumentException blank = Assert.ThrowsAny<ArgumentException>(
                () => policy.WithCapability("   ", allowed: false));
            Assert.Equal("capabilityId", blank.ParamName);
        }

        // ---------------------------------------------------------------------
        // RestrictWith
        // ---------------------------------------------------------------------

        [Fact]
        public void RestrictWithNullReturnsTheSameInstance()
        {
            AiCheatPolicy policy = new AiCheatPolicy(enabled: true);
            Assert.Same(policy, policy.RestrictWith(null));
        }

        [Fact]
        public void RestrictWithItselfYieldsAnEqualButDistinctInstance()
        {
            AiCheatPolicy policy = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 42,
                reservePoints: 7,
                riskTolerance: 0.75,
                candidateLimit: 11,
                takeoverMaySpend: true,
                capabilityToggles: new Dictionary<string, bool> { ["a"] = false, ["b"] = true });

            AiCheatPolicy restricted = policy.RestrictWith(policy);

            Assert.NotSame(policy, restricted);
            Assert.True(policy.Equals(restricted));
            Assert.Equal(policy.GetHashCode(), restricted.GetHashCode());
        }

        [Fact]
        public void RestrictWithIntersectsEveryScalarInTheDocumentedDirection()
        {
            AiCheatPolicy room = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 60,
                reservePoints: 0,
                riskTolerance: 0.9,
                candidateLimit: 64,
                takeoverMaySpend: true);
            AiCheatPolicy strategy = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 10,
                reservePoints: 45,
                riskTolerance: 0.2,
                candidateLimit: 6);

            AiCheatPolicy merged = room.RestrictWith(strategy);

            Assert.True(merged.Enabled);
            Assert.Equal(10, merged.MaxSpendPerRound);
            Assert.Equal(45, merged.ReservePoints);
            Assert.Equal(0.2, merged.RiskTolerance);
            Assert.Equal(6, merged.CandidateLimit);
            Assert.False(merged.TakeoverMaySpend);
        }

        [Fact]
        public void BudgetAndCandidateTakeTheMinReserveTakesTheMaxRegardlessOfDirection()
        {
            AiCheatPolicy small = new AiCheatPolicy(
                maxSpendPerRound: 5, reservePoints: 45, candidateLimit: 1, riskTolerance: 0.1);
            AiCheatPolicy large = new AiCheatPolicy(
                maxSpendPerRound: 70, reservePoints: 3, candidateLimit: 64, riskTolerance: 0.95);

            AiCheatPolicy leftFirst = small.RestrictWith(large);
            AiCheatPolicy rightFirst = large.RestrictWith(small);

            Assert.Equal(5, leftFirst.MaxSpendPerRound);
            Assert.Equal(5, rightFirst.MaxSpendPerRound);
            Assert.Equal(45, leftFirst.ReservePoints);
            Assert.Equal(45, rightFirst.ReservePoints);
            Assert.Equal(1, leftFirst.CandidateLimit);
            Assert.Equal(1, rightFirst.CandidateLimit);
            Assert.Equal(0.1, leftFirst.RiskTolerance);
            Assert.Equal(0.1, rightFirst.RiskTolerance);
        }

        [Fact]
        public void RestrictWithRiskZeroSignFollowsJavaLangMathMin()
        {
            // Kotlin minOf(Double, Double) delegates to java.lang.Math.min: when both
            // operands are zero the negative zero wins REGARDLESS of argument order
            // (caller parity probe D-1-01). A naive tie-keeps-left port would produce
            // +0.0 for one of the two mixed orders. Modern .NET Math.Min also selects
            // negative zero; these assertions verify the explicit portable rule.
            AiCheatPolicy positive = new AiCheatPolicy(riskTolerance: 0.0);
            AiCheatPolicy negative = new AiCheatPolicy(riskTolerance: -0.0);

            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(positive.RestrictWith(negative).RiskTolerance));
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(negative.RestrictWith(positive).RiskTolerance));
            Assert.Equal(0L, BitConverter.DoubleToInt64Bits(positive.RestrictWith(positive).RiskTolerance));
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(negative.RestrictWith(negative).RiskTolerance));

            // The two mixed orders agree bitwise (data-class equality is commutative in
            // value), and either mixed result is a distinct policy from (+0,+0).
            Assert.True(positive.RestrictWith(negative).Equals(negative.RestrictWith(positive)));
            Assert.False(positive.RestrictWith(negative).Equals(positive.RestrictWith(positive)));
            Assert.NotEqual(
                positive.RestrictWith(negative).GetHashCode(),
                positive.RestrictWith(positive).GetHashCode());

            // When zero is the numeric minimum its sign survives the merge; when a
            // non-zero risk is smaller, the zero sign is irrelevant to the value.
            AiCheatPolicy lower = new AiCheatPolicy(riskTolerance: 0.25);
            Assert.Equal(0L, BitConverter.DoubleToInt64Bits(positive.RestrictWith(lower).RiskTolerance));
            Assert.Equal(0L, BitConverter.DoubleToInt64Bits(lower.RestrictWith(positive).RiskTolerance));
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(negative.RestrictWith(lower).RiskTolerance));
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(lower.RestrictWith(negative).RiskTolerance));
        }

        [Fact]
        public void RestrictWithUnionsToggleKeysAndPreservesUnknownStableIds()
        {
            AiCheatPolicy room = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool>
                {
                    ["TILE_MARK"] = false,
                    ["custom_cap_v9"] = true
                });
            AiCheatPolicy strategy = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool>
                {
                    ["SLEEVE_BLOOM"] = false,
                    ["custom_cap_v9"] = false
                });

            AiCheatPolicy merged = room.RestrictWith(strategy);

            // The union contains exactly the keys of both sides — unknown-but-present
            // stable ids survive the merge with their AND-ed value.
            Assert.Equal(
                new HashSet<string> { "TILE_MARK", "custom_cap_v9", "SLEEVE_BLOOM" },
                new HashSet<string>(merged.CapabilityToggles.Keys));
            Assert.False(merged.IsCapabilityToggled("TILE_MARK"));
            Assert.False(merged.IsCapabilityToggled("SLEEVE_BLOOM"));
            Assert.False(merged.IsCapabilityToggled("custom_cap_v9"));

            // A capability named on neither side stays absent from the map and enabled.
            Assert.False(merged.CapabilityToggles.ContainsKey("BEAM_SWAP"));
            Assert.True(merged.IsCapabilityToggled("BEAM_SWAP"));
        }

        [Fact]
        public void RestrictWithToggleValuesAreAndedAcrossBothSides()
        {
            AiCheatPolicy off = new AiCheatPolicy(
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = false });
            AiCheatPolicy on = new AiCheatPolicy(
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = true });
            AiCheatPolicy silent = new AiCheatPolicy();

            // A missing key counts as enabled, so only one side can forbid.
            Assert.False(off.RestrictWith(silent).IsCapabilityToggled("TILE_MARK"));
            Assert.False(silent.RestrictWith(off).IsCapabilityToggled("TILE_MARK"));
            Assert.False(off.RestrictWith(on).IsCapabilityToggled("TILE_MARK"));
            Assert.False(on.RestrictWith(off).IsCapabilityToggled("TILE_MARK"));
            Assert.True(on.RestrictWith(on).IsCapabilityToggled("TILE_MARK"));
            Assert.True(silent.RestrictWith(silent).IsCapabilityToggled("TILE_MARK"));
        }

        [Fact]
        public void RestrictWithUnionIteratesLeftKeysBeforeNewRightKeys()
        {
            AiCheatPolicy left = new AiCheatPolicy(capabilityToggles: new Dictionary<string, bool>
            {
                ["a"] = true,
                ["c"] = false
            });
            AiCheatPolicy right = new AiCheatPolicy(capabilityToggles: new Dictionary<string, bool>
            {
                ["c"] = true,
                ["b"] = false
            });

            // V4 builds a LinkedHashSet of the left keys, then appends the right keys it
            // has not seen — the iteration order of the merged map is [a, c, b].
            Assert.Equal(new[] { "a", "c", "b" }, CollectionKeys(left.RestrictWith(right)));
        }

        [Fact]
        public void RestrictWithIsCommutativeInValueButNotInIterationOrder()
        {
            AiCheatPolicy left = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 30,
                reservePoints: 10,
                riskTolerance: 0.7,
                candidateLimit: 20,
                capabilityToggles: new Dictionary<string, bool> { ["a"] = false, ["b"] = true });
            AiCheatPolicy right = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 15,
                reservePoints: 25,
                riskTolerance: 0.4,
                candidateLimit: 8,
                capabilityToggles: new Dictionary<string, bool> { ["b"] = false, ["a"] = true });

            AiCheatPolicy leftFirst = left.RestrictWith(right);
            AiCheatPolicy rightFirst = right.RestrictWith(left);

            // Content equality ignores iteration order (Kotlin Map.equals).
            Assert.True(leftFirst.Equals(rightFirst));
            // …but the insertion order follows the merge direction.
            Assert.Equal(new[] { "a", "b" }, CollectionKeys(leftFirst));
            Assert.Equal(new[] { "b", "a" }, CollectionKeys(rightFirst));
        }

        [Fact]
        public void RestrictWithIsAssociative()
        {
            AiCheatPolicy a = new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 60, reservePoints: 0,
                riskTolerance: 0.9, candidateLimit: 64, takeoverMaySpend: true,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = true, ["x"] = false });
            AiCheatPolicy b = new AiCheatPolicy(
                enabled: false, maxSpendPerRound: 15, reservePoints: 5,
                riskTolerance: 0.3, candidateLimit: 6,
                capabilityToggles: new Dictionary<string, bool> { ["SLEEVE_BLOOM"] = false });
            AiCheatPolicy c = new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 40, reservePoints: 12,
                riskTolerance: 0.6, candidateLimit: 10, takeoverMaySpend: true,
                capabilityToggles: new Dictionary<string, bool> { ["x"] = true, ["y"] = false });

            AiCheatPolicy leftGrouped = a.RestrictWith(b).RestrictWith(c);
            AiCheatPolicy rightGrouped = a.RestrictWith(b.RestrictWith(c));

            Assert.True(leftGrouped.Equals(rightGrouped));
            Assert.False(leftGrouped.Enabled);
            Assert.Equal(15, leftGrouped.MaxSpendPerRound);
            Assert.Equal(12, leftGrouped.ReservePoints);
            Assert.Equal(0.3, leftGrouped.RiskTolerance);
            Assert.Equal(6, leftGrouped.CandidateLimit);
        }

        [Fact]
        public void RestrictedPolicyIsNeverMorePermissiveThanEitherInput()
        {
            // Monotonicity over a fixed policy set: for every pair and every probed
            // capability id (present on both, one or neither side), the intersection
            // allows exactly what both inputs allow, and every scalar bound shrinks.
            AiCheatPolicy[] policies =
            {
                new AiCheatPolicy(),
                new AiCheatPolicy(enabled: true),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 10, candidateLimit: 1),
                new AiCheatPolicy(
                    enabled: true,
                    maxSpendPerRound: 25,
                    reservePoints: 20,
                    riskTolerance: 0.8,
                    candidateLimit: 32,
                    takeoverMaySpend: true,
                    capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = false }),
                new AiCheatPolicy(
                    enabled: false,
                    maxSpendPerRound: 70,
                    reservePoints: 3,
                    riskTolerance: 0.2,
                    capabilityToggles: new Dictionary<string, bool> { ["SLEEVE_BLOOM"] = false }),
                new AiCheatPolicy(
                    enabled: true,
                    riskTolerance: 1.0,
                    capabilityToggles: new Dictionary<string, bool> { ["custom_cap_v9"] = false })
            };
            string[] capabilityIds = { "TILE_MARK", "SLEEVE_BLOOM", "BEAM_SWAP", "custom_cap_v9", "absent_id" };

            foreach (AiCheatPolicy left in policies)
            {
                foreach (AiCheatPolicy right in policies)
                {
                    AiCheatPolicy merged = left.RestrictWith(right);

                    Assert.True(merged.MaxSpendPerRound <= Math.Min(left.MaxSpendPerRound, right.MaxSpendPerRound));
                    Assert.True(merged.ReservePoints >= Math.Max(left.ReservePoints, right.ReservePoints));
                    Assert.True(merged.RiskTolerance <= Math.Min(left.RiskTolerance, right.RiskTolerance));
                    Assert.True(merged.CandidateLimit <= Math.Min(left.CandidateLimit, right.CandidateLimit));
                    Assert.False(merged.Enabled && !(left.Enabled && right.Enabled));
                    Assert.False(merged.TakeoverMaySpend && !(left.TakeoverMaySpend && right.TakeoverMaySpend));

                    foreach (string capabilityId in capabilityIds)
                    {
                        Assert.Equal(
                            left.Allows(capabilityId) && right.Allows(capabilityId),
                            merged.Allows(capabilityId));
                    }
                }
            }
        }

        [Fact]
        public void RestrictWithMutatesNeitherInput()
        {
            AiCheatPolicy left = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 20,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = true });
            AiCheatPolicy right = new AiCheatPolicy(
                maxSpendPerRound: 5,
                reservePoints: 2,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = false });

            AiCheatPolicy leftSnapshot = Copy(left);
            AiCheatPolicy rightSnapshot = Copy(right);
            left.RestrictWith(right);
            right.RestrictWith(left);

            // Both inputs still describe exactly what they described before the merge.
            Assert.True(left.Equals(leftSnapshot));
            Assert.True(right.Equals(rightSnapshot));
            Assert.Single(left.CapabilityToggles);
            Assert.True(left.IsCapabilityToggled("TILE_MARK"));
            Assert.Single(right.CapabilityToggles);
            Assert.False(right.IsCapabilityToggled("TILE_MARK"));
        }

        // ---------------------------------------------------------------------
        // Defensive copies
        // ---------------------------------------------------------------------

        [Fact]
        public void ConstructorCopiesTheSourceDictionary()
        {
            var source = new Dictionary<string, bool> { ["TILE_MARK"] = false };
            AiCheatPolicy policy = new AiCheatPolicy(enabled: true, capabilityToggles: source);

            // Mutate the caller's map after construction: the policy must not notice.
            source["TILE_MARK"] = true;
            source["SLEEVE_BLOOM"] = true;
            source.Remove("TILE_MARK");

            Assert.Single(policy.CapabilityToggles);
            Assert.False(policy.IsCapabilityToggled("TILE_MARK"));
            Assert.False(policy.CapabilityToggles.ContainsKey("SLEEVE_BLOOM"));
            Assert.False(policy.Allows("TILE_MARK"));
            Assert.True(policy.Allows("SLEEVE_BLOOM"));
        }

        [Fact]
        public void ExposedToggleViewCannotBeCastBackToAWritableDictionary()
        {
            AiCheatPolicy policy = new AiCheatPolicy(
                capabilityToggles: new Dictionary<string, bool> { ["a"] = true });

            Assert.Null(policy.CapabilityToggles as IDictionary<string, bool>);
            Assert.Null(policy.CapabilityToggles as System.Collections.IDictionary);
            Assert.Throws<InvalidCastException>(
                () => (Dictionary<string, bool>)policy.CapabilityToggles);

            // The read-only surface still answers lookups and enumeration.
            Assert.True(policy.CapabilityToggles.ContainsKey("a"));
            Assert.True(policy.CapabilityToggles.TryGetValue("a", out bool value) && value);
            Assert.Equal(new[] { "a" }, CollectionKeys(policy));
        }

        // ---------------------------------------------------------------------
        // Equality semantics (Kotlin data class)
        // ---------------------------------------------------------------------

        [Fact]
        public void EqualityCoversEveryScalarField()
        {
            AiCheatPolicy baseline = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 30,
                reservePoints: 5,
                riskTolerance: 0.25,
                candidateLimit: 9,
                takeoverMaySpend: true,
                capabilityToggles: new Dictionary<string, bool> { ["a"] = true });

            Assert.NotEqual(baseline, Copy(baseline, enabled: false));
            Assert.NotEqual(baseline, Copy(baseline, maxSpendPerRound: 31));
            Assert.NotEqual(baseline, Copy(baseline, reservePoints: 6));
            Assert.NotEqual(baseline, Copy(baseline, riskTolerance: 0.5));
            Assert.NotEqual(baseline, Copy(baseline, candidateLimit: 10));
            Assert.NotEqual(baseline, Copy(baseline, takeoverMaySpend: false));
            Assert.NotEqual(baseline, Copy(baseline, capabilityToggles: ToggleMap(("a", false))));
            Assert.NotEqual(
                baseline,
                Copy(baseline, capabilityToggles: ToggleMap(("a", true), ("b", false))));
        }

        [Fact]
        public void ToggleEqualityIsContentBasedAndIgnoresIterationOrder()
        {
            AiCheatPolicy first = new AiCheatPolicy(capabilityToggles: ToggleMap(("a", true), ("b", false)));
            AiCheatPolicy second = new AiCheatPolicy(capabilityToggles: ToggleMap(("b", false), ("a", true)));

            Assert.Equal(new[] { "a", "b" }, CollectionKeys(first));
            Assert.Equal(new[] { "b", "a" }, CollectionKeys(second));
            Assert.True(first.Equals(second));
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
        }

        [Fact]
        public void NegativeZeroRiskToleranceDiffersFromZeroInEqualityLikeKotlin()
        {
            // Kotlin data-class equality uses Double.equals, which compares bit patterns:
            // -0.0 is a legal, distinct policy from 0.0 even though both pass the range
            // check and compare numerically equal.
            AiCheatPolicy positiveZero = new AiCheatPolicy(riskTolerance: 0.0);
            AiCheatPolicy negativeZero = new AiCheatPolicy(riskTolerance: -0.0);

            Assert.True(positiveZero.RiskTolerance == negativeZero.RiskTolerance);
            Assert.False(positiveZero.Equals(negativeZero));
            Assert.True(positiveZero != negativeZero);
            Assert.True(positiveZero.Equals(new AiCheatPolicy(riskTolerance: 0.0)));
        }

        [Fact]
        public void EqualityOperatorsTrackEqualsIncludingNulls()
        {
            AiCheatPolicy policy = new AiCheatPolicy(enabled: true);
            AiCheatPolicy copy = new AiCheatPolicy(enabled: true);
            AiCheatPolicy other = new AiCheatPolicy(enabled: false);

            Assert.True(policy == copy);
            Assert.False(policy != copy);
            Assert.True(policy != other);
            Assert.True((AiCheatPolicy?)null == (AiCheatPolicy?)null);
            Assert.False(null == policy);
            Assert.False(policy == null);
            Assert.True(policy.Equals((object?)policy));
            Assert.False(policy.Equals((object?)"not a policy"));
            Assert.False(policy.Equals(null));
            Assert.NotEqual(policy.GetHashCode(), other.GetHashCode());
        }

        // ---------------------------------------------------------------------
        // Diagnostic surface
        // ---------------------------------------------------------------------

        [Fact]
        public void ToStringIsDeterministicAndRendersTheKotlinDataClassShape()
        {
            AiCheatPolicy policy = new AiCheatPolicy(
                enabled: true,
                capabilityToggles: new Dictionary<string, bool> { ["TILE_MARK"] = false });

            string text = policy.ToString();
            Assert.Equal(text, policy.ToString());
            Assert.Contains("enabled=true", text);
            Assert.Contains("maxSpendPerRound=60", text);
            Assert.Contains("reservePoints=0", text);
            Assert.Contains("riskTolerance=0.5", text);
            Assert.Contains("candidateLimit=6", text);
            Assert.Contains("takeoverMaySpend=false", text);
            Assert.Contains("capabilityToggles={TILE_MARK=false}", text);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Rebuilds an equal-by-construction copy of a policy, optionally overriding
        /// single fields. Used to keep the assertions honest: every comparison is between
        /// two independently constructed instances, never an identity check.
        /// </summary>
        private static AiCheatPolicy Copy(
            AiCheatPolicy policy,
            bool? enabled = null,
            int? maxSpendPerRound = null,
            int? reservePoints = null,
            double? riskTolerance = null,
            int? candidateLimit = null,
            bool? takeoverMaySpend = null,
            IReadOnlyDictionary<string, bool>? capabilityToggles = null)
        {
            return new AiCheatPolicy(
                enabled: enabled ?? policy.Enabled,
                maxSpendPerRound: maxSpendPerRound ?? policy.MaxSpendPerRound,
                reservePoints: reservePoints ?? policy.ReservePoints,
                riskTolerance: riskTolerance ?? policy.RiskTolerance,
                candidateLimit: candidateLimit ?? policy.CandidateLimit,
                takeoverMaySpend: takeoverMaySpend ?? policy.TakeoverMaySpend,
                capabilityToggles: capabilityToggles ?? Snapshot(policy.CapabilityToggles));
        }

        private static Dictionary<string, bool> ToggleMap(params (string Key, bool Value)[] entries)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach ((string key, bool value) in entries)
            {
                map.Add(key, value);
            }

            return map;
        }

        private static Dictionary<string, bool> Snapshot(IReadOnlyDictionary<string, bool> toggles)
        {
            var copy = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, bool> entry in toggles)
            {
                copy.Add(entry.Key, entry.Value);
            }

            return copy;
        }

        private static string[] CollectionKeys(AiCheatPolicy policy)
        {
            var keys = new List<string>();
            foreach (KeyValuePair<string, bool> entry in policy.CapabilityToggles)
            {
                keys.Add(entry.Key);
            }

            return keys.ToArray();
        }
    }
}

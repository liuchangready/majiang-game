using System;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class RoundReasonCodeMappingTests
    {
        [Fact]
        public void EveryDomainRejectionHasAStableNonInternalRuntimeReason()
        {
            foreach (RoundTransitionErrorCode code in Enum.GetValues(typeof(RoundTransitionErrorCode)))
            {
                if (code == RoundTransitionErrorCode.None) continue;

                string mapped = TableRuntimeReasonCode.FromDomain(code);
                Assert.NotEqual(TableRuntimeReasonCode.InternalError, mapped);
                Assert.False(string.IsNullOrWhiteSpace(mapped));
            }
        }

        [Theory]
        [InlineData(RoundTransitionErrorCode.InvalidMeld, "INVALID_MELD")]
        [InlineData(RoundTransitionErrorCode.MeldNotAllowed, "MELD_NOT_ALLOWED")]
        [InlineData(RoundTransitionErrorCode.RiichiAlreadyDeclared, "RIICHI_ALREADY_DECLARED")]
        [InlineData(RoundTransitionErrorCode.RiichiNotMenzen, "RIICHI_NOT_MENZEN")]
        [InlineData(RoundTransitionErrorCode.RiichiInsufficientPoints, "RIICHI_INSUFFICIENT_POINTS")]
        [InlineData(RoundTransitionErrorCode.RiichiNotTenpai, "RIICHI_NOT_TENPAI")]
        [InlineData(RoundTransitionErrorCode.RiichiWallTooShort, "RIICHI_WALL_TOO_SHORT")]
        [InlineData(RoundTransitionErrorCode.RiichiDiscardLocked, "RIICHI_DISCARD_LOCKED")]
        [InlineData(RoundTransitionErrorCode.KanLimitReached, "KAN_LIMIT_REACHED")]
        public void AddedDomainRejectionsKeepTheirStableRuntimeIdentity(
            RoundTransitionErrorCode source,
            string expected)
        {
            Assert.Equal(expected, TableRuntimeReasonCode.FromDomain(source));
        }
    }
}

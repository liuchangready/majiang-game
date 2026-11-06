using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain.Cheat;
using Mahjong.TableService;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// D-1-02: the wire's raw BEAM_SWAP target slot is resolved through the REAL trusted
    /// offer candidate context (the actor's own owner projection, mirrored into the
    /// connection's sink) — every namespace collision (dead-wall index and replacement index
    /// share small numbers by design) refuses with the explicit boundary error instead of a
    /// guessed target, and a resolved candidate keeps its REAL slot kind.
    /// </summary>
    public sealed class BeamSwapSlotResolutionTests
    {
        private static WireTableStreamSink SinkWith(params (int index, BeamSwapSlotKind kind)[] candidates)
        {
            var list = new List<TableBeamSwapCandidate>(candidates.Length);
            foreach ((int index, BeamSwapSlotKind kind) in candidates)
            {
                list.Add(new TableBeamSwapCandidate(kind, index));
            }

            var sink = new WireTableStreamSink(capacity: 4, maxQueuedBytes: 1, maxSnapshotBytes: 1024);
            sink.NoteBeamSwapOfferContext(new TableBeamSwapOfferContext(
                "offer-line-1",
                BeamSwapWindow.Wall,
                list));
            return sink;
        }

        private static BeamSwapSlotKind? LegacyResolvedKind(WireTableStreamSink sink, uint rawIndex)
        {
            TableBeamSwapSlotResolution? resolution = sink.ResolveBeamSwapTargetSlot(rawIndex);
            return resolution == null ? null : resolution.Status switch
            {
                TableBeamSwapResolutionStatus.Resolved when resolution.Candidate is TableBeamSwapCandidate candidate
                    => candidate.Kind,
                TableBeamSwapResolutionStatus.Ambiguous => null,
                _ => null
            };
        }

        [Fact]
        public void RawIndex0_WithReplacementCandidate_INTrustedContext_ResolvesReplacementNotDeadWall()
        {
            // The lineage from the round-2 probe: a replacement candidate at raw index 0 was
            // mapped to DeadWall(0) purely by the numeric dead-wall bound. With the trusted
            // offer context, the SAME raw index resolves to the ACTUAL candidate (Replacement).
            WireTableStreamSink sink = SinkWith(
                (0, BeamSwapSlotKind.ReplacementSlot),
                (1, BeamSwapSlotKind.ReplacementSlot));
            TableBeamSwapSlotResolution resolution = sink.ResolveBeamSwapTargetSlot(0)!;
            Assert.Equal(TableBeamSwapResolutionStatus.Resolved, resolution.Status);
            Assert.Equal(BeamSwapSlotKind.ReplacementSlot, resolution.Candidate!.Kind);
            Assert.NotEqual(BeamSwapSlotKind.DeadWallSlot, resolution.Candidate.Kind);
        }

        [Fact]
        public void IndexCollision_DeadWallVersusReplacement_AreExplicitlyAmbiguous()
        {
            WireTableStreamSink sink = SinkWith(
                (0, BeamSwapSlotKind.DeadWallSlot),
                (0, BeamSwapSlotKind.ReplacementSlot));
            TableBeamSwapSlotResolution resolution = sink.ResolveBeamSwapTargetSlot(0)!;
            Assert.Equal(TableBeamSwapResolutionStatus.Ambiguous, resolution.Status);
            Assert.False(
                TestedMappingNeverGuesses(resolution),
                "the colliding namespace block must fail closed, not guess either entity");
        }

        [Fact]
        public void EmptyContext_FailsClosed()
        {
            var sink = new WireTableStreamSink(capacity: 4, maxQueuedBytes: 1, maxSnapshotBytes: 1024);
            Assert.Null(sink.ResolveBeamSwapTargetSlot(0));
        }

        [Fact]
        public void UnknownIndex_IsRefused()
        {
            WireTableStreamSink sink = SinkWith((0, BeamSwapSlotKind.WallSlot));
            TableBeamSwapSlotResolution resolution = sink.ResolveBeamSwapTargetSlot(7)!;
            Assert.Equal(TableBeamSwapResolutionStatus.UnknownIndex, resolution.Status);
        }

        private static bool TestedMappingNeverGuesses(TableBeamSwapSlotResolution resolution)
        {
            return resolution.Candidate != null;
        }
    }
}

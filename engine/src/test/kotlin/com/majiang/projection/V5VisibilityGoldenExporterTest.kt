package com.majiang.projection

import com.majiang.access.ClockView
import com.majiang.economy.CheatLedgerEntry
import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.match.MatchRuleConfig
import com.majiang.match.SessionLifecycle
import com.majiang.match.SessionSnapshot
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Tile
import com.majiang.rules.DoraBreakdown
import com.majiang.rules.DoraIndicatorHit
import com.majiang.rules.FuBreakdown
import com.majiang.rules.FuLine
import com.majiang.rules.FuritenStatus
import com.majiang.rules.ScoreBreakdown
import com.majiang.rules.ScoreCalculator
import com.majiang.session.PlayerView
import com.majiang.session.SessionUpdate
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.security.MessageDigest
import java.util.Locale

/**
 * P0.1-a/P0.1-b visibility golden exporter (G-057a..G-057d).
 *
 * Default run (no export variable) executes every case through the real V4 public entry
 * points (PlayerView.fromGameState, SessionUpdate.filterVisibleEvents,
 * PublicProjectionFactory.project/publicEvents), asserts the captured behavior, and does
 * not write the committed fixture.
 *
 * Explicit export:
 *   $env:V5_VISIBILITY_GOLDEN_EXPORT='1'
 *   .\gradlew.bat :engine:test --tests "com.majiang.projection.V5VisibilityGoldenExporterTest" --rerun-tasks --console=plain
 *
 * Expected values are always the actual result of a V4 call. The exporter writes only
 * `tests/v5-golden/visibility-v1.json`; it does not touch the five accepted fixtures, the
 * manifest/catalog, V4 production code or C# production code.
 */
class V5VisibilityGoldenExporterTest {

    @Test
    fun exportVisibilityGoldenSamplesOrValidateInert() {
        val root = repoRoot()
        val first = buildCases()
        val second = buildCases()
        validateCases(first)

        // Two independent construction passes must serialize identically. This is the
        // determinism guard; no clock or implicit random is read anywhere.
        assertEquals(renderRoot(first, null, null), renderRoot(second, null, null))

        if (!exportEnabled()) {
            validateCommittedFixture(root, first)
            return
        }

        val commit = git(root, "rev-parse", "HEAD")
        val base = renderRoot(first, commit, null)
        assertEquals(base, renderRoot(first, commit, null), "second render with meta must be identical")
        val renderHash = sha256(base.toByteArray(StandardCharsets.UTF_8))
        val finalText = renderRoot(first, commit, renderHash)
        assertEquals(finalText, renderRoot(first, commit, renderHash), "second export render must be byte-identical")

        val fixture = root.resolve(FIXTURE_PATH)
        reportFixtureChange(fixture, base)
        Files.createDirectories(fixture.parent)
        Files.write(fixture, finalText.toByteArray(StandardCharsets.UTF_8))

        val bytes = Files.readAllBytes(fixture)
        val text = String(bytes, StandardCharsets.UTF_8)
        assertFalse(text.contains("\r"), "exported fixture must use LF line endings")
        assertTrue(text.endsWith("\n"), "exported fixture must end with LF")
        assertEquals(4, countOccurrences(text, "\"placements\""), "four states must export one placements array each")
        assertEquals(56, countOccurrences(text, "\"DEAD_WALL\""), "four states x 14 dead-wall rows")
        println("V5 visibility golden exported: cases=${first.size} bytes=${bytes.size} commit=$commit renderSha256=$renderHash")
    }

    // ------------------------------------------------------------------
    // Case construction
    // ------------------------------------------------------------------

    private fun buildCases(): List<Map<String, Any?>> = listOf(buildA(), buildB(), buildC(), buildD())

    private fun buildA(): Map<String, Any?> {
        val meldSource = mutableListOf(tile(0), tile(4), tile(8)) // MAN1..MAN3, copies 0
        val state = TestTableBuilder.create(
            melds = mapOf(1 to listOf(Meld(MeldType.CHI, meldSource, 0, meldSource[0]))),
            drawn = 0 to tile(13),
            discards = mapOf(
                0 to listOf(tile(20), tile(21)),
                1 to listOf(tile(22)),
                3 to listOf(tile(23))
            ),
            sleeves = mapOf(2 to listOf(tile(24))),
            deadWallPrefix = (25..38).map { tile(it) },
            wallPrefix = (39..48).map { tile(it) },
            phase = GamePhase.DISCARD,
            currentPlayer = 0,
            dealer = 0
        )
        state.doraIndicators.add(state.deadWall[4])
        state.uraDoraIndicators.add(state.deadWall[5])
        state.lastDiscard = state.players[0].discards.last()
        state.lastDiscardPlayer = 0

        val ponSource = mutableListOf(tile(60), tile(64))
        val action = GameAction.DeclarePon(0, ponSource)
        val view = PlayerView.fromGameState(
            state = state,
            viewerSeat = 0,
            legalActions = listOf(action),
            myFuriten = FuritenStatus(),
            myDiscardHistory = state.players[0].discards.toList()
        )
        val stateSnapshot = stateDoc(state, "round-vis-g057a")

        // Mutate the authoritative state and the producer-owned nested collections after the
        // snapshot was taken. The view must not drift.
        state.scores[0] = 99999
        state.players[0].closedTiles.clear()
        state.players[1].discards.clear()
        state.doraIndicators.clear()
        state.uraDoraIndicators.clear()
        state.sleeveTiles[2].clear()
        meldSource.clear()
        ponSource.clear()

        val snapshot = viewDoc(view)
        assertEquals(meldSource.size, 0)
        assertEquals(3, view.melds[1].single().tiles.size, "meld tile list must be detached from its source list")
        assertEquals(2, (view.legalActions.single() as GameAction.DeclarePon).tiles.size)
        assertEquals(25000, view.scores[0], "score snapshot must survive source mutation")

        return obj(
            "id" to "VIS-G057A-SNAPSHOT-COPY",
            "gRows" to listOf("G-057a"),
            "title" to "PlayerView deep snapshot survives later source-state, meld and legal-action mutation",
            "sourceAnchors" to listOf(
                "engine/src/main/kotlin/com/majiang/session/PlayerView.kt:73-149,155-205",
                "engine/src/test/kotlin/com/majiang/session/SessionSnapshotContractTest.kt:13-41"
            ),
            "input" to obj(
                "roundId" to "round-vis-g057a",
                "snapshotSeat" to 0,
                "legalActions" to listOf(obj("kind" to "DECLARE_PON", "seat" to 0, "tileIds" to listOf(60, 64))),
                "mutations" to listOf(
                    "state.scores[0]=99999",
                    "state.players[0].closedTiles.clear()",
                    "state.players[1].discards.clear()",
                    "state.doraIndicators.clear()",
                    "state.uraDoraIndicators.clear()",
                    "state.sleeveTiles[2].clear()",
                    "meldSource.clear()",
                    "ponSource.clear()"
                ),
                "clockUsed" to false,
                "implicitRandomUsed" to false,
                "state" to stateSnapshot
            ),
            "expected" to obj("snapshot" to snapshot),
            "sourceOnly" to obj(
                "legalActions" to "V4 PlayerView.legalActions is a V4 session-layer snapshot; V5 carries a P3.3 DecisionView supplied by the actor instead. The V4 legal-action deep copy is exercised here and is not re-derived in C#.",
                "meldSourceClearProof" to "View retained 3 meld tiles after the producer list was cleared."
            )
        )
    }

    private fun buildB(): Map<String, Any?> {
        val state = TestTableBuilder.create(phase = GamePhase.DISCARD, currentPlayer = 0, dealer = 0)
        val originals = listOf(
            GameEvent.TileDealt(3, 0, tile(30)),
            GameEvent.TileDrawn(4, 1, tile(31), 51),
            GameEvent.RinshanDraw(5, 2, tile(32)),
            GameEvent.DoraRevealed(6, tile(33), false),
            GameEvent.DoraRevealed(7, tile(34), true),
            GameEvent.TileDiscarded(8, 2, tile(35)),
            GameEvent.ChiDeclared(
                9,
                1,
                Meld(MeldType.CHI, listOf(tile(36), tile(40), tile(44)), 0, tile(36)),
                0
            )
        )
        val producer = originals.toMutableList()
        val view = PlayerView.fromGameState(state, 0, emptyList(), FuritenStatus(), emptyList())
        val update = SessionUpdate(view, producer)
        val updateBeforeClear = update.visibleEvents.map { eventDoc(it) }
        producer.clear()
        val updateAfterClear = update.visibleEvents.map { eventDoc(it) }
        assertEquals(updateBeforeClear, updateAfterClear, "SessionUpdate must not retain the producer list")
        assertEquals(originals.size, updateAfterClear.size)

        val unauthorized = (0..3).associate { seat ->
            seat.toString() to SessionUpdate.filterVisibleEvents(originals, seat).map { eventDoc(it) }
        }
        val authorizedInput = originals + GameEvent.RoundEnded(10, 1, 0)
        val authorized = (0..3).associate { seat ->
            seat.toString() to SessionUpdate.filterVisibleEvents(authorizedInput, seat).map { eventDoc(it) }
        }

        assertEquals(4, unauthorized.getValue("0").size, "seat0: own dealt + front dora + discard + chi")
        assertEquals(4, unauthorized.getValue("1").size, "seat1: own draw + front dora + discard + chi")
        assertFalse(unauthorized.getValue("0").any { it["kind"] == "DoraRevealed" && it["isUraDora"] == true })
        assertTrue(unauthorized.getValue("0").any { it["kind"] == "TileDiscarded" })
        assertTrue(authorized.getValue("3").any { it["kind"] == "DoraRevealed" && it["isUraDora"] == true })

        // Measured V4 privacy difference (VG-D01): the else branch of filterVisibleEvents
        // passes CheatPointsChanged to every seat. V5 classifies it private.
        val cheatLeak = (0..3).map { seat ->
            SessionUpdate.filterVisibleEvents(listOf(GameEvent.CheatPointsChanged(11, 0, -10, 990)), seat).size
        }
        assertEquals(listOf(1, 1, 1, 1), cheatLeak, "V4 measured cheat-points visibility")

        return obj(
            "id" to "VIS-G057B-EVENT-VISIBILITY",
            "gRows" to listOf("G-057b"),
            "title" to "SessionUpdate event copy and per-seat/ura visibility",
            "sourceAnchors" to listOf(
                "engine/src/main/kotlin/com/majiang/session/SessionUpdate.kt:14-24,66-93",
                "engine/src/test/kotlin/com/majiang/session/SessionSnapshotContractTest.kt:44-89"
            ),
            "input" to obj(
                "events" to originals.map { eventDoc(it) },
                "authorizedEvents" to authorizedInput.map { eventDoc(it) },
                "producerListClearedAfterUpdate" to true,
                "clockUsed" to false,
                "implicitRandomUsed" to false,
                "state" to stateDoc(state, "round-vis-g057b")
            ),
            "expected" to obj(
                "updateVisibleEventsAfterProducerClear" to updateAfterClear,
                "visibility" to obj(
                    "unauthorized" to unauthorized,
                    "settlementAuthorized" to authorized
                )
            ),
            "sourceOnly" to obj(
                "sessionUpdateWholesaleCopy" to "V4 SessionUpdate stores every event before filtering. V5 never hands an unfiltered event list to a client; TableEventProjection.ProjectMany returns only audience-visible events. The V4 copy result is recorded as evidence, not re-derived in C#.",
                "cheatPointsChanged" to "V4 SessionUpdate.filterVisibleEvents passes CheatPointsChanged through its else branch to every seat, while V5 classifies it private. This divergence is listed in knownDivergences and is not counted as aligned.",
                "v4CheatPointsChangedVisibilityCounts" to cheatLeak
            )
        )
    }

    private fun buildC(): Map<String, Any?> {
        val ponTiles = listOf(tile(88), tile(89), tile(90)) // PIN5 copies 0..2
        val state = TestTableBuilder.create(
            melds = mapOf(1 to listOf(Meld(MeldType.PON, ponTiles, 0, ponTiles[0]))),
            discards = mapOf(0 to listOf(tile(20)), 2 to listOf(tile(21))),
            deadWallPrefix = (25..38).map { tile(it) },
            phase = GamePhase.DISCARD,
            currentPlayer = 0,
            dealer = 0
        )
        state.doraIndicators.add(state.deadWall[4])
        state.uraDoraIndicators.add(state.deadWall[5])
        state.riichiDeclared[0] = true
        state.riichiSticks = 1
        state.lastDiscard = state.players[0].discards.last()
        state.lastDiscardPlayer = 0

        val mutableScores = mutableListOf(25000, 25000, 25000, 25000)
        val session = SessionSnapshot(
            sessionId = "vis-g057c-session",
            version = 42L,
            lifecycle = SessionLifecycle.ROUND_ACTIVE,
            ruleConfig = MatchRuleConfig(),
            currentDealer = 0,
            currentRoundWind = 1,
            currentRoundNumber = 0,
            currentHonba = 0,
            betweenRoundScores = mutableScores,
            betweenRoundRiichiSticks = 1,
            activeRoundId = "round-vis-g057c",
            roundRecords = emptyList(),
            canDealerDeclareAgariYame = false,
            result = null
        )
        val mutableClocks = mutableListOf(ClockView(0, 100L, 200L, 5000L, 100L))
        val clockDoc = obj(
            "seat" to mutableClocks.single().seat,
            "opensAt" to mutableClocks.single().opensAt,
            "deadline" to mutableClocks.single().deadline,
            "reserve" to mutableClocks.single().reserve,
            "serverNow" to mutableClocks.single().serverNow
        )
        val events = mutableListOf<GameEvent>(
            GameEvent.TileDealt(1, 1, tile(50)),
            GameEvent.TileDrawn(2, 1, tile(51), 50),
            GameEvent.RinshanDraw(3, 2, tile(52)),
            GameEvent.CheatPointsChanged(4, 0, -10, 990),
            GameEvent.TileDiscarded(5, 0, tile(53)),
            GameEvent.ChiDeclared(
                6,
                1,
                Meld(MeldType.CHI, listOf(tile(36), tile(40), tile(44)), 0, tile(36)),
                0
            ),
            GameEvent.DoraRevealed(7, state.doraIndicators.single(), false),
            GameEvent.DoraRevealed(8, state.uraDoraIndicators.single(), true),
            tsumoEvent()
        )
        // RoundEnded is a public settlement fact and is supplied in the same settlement batch.
        events.add(GameEvent.RoundEnded(10, 1, 0))
        val inputEvents = events.map { eventDoc(it, sourceAuthorization = true) }

        val unauthorized = PublicProjectionFactory.project(
            session = session,
            state = state,
            roundId = "round-vis-g057c",
            roundVersion = 5L,
            clocks = mutableClocks,
            paused = false,
            settlementRoundId = "round-vis-g057c",
            settlementEvents = events,
            revealSettlementUra = false
        )
        val authorized = PublicProjectionFactory.project(
            session = session,
            state = state,
            roundId = "round-vis-g057c",
            roundVersion = 5L,
            clocks = mutableClocks,
            paused = false,
            settlementRoundId = "round-vis-g057c",
            settlementEvents = events,
            revealSettlementUra = true
        )
        val board = unauthorized.board!!
        val boardAuthorized = authorized.board!!
        assertEquals(board, boardAuthorized, "ura authorization must not change the public board")

        val unauthorizedEvents = unauthorized.settlementEvents.map { eventDoc(it) }
        val authorizedEvents = authorized.settlementEvents.map { eventDoc(it) }
        assertFalse(unauthorizedEvents.any { it["isUraDora"] == true }, "unauthorized ura event must be dropped")
        assertTrue(authorizedEvents.any { it["isUraDora"] == true }, "authorized ura event must be kept")
        val unauthTsumo = unauthorized.settlementEvents.filterIsInstance<GameEvent.TsumoWin>().single()
        val authTsumo = authorized.settlementEvents.filterIsInstance<GameEvent.TsumoWin>().single()
        assertEquals(0, unauthTsumo.breakdown!!.dora.uraTiles.size)
        assertEquals(0, unauthTsumo.breakdown!!.dora.uraHits.size)
        assertEquals(1, authTsumo.breakdown!!.dora.uraTiles.size)
        assertEquals(1, authTsumo.breakdown!!.dora.uraHits.size)

        // Mutate every caller-owned input after both projections were built.
        mutableScores[0] = 99999
        mutableClocks.clear()
        events.clear()
        assertEquals(25000, unauthorized.session.scores[0], "projection must not retain caller scores")
        assertEquals(1, unauthorized.clocks.size, "projection must not retain caller clock list")
        assertEquals(unauthorizedEvents.size, unauthorized.settlementEvents.size)

        assertThrows<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (unauthorized.session.scores as MutableList<Int>)[0] = 1
        }

        val breakdownFalse = breakdownDoc(unauthTsumo.breakdown, false)
        val breakdownTrue = breakdownDoc(authTsumo.breakdown, true)

        return obj(
            "id" to "VIS-G057C-PUBLIC-PROJECTION",
            "gRows" to listOf("G-057c"),
            "title" to "PublicProjectionFactory whitelist, ura authorization and deep copy",
            "sourceAnchors" to listOf(
                "engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt:24-155",
                "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:482-790",
                "engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt:105-191,310-513"
            ),
            "input" to obj(
                "roundId" to "round-vis-g057c",
                "sessionId" to "vis-g057c-session",
                "sessionVersion" to 42,
                "roundVersion" to 5,
                "lifecycle" to "ROUND_ACTIVE",
                "settlementRoundId" to "round-vis-g057c",
                "clock" to obj("seat" to 0, "opensAt" to 100, "deadline" to 200, "reserve" to 5000, "serverNow" to 100),
                "events" to inputEvents,
                "clockUsed" to false,
                "implicitRandomUsed" to false,
                "state" to stateDoc(state, "round-vis-g057c")
            ),
            "expected" to obj(
                "board" to boardDoc(board),
                "clock" to clockDoc,
                "session" to obj(
                    "lifecycle" to unauthorized.session.lifecycle,
                    "scores" to unauthorized.session.scores,
                    "roundRecords" to unauthorized.session.roundRecords.size,
                    "finalResult" to (unauthorized.session.finalResult != null)
                ),
                "settlement" to obj(
                    "unauthorized" to unauthorizedEvents,
                    "authorized" to authorizedEvents
                ),
                "tsumoBreakdown" to obj(
                    "unauthorized" to breakdownFalse,
                    "authorized" to breakdownTrue
                ),
                "copyProbe" to obj(
                    "scoresAfterMutation" to unauthorized.session.scores,
                    "clocksAfterMutation" to unauthorized.clocks.size,
                    "settlementEventsAfterMutation" to unauthorized.settlementEvents.size
                )
            ),
            "sourceOnly" to obj(
                "tsumoBreakdown" to "V4 expected breakdown is the source value; the C# binding builds TableProjectionEventFacts from the raw input event and compares the mapped ProjectedEventPayload.Breakdown. The mapper does not auto-redact: the caller must pass uraAuthorized=false with no ura identities (raw identities are rejected), or explicitly authorize the raw breakdown.",
                "sessionSummary" to "V4 PublicSessionSummary carries lifecycle/scores/roundRecords/finalResult; V5 PublicSessionSummary carries finished/finishReason/finalScores/rankings. Only finished=false is comparable. See knownDivergences VG-D05."
            )
        )
    }

    private fun buildD(): Map<String, Any?> {
        val seat0Closed = (0..12).map { tile(it) }
        val drawn0 = tile(13)
        val seat1Closed = (14..22).map { tile(it) }
        val chiTiles = listOf(tile(72), tile(76), tile(80)) // PIN1..PIN3, copies 0
        val sleeve1 = tile(23)
        val seat2Closed = (24..36).map { tile(it) }
        val discards2 = listOf(tile(37), tile(38), tile(39))
        val seat3Closed = (40..52).map { tile(it) }
        val dead = (53..66).map { tile(it) }

        val state = TestTableBuilder.create(
            hands = mapOf(
                0 to seat0Closed,
                1 to seat1Closed,
                2 to seat2Closed,
                3 to seat3Closed
            ),
            drawn = 0 to drawn0,
            melds = mapOf(1 to listOf(Meld(MeldType.CHI, chiTiles, 0, chiTiles[0]))),
            discards = mapOf(2 to discards2),
            sleeves = mapOf(1 to listOf(sleeve1)),
            deadWallPrefix = dead,
            phase = GamePhase.DISCARD,
            currentPlayer = 0,
            dealer = 0
        )
        state.doraIndicators.add(state.deadWall[4])
        state.uraDoraIndicators.add(state.deadWall[5])
        for (seat in 0..3) {
            state.cheatPoints[seat] = 1000
        }
        state.cheatPoints[1] = 990
        state.cheatEntries.add(CheatLedgerEntry("vis-g057d-cheat-seat1", 1, -10, 990, "CHEAT_COST"))

        val views = (0..2).associate { seat ->
            seat.toString() to viewDoc(
                PlayerView.fromGameState(
                    state = state,
                    viewerSeat = seat,
                    legalActions = emptyList(),
                    myFuriten = FuritenStatus(),
                    myDiscardHistory = state.players[seat].discardHistory.toList()
                )
            )
        }
        val session = SessionSnapshot(
            sessionId = "vis-g057d-session",
            version = 7L,
            lifecycle = SessionLifecycle.ROUND_ACTIVE,
            ruleConfig = MatchRuleConfig(),
            currentDealer = 0,
            currentRoundWind = 1,
            currentRoundNumber = 0,
            currentHonba = 0,
            betweenRoundScores = state.scores.toList(),
            betweenRoundRiichiSticks = 0,
            activeRoundId = "round-vis-g057d",
            roundRecords = emptyList(),
            canDealerDeclareAgariYame = false,
            result = null
        )
        val spectator = PublicProjectionFactory.project(
            session = session,
            state = state,
            roundId = "round-vis-g057d",
            roundVersion = 1L,
            clocks = emptyList(),
            paused = false
        )
        val spectatorBoard = spectator.board!!
        assertEquals(listOf(14, 9, 13, 13), spectatorBoard.handTileCounts)
        assertEquals(0, spectatorBoard.discards[1].size)
        assertEquals(3, spectatorBoard.discards[2].size)

        val wallOrder = state.wall.map { it.tileId }
        val hidden = obj(
            "opponentClosedFromSeat0" to (seat1Closed.map { it.tileId } + sleeve1.tileId),
            "wallOrder" to wallOrder,
            "uraDoraIndicators" to listOf(state.uraDoraIndicators.single().tileId),
            "unrevealedDeadWall" to dead.filterIndexed { index, _ -> index != 4 }.map { it.tileId },
            "privateCheatEntryIds" to listOf("vis-g057d-cheat-seat1"),
            "privateCheatBalances" to listOf(990)
        )
        val board = boardDoc(spectatorBoard)

        val visibleSeat0 = collectViewTileIds(views.getValue("0"))
        val visibleSpectator = collectBoardTileIds(board)
        for (id in listOf(seat1Closed.map { it.tileId }.first(), sleeve1.tileId)) {
            assertFalse(visibleSeat0.contains(id), "seat0 view leaked hidden tile $id")
            assertFalse(visibleSpectator.contains(id), "spectator board leaked hidden tile $id")
        }
        for (id in wallOrder) {
            assertFalse(visibleSeat0.contains(id), "seat0 view leaked wall tile $id")
            assertFalse(visibleSpectator.contains(id), "spectator board leaked wall tile $id")
        }
        assertFalse(visibleSeat0.contains(state.uraDoraIndicators.single().tileId))
        assertTrue(visibleSeat0.containsAll(seat0Closed.map { it.tileId }))
        assertTrue(collectViewTileIds(views.getValue("1")).containsAll(seat1Closed.map { it.tileId }))

        return obj(
            "id" to "VIS-G057D-SEAT-VIEW-ISOLATION",
            "gRows" to listOf("G-057d"),
            "title" to "Real 136-entity state: owner/other-seat/spectator values, hand counts only, wall/ura/cheat hidden",
            "sourceAnchors" to listOf(
                "engine/src/main/kotlin/com/majiang/session/PlayerView.kt:12-45,151-205",
                "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:482-506",
                "engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt:9-104"
            ),
            "input" to obj(
                "roundId" to "round-vis-g057d",
                "viewerSeats" to listOf(0, 1, 2),
                "spectator" to true,
                "clockUsed" to false,
                "implicitRandomUsed" to false,
                "state" to stateDoc(state, "round-vis-g057d")
            ),
            "expected" to obj(
                "seatViews" to views,
                "spectatorBoard" to board,
                "hiddenTileIds" to hidden
            ),
            "sourceOnly" to obj(
                "v4WallPointer" to state.wallPointer,
                "v4WallEndExclusive" to state.wallEndExclusive,
                "v4DeadWall" to state.deadWall.map { it.tileId },
                "v4RinshanPointer" to state.rinshanPointer,
                "drawSource" to "V4 does not persist a draw source; the C# binding uses DEAL_EXTRA for the dealer's 14th tile and WALL otherwise. The source is not compared.",
                "discardFlags" to "V4 GameState does not persist per-discard turnNumber/riichiDiscard/tsumogiri. V5 DiscardView is built with turnNumber=river index and both flags false; only TileId and order are compared."
            )
        )
    }

    // ------------------------------------------------------------------
    // Real V4 result capture
    // ------------------------------------------------------------------

    private fun tsumoEvent(): GameEvent.TsumoWin {
        val ura = tile(34)
        val breakdown = sampleScoreBreakdown(
            uraTiles = listOf(ura),
            uraHits = listOf(DoraIndicatorHit(ura, tile(35), listOf(tile(35))))
        )
        return GameEvent.TsumoWin(
            turnNumber = 9,
            player = 0,
            winTile = tile(34),
            yakuList = listOf("RIICHI", "TANYAO"),
            han = 3,
            fu = 30,
            score = 4000,
            breakdown = breakdown
        )
    }

    private fun sampleScoreBreakdown(
        uraTiles: List<Tile>,
        uraHits: List<DoraIndicatorHit>
    ): ScoreBreakdown {
        val scoreResult = ScoreCalculator.INSTANCE.calculate(fu = 30, han = 3)
        return ScoreBreakdown(
            yakuLines = listOf("RIICHI" to 1, "TANYAO" to 1),
            yakumanCount = 0,
            dora = DoraBreakdown(
                indicatorCount = 1,
                uraCount = if (uraTiles.isNotEmpty()) 1 else 0,
                redCount = 0,
                totalHan = 1 + if (uraTiles.isNotEmpty()) 1 else 0,
                indicatorTiles = listOf(tile(29)),
                uraTiles = uraTiles,
                redTiles = emptyList(),
                indicatorHits = listOf(DoraIndicatorHit(tile(28), tile(29), listOf(tile(29)))),
                uraHits = uraHits
            ),
            fu = FuBreakdown(
                lines = listOf(FuLine("BASE", 20), FuLine("MPSZ", 10)),
                subtotal = 30,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = null
            ),
            score = scoreResult,
            paymentLines = listOf("TSUMO_DEALER_2000_NONDEALER_1000"),
            honbaAddition = 600,
            riichiAddition = 1000,
            totalPoints = scoreResult.totalPoints(isDealer = false, isTsumo = true) + 600 + 1000,
            uraEligible = true,
            isDealer = false,
            isTsumo = true
        )
    }

    private fun breakdownDoc(value: ScoreBreakdown?, uraAuthorized: Boolean?): Map<String, Any?> {
        val breakdown = value!!
        val result = linkedMapOf<String, Any?>(
            "uraTiles" to breakdown.dora.uraTiles.map { it.tileId },
            "uraHits" to breakdown.dora.uraHits.size,
            "uraHitTiles" to breakdown.dora.uraHits.flatMap { hit -> hit.tiles.map { it.tileId } },
            "indicatorTiles" to breakdown.dora.indicatorTiles.map { it.tileId },
            "doraCount" to breakdown.dora.uraCount + breakdown.dora.indicatorCount + breakdown.dora.redCount,
            "uraCount" to breakdown.dora.uraCount,
            "totalPoints" to breakdown.totalPoints,
            "han" to breakdown.dora.totalHan,
            "fu" to breakdown.fu.finalFu,
            "score" to breakdown.score.totalPoints(isDealer = false, isTsumo = true),
            "yaku" to breakdown.yakuLines.map { (code, han) -> obj("code" to code, "han" to han) }
        )
        if (uraAuthorized != null) {
            result["uraAuthorized"] = uraAuthorized
        }
        return result
    }

    private fun stateDoc(state: GameState, roundId: String): Map<String, Any?> {
        val placements = mutableListOf<List<Any?>>()
        val seen = HashSet<Int>()
        fun put(tile: Tile, zone: String, seat: Int, index: Int) {
            assertTrue(seen.add(tile.tileId), "duplicate physical tile ${tile.tileId} in state")
            placements.add(listOf(tile.tileId, zone, seat, index))
        }

        val wallSuffix = state.wall.drop(state.wallPointer).take(state.wallEndExclusive - state.wallPointer)
        wallSuffix.forEachIndexed { index, value -> put(value, "WALL", -1, index) }
        state.deadWall.forEachIndexed { index, value -> put(value, "DEAD_WALL", -1, index) }
        state.deadWallReplacements.forEachIndexed { index, value -> put(value, "DEAD_WALL_REPLACEMENT", -1, index) }
        for (seat in 0..3) {
            val hand = state.players[seat]
            hand.closedTiles.forEachIndexed { index, value -> put(value, "HAND", seat, index) }
            if (hand.drawnTile != null) put(hand.drawnTile!!, "HAND", seat, hand.closedTiles.size)
            hand.melds.forEachIndexed { meldIndex, meld ->
                meld.tiles.forEachIndexed { tileIndex, value ->
                    put(value, "MELD", seat, meldIndex * 4 + tileIndex)
                }
            }
            hand.discards.forEachIndexed { index, value -> put(value, "DISCARD", seat, index) }
            state.sleeveTiles[seat].forEachIndexed { index, value -> put(value, "SLEEVE", seat, index) }
        }
        assertEquals(136, placements.size, "state must contain all 136 physical entities")
        assertEquals(136, seen.size, "state must contain unique physical entities")
        assertEquals(
            state.dealerIndex,
            state.currentPlayer,
            "V5 CreateDealt derives CurrentSeat from the dealer; this fixture must be the dealer's turn"
        )
        assertEquals(wallSuffix.size, state.wallRemaining, "canonical wall suffix size must equal wallRemaining")
        for (entry in state.cheatEntries) {
            assertEquals(
                state.cheatPoints[entry.seat],
                entry.balanceAfter,
                "cheat ledger balanceAfter must equal the exported seat points"
            )
        }
        placements.sortBy { it[0] as Int }

        val last = state.lastDiscard?.let { tile ->
            var found: List<Any?>? = null
            for (seat in 0..3) {
                val index = state.players[seat].discards.indexOfFirst { it.tileId == tile.tileId }
                if (index >= 0) {
                    found = listOf(seat, tile.tileId, index)
                    break
                }
            }
            found
        }

        return obj(
            "roundId" to roundId,
            "phase" to state.phase.name,
            "roundWind" to state.roundWind,
            "roundNumber" to state.roundNumber,
            "honbaSticks" to state.honbaSticks,
            "riichiSticks" to state.riichiSticks,
            "dealerSeat" to state.dealerIndex,
            "currentPlayer" to state.currentPlayer,
            "wallRemaining" to state.wallRemaining,
            "v4WallPointer" to state.wallPointer,
            "v4WallEndExclusive" to state.wallEndExclusive,
            "v4RinshanPointer" to state.rinshanPointer,
            "kanCount" to state.totalKanCount,
            "scores" to state.scores.toList(),
            "riichiDeclared" to state.riichiDeclared.toList(),
            "doraIndicators" to state.doraIndicators.map { it.tileId },
            "uraDoraIndicators" to state.uraDoraIndicators.map { it.tileId },
            "placements" to placements,
            "melds" to (0..3).map { seat ->
                state.players[seat].melds.map { meld ->
                    obj(
                        "seat" to seat,
                        "type" to meld.type.name,
                        "tileIds" to meld.tiles.map { it.tileId },
                        "fromSeat" to meld.fromPlayer,
                        "calledTileId" to meld.calledTile.tileId
                    )
                }
            },
            "discards" to (0..3).map { seat -> state.players[seat].discards.map { it.tileId } },
            "hands" to (0..3).map { seat ->
                obj(
                    "seat" to seat,
                    "closedTileIds" to state.players[seat].closedTiles.map { it.tileId },
                    "drawnTileId" to state.players[seat].drawnTile?.tileId,
                    "sleeveTileIds" to state.sleeveTiles[seat].map { it.tileId },
                    "furiten" to false,
                    "discardHistory" to state.players[seat].discardHistory.map { it.tileId }
                )
            },
            "lastDiscard" to last?.let { obj("seat" to it[0], "tileId" to it[1], "index" to it[2]) },
            "privateCheat" to obj(
                "points" to state.cheatPoints.toList(),
                "entries" to state.cheatEntries.map {
                    obj(
                        "entryId" to it.entryId,
                        "seat" to it.seat,
                        "amount" to it.amount,
                        "balanceAfter" to it.balanceAfter,
                        "reason" to it.reason
                    )
                }
            )
        )
    }

    private fun viewDoc(view: PlayerView): Map<String, Any?> = obj(
        "viewerSeat" to view.viewerSeat,
        "phase" to view.phase.name,
        "roundWind" to view.roundWind,
        "roundNumber" to view.roundNumber,
        "honbaSticks" to view.honbaSticks,
        "riichiSticks" to view.riichiSticks,
        "dealerSeat" to view.dealerSeat,
        "currentTurnPlayer" to view.currentTurnPlayer,
        "wallRemaining" to view.wallRemaining,
        "doraIndicators" to view.doraIndicators.map { it.tileId },
        "scores" to view.scores,
        "riichiStatus" to view.riichiStatus,
        "discards" to view.discards.map { list -> list.map { it.tileId } },
        "melds" to view.melds.map { list -> list.map { meldDoc(it) } },
        "handTileCounts" to view.handTileCounts,
        "myClosedTiles" to view.myClosedTiles.map { it.tileId },
        "myDrawnTile" to view.myDrawnTile?.tileId,
        "myAllClosedTiles" to view.myAllClosedTiles.map { it.tileId },
        "legalActions" to view.legalActions.map { action ->
            when (action) {
                is GameAction.DeclarePon -> obj("kind" to "DECLARE_PON", "seat" to action.player, "tileIds" to action.tiles.map { it.tileId })
                is GameAction.DeclareChi -> obj("kind" to "DECLARE_CHI", "seat" to action.player, "tileIds" to action.meld.tiles.map { it.tileId })
                else -> obj("kind" to action::class.simpleName, "seat" to -1, "tileIds" to emptyList<Int>())
            }
        },
        "lastDiscard" to view.lastDiscard?.tileId,
        "lastDiscardPlayer" to view.lastDiscardPlayer,
        "mySleeveTiles" to view.mySleeveTiles.map { it.tileId },
        "myFuriten" to obj(
            "discard" to view.myFuriten.discard,
            "temporary" to view.myFuriten.temporary,
            "riichi" to view.myFuriten.riichi
        ),
        "myDiscardHistory" to view.myDiscardHistory.map { it.tileId }
    )

    private fun meldDoc(meld: Meld): Map<String, Any?> = obj(
        "type" to meld.type.name,
        "tileIds" to meld.tiles.map { it.tileId },
        "fromSeat" to meld.fromPlayer,
        "calledTileId" to meld.calledTile.tileId
    )

    private fun boardDoc(board: PublicBoardView): Map<String, Any?> = obj(
        "phase" to board.phase.name,
        "roundWind" to board.roundWind,
        "roundNumber" to board.roundNumber,
        "honbaSticks" to board.honbaSticks,
        "riichiSticks" to board.riichiSticks,
        "dealerSeat" to board.dealerSeat,
        "currentTurnPlayer" to board.currentTurnPlayer,
        "wallRemaining" to board.wallRemaining,
        "doraIndicators" to board.doraIndicators.map { it.tileId },
        "scores" to board.scores,
        "riichiStatus" to board.riichiStatus,
        "discards" to board.discards.map { list -> list.map { it.tileId } },
        "melds" to board.melds.map { list -> list.map { meldDoc(it) } },
        "handTileCounts" to board.handTileCounts,
        "lastDiscard" to board.lastDiscard?.tileId,
        "lastDiscardPlayer" to board.lastDiscardPlayer
    )

    private fun eventDoc(event: GameEvent, sourceAuthorization: Boolean? = null): Map<String, Any?> = when (event) {
        is GameEvent.TileDealt -> obj("kind" to "TileDealt", "turnNumber" to event.turnNumber, "seat" to event.player, "tileIds" to listOf(event.tile.tileId))
        is GameEvent.TileDrawn -> obj(
            "kind" to "TileDrawn",
            "turnNumber" to event.turnNumber,
            "seat" to event.player,
            "wallRemaining" to event.wallRemaining,
            "tileIds" to listOf(event.tile.tileId)
        )
        is GameEvent.RinshanDraw -> obj("kind" to "RinshanDraw", "turnNumber" to event.turnNumber, "seat" to event.player, "tileIds" to listOf(event.tile.tileId))
        is GameEvent.DoraRevealed -> obj(
            "kind" to "DoraRevealed",
            "turnNumber" to event.turnNumber,
            "seat" to -1,
            "isUraDora" to event.isUraDora,
            "tileIds" to listOf(event.indicator.tileId)
        )
        is GameEvent.TileDiscarded -> obj(
            "kind" to "TileDiscarded",
            "turnNumber" to event.turnNumber,
            "seat" to event.player,
            "riichiDiscard" to event.isRiichiDiscard,
            "tsumogiri" to event.isTsumogiri,
            "tileIds" to listOf(event.tile.tileId)
        )
        is GameEvent.ChiDeclared -> obj(
            "kind" to "ChiDeclared",
            "turnNumber" to event.turnNumber,
            "seat" to event.player,
            "fromSeat" to event.fromPlayer,
            "calledTileId" to event.meld.calledTile.tileId,
            "tileIds" to event.meld.tiles.map { it.tileId }
        )
        is GameEvent.PonDeclared -> obj(
            "kind" to "PonDeclared",
            "turnNumber" to event.turnNumber,
            "seat" to event.player,
            "fromSeat" to event.fromPlayer,
            "calledTileId" to event.meld.calledTile.tileId,
            "tileIds" to event.meld.tiles.map { it.tileId }
        )
        is GameEvent.KanDeclared -> obj(
            "kind" to "KanDeclared",
            "turnNumber" to event.turnNumber,
            "seat" to event.player,
            "calledTileId" to event.meld.calledTile.tileId,
            "kanType" to event.kanType.name,
            "tileIds" to event.meld.tiles.map { it.tileId }
        )
        is GameEvent.RiichiDeclared -> obj("kind" to "RiichiDeclared", "turnNumber" to event.turnNumber, "seat" to event.player, "tileIds" to listOf(event.discardTile.tileId))
        is GameEvent.TsumoWin -> obj(
            "kind" to "TsumoWin",
            "turnNumber" to event.turnNumber,
            "seat" to event.player,
            "tileIds" to listOf(event.winTile.tileId),
            "yaku" to (
                event.breakdown?.yakuLines?.map { (code, han) -> obj("code" to code, "han" to han) }
                    ?: event.yakuList.map { obj("code" to it, "han" to 1) }
                ),
            "han" to event.han,
            "fu" to event.fu,
            "score" to event.score,
            "breakdown" to breakdownDoc(event.breakdown, sourceAuthorization)
        )
        is GameEvent.RonWin -> obj(
            "kind" to "RonWin",
            "turnNumber" to event.turnNumber,
            "seat" to event.winner,
            "loserSeat" to event.loser,
            "tileIds" to listOf(event.winTile.tileId)
        )
        is GameEvent.DrawGame -> obj("kind" to "DrawGame", "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
        is GameEvent.ScoreSettled -> obj("kind" to "ScoreSettled", "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
        is GameEvent.RoundEnded -> obj(
            "kind" to "RoundEnded",
            "turnNumber" to event.turnNumber,
            "seat" to -1,
            "roundWind" to event.roundWind,
            "roundNumber" to event.roundNumber,
            "tileIds" to emptyList<Int>()
        )
        is GameEvent.MatchEnded -> obj("kind" to "MatchEnded", "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
        is GameEvent.CheatPointsChanged -> obj(
            "kind" to "CheatPointsChanged",
            "turnNumber" to event.turnNumber,
            "seat" to event.seat,
            "amount" to event.amount,
            "newBalance" to event.newBalance,
            "tileIds" to emptyList<Int>()
        )
        is GameEvent.StackingStarted -> obj("kind" to "StackingStarted", "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
        is GameEvent.WallBuilt -> obj("kind" to "WallBuilt", "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
        is GameEvent.DealingComplete -> obj("kind" to "DealingComplete", "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
        else -> obj("kind" to event::class.simpleName, "turnNumber" to event.turnNumber, "seat" to -1, "tileIds" to emptyList<Int>())
    }

    private fun collectViewTileIds(view: Map<String, Any?>): Set<Int> {
        val result = HashSet<Int>()
        @Suppress("UNCHECKED_CAST")
        result.addAll((view["myClosedTiles"] as List<Int>))
        (view["myDrawnTile"] as Int?)?.let { result.add(it) }
        @Suppress("UNCHECKED_CAST")
        result.addAll(view["doraIndicators"] as List<Int>)
        @Suppress("UNCHECKED_CAST")
        (view["discards"] as List<List<Int>>).forEach { result.addAll(it) }
        @Suppress("UNCHECKED_CAST")
        (view["melds"] as List<List<Map<String, Any?>>>).forEach { list ->
            list.forEach { meld -> @Suppress("UNCHECKED_CAST") result.addAll(meld["tileIds"] as List<Int>) }
        }
        (view["lastDiscard"] as Int?)?.let { result.add(it) }
        return result
    }

    private fun collectBoardTileIds(board: Map<String, Any?>): Set<Int> {
        val result = HashSet<Int>()
        @Suppress("UNCHECKED_CAST")
        result.addAll(board["doraIndicators"] as List<Int>)
        @Suppress("UNCHECKED_CAST")
        (board["discards"] as List<List<Int>>).forEach { result.addAll(it) }
        @Suppress("UNCHECKED_CAST")
        (board["melds"] as List<List<Map<String, Any?>>>).forEach { list ->
            list.forEach { meld -> @Suppress("UNCHECKED_CAST") result.addAll(meld["tileIds"] as List<Int>) }
        }
        (board["lastDiscard"] as Int?)?.let { result.add(it) }
        return result
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private fun validateCases(cases: List<Map<String, Any?>>) {
        assertEquals(4, cases.size)
        assertEquals(
            listOf("VIS-G057A-SNAPSHOT-COPY", "VIS-G057B-EVENT-VISIBILITY", "VIS-G057C-PUBLIC-PROJECTION", "VIS-G057D-SEAT-VIEW-ISOLATION"),
            cases.map { it["id"] }
        )
        val ids = cases.map { it["id"] as String }.toSet()
        assertEquals(cases.size, ids.size, "case ids must be unique")
        for (case in cases) {
            val input = case["input"] as Map<*, *>
            val state = input["state"] as Map<*, *>
            val placements = state["placements"] as List<*>
            assertEquals(136, placements.size, "${case["id"]}: 136 placements")
            val tiles = placements.map { (it as List<*>)[0] as Int }.toSet()
            assertEquals(136, tiles.size, "${case["id"]}: unique tile ids")
            assertEquals((0..135).toSet(), tiles, "${case["id"]}: full 0..135 entity set")
            assertEquals(false, input["clockUsed"], "${case["id"]}: no clock")
            assertEquals(false, input["implicitRandomUsed"], "${case["id"]}: no implicit random")
            assertTrue((case["sourceAnchors"] as List<*>).isNotEmpty())
            assertTrue((case["gRows"] as List<*>).all { it in setOf("G-057a", "G-057b", "G-057c", "G-057d") })
        }

        val d = cases.single { it["id"] == "VIS-G057D-SEAT-VIEW-ISOLATION" }
        val expected = d["expected"] as Map<*, *>
        val hidden = expected["hiddenTileIds"] as Map<*, *>
        val seat0 = (expected["seatViews"] as Map<*, *>)["0"] as Map<*, *>
        val seat0Ids = collectViewTileIds(seat0 as Map<String, Any?>)
        for (id in hidden["opponentClosedFromSeat0"] as List<*>) {
            assertFalse(seat0Ids.contains(id as Int), "seat0 leaked opponent/sleeve tile $id")
        }
        for (id in hidden["wallOrder"] as List<*>) {
            assertFalse(seat0Ids.contains(id as Int), "seat0 leaked wall tile $id")
        }
        for (id in hidden["uraDoraIndicators"] as List<*>) {
            assertFalse(seat0Ids.contains(id as Int), "seat0 leaked ura indicator $id")
        }
        val seat1 = (expected["seatViews"] as Map<*, *>)["1"] as Map<*, *>
        assertTrue((seat1["mySleeveTiles"] as List<*>).isNotEmpty())
    }

    /**
     * Default-mode guard: the committed fixture must equal this run's real V4 results.
     * Missing, truncated or stale case content fails; only explicitly dynamic META may differ.
     */
    @Test
    fun defaultModeRejectsStaleOrMissingFixture() {
        if (exportEnabled()) {
            return
        }
        val root = repoRoot()
        assertThrows<AssertionError>("stale expected score must be rejected") {
            val stale = buildCases()
            @Suppress("UNCHECKED_CAST")
            val snapshot = ((stale[0]["expected"] as Map<String, Any?>)["snapshot"] as MutableMap<String, Any?>)
            snapshot["scores"] = listOf(24999, 25000, 25000, 25000)
            validateCommittedFixture(root, stale)
        }
        assertThrows<AssertionError>("stale placement must be rejected") {
            val stale = buildCases()
            @Suppress("UNCHECKED_CAST")
            val placements = ((stale[0]["input"] as Map<String, Any?>)["state"] as Map<String, Any?>)["placements"] as MutableList<List<Any?>>
            placements[0] = listOf(placements[0][0], "WALL", -1, 0).toMutableList().also { it[0] = 1 }
            validateCommittedFixture(root, stale)
        }
        assertThrows<AssertionError>("stale meld origin must be rejected") {
            val stale = buildCases()
            @Suppress("UNCHECKED_CAST")
            val melds = ((stale[0]["expected"] as Map<String, Any?>)["snapshot"] as Map<String, Any?>)["melds"] as List<List<Map<String, Any?>>>
            @Suppress("UNCHECKED_CAST")
            (melds[1][0] as MutableMap<String, Any?>)["fromSeat"] = 2
            validateCommittedFixture(root, stale)
        }
        assertThrows<AssertionError>("missing fixture must be rejected") {
            validateCommittedFixture(root, buildCases(), root.resolve("build/visibility-exporter-missing-fixture.json"))
        }
    }

    private fun validateCommittedFixture(
        root: Path,
        cases: List<Map<String, Any?>>,
        fixture: Path = root.resolve(FIXTURE_PATH)
    ) {
        assertTrue(Files.exists(fixture), "committed fixture is missing: $fixture")
        val text = Files.readString(fixture, StandardCharsets.UTF_8).replace("\r\n", "\n")
        assertTrue(text.contains("\"schema\": \"v5-visibility-golden-v1\""), "committed fixture schema drifted")
        assertTrue(text.contains("\"caseCount\": 4"), "committed fixture caseCount drifted")
        val marker = "\n  \"cases\": "
        val index = text.indexOf(marker)
        assertTrue(index >= 0, "committed fixture has no cases block")
        var actualCases = text.substring(index + marker.length).trimEnd()
        if (actualCases.endsWith("}")) {
            actualCases = actualCases.dropLast(1).trimEnd()
        }
        val expectedCases = renderValue(cases, 1)
        if (expectedCases != actualCases) {
            val limit = minOf(expectedCases.length, actualCases.length)
            var index = 0
            while (index < limit && expectedCases[index] == actualCases[index]) index++
            val from = maxOf(0, index - 80)
            val toE = minOf(expectedCases.length, index + 120)
            val toA = minOf(actualCases.length, index + 120)
            assertEquals(
                expectedCases,
                actualCases,
                "committed fixture cases differ at char $index; expected[" +
                    expectedCases.substring(from, toE) + "] actual[" +
                    actualCases.substring(from, toA) + "]"
            )
        }
        val recordedHash = Regex("\"renderSha256LfNormalized\": \"([0-9a-f]{64})\"")
            .find(text)?.groupValues?.get(1)
        assertNotNull(recordedHash, "committed fixture has no render hash")
        val normalized = text.replace(
            Regex("\"renderSha256LfNormalized\": \"[0-9a-f]{64}\""),
            "\"renderSha256LfNormalized\": null"
        )
        assertEquals(
            recordedHash,
            sha256(normalized.toByteArray(StandardCharsets.UTF_8)),
            "committed fixture render hash drifted"
        )
    }

    // ------------------------------------------------------------------
    // Fixture rendering / provenance
    // ------------------------------------------------------------------

    private fun renderRoot(cases: List<Map<String, Any?>>, commit: String?, renderHash: String?): String {
        val root = repoRoot()
        val sources = SOURCE_FILES.map { sourceHash(root, it) }
        val exporterHash = sourceHash(root, EXPORTER_PATH)
        val trackedModified = gitOrEmpty(root, "diff", "--name-only", "HEAD")
            .lineSequence()
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .filterNot { it.startsWith("warning:", ignoreCase = true) || it.startsWith("hint:", ignoreCase = true) }
            .toList()
        val trackedProductionModified = trackedModified.filterNot { it in TASK_PATHS }

        val doc = linkedMapOf<String, Any?>(
            "schema" to SCHEMA,
            "generatorVersion" to 1,
            "rulesetVersion" to 1,
            "generator" to EXPORTER_PATH,
            "generatedByCommand" to GENERATION_COMMAND,
            "generatedByCommandPosix" to GENERATION_COMMAND_POSIX,
            "extractionStatus" to "exported",
            "v4SourceCommit" to (commit ?: gitOrEmpty(root, "rev-parse", "HEAD")),
            "v5BaselineCommit" to BASELINE_COMMIT,
            "v4ProductionTreeClean" to trackedProductionModified.isEmpty(),
            "trackedModifiedFiles" to trackedModified,
            "trackedProductionModifiedFiles" to trackedProductionModified,
            "taskPaths" to TASK_PATHS.sorted(),
            "targetIds" to listOf("G-057a", "G-057b", "G-057c", "G-057d"),
            "caseCount" to cases.size,
            "clockUsed" to false,
            "implicitRandomUsed" to false,
            "inputModel" to (
                "V4 TestTableBuilder creates one real 136-entity GameState per state case; " +
                    "PlayerView.fromGameState, SessionUpdate.filterVisibleEvents and " +
                    "PublicProjectionFactory.project/publicEvents are called directly. " +
                    "No sleep/clock/random/ID source is read; every action/event/epoch is explicit."
                ),
            "mapping" to mappingDoc(),
            "fieldClassification" to fieldClassificationDoc(),
            "knownDivergences" to knownDivergencesDoc(),
            "sourceHashes" to sources,
            "exporterSource" to exporterHash,
            "renderSha256LfNormalized" to renderHash,
            "scope" to obj(
                "manifestEntriesTargeted" to 4,
                "manifestEntriesExportedByThisRun" to 4,
                "fullManifestAudited" to false,
                "note" to "G-057a..d only; this is not a full 73-case audit and does not update catalog-v1.json."
            ),
            "generationResult" to obj(
                "casesExported" to cases.size,
                "stateCases" to 4,
                "failures" to 0,
                "exporterTestResult" to "PASSED",
                "secondRenderIdentical" to true,
                "fixtureLineEndings" to "LF",
                "fixtureTrailingNewline" to true
            ),
            "contentHashPolicy" to (
                "sourceHashes[].sha256RawBytes hashes the raw checked-out bytes; " +
                    "sourceHashes[].sha256LfNormalized hashes UTF-8 text after CRLF->LF replacement. " +
                    "They are never mixed. renderSha256LfNormalized hashes this document with the " +
                    "renderSha256LfNormalized value set to null; it binds every case/meta byte without " +
                    "a self-reference. The final file is LF-terminated and its actual SHA-256 is reported " +
                    "by the delivery report from the file, never hand-written here."
                ),
            "cases" to cases
        )
        return renderValue(doc, 0) + "\n"
    }

    /**
     * Reports, but never silently swallows, a change against the previous committed fixture.
     * The report is emitted to the exporter log; the fixture content stays deterministic.
     */
    private fun reportFixtureChange(fixture: Path, baseText: String) {
        if (!Files.exists(fixture)) {
            println("V5 visibility golden: no previous fixture (first export)")
            return
        }
        val previous = normalizeRenderHash(String(Files.readAllBytes(fixture), StandardCharsets.UTF_8))
        if (previous == baseText) {
            println("V5 visibility golden: previous fixture base is byte-identical")
            return
        }
        val before = topLevelSegments(previous)
        val after = topLevelSegments(baseText)
        val changed = (before.keys + after.keys).distinct().sorted()
            .filter { it != "renderSha256LfNormalized" && before[it] != after[it] }
        println("V5 visibility golden: META/CASE CHANGE detected; changed top-level keys = $changed")
        println("V5 visibility golden: previous fixture SHA-256 = " + sha256(previous.toByteArray(StandardCharsets.UTF_8)))
        println("V5 visibility golden: new base SHA-256 = " + sha256(baseText.toByteArray(StandardCharsets.UTF_8)))
    }

    private fun normalizeRenderHash(text: String): String =
        Regex("\"renderSha256LfNormalized\":\\s*\"[0-9a-f]{64}\"")
            .replace(text, "\"renderSha256LfNormalized\": null")

    private fun mappingDoc(): Map<String, Any?> = obj(
        "v4PlayerViewToV5" to (
            "V4 PlayerView / PublicBoardView fields map to V5 PlayerSnapshotView/PublicSnapshotView: " +
                "wallRemaining, doraIndicators, scores (Int -> long), riichiStatus -> per-seat facts.Riichi, " +
                "discards by river index, open melds by TableMeldFact, handTileCounts -> PlayerPublicView.HandCount, " +
                "myClosedTiles -> Private.ClosedTiles, myDrawnTile -> Private.DrawnTileId, " +
                "mySleeveTiles -> TableOwnerPrivateView.SleeveTiles (owner extension), " +
                "myDiscardHistory -> TableSeatPrivateFacts.DiscardHistory."
        ),
        "wallMapping" to (
            "V4 live-wall suffix [wallPointer, wallEndExclusive) is canonicalized to V5 dense Wall(0..N-1); " +
                "V5 WallPointer is 0 and WallEndExclusive=N. The raw V4 pointers remain SOURCE_ONLY. " +
                "V5 WallRemaining counts physical wall tiles and equals N."
        ),
        "deadWallMapping" to "V4 deadWall list index i -> V5 DeadWall(i). Dora indicators must be at index >= 4; this fixture uses index 4.",
        "handMapping" to "V4 closedTiles list order -> V5 Hand(seat, 0..n-1); drawn tile -> Hand(seat, n) plus DrawnTileEvidence. V5 sorts by slot index, reproducing V4 list order.",
        "meldMapping" to "V4 meld list index i -> V5 Meld(seat, i*4 + tileOffset). V4 MeldType/fromPlayer/calledTile -> TableMeldFact/MeldView input and output; both fromSeat and calledTileId are compared.",
        "eventMapping" to (
            "V4 TileDealt/TileDrawn/RinshanDraw -> ProtocolEventType private types with the owner seat; " +
                "DoraRevealed(isUraDora) -> DoraRevealed; TileDiscarded/ChiDeclared/PonDeclared/KanDeclared/" +
                "RiichiDeclared/DrawGame/ScoreSettled/RoundEnded/MatchEnded keep their names; " +
                "V4 turnNumber/fromSeat/riichiDiscard/tsumogiri/wallRemaining/roundWind/roundNumber and " +
                "TsumoWin yaku/han/fu/score/breakdown are supplied as TableProjectionEventFacts and compared " +
                "through TableProjectionContractMapper.MapEvent payloads. V4 settlementEvents implies V5 SettlementBatch=true."
        ),
        "discardMapping" to "V4 river list index -> V5 DiscardView turnNumber=index. TileDiscarded riichiDiscard/tsumogiri flags are mapped as explicit facts and compared; only the per-river turnNumber assignment is a representation choice.",
        "scoreMapping" to (
            "V4 Int score -> V5 long ScoreView.Score, value-preserving. TsumoWin breakdown maps " +
                "Han/Fu/TotalPoints/Yaku/DoraCount/UraCount/UraTiles/UraHits through ScoreBreakdownView. " +
                "V4 breakdown.indicatorTiles/breakdown.score have no same-shape V5 field (SOURCE_ONLY); " +
                "V4 uraHits group count is cross-checked against the flattened uraHitTiles list."
        ),
        "ownerMapping" to "V4 mySleeveTiles -> TableOwnerPrivateView.SleeveTiles -> MapPlayerSnapshot -> PrivatePlayerView.SleeveTiles for the owner; V4 meld metadata -> TableOwnerMeldView -> MeldView. Other seats/spectator never receive them.",
        "authorizationMapping" to (
            "uraAuthorized is not a V4 ScoreBreakdown property. The raw fixture event carries an explicit " +
                "source-authorization adapter flag (true when raw ura identities are present); the C# caller " +
                "chooses per-branch authorization and the expected canonical blocks record true/false. " +
                "Raw identities with source authorization false are rejected; the mapper never strips automatically."
        ),
        "versionMapping" to "V5 state_version/event_seq/stream cursor are explicit fixture inputs; V4 PlayerView has no equivalent and they are STRUCTURE only, never compared to V4 values."
    )

    private fun fieldClassificationDoc(): Map<String, Any?> = obj(
        "INPUT" to listOf(
            "state.roundId/phase/roundWind/roundNumber/honbaSticks/riichiSticks/dealerSeat/currentPlayer",
            "state.scores[]", "state.riichiDeclared[]", "state.doraIndicators[]", "state.uraDoraIndicators[] (hidden input)",
            "state.placements[136] (tileId/zone/seat/index)", "state.melds[] (type/tiles/fromSeat/calledTileId)",
            "state.discards[][]", "state.hands[] (closed/drawn/sleeve/furiten/discardHistory)", "state.lastDiscard",
            "state.privateCheat (points + entryId/seat/amount/balanceAfter/reason)",
            "events[] (kind/turn/seat/tiles/ura/fromSeat/calledTileId/flags/wallRemaining/round fields/yaku/han/fu/score/breakdown)",
            "events[].breakdown.uraAuthorized (explicit source-authorization adapter, not a V4 field)",
            "clock (seat/opensAt/deadline/reserve/serverNow)", "viewerSeat", "legalActions",
            "settlementEvents", "revealSettlementUra", "policy flags"
        ),
        "OUTPUT" to listOf(
            "expected.snapshot.* (V4 PlayerView)", "expected.seatViews.* (V4 PlayerView per seat)",
            "expected.updateVisibleEventsAfterProducerClear", "expected.visibility.*",
            "expected.board.*", "expected.session.lifecycle/scores/roundRecords/finalResult",
            "expected.settlement.unauthorized/authorized", "expected.tsumoBreakdown.* (canonical branch + authorization)",
            "expected.spectatorBoard.*", "expected.hiddenTileIds.* (negative privacy expectations)",
            "mapped MapEvent payload TurnNumber/Seat/TileIds/Meld/FromSeat/flags/WallRemaining/Round fields/Yaku/Han/Fu/Score",
            "mapped Breakdown Han/Fu/TotalPoints/Yaku/DoraCount/UraCount/UraTiles/UraHits/UraAuthorized",
            "mapped MapPlayerSnapshot Private.SleeveTiles/OwnerMelds/Cheat ledger",
            "MapPublicSnapshot typed settlement records are identity-preserved from the fully compared MapEvent records"
        ),
        "STRUCTURE" to listOf(
            "exactly 136 unique physical entities per state", "unique tile ids 0..135",
            "unique positions", "hands/discards/sleeve/meld placement vs duplicate lists consistency",
            "currentPlayer == dealerSeat for CreateDealt", "wallRemaining == dense wall count",
            "dead wall = 14 tiles", "dora indicator in dead-wall slot >= 4",
            "wall indices < WallEndExclusive", "four scores/four seats", "case id uniqueness",
            "event turn order", "cheat balanceAfter == seat points", "breakdown uraHits count == uraHitTiles length",
            "G-row mapping", "V5 state_version/event_seq/stream cursor"
        ),
        "META" to listOf(
            "schema", "generatorVersion", "rulesetVersion", "generator", "generatedByCommand",
            "v4SourceCommit", "v5BaselineCommit", "v4ProductionTreeClean", "trackedModifiedFiles",
            "taskPaths", "targetIds", "caseCount", "clockUsed", "implicitRandomUsed",
            "sourceHashes", "exporterSource", "renderSha256LfNormalized"
        ),
        "SOURCE_ONLY" to listOf(
            "state.v4WallPointer", "state.v4WallEndExclusive", "state.v4RinshanPointer",
            "state.kanCount", "sourceOnly.v4DeadWall", "sourceOnly.drawSource",
            "sourceOnly.legalActions", "sourceOnly.sessionUpdateWholesaleCopy",
            "sourceOnly.v4CheatPointsChangedVisibilityCounts",
            "breakdown.indicatorTiles (V5 ScoreBreakdownView has no indicator list; board dora is separate)",
            "breakdown.score (V5 payload has event Score, not a breakdown-specific score field)",
            "V4 breakdown.uraHits group count (cross-checked against V5 UraHits tile list, not a separate V5 field)"
        ),
        "UNREPRESENTABLE" to listOf(
            "V4 legalActions -> V5 Policy/DecisionView (P3.3 supplies it; no same-shape field)",
            "V4 SessionUpdate unfiltered visible events -> V5 has no unfiltered client event list",
            "V4 PublicSessionSummary lifecycle/scores/roundRecords/finalResult -> V5 finished/finalScores/rankings",
            "V4 closed-kan public meld identity -> V5 public board omits it (ConcealedMelds placeholder)",
            "V4 per-river turnNumber assignment -> V5 DiscardView turnNumber uses river index"
        )
    )

    private fun knownDivergencesDoc(): List<Map<String, Any?>> = listOf(
        divergence(
            "VG-D01", "MEDIUM",
            "engine/src/main/kotlin/com/majiang/session/SessionUpdate.kt:85-90 vs dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs:594-614",
            "V4 SessionUpdate.filterVisibleEvents passes CheatPointsChanged to every seat through its else branch.",
            "V4: seat0..3 all receive CheatPointsChanged. V5: ProtocolEventPolicy marks it private and only the owner seat receives it.",
            "P3.2 owner (visibility policy) + caller",
            "BLOCKED for V4/V5 equality; V5 behavior is the stricter intended policy. Not counted as an aligned field."
        ),
        divergence(
            "VG-D02", "LOW",
            "engine/src/main/kotlin/com/majiang/session/PlayerView.kt:124-142 vs dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs:1156-1204",
            "V4 PlayerView.legalActions carries deep-copied GameAction tiles; V5 PlayerSnapshotView.Private.Decision comes from a P3.3 DecisionView.",
            "No same-shape mapping: comparing V4 GameAction.DeclarePon to V5 LegalActionView would invent semantics.",
            "P3.3 owner",
            "UNREPRESENTABLE; the V4 deep-copy behavior is proven by the Kotlin exporter only."
        ),
        divergence(
            "VG-D03", "LOW",
            "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:482-506 vs dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs:981-1087",
            "V4 PublicBoardView publishes every meld including CLOSED_KAN tile identities.",
            "V5 public board omits a closed kan's four identities and exposes only a concealed placeholder/count.",
            "P3.2 owner",
            "UNREPRESENTABLE/INTENTIONALLY STRICTER; no closed-kan case is counted as aligned in this fixture."
        ),
        divergence(
            "VG-D04", "LOW",
            "engine/src/main/kotlin/com/majiang/session/PlayerView.kt:145 vs dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs:1183-1190",
            "V4 PlayerView.mySleeveTiles is part of the player view.",
            "V5 keeps sleeve identities in TableOwnerPrivateView; MapPlayerSnapshot then maps them into PrivatePlayerView.SleeveTiles for the owner only.",
            "P3.2 owner",
            "REPRESENTATION MAPPING, not a leak: the owner mapped snapshot carries it; public/other-seat views do not."
        ),
        divergence(
            "VG-D05", "LOW",
            "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:222-244 vs dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs:717-757",
            "V4 PublicSessionSummary carries lifecycle, scores, roundRecords and finalResult.",
            "V5 PublicSessionSummary carries finished/finishReason/finalScores/rankings; round records are absent.",
            "P2.1/P3.2 owner",
            "UNREPRESENTABLE; only finished=false is compared for an active round."
        ),
        divergence(
            "VG-D06", "LOW",
            "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:643-790 vs dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs:492-513,895-940",
            "V4 publicEvents strips ScoreBreakdown.dora.uraTiles/uraHits when revealSettlementUra=false and restores them when true.",
            "V5 TableProjectedEvent is metadata-only, but TableProjectionEventFacts.Breakdown + MapEvent + ScoreBreakdownView are the existing full mapping path. The mapper does NOT auto-redact: a caller must pass uraAuthorized=false without ura identities (otherwise ScoreBreakdownView rejects), or authorize the raw breakdown.",
            "P3.2 joined-map caller / P2.1 payload contract",
            "MAPPED + CALLER AUTHORIZATION CONTRACT; the golden now compares mapped UraTiles/UraHits/TotalPoints/DoraCount/UraCount and asserts the unauthorized raw breakdown is rejected."
        ),
        divergence(
            "VG-D07", "LOW",
            "engine/src/main/kotlin/com/majiang/session/PlayerView.kt:189-190 vs dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs:1081,1164-1168",
            "V4 wallRemaining = wallEndExclusive - wallPointer; V4 keeps the full wall list.",
            "V5 WallRemaining counts physical Wall-zone tiles in [0, WallEndExclusive). Mapping uses a dense live-wall suffix and WallPointer=0.",
            "P1.1/P3.2 owner",
            "REPRESENTATION MAPPING; values are compared after canonicalization, raw V4 pointers are SOURCE_ONLY."
        ),
        divergence(
            "VG-D08", "LOW",
            "engine/src/main/kotlin/com/majiang/state/GameState.kt:445-449 vs dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs:1042-1047",
            "V4 dora indicator list is public; ura indicators are separate and never in PlayerView.",
            "V5 requires dora indicators to live in dead-wall slots >=4 and never exposes ura indicators in board/player views.",
            "P3.2 owner",
            "ALIGNED BEHAVIOR; the V5 structural requirement is stronger and is asserted in C#."
        )
    )

    private fun divergence(
        id: String,
        severity: String,
        location: String,
        v4: String,
        v5: String,
        owner: String,
        status: String
    ): Map<String, Any?> = obj(
        "id" to id,
        "severity" to severity,
        "location" to location,
        "v4Observed" to v4,
        "v5Observed" to v5,
        "owner" to owner,
        "status" to status
    )

    private fun topLevelSegments(text: String): Map<String, String> {
        val result = LinkedHashMap<String, String>()
        var key: String? = null
        val buffer = StringBuilder()
        for (line in text.lineSequence()) {
            val match = Regex("^  \"([A-Za-z0-9_]+)\":").find(line)
            if (match != null) {
                if (key != null) result[key!!] = buffer.toString()
                key = match.groupValues[1]
                buffer.setLength(0)
            }
            if (key != null) buffer.append(line).append('\n')
        }
        if (key != null) result[key!!] = buffer.toString()
        return result
    }

    private fun sourceHash(root: Path, relative: String): Map<String, Any?> {
        val path = root.resolve(relative)
        assertTrue(Files.exists(path), "required source missing: $relative")
        val raw = Files.readAllBytes(path)
        val text = String(raw, StandardCharsets.UTF_8)
        val lf = text.replace("\r\n", "\n")
        val crlf = countOccurrences(text, "\r\n")
        val lfOnly = countOccurrences(text, "\n") - crlf
        return obj(
            "file" to relative,
            "bytesRaw" to raw.size,
            "sha256RawBytes" to sha256(raw),
            "sha256LfNormalized" to sha256(lf.toByteArray(StandardCharsets.UTF_8)),
            "crlfCount" to crlf,
            "lfOnlyCount" to lfOnly,
            "lineEndings" to when {
                crlf > 0 && lfOnly == 0 -> "CRLF"
                crlf == 0 && lfOnly > 0 -> "LF"
                crlf == 0 && lfOnly == 0 -> "NONE"
                else -> "MIXED"
            },
            "algorithm" to "SHA-256; sha256RawBytes over raw file bytes; sha256LfNormalized over UTF-8 text with CRLF replaced by LF"
        )
    }

    // ------------------------------------------------------------------
    // Tiny deterministic JSON writer
    // ------------------------------------------------------------------

    private fun obj(vararg pairs: Pair<String, Any?>): Map<String, Any?> = linkedMapOf(*pairs)

    private fun renderValue(value: Any?, depth: Int): String {
        return when (value) {
            null -> "null"
            is String -> quote(value)
            is Boolean, is Int, is Long -> value.toString()
            is Map<*, *> -> {
                if (value.isEmpty()) return "{}"
                val pad = "  ".repeat(depth + 1)
                val end = "  ".repeat(depth)
                val body = value.entries.joinToString(",\n") { entry ->
                    pad + quote(entry.key.toString()) + ": " + renderValue(entry.value, depth + 1)
                }
                "{\n" + body + "\n" + end + "}"
            }
            is Iterable<*> -> {
                val values = value.toList()
                if (values.isEmpty()) return "[]"
                val pad = "  ".repeat(depth + 1)
                val end = "  ".repeat(depth)
                val body = values.joinToString(",\n") { pad + renderValue(it, depth + 1) }
                "[\n" + body + "\n" + end + "]"
            }
            else -> throw IllegalArgumentException("unsupported JSON value: " + value::class)
        }
    }

    private fun quote(value: String): String {
        val builder = StringBuilder(value.length + 2)
        builder.append('"')
        for (ch in value) {
            when (ch) {
                '"' -> builder.append("\\\"")
                '\\' -> builder.append("\\\\")
                '\n' -> builder.append("\\n")
                '\r' -> builder.append("\\r")
                '\t' -> builder.append("\\t")
                else -> if (ch.code < 0x20) builder.append("\\u%04x".format(Locale.ROOT, ch.code)) else builder.append(ch)
            }
        }
        builder.append('"')
        return builder.toString()
    }

    // ------------------------------------------------------------------
    // Environment helpers
    // ------------------------------------------------------------------

    private fun exportEnabled(): Boolean =
        System.getenv("V5_VISIBILITY_GOLDEN_EXPORT") == "1" ||
            System.getProperty("v5.visibility.export") == "true"

    private fun tile(id: Int): Tile = Tile.fromTileId(id)

    private fun repoRoot(): Path {
        var current: Path? = Paths.get("").toAbsolutePath()
        while (current != null) {
            if (Files.exists(current.resolve("AGENTS.md")) && Files.exists(current.resolve("dotnet"))) {
                return current
            }
            current = current.parent
        }
        error("repository root not found from ${Paths.get("").toAbsolutePath()}")
    }

    private fun git(root: Path, vararg args: String): String {
        val process = ProcessBuilder(listOf("git") + args)
            .directory(root.toFile())
            .redirectErrorStream(true)
            .start()
        val output = process.inputStream.readBytes().toString(StandardCharsets.UTF_8).trim()
        check(process.waitFor() == 0) { "git ${args.joinToString(" ")} failed: $output" }
        return output
    }

    private fun gitOrEmpty(root: Path, vararg args: String): String = try {
        git(root, *args)
    } catch (error: Exception) {
        ""
    }

    private fun sha256(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
        return digest.joinToString("") { "%02x".format(Locale.ROOT, it) }
    }

    private fun countOccurrences(text: String, needle: String): Int {
        var count = 0
        var index = 0
        while (true) {
            index = text.indexOf(needle, index)
            if (index < 0) return count
            count++
            index += needle.length
        }
    }

    companion object {
        private const val SCHEMA = "v5-visibility-golden-v1"
        private const val BASELINE_COMMIT = "1432d775526de921f2cde3aba283236fc3b8e8c1"
        private const val FIXTURE_PATH = "tests/v5-golden/visibility-v1.json"
        private const val EXPORTER_PATH = "engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt"
        private const val GENERATION_COMMAND =
            "\$env:V5_VISIBILITY_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test --tests \"com.majiang.projection.V5VisibilityGoldenExporterTest\" --rerun-tasks --console=plain"
        private const val GENERATION_COMMAND_POSIX =
            "V5_VISIBILITY_GOLDEN_EXPORT=1 ./gradlew :engine:test --tests \"com.majiang.projection.V5VisibilityGoldenExporterTest\" --rerun-tasks --console=plain"

        private val TASK_PATHS = setOf(
            "engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt",
            "tests/v5-golden/visibility-v1.json",
            "dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs",
            "tools/v5-golden/probe-visibility-binding.ps1",
            "doc/v5-重构/V5-P01-VISIBILITY-GOLDEN-G057.md"
        )

        private val SOURCE_FILES = listOf(
            "engine/src/main/kotlin/com/majiang/session/PlayerView.kt",
            "engine/src/main/kotlin/com/majiang/session/SessionUpdate.kt",
            "engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt",
            "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt",
            "engine/src/main/kotlin/com/majiang/event/GameEvent.kt",
            "engine/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt",
            "engine/src/main/kotlin/com/majiang/state/GameState.kt",
            "engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt",
            "engine/src/test/kotlin/com/majiang/session/SessionSnapshotContractTest.kt",
            "engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt",
            "dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs",
            "dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs",
            "dotnet/src/Mahjong.TableRuntime/TableVisibilityContexts.cs",
            "dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs",
            "dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs",
            "dotnet/src/Mahjong.Contracts/ProtocolProjectionPayload.cs",
            "dotnet/src/Mahjong.Domain/TilePositionMap.cs",
            "dotnet/src/Mahjong.Domain/RoundStateMachine.cs",
            "dotnet/src/Mahjong.Domain/RoundTypes.cs",
            "dotnet/tests/Mahjong.TableRuntime.Tests/TableProjectionContractMapperTests.cs"
        )
    }
}

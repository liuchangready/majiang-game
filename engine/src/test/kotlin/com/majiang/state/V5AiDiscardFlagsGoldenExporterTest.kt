package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEvent
import com.majiang.event.GameEventBus
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.security.MessageDigest
import java.util.Locale

/**
 * N3-B6 companion-fixture exporter for per-discard AI flags (isRiichi / isClaimed).
 *
 * Drives real V4 domain play through ApplicationCoordinator (legal actions only), then
 * extracts per tileId/seat/isClaimed/isRiichi evidence exclusively from public outputs:
 * the published GameEvent stream (TileDiscarded/RiichiDeclared/PonDeclared), the public
 * PlayerView projection (discards / riichiStatus / melds) and PlayerHand.discardHistory.
 *
 * V4 boundary kept honest: PlayerView exposes no per-discard claimed/riichi boolean. River
 * entries therefore always export isClaimed=false; a tile claimed by pon leaves the active
 * river, stays in discardHistory and is identified by public meld metadata
 * (Meld.calledTile + Meld.fromPlayer). Those tiles are exported as claimedDiscards entries
 * with isClaimed=true. No V4 production code is modified.
 *
 * Writes tests/v5-golden/ai-discard-flags-v1.json only when V5_AI_DISCARD_FLAGS_EXPORT=1.
 * It is inert during ordinary test runs; the default mode re-derives the cases and
 * byte-compares them against the committed fixture and re-checks the render hash.
 *
 * All JSON-writer helpers are members of this class on purpose: the frozen ai-v1 exporter
 * in this package owns same-named file-private top-level declarations.
 */
class V5AiDiscardFlagsGoldenExporterTest {

    @Test
    fun exportAiDiscardFlagsGoldenFixture() {
        if (System.getenv("V5_AI_DISCARD_FLAGS_EXPORT") != "1") {
            return
        }
        val root = repoRoot()
        val fixture = root.resolve(FIXTURE_RELATIVE)

        // Two-pass determinism guard: independent derivations must render identically.
        val first = buildAll()
        val second = buildAll()
        val firstRender = renderCases(first)
        val secondRender = renderCases(second)
        assertEquals(firstRender, secondRender, "two independent derivations must render identically")
        assertEquals(3, first.size, "exactly three cases are exported")

        val head = currentHead()
        val dirty = trackedDirtyPaths().map { porcelainPath(it) }
        val dirtyClass = when {
            dirty.isEmpty() -> "clean"
            dirty.all { isTestOnlyDirtyPath(it) } -> "test-only"
            else -> "mixed"
        }

        val baseText = renderDocument(
            first,
            renderSha256 = null,
            head = head,
            dirty = dirty,
            dirtyClass = dirtyClass
        )
        assertFalse(baseText.contains('\r'), "rendered fixture must use LF line endings")
        val renderHash = sha256(baseText.toByteArray(StandardCharsets.UTF_8))
        val finalText = baseText.replace(
            "\"renderSha256\": null",
            "\"renderSha256\": \"$renderHash\""
        )
        assertFalse(finalText.contains("\"renderSha256\": null"), "render hash must be filled in")

        val previous = if (Files.exists(fixture)) {
            String(Files.readAllBytes(fixture), StandardCharsets.UTF_8)
        } else null
        if (previous != null && normalizeRenderHash(previous) != baseText) {
            println(
                "V5 AI discard-flags golden: fixture base changed; previous sha256=" +
                    sha256(previous.toByteArray(StandardCharsets.UTF_8)) +
                    " new base sha256=" + sha256(baseText.toByteArray(StandardCharsets.UTF_8))
            )
        }

        Files.createDirectories(fixture.parent)
        Files.write(fixture, finalText.toByteArray(StandardCharsets.UTF_8))
        val written = String(Files.readAllBytes(fixture), StandardCharsets.UTF_8)
        assertFalse(written.contains('\r'), "exported fixture must use LF line endings")
        assertTrue(written.endsWith("\n"), "exported fixture must end with LF")
        println(
            "V5 AI discard-flags golden exported: cases=${first.size} " +
                "bytes=${finalText.toByteArray(StandardCharsets.UTF_8).size} " +
                "head=$head renderSha256=$renderHash dirtyClass=$dirtyClass"
        )
    }

    @Test
    fun validateCommittedAiDiscardFlagsGoldenFixture() {
        if (System.getenv("V5_AI_DISCARD_FLAGS_EXPORT") == "1") {
            return // export mode writes the fixture; validation runs in ordinary runs
        }
        val fixture = repoRoot().resolve(FIXTURE_RELATIVE)
        assertTrue(
            Files.exists(fixture),
            "missing $FIXTURE_RELATIVE; export it first with $COMMAND_POSIX"
        )
        val text = String(Files.readAllBytes(fixture), StandardCharsets.UTF_8)
        assertFalse(text.contains('\r'), "committed fixture must use LF line endings")
        assertTrue(text.endsWith("\n"), "committed fixture must end with LF")

        // The cases section must byte-match an independent re-derivation from the live domain.
        // Inside the document the cases array is a root-object field, rendered at indent 2;
        // replicate exactly that embedding rather than a standalone top-level render.
        val committedCases = casesSection(text)
        val rederived = renderCases(buildAll(), embeddedIndent = 2)
        assertEquals(rederived, committedCases, "committed cases section diverged from live re-derivation")

        // Self-referential render hash: nulling the recorded value must reproduce it.
        val recorded = Regex("\"renderSha256\": \"([0-9a-f]{64})\"").find(text)?.groupValues?.get(1)
        assertTrue(recorded != null, "committed fixture must contain a renderSha256 digest")
        val recomputed = sha256(normalizeRenderHash(text).toByteArray(StandardCharsets.UTF_8))
        assertEquals(recorded, recomputed, "renderSha256 does not bind the committed bytes")
        println("V5 AI discard-flags golden validation passed: renderSha256=$recorded")
    }

    // ------------------------------------------------------------------
    // Case construction (real V4 play, public outputs only)
    // ------------------------------------------------------------------

    private fun buildAll(): List<J> {
        val riichiRiver = buildRiichiRiverCase()
        val claimed = buildClaimedDiscardCase()
        val riichiClaimed = buildRiichiDiscardClaimedCase()
        return listOf(riichiRiver.doc, claimed.doc, riichiClaimed.doc)
    }

    /**
     * Case riichi-river: seat 0 declares riichi (the declaration discard is a real
     * per-discard riichi instance), then all four seats complete one go-around of
     * tsumogiri. Positive: seat 0's declaration discard exports isRiichi=true and stays
     * in the active river. Negatives: the same seat's later tsumogiri and every other
     * seat's discard export isRiichi=false.
     */
    private fun buildRiichiRiverCase(): CaseBuild {
        val east0 = Tile(Suit.WIND, 1)
        val east1 = Tile(Suit.WIND, 1, 1)
        val closed = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } +
            (1..3).map { Tile(Suit.SOU, it) } + east0
        val s = TestTableBuilder.create(hands = mapOf(0 to closed), drawn = 0 to east1)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false)
        val recorded = mutableListOf<J>()

        val riichiOptions = e.legalActions(0).filterIsInstance<GameAction.DeclareRiichi>()
        assertTrue(
            GameAction.DeclareRiichi(0, east0) in riichiOptions,
            "east0 must be a legal riichi discard choice for seat 0"
        )
        submit(e, GameAction.DeclareRiichi(0, east0), recorded)

        // One full go-around: four further discards after the declaration discard. Seats 1-3
        // are driven by explicit tsumogiri; the riichi seat's own next turn auto-tsumogiris
        // through the engine's autoRiichiDiscard pipeline (still a real DiscardTile action
        // with a real TileDiscarded event). Advance itself auto-draws the next seat.
        var guard = 0
        while (bus.getHistory().filterIsInstance<GameEvent.TileDiscarded>().size < 5) {
            check(++guard <= 64) { "riichi-river go-around did not converge" }
            passWindow(e, recorded)
            if (bus.getHistory().filterIsInstance<GameEvent.TileDiscarded>().size >= 5) break
            val live = e.activeRound!!.readState()
            val cur = live.currentPlayer
            if (live.players[cur].drawnTile != null && !live.riichiDeclared[cur]) {
                tsumogiri(e, cur, recorded)
            }
        }

        val live = e.activeRound!!.readState()
        val events = bus.getHistory()
        assertTrue(
            events.none {
                it is GameEvent.TsumoWin || it is GameEvent.RonWin || it is GameEvent.DrawGame ||
                    it is GameEvent.RoundEnded || it is GameEvent.MatchEnded
            },
            "riichi-river case must not reach a settlement"
        )
        assertEquals(1, live.riichiSticks, "exactly one riichi stick was paid")
        assertEquals(24000, live.scores[0], "riichi declaration costs 1000 points")
        assertTrue(live.players[0].riichiDeclared, "seat 0 is riichi in the live state")
        assertFalse((1..3).any { live.players[it].riichiDeclared }, "no other seat declared riichi")

        val build = extractCaseDoc("riichi-river", viewerSeat = 3, live = live, events = events, recorded = recorded)
        // Case-specific guarantees: a non-empty river with exactly one riichi discard.
        assertTrue(build.riverSize >= 5, "riichi-river must export a non-empty go-around river")
        assertEquals(1, build.riverRiichiCount, "exactly the declaration discard exports isRiichi=true")
        assertEquals(0, build.riverRiichiSeat, "the riichi discard must belong to the riichi declarer")
        assertTrue(build.riverFalseCount >= 4, "the go-around must export several isRiichi=false negatives")
        assertEquals(0, build.claimedCount, "no claim happens in this case")
        return build
    }

    /**
     * Case claimed-discard: seat 1 discards MAN1(c0) and seat 2 pons it with its own two
     * copies. Positive: the claimed tile exports isClaimed=true, derived from the public
     * PonDeclared meld metadata, and is absent from the active river while remaining in
     * PlayerHand.discardHistory. Negative: every active-river entry exports isClaimed=false.
     */
    private fun buildClaimedDiscardCase(): CaseBuild {
        val m1c0 = Tile(Suit.MAN, 1)
        val m1c1 = Tile(Suit.MAN, 1, 1)
        val m1c2 = Tile(Suit.MAN, 1, 2)
        val pin7 = Tile(Suit.PIN, 7)
        val hand1 = (2..9).map { Tile(Suit.MAN, it) } + (1..5).map { Tile(Suit.PIN, it) }
        val hand2 = listOf(m1c1, m1c2) + (1..9).map { Tile(Suit.SOU, it) } +
            listOf(Tile(Suit.PIN, 6), pin7)
        val s = TestTableBuilder.create(
            hands = mapOf(1 to hand1, 2 to hand2),
            drawn = 1 to m1c0,
            currentPlayer = 1
        )
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false)
        val recorded = mutableListOf<J>()

        submit(e, GameAction.DiscardTile(1, m1c0), recorded)
        respondWindow(
            e, recorded,
            claims = mapOf(2 to { acts ->
                acts.filterIsInstance<GameAction.DeclarePon>()
                    .firstOrNull { it.tiles.toSet() == setOf(m1c1, m1c2) }
            })
        )
        val liveAfterPon = e.activeRound!!.readState()
        val ponMeld = liveAfterPon.players[2].melds.single()
        assertEquals(MeldType.PON, ponMeld.type)
        assertEquals(m1c0, ponMeld.calledTile, "the pon meld must call the discarded physical tile")
        assertEquals(1, ponMeld.fromPlayer, "the pon must record the original discarder")

        // Seat 2 sheds a tile to finish its turn naturally.
        val discards2 = e.legalActions(2).filterIsInstance<GameAction.DiscardTile>()
        assertTrue(discards2.any { it.tile == pin7 }, "seat 2 must be able to discard PIN7 after the pon")
        submit(e, GameAction.DiscardTile(2, pin7), recorded)
        passWindow(e, recorded)

        val live = e.activeRound!!.readState()
        val events = bus.getHistory()
        assertTrue(
            events.none {
                it is GameEvent.TsumoWin || it is GameEvent.RonWin || it is GameEvent.DrawGame ||
                    it is GameEvent.RoundEnded || it is GameEvent.MatchEnded
            },
            "claimed-discard case must not reach a settlement"
        )
        assertTrue(
            live.players[1].discardHistory.contains(m1c0),
            "the claimed tile must remain in the discarder's discardHistory"
        )
        assertFalse(
            live.players[1].discards.contains(m1c0),
            "the claimed tile must have left the discarder's active river"
        )

        val build = extractCaseDoc("claimed-discard", viewerSeat = 3, live = live, events = events, recorded = recorded)
        assertTrue(build.riverSize >= 1, "claimed-discard must still export a non-empty active river")
        assertEquals(1, build.claimedCount, "exactly the ponned tile exports isClaimed=true")
        assertEquals(0, build.riverRiichiCount, "no riichi declaration happens in this case")
        assertEquals(0, build.claimedRiichiCount, "the claimed tile is not a riichi discard")
        return build
    }

    /**
     * Case riichi-discard-claimed: seat 0 declares riichi discarding East(c0) and seat 1
     * immediately pons it. The strongest combination: the claimed tile exports BOTH
     * isRiichi=true (from the declaration's TileDiscarded) and isClaimed=true (from the
     * public PonDeclared meld), and seat 0's active river is empty afterwards.
     */
    private fun buildRiichiDiscardClaimedCase(): CaseBuild {
        val east0 = Tile(Suit.WIND, 1)
        val east1 = Tile(Suit.WIND, 1, 1)
        val east2 = Tile(Suit.WIND, 1, 2)
        val east3 = Tile(Suit.WIND, 1, 3)
        val sou5 = Tile(Suit.SOU, 5)
        val closed0 = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } +
            (1..3).map { Tile(Suit.SOU, it) } + east0
        val hand1 = (1..9).map { Tile(Suit.MAN, it, 1) } + listOf(Tile(Suit.SOU, 4), sou5, east2, east3)
        val s = TestTableBuilder.create(
            hands = mapOf(0 to closed0, 1 to hand1),
            drawn = 0 to east1,
            currentPlayer = 0
        )
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false)
        val recorded = mutableListOf<J>()

        val riichiOptions = e.legalActions(0).filterIsInstance<GameAction.DeclareRiichi>()
        assertTrue(
            GameAction.DeclareRiichi(0, east0) in riichiOptions,
            "east0 must be a legal riichi discard choice for seat 0"
        )
        submit(e, GameAction.DeclareRiichi(0, east0), recorded)
        respondWindow(
            e, recorded,
            claims = mapOf(1 to { acts ->
                acts.filterIsInstance<GameAction.DeclarePon>()
                    .firstOrNull { it.tiles.toSet() == setOf(east2, east3) }
            })
        )
        val liveAfterPon = e.activeRound!!.readState()
        val ponMeld = liveAfterPon.players[1].melds.single()
        assertEquals(MeldType.PON, ponMeld.type)
        assertEquals(east0, ponMeld.calledTile, "the pon meld must call the riichi discard")
        assertEquals(0, ponMeld.fromPlayer, "the pon must record the riichi declarer as discarder")
        assertEquals(24000, liveAfterPon.scores[0], "the riichi stick stays paid even when the discard is claimed")

        val discards1 = e.legalActions(1).filterIsInstance<GameAction.DiscardTile>()
        assertTrue(discards1.any { it.tile == sou5 }, "seat 1 must be able to discard SOU5 after the pon")
        submit(e, GameAction.DiscardTile(1, sou5), recorded)
        passWindow(e, recorded)

        val live = e.activeRound!!.readState()
        val events = bus.getHistory()
        assertTrue(
            events.none {
                it is GameEvent.TsumoWin || it is GameEvent.RonWin || it is GameEvent.DrawGame ||
                    it is GameEvent.RoundEnded || it is GameEvent.MatchEnded
            },
            "riichi-discard-claimed case must not reach a settlement"
        )
        assertTrue(
            live.players[0].discards.isEmpty(),
            "the riichi declarer's active river must be empty: its only discard was claimed"
        )
        assertTrue(
            live.players[0].discardHistory.contains(east0),
            "the claimed riichi discard stays in the declarer's discardHistory"
        )
        assertEquals(1, live.riichiSticks, "exactly one riichi stick was paid")

        val build = extractCaseDoc(
            "riichi-discard-claimed", viewerSeat = 3, live = live, events = events, recorded = recorded
        )
        assertTrue(build.riverSize >= 1, "the case must still export a non-empty active river")
        assertEquals(1, build.claimedCount, "exactly the ponned riichi discard exports isClaimed=true")
        assertEquals(1, build.claimedRiichiCount, "the claimed riichi discard exports isRiichi=true AND isClaimed=true")
        assertEquals(0, build.riverRiichiCount, "the riichi discard left the active river, so no river entry is riichi")
        return build
    }

    // ------------------------------------------------------------------
    // Drive helpers (public legal actions only)
    // ------------------------------------------------------------------

    private fun submit(e: ApplicationCoordinator, action: GameAction, recorded: MutableList<J>) {
        val result = e.handleAction(action)
        assertTrue(result is ActionResult.Accepted, "$action -> $result")
        recorded += actionDoc(action)
    }

    private fun actionDoc(action: GameAction): J = when (action) {
        is GameAction.DiscardTile -> obj(
            "action" to str("DiscardTile"), "player" to num(action.player), "tileId" to num(action.tile.tileId)
        )
        is GameAction.DeclareRiichi -> obj(
            "action" to str("DeclareRiichi"), "player" to num(action.player),
            "discardTileId" to num(action.discardTile.tileId)
        )
        is GameAction.DeclarePon -> obj(
            "action" to str("DeclarePon"), "player" to num(action.player),
            "tileIds" to arr(action.tiles.map { num(it.tileId) })
        )
        is GameAction.DrawTile -> obj("action" to str("DrawTile"), "player" to num(action.player))
        is GameAction.Pass -> obj("action" to str("Pass"), "player" to num(action.player))
        else -> obj("action" to str(action::class.simpleName ?: "Action"), "player" to num(-1))
    }

    /** Passes every open response window; never submits claims, draws or discards. */
    private fun passWindow(e: ApplicationCoordinator, recorded: MutableList<J>) {
        repeat(12) {
            var acted = false
            for (seat in 0..3) {
                val pass = e.legalActions(seat).filterIsInstance<GameAction.Pass>().firstOrNull { it.player == seat }
                if (pass != null) {
                    submit(e, pass, recorded)
                    acted = true
                }
            }
            if (!acted) return
        }
        error("response window did not converge")
    }

    /**
     * Resolves one response window, submitting the scenario's designated claim (once) and
     * passing everything else. A claim lambda returning null means "not offered yet"; the
     * seat then falls back to Pass. Claims are consumed after their first invocation so a
     * committed claim cannot be re-evaluated against the claimer's later discard phase.
     */
    private fun respondWindow(
        e: ApplicationCoordinator,
        recorded: MutableList<J>,
        claims: Map<Int, (List<GameAction>) -> GameAction?>
    ) {
        val pendingClaims = claims.keys.toMutableSet()
        repeat(12) {
            var acted = false
            for (seat in 0..3) {
                val acts = e.legalActions(seat)
                if (acts.isEmpty()) continue
                val claimFn = if (seat in pendingClaims) claims[seat] else null
                val claimed = claimFn?.invoke(acts)
                if (claimFn != null) pendingClaims.remove(seat)
                val chosen = claimed
                    ?: acts.filterIsInstance<GameAction.Pass>().firstOrNull { it.player == seat }
                    ?: continue
                submit(e, chosen, recorded)
                acted = true
            }
            if (!acted) return
        }
        error("response window did not converge")
    }

    private fun tsumogiri(e: ApplicationCoordinator, seat: Int, recorded: MutableList<J>) {
        val live = e.activeRound!!.readState()
        val drawn = live.players[seat].drawnTile
            ?: error(
                "seat $seat has no drawn tile: phase=${live.phase} current=${live.currentPlayer} " +
                    "actions=${e.legalActions(seat)}"
            )
        val action = e.legalActions(seat).filterIsInstance<GameAction.DiscardTile>()
            .firstOrNull { it.tile == drawn }
            ?: error("seat $seat cannot tsumogiri the drawn tile $drawn")
        submit(e, action, recorded)
    }

    // ------------------------------------------------------------------
    // Extraction from public outputs
    // ------------------------------------------------------------------

    private class CaseBuild(
        val doc: J,
        val riverSize: Int,
        val riverRiichiCount: Int,
        val riverRiichiSeat: Int,
        val riverFalseCount: Int,
        val claimedCount: Int,
        val claimedRiichiCount: Int,
    )

    private fun extractCaseDoc(
        caseId: String,
        viewerSeat: Int,
        live: GameState,
        events: List<GameEvent>,
        recorded: MutableList<J>,
    ): CaseBuild {
        val view = PlayerView.fromGameState(live, viewerSeat, emptyList())

        val riichiDiscards = events.filterIsInstance<GameEvent.TileDiscarded>().filter { it.isRiichiDiscard }

        var riverRiichiCount = 0
        var riverRiichiSeat = -1
        var riverFalseCount = 0
        var riverSize = 0
        val riverDocs = mutableListOf<J>()
        for (seat in 0..3) {
            for ((index, tile) in view.discards[seat].withIndex()) {
                val isRiichi = riichiDiscards.any { it.player == seat && it.tile == tile }
                // V4 construction: the active river never contains a claimed tile.
                val isClaimed = false
                if (isRiichi) {
                    riverRiichiCount++
                    riverRiichiSeat = seat
                } else {
                    riverFalseCount++
                }
                riverSize++
                riverDocs += obj(
                    "seat" to num(seat),
                    "riverIndex" to num(index),
                    "tileId" to num(tile.tileId),
                    "isRiichi" to bool(isRiichi),
                    "isClaimed" to bool(isClaimed)
                )
            }
        }

        var claimedCount = 0
        var claimedRiichiCount = 0
        val claimedDocs = mutableListOf<J>()
        for (seat in 0..3) {
            for (meld in view.melds[seat]) {
                val from = meld.fromPlayer
                if (from < 0) continue
                val called = meld.calledTile
                val isRiichi = riichiDiscards.any { it.player == from && it.tile == called }
                val absentFromRiver = !view.discards[from].contains(called)
                val presentInHistory = live.players[from].discardHistory.contains(called)
                assertTrue(absentFromRiver, "a called tile must be absent from the caller's active river")
                assertTrue(presentInHistory, "a called tile must remain in the discarder's discardHistory")
                assertFalse(
                    view.discards.any { river -> river.contains(called) },
                    "a called tile must not appear in any active river"
                )
                claimedCount++
                if (isRiichi) claimedRiichiCount++
                claimedDocs += obj(
                    "seat" to num(from),
                    "tileId" to num(called.tileId),
                    "isRiichi" to bool(isRiichi),
                    "isClaimed" to bool(true),
                    "absentFromActiveRiver" to bool(absentFromRiver),
                    "presentInDiscardHistory" to bool(presentInHistory),
                    "derivedFrom" to obj(
                        "eventType" to str("PonDeclared"),
                        "meldSeat" to num(seat),
                        "calledTileId" to num(called.tileId),
                        "fromPlayer" to num(from)
                    )
                )
            }
        }

        val eventDocs = events.mapNotNull { eventDoc(it) }
        val meldDocs = mutableListOf<J>()
        for (seat in 0..3) {
            for (meld in view.melds[seat]) meldDocs += meldDoc(seat, meld)
        }
        val historyDocs = (0..3).map { seat ->
            obj(
                "seat" to num(seat),
                "discardHistoryTileIds" to arr(live.players[seat].discardHistory.map { num(it.tileId) })
            )
        }

        val doc = obj(
            "caseId" to str(caseId),
            "viewerSeat" to num(viewerSeat),
            "recordedActions" to arr(recorded.toList()),
            "recordedEvents" to arr(eventDocs),
            "river" to arr(riverDocs),
            "riichiStatusBySeat" to arr(view.riichiStatus.map { bool(it) }),
            "claimedDiscards" to arr(claimedDocs),
            "publicMelds" to arr(meldDocs),
            "discardHistoryBySeat" to arr(historyDocs),
            "postCaseScores" to arr(live.scores.map { num(it) }),
            "riichiSticks" to num(live.riichiSticks)
        )
        return CaseBuild(doc, riverSize, riverRiichiCount, riverRiichiSeat, riverFalseCount, claimedCount, claimedRiichiCount)
    }

    private fun eventDoc(event: GameEvent): J? = when (event) {
        is GameEvent.TileDiscarded -> obj(
            "type" to str("TileDiscarded"),
            "turnNumber" to num(event.turnNumber),
            "player" to num(event.player),
            "tileId" to num(event.tile.tileId),
            "isRiichiDiscard" to bool(event.isRiichiDiscard),
            "isTsumogiri" to bool(event.isTsumogiri)
        )
        is GameEvent.RiichiDeclared -> obj(
            "type" to str("RiichiDeclared"),
            "turnNumber" to num(event.turnNumber),
            "player" to num(event.player),
            "discardTileId" to num(event.discardTile.tileId),
            "isDoubleRiichi" to bool(event.isDoubleRiichi)
        )
        is GameEvent.PonDeclared -> obj(
            "type" to str("PonDeclared"),
            "turnNumber" to num(event.turnNumber),
            "player" to num(event.player),
            "calledTileId" to num(event.meld.calledTile.tileId),
            "fromPlayer" to num(event.fromPlayer)
        )
        is GameEvent.ChiDeclared -> obj(
            "type" to str("ChiDeclared"),
            "turnNumber" to num(event.turnNumber),
            "player" to num(event.player),
            "calledTileId" to num(event.meld.calledTile.tileId),
            "fromPlayer" to num(event.fromPlayer)
        )
        is GameEvent.KanDeclared -> obj(
            "type" to str("KanDeclared"),
            "turnNumber" to num(event.turnNumber),
            "player" to num(event.player),
            "calledTileId" to num(event.meld.calledTile.tileId),
            "fromPlayer" to num(event.meld.fromPlayer),
            "kanType" to str(event.kanType.name)
        )
        // Private-information events (TileDealt/TileDrawn/RinshanDraw), dora/cheat/score
        // events and settlement events are irrelevant to the discard flags and are omitted;
        // settlements are additionally asserted absent per case.
        else -> null
    }

    private fun meldDoc(seat: Int, meld: Meld): J = obj(
        "seat" to num(seat),
        "type" to str(meld.type.name),
        "tileIds" to arr(meld.tiles.map { num(it.tileId) }),
        "calledTileId" to num(meld.calledTile.tileId),
        "fromPlayer" to num(meld.fromPlayer)
    )

    // ------------------------------------------------------------------
    // Rendering / hashing / provenance
    // ------------------------------------------------------------------

    private fun renderCases(cases: List<J>, embeddedIndent: Int = 0): String =
        J.Arr(cases).render(embeddedIndent)

    private fun casesSection(text: String): String {
        val start = text.indexOf("\"cases\": ")
        assertTrue(start >= 0, "committed fixture must contain a cases section")
        // Slice exactly the cases array: from its opening '[' to the document's last ']'
        // (cases is the final document field). Suffix-stripping is unreliable here because
        // the file's trailing LF defeats removeSuffix, and a pre-trim leaves ']\\n' inside.
        val end = text.lastIndexOf(']')
        assertTrue(end > start, "committed fixture must contain a closed cases array")
        return text.substring(start + "\"cases\": ".length, end + 1)
    }

    private fun normalizeRenderHash(text: String): String =
        Regex("\"renderSha256\":\\s*\"[0-9a-f]{64}\"").replace(text, "\"renderSha256\": null")

    private fun renderDocument(
        cases: List<J>,
        renderSha256: String?,
        head: String,
        dirty: List<String>,
        dirtyClass: String,
    ): String = obj(
        "fixture" to str("ai-discard-flags-v1"),
        "version" to num(1),
        "description" to str(
            "Per-discard isRiichi/isClaimed behavior evidence exported from real V4 domain play. " +
                "Cases are driven through ApplicationCoordinator legal actions; every value is read " +
                "from the published GameEvent stream, the public PlayerView projection or " +
                "PlayerHand.discardHistory. No expected value is hand-written."
        ),
        "flagSemantics" to obj(
            "isRiichi" to str(
                "true iff the matching GameEvent.TileDiscarded published by V4 for this physical " +
                    "tile had isRiichiDiscard=true (set by the DeclareRiichi transition)."
            ),
            "isClaimed" to str(
                "V4 PlayerView.discards holds only tiles not yet claimed, so every river entry " +
                    "exports isClaimed=false. A tile claimed by pon leaves the active river, stays " +
                    "in PlayerHand.discardHistory and is identified by public meld metadata " +
                    "(Meld.calledTile + Meld.fromPlayer, published via PonDeclared); such tiles are " +
                    "exported as claimedDiscards entries with isClaimed=true."
            ),
            "sourceBoundary" to str(
                "V4 PlayerView exposes no per-discard claimed/riichi boolean. Both flags here are " +
                    "derived only from public events, the public projection and public meld metadata. " +
                    "No V4 production code was modified to manufacture them."
            )
        ),
        "provenance" to obj(
            "generator" to str(GENERATOR_PATH),
            "generatorVersion" to num(1),
            "generatedByCommandPosix" to str(COMMAND_POSIX),
            "generatedByCommandPowershell" to str(COMMAND_POWERSHELL),
            "nativeExitCode" to num(0),
            "nativeExitCodeNote" to str(
                "This file is written only after every exporter assertion passed, so the exporting " +
                    "Gradle test invocation exited 0. The observed numeric exit code and the full log " +
                    "SHA-256 are additionally recorded in the delivery report."
            ),
            "v4EngineLastCommit" to str(git("log", "-1", "--format=%H", "--", "engine/src/main/kotlin")),
            "generatedAtHead" to str(head),
            "trackedDirtyPaths" to arr(dirty.map { str(it) }),
            "trackedDirtyClass" to str(dirtyClass),
            "renderSha256" to (if (renderSha256 == null) J.Null else str(renderSha256)),
            "sourceHashes" to arr(SOURCE_PATHS.map { path -> sourceHashDoc(repoRoot(), path) })
        ),
        "cases" to J.Arr(cases)
    ).render() + "\n"

    private fun sourceHashDoc(root: Path, relativePath: String): J {
        val path = root.resolve(relativePath)
        assertTrue(Files.exists(path), "source file to pin must exist: $relativePath")
        val raw = Files.readAllBytes(path)
        val text = String(raw, StandardCharsets.UTF_8)
        val lf = text.replace("\r\n", "\n").replace("\r", "\n")
        return obj(
            "path" to str(relativePath),
            "sha256" to str(sha256(raw)),
            "sha256Lf" to str(sha256(lf.toByteArray(StandardCharsets.UTF_8))),
            "lastCommit" to str(lastCommitFor(relativePath))
        )
    }

    private fun repoRoot(): Path {
        var path: Path? = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        while (path != null) {
            if (Files.exists(path.resolve("AGENTS.md")) && Files.exists(path.resolve("dotnet"))) {
                return path
            }
            path = path.parent
        }
        error("cannot locate repository root from ${System.getProperty("user.dir")}")
    }

    private fun git(vararg args: String): String {
        return try {
            val process = ProcessBuilder(listOf("git", "-C", repoRoot().toString()) + args)
                .redirectErrorStream(true)
                .start()
            val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
            if (process.waitFor() == 0) text else "unknown"
        } catch (e: Exception) {
            "unknown"
        }
    }

    private fun currentHead(): String = git("rev-parse", "HEAD")

    private fun lastCommitFor(relativePath: String): String =
        git("log", "-1", "--format=%H", "--", relativePath).ifEmpty { "unknown" }

    private fun trackedDirtyPaths(): List<String> {
        val text = gitRaw("status", "--porcelain", "--untracked-files=no")
        if (text == "unknown" || text.isBlank()) return emptyList()
        return text.lineSequence().map { it.trimEnd() }.filter { it.isNotBlank() }.toList()
    }

    private fun gitRaw(vararg args: String): String {
        return try {
            val process = ProcessBuilder(listOf("git", "-C", repoRoot().toString()) + args)
                .redirectErrorStream(true)
                .start()
            val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }
            if (process.waitFor() == 0) text else "unknown"
        } catch (e: Exception) {
            "unknown"
        }
    }

    private fun porcelainPath(line: String): String =
        line.drop(3).substringAfter(" -> ").trim()

    private fun isTestOnlyDirtyPath(path: String): Boolean =
        path.startsWith("engine/src/test/") ||
            path.startsWith("tests/v5-golden/") ||
            path.startsWith("dotnet/tests/Mahjong.Ai.Tests/") ||
            path.startsWith("doc/v5-重构/")

    private fun sha256(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(bytes).joinToString("") { String.format(Locale.ROOT, "%02x", it.toInt() and 0xff) }
    }

    // ----------------------------------------------------------------------
    // Minimal deterministic JSON writer (test-owned, no external dependency).
    // Nested on purpose: the frozen ai-v1 exporter in this package owns same-named
    // file-private top-level declarations, and top-level names would collide.
    // ----------------------------------------------------------------------
    private sealed class J {
        class Obj(val fields: List<Pair<String, J>>) : J()
        class Arr(val items: List<J>) : J()
        class Str(val value: String) : J()
        class Num(val value: Long) : J()
        class Bool(val value: Boolean) : J()
        object Null : J()

        fun render(): String {
            val sb = StringBuilder()
            renderTo(sb, 0)
            return sb.toString()
        }

        fun render(indent: Int): String {
            val sb = StringBuilder()
            renderTo(sb, indent)
            return sb.toString()
        }

        private fun renderTo(sb: StringBuilder, indent: Int) {
            when (this) {
                is Str -> sb.append(quote(value))
                is Num -> sb.append(value)
                is Bool -> sb.append(if (value) "true" else "false")
                is Arr -> {
                    sb.append('[')
                    items.forEachIndexed { index, item ->
                        if (index > 0) sb.append(',')
                        sb.append('\n').append(" ".repeat(indent + 2))
                        item.renderTo(sb, indent + 2)
                    }
                    if (items.isNotEmpty()) sb.append('\n').append(" ".repeat(indent))
                    sb.append(']')
                }
                is Obj -> {
                    sb.append('{')
                    fields.forEachIndexed { index, (name, value) ->
                        if (index > 0) sb.append(',')
                        sb.append('\n').append(" ".repeat(indent + 2))
                        sb.append(quote(name)).append(": ")
                        value.renderTo(sb, indent + 2)
                    }
                    if (fields.isNotEmpty()) sb.append('\n').append(" ".repeat(indent))
                    sb.append('}')
                }
                Null -> sb.append("null")
            }
        }

        private fun quote(value: String): String {
            val sb = StringBuilder("\"")
            for (c in value) {
                when (c) {
                    '"' -> sb.append("\\\"")
                    '\\' -> sb.append("\\\\")
                    '\n' -> sb.append("\\n")
                    '\r' -> sb.append("\\r")
                    '\t' -> sb.append("\\t")
                    else -> if (c < ' ') sb.append(String.format("\\u%04x", c.code)) else sb.append(c)
                }
            }
            return sb.append('"').toString()
        }
    }

    private fun str(value: String): J = J.Str(value)
    private fun num(value: Int): J = J.Num(value.toLong())
    private fun num(value: Long): J = J.Num(value)
    private fun bool(value: Boolean): J = J.Bool(value)
    private fun obj(vararg fields: Pair<String, J>): J = J.Obj(fields.toList())
    private fun arr(items: List<J>): J = J.Arr(items)

    companion object {
        private const val FIXTURE_RELATIVE = "tests/v5-golden/ai-discard-flags-v1.json"
        private const val GENERATOR_PATH =
            "engine/src/test/kotlin/com/majiang/state/V5AiDiscardFlagsGoldenExporterTest.kt"
        private const val COMMAND_POSIX =
            "V5_AI_DISCARD_FLAGS_EXPORT=1 ./gradlew :engine:test --tests \"com.majiang.state.V5AiDiscardFlagsGoldenExporterTest\" --rerun-tasks --console=plain"
        private const val COMMAND_POWERSHELL =
            "\$env:V5_AI_DISCARD_FLAGS_EXPORT='1'; .\\gradlew.bat :engine:test --tests \"com.majiang.state.V5AiDiscardFlagsGoldenExporterTest\" --console=plain"

        /** The real V4 sources and the C# consumption boundary this fixture's values come from. */
        private val SOURCE_PATHS = listOf(
            "engine/src/main/kotlin/com/majiang/session/PlayerView.kt",
            "engine/src/main/kotlin/com/majiang/event/GameEvent.kt",
            "engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt",
            "engine/src/main/kotlin/com/majiang/model/TileMovement.kt",
            "engine/src/main/kotlin/com/majiang/model/PlayerHand.kt",
            "engine/src/main/kotlin/com/majiang/model/Meld.kt",
            "engine/src/main/kotlin/com/majiang/model/Tile.kt",
            "engine/src/main/kotlin/com/majiang/state/GameAction.kt",
            GENERATOR_PATH,
            "dotnet/src/Mahjong.Ai/AiObservation.cs",
            "dotnet/src/Mahjong.TableRuntime/TableActor.cs"
        )
    }
}

package com.majiang.v4lobby

import com.google.gson.JsonParser
import com.majiang.access.PlayerSnapshot
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.authority.TableAuthority
import com.majiang.client.api.ProtocolViewMapper
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.session.MeldChoiceKind
import com.majiang.session.MeldChoiceProvider
import com.majiang.session.RiichiPreviewProvider
import com.majiang.session.TenpaiPreviewProvider
import com.majiang.state.GameAction
import com.majiang.state.SeatController
import com.majiang.testing.MeldChoiceFixtures
import com.majiang.testing.TestTableBuilder
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolPlayerUpdate
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/** Independent authority-to-client probes. Expected rules come from the pre-existing providers. */
@Timeout(15)
class V4PreviewAcceptanceTest {
    private val humans = List(4) { SeatController.LocalHuman() }
    private fun waiting() = MeldChoiceFixtures.tiles(Suit.MAN, "123456") +
        MeldChoiceFixtures.tiles(Suit.PIN, "789") + MeldChoiceFixtures.tiles(Suit.SOU, "2345")

    private fun snapshot(authority: TableAuthority, seat: Int = 2) =
        authority.querySnapshot(seat, "preview-review", 7L, List(4) {
            val role = when (authority.controllerFor(it)) {
                is SeatController.LocalHuman, is SeatController.RemoteHuman -> "HUMAN"
                is SeatController.Computer -> "COMPUTER"
                is SeatController.Takeover -> "TAKEOVER"
            }
            SeatControlView(it, true, role,
                authority.coordinator.epochFor(it))
        })

    // Only new preview getters are reflected while the independently implemented DTO names settle.
    // There is no fallback for a missing field: omission is an acceptance failure.
    private fun field(value: Any, name: String): Any? {
        val suffix = name.replaceFirstChar { it.uppercaseChar() }
        val getter = value.javaClass.methods.singleOrNull {
            it.parameterCount == 0 && (it.name == "get$suffix" || it.name == "is$suffix")
        } ?: error("Missing preview getter ${value.javaClass.name}.$name")
        return getter.invoke(value)
    }
    private fun required(value: Any, name: String): Any =
        requireNotNull(field(value, name)) { "${value.javaClass.name}.$name is null" }
    private fun preview(snapshot: Any) = required(snapshot, "presentationPreview")
    @Suppress("UNCHECKED_CAST")
    private fun items(value: Any, name: String): List<Any> = required(value, name) as List<Any>
    private fun tileId(value: Any) = field(value, "tileId") as Int
    private fun tileIds(value: Any, name: String) = items(value, name).map(::tileId)
    private fun optionId(value: Any) = required(value, "optionId") as String

    private fun representations(source: PlayerSnapshot): List<Any> {
        val wire = EngineProtocolMapper.toProtocolSnapshot(source)
        val decoded = ApiMessageCodec.decodeSnapshot(ApiMessageCodec.encodeSnapshot(wire))
        val push = ApiMessageCodec.decodePush(ApiMessageCodec.encodePush(
            ProtocolPlayerUpdate(wire, fromCursor = 6L, isFullSnapshot = true)))
        assertEquals(wire, decoded, "Snapshot codec must preserve preview equality")
        assertEquals(wire.hashCode(), decoded.hashCode())
        assertEquals(decoded, push.snapshot, "Full recovery push must preserve private previews")
        assertEquals(source, source.deepCopy())
        return listOf(source, source.deepCopy(), wire, wire.copy(), decoded,
            ProtocolViewMapper.mapSnapshot(decoded), ProtocolViewMapper.mapSnapshot(push.snapshot))
    }

    private fun assertMetadata(source: PlayerSnapshot, result: Any) {
        assertEquals(source.roundId, field(result, "roundId"))
        assertEquals(source.decision.decisionId, field(result, "decisionId"))
        assertEquals(source.decision.controlEpoch, field(result, "controlEpoch"))
    }

    @Test fun authoritativeWaitsSurviveSnapshotAndFullRecoveryAndChangeAfterActualDiscard() {
        val draw = Tile(Suit.SOU, 6)
        val state = TestTableBuilder.create(hands = mapOf(2 to waiting()), drawn = 2 to draw)
        TableAuthority(initialState = state, controllers = humans).use { authority ->
            val before = snapshot(authority)
            assertEquals(listOf(10, 13), TenpaiPreviewProvider.waits(before.view).map { it.typeIndex })
            for (shape in representations(before)) {
                val data = preview(shape)
                assertMetadata(before, data)
                assertEquals(TenpaiPreviewProvider.waits(before.view).map { it.tileId }, tileIds(data, "waits"))
            }
            val discard = before.decision.options.single { it.action == GameAction.DiscardTile(2, Tile(Suit.SOU, 2)) }
            val result = authority.submitChoice(2, ActionChoice("preview-discard", before.roundId,
                before.decision.decisionId, before.decision.controlEpoch, discard.optionId), authority.clock.nowMillis())
            assertTrue(result.receipt.status in setOf(com.majiang.access.ReceiptStatus.APPLIED,
                com.majiang.access.ReceiptStatus.RECORDED), "Committed discard may await other seats' responses")
            assertTrue(result.stateChanged, "Discard must actually commit before checking the new waiting shape")
            val discarded = result.events.filterIsInstance<com.majiang.event.GameEvent.TileDiscarded>().single()
            assertEquals(2, discarded.player)
            assertEquals(Tile(Suit.SOU, 2), discarded.tile, "Discard event must retain the selected physical copy")
            assertFalse(discarded.isRiichiDiscard)
            val after = snapshot(authority)
            assertEquals(Tile(Suit.SOU, 2), after.view.discards[2].last())
            assertEquals(listOf(11, 14), TenpaiPreviewProvider.waits(after.view).map { it.typeIndex })
            for (shape in representations(after)) {
                assertEquals(TenpaiPreviewProvider.waits(after.view).map { it.tileId }, tileIds(preview(shape), "waits"))
            }
            TestTableBuilder.assertPhysical(authority.coordinator.activeRound!!.readState())
        }
    }

    private fun redFiveState() = TestTableBuilder.create(
        hands = mapOf(2 to (MeldChoiceFixtures.tiles(Suit.MAN, "123789") +
            MeldChoiceFixtures.tiles(Suit.PIN, "123") + MeldChoiceFixtures.tiles(Suit.SOU, "789") + Tile(Suit.MAN, 5))),
        drawn = 2 to Tile(Suit.MAN, 5, 3),
        discards = mapOf(2 to listOf(Tile(Suit.MAN, 5, 1))))

    @Test fun redAndNormalFiveRiichiKeepDistinctPhysicalDiscardsAndExactAuthorityOptionIds() {
        TableAuthority(initialState = redFiveState(), controllers = humans).use { authority ->
            val source = snapshot(authority)
            val expected = RiichiPreviewProvider.options(source.view)
            assertTrue(expected.any { it.action.discardTile.isRedFive })
            assertTrue(expected.any { it.action.discardTile == Tile(Suit.MAN, 5) })
            assertTrue(expected.any { it.furiten.discard }, "Fixture must exercise non-default furiten")
            for (shape in representations(source)) {
                val data = preview(shape)
                assertMetadata(source, data)
                val actual = items(data, "riichiOptions")
                assertEquals(expected.size, actual.size)
                for (option in expected) {
                    val id = source.decision.options.single { it.action == option.action }.optionId
                    val found = actual.single { optionId(it) == id }
                    assertEquals(option.action.discardTile.tileId, tileId(required(found, "discardTile")))
                    assertEquals(option.waits.map { it.tileId }, tileIds(found, "waits"))
                    val furiten = required(found, "furiten")
                    assertEquals(option.furiten.discard, field(furiten, "discard"))
                    assertEquals(option.furiten.temporary, field(furiten, "temporary"))
                    assertEquals(option.furiten.riichi, field(furiten, "riichi"))
                }
                assertEquals(actual.size, actual.map(::optionId).toSet().size)
            }
        }
    }

    @Test fun allMeldAlternativesPreserveFullPhysicalCompositionAndAuthorityBinding() {
        val seen = mutableSetOf<String>()
        for (fixture in listOf(MeldChoiceFixtures.chi(), MeldChoiceFixtures.ponRon(),
            MeldChoiceFixtures.openKan(), MeldChoiceFixtures.ownKans())) {
            TableAuthority(initialCoordinator = fixture.coordinator, controllers = humans).use { authority ->
                val source = snapshot(authority, fixture.seat)
                val expected = MeldChoiceKind.entries.flatMap { MeldChoiceProvider.options(source.view, it) }
                assertTrue(expected.isNotEmpty(), "Fixture must exercise actual legal meld choices")
                seen += expected.map { it.type.name }
                for (shape in representations(source)) {
                    val actual = items(preview(shape), "meldOptions")
                    assertEquals(expected.size, actual.size)
                    for (option in expected) {
                        val id = source.decision.options.single { it.action == option.action }.optionId
                        val found = actual.single { optionId(it) == id }
                        assertEquals(option.type.name, field(found, "type").toString())
                        assertEquals(option.tiles.map { it.tileId }, tileIds(found, "tiles"))
                        assertEquals(option.handTiles.map { it.tileId }, tileIds(found, "handTiles"))
                        assertEquals(option.calledTile?.tileId, field(found, "calledTile")?.let(::tileId))
                        assertEquals(option.addedTile?.tileId, field(found, "addedTile")?.let(::tileId))
                        assertEquals(option.fromPlayer, field(found, "fromPlayer"))
                    }
                }
            }
        }
        assertEquals(setOf("CHI", "PON", "OPEN_KAN", "CLOSED_KAN", "ADDED_KAN"), seen)
    }

    @Test fun automatedControlNeverPublishesHumanSubmittablePreviews() {
        TableAuthority(initialState = redFiveState(), controllers = humans).use { authority ->
            val humanEpoch = authority.coordinator.epochFor(2)
            authority.setSeatController(2, SeatController.Takeover())
            val source = snapshot(authority)
            assertTrue(source.decision.controlEpoch > humanEpoch)
            assertEquals("TAKEOVER", source.controls[2].role)
            assertTrue(source.decision.options.isEmpty())
            for (shape in representations(source)) {
                val data = preview(shape)
                assertMetadata(source, data)
                assertTrue(items(data, "riichiOptions").isEmpty())
                assertTrue(items(data, "meldOptions").isEmpty())
            }
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun immutable(list: List<Any>) {
        val original = list.toList()
        assertThrows(UnsupportedOperationException::class.java) { (list as MutableList<Any>).clear() }
        assertEquals(original, list)
    }

    @Test fun previewGettersAndNestedListsAreImmutableAcrossAllBoundaries() {
        TableAuthority(initialState = redFiveState(), controllers = humans).use { authority ->
            for (shape in representations(snapshot(authority))) {
                val data = preview(shape)
                immutable(items(data, "waits"))
                val options = items(data, "riichiOptions")
                assertTrue(options.isNotEmpty())
                options.forEach { immutable(items(it, "waits")) }
                immutable(options)
            }
        }
        val fixture = MeldChoiceFixtures.ownKans()
        TableAuthority(initialCoordinator = fixture.coordinator, controllers = humans).use { authority ->
            for (shape in representations(snapshot(authority))) {
                val options = items(preview(shape), "meldOptions")
                assertTrue(options.isNotEmpty())
                options.forEach {
                    immutable(items(it, "tiles"))
                    immutable(items(it, "handTiles"))
                    val action = required(it, "action")
                    val tileOwner = if (action.javaClass.methods.any { method -> method.name == "getMeld" }) {
                        required(action, "meld")
                    } else action
                    immutable(items(tileOwner, "tiles"))
                }
                immutable(options)
            }
        }
    }

    @Test fun previewConstructorsDefensivelyFreezeCallerOwnedMeldActionCollections() {
        for (fixture in listOf(MeldChoiceFixtures.chi(), MeldChoiceFixtures.ponRon(), MeldChoiceFixtures.ownKans())) {
            TableAuthority(initialCoordinator = fixture.coordinator, controllers = humans).use { authority ->
                val source = snapshot(authority, fixture.seat)
                val authoritative = requireNotNull(source.presentationPreview).meldOptions.first()
                // Preserve a real authority-produced alternative, but provide a caller-owned mutable
                // action list to the public constructor/copy boundary. Providers cannot mask this alias.
                val original = when (val action = authoritative.action) {
                    is GameAction.DeclareChi -> action.meld.tiles
                    is GameAction.DeclarePon -> action.tiles
                    is GameAction.DeclareKan -> action.tiles
                    else -> error("Fixture has no supported meld action")
                }.toList()
                val mutableTiles = original.toMutableList()
                val input = when (val action = authoritative.action) {
                    is GameAction.DeclareChi -> action.copy(meld = action.meld.copy(tiles = mutableTiles))
                    is GameAction.DeclarePon -> action.copy(tiles = mutableTiles)
                    is GameAction.DeclareKan -> action.copy(tiles = mutableTiles)
                    else -> error("Fixture has no supported meld action")
                }
                val frozen = authoritative.copy(action = input)
                val hashBeforeMutation = frozen.hashCode()
                mutableTiles.clear()
                val frozenTiles = when (val action = frozen.action) {
                    is GameAction.DeclareChi -> action.meld.tiles
                    is GameAction.DeclarePon -> action.tiles
                    is GameAction.DeclareKan -> action.tiles
                    else -> error("Meld action type changed")
                }
                assertEquals(original, frozenTiles, "Constructor must detach the supplied action list")
                assertEquals(hashBeforeMutation, frozen.hashCode(), "Caller mutation must not change value hash")
                immutable(frozenTiles)
            }
        }
    }

    @Test fun strictCodecRejectsPreviewFromOtherRoundDecisionEpochOrUnrelatedOption() {
        TableAuthority(initialState = redFiveState(), controllers = humans).use { authority ->
            val source = snapshot(authority)
            val wire = EngineProtocolMapper.toProtocolSnapshot(source)
            val json = ApiMessageCodec.encodeSnapshot(wire)
            fun reject(change: (com.google.gson.JsonObject) -> Unit) {
                val root = JsonParser.parseString(json).asJsonObject
                change(root.getAsJsonObject("presentationPreview"))
                assertThrows(RuntimeException::class.java) { ApiMessageCodec.decodeSnapshot(root.toString()) }
            }
            reject { it.addProperty("roundId", "old-round") }
            reject { it.addProperty("decisionId", "old-decision") }
            reject { it.addProperty("controlEpoch", source.decision.controlEpoch + 1L) }
            reject { it.getAsJsonArray("riichiOptions")[0].asJsonObject.addProperty("optionId", "missing-option") }
            // An existing but unrelated option ID must also fail; checking membership alone is insufficient.
            val other = source.decision.options.first { it.action is GameAction.DiscardTile }.optionId
            reject { it.getAsJsonArray("riichiOptions")[0].asJsonObject.addProperty("optionId", other) }
            val noActiveRound = JsonParser.parseString(json).asJsonObject
            noActiveRound.add("roundId", com.google.gson.JsonNull.INSTANCE)
            assertThrows(RuntimeException::class.java) {
                ApiMessageCodec.decodeSnapshot(noActiveRound.toString())
            }
        }
    }

    @Test fun strictCodecRejectsMeldCompositionContradictingUnchangedAuthorityAction() {
        val fixture = MeldChoiceFixtures.chi()
        TableAuthority(initialCoordinator = fixture.coordinator, controllers = humans).use { authority ->
            val wire = EngineProtocolMapper.toProtocolSnapshot(snapshot(authority, fixture.seat))
            val json = ApiMessageCodec.encodeSnapshot(wire)
            assertEquals(wire, ApiMessageCodec.decodeSnapshot(json), "Untampered authority snapshot must decode")

            fun reject(label: String, change: (com.google.gson.JsonObject) -> Unit) {
                val root = JsonParser.parseString(json).asJsonObject
                val option = root.getAsJsonObject("presentationPreview")
                    .getAsJsonArray("meldOptions")[0].asJsonObject
                assertEquals("CHI", option.get("type").asString)
                val originalAction = option.get("action").deepCopy()
                val originalOptionId = option.get("optionId").asString
                change(option)
                assertEquals(originalAction, option.get("action"), "Counterexample must retain the valid action")
                assertEquals(originalOptionId, option.get("optionId").asString)
                assertThrows(RuntimeException::class.java,
                    { ApiMessageCodec.decodeSnapshot(root.toString()) }, label)
            }

            assertAll("Preview fields must describe the exact unchanged decision action",
                { reject("CHI action cannot advertise a PON preview") { it.addProperty("type", "PON") } },
                { reject("CHI action cannot advertise empty participating hand tiles") {
                    it.add("handTiles", com.google.gson.JsonArray())
                } },
                { reject("The externally called tile cannot replace an owned hand tile") {
                    it.getAsJsonArray("handTiles").set(0, it.get("calledTile").deepCopy())
                } })
        }
    }

    @Test fun strictCodecRejectsMalformedPonAndAllKanVariantsWithoutChangingDecisionActions() {
        val cases = mutableListOf<org.junit.jupiter.api.function.Executable>()
        val fixtures = listOf(
            MeldChoiceFixtures.ponRon() to listOf("PON"),
            MeldChoiceFixtures.openKan() to listOf("OPEN_KAN"),
            MeldChoiceFixtures.ownKans() to listOf("CLOSED_KAN", "ADDED_KAN"))
        for ((fixture, types) in fixtures) {
            TableAuthority(initialCoordinator = fixture.coordinator, controllers = humans).use { authority ->
                val wire = EngineProtocolMapper.toProtocolSnapshot(snapshot(authority, fixture.seat))
                val json = ApiMessageCodec.encodeSnapshot(wire)
                assertEquals(wire, ApiMessageCodec.decodeSnapshot(json), "Valid ${types.joinToString()} baseline must decode")
                for (type in types) {
                    val root = JsonParser.parseString(json).asJsonObject
                    val option = root.getAsJsonObject("presentationPreview").getAsJsonArray("meldOptions")
                        .map { it.asJsonObject }.first { it.get("type").asString == type }
                    val originalAction = option.get("action").deepCopy()
                    val originalOptionId = option.get("optionId").asString
                    when (type) {
                        "PON" -> option.getAsJsonArray("handTiles").remove(0)
                        "OPEN_KAN" -> option.getAsJsonArray("tiles").let { tiles ->
                            tiles.set(tiles.size() - 1, tiles[0].deepCopy())
                        }
                        "CLOSED_KAN" -> option.getAsJsonArray("tiles").remove(0)
                        "ADDED_KAN" -> {
                            // The action carries all four physical tiles; only handTiles is the added one.
                            assertEquals(4, option.getAsJsonArray("tiles").size())
                            assertEquals(1, option.getAsJsonArray("handTiles").size())
                            assertNotEquals(option.get("addedTile"), option.get("calledTile"))
                            option.add("addedTile", option.get("calledTile").deepCopy())
                        }
                    }
                    assertEquals(originalAction, option.get("action"))
                    assertEquals(originalOptionId, option.get("optionId").asString)
                    val tamperedJson = root.toString()
                    cases += org.junit.jupiter.api.function.Executable {
                        assertThrows(RuntimeException::class.java,
                            { ApiMessageCodec.decodeSnapshot(tamperedJson) },
                            "$type malformed physical composition must be rejected without changing its valid action")
                    }
                }
            }
        }
        assertEquals(4, cases.size, "All four distinct non-CHI branches must be exercised")
        assertAll("PON and every KAN variant retain exact physical composition", cases)
    }
}

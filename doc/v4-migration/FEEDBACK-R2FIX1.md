# R2FIX1 current compiler evidence

Latest authoritative compile log is now `build/v4-migration/R2FIX1-interim-compile-2.log`: ALL THREE production modules compile. Test source compilation still fails:

- protocol: same 16 diagnostics listed below.
- client: old EngineDtoMapperTest.kt still calls removed EngineDtoMapper. This file has valid fidelity/immutability assertions; MIGRATE its fixtures/imports to protocol models and ProtocolViewMapper while preserving assertions. Do not delete/disable it or introduce EngineDtoMapper compatibility shell. Existing new ProtocolViewMapperTest does not justify dropping old coverage.
- server InProcessBindingTest.kt:129 old requestControl -> claim/release.
- server EngineProtocolMapperTest.kt:352 and :354 domain GameEvent.ScoreSettled/MatchEnded require IntArray, not List<Int>; use intArrayOf for those fixtures.
- server UdpClientSessionTest.kt:166 ActionReceipt/ReceiptStatus must protocol equivalents; same ReceiptStatus mismatch at UdpRoomTest.kt:104,110,187,193.

Read the full log for exact call sites. Keep production compile fixes focused, finish module test migrations in your assigned scope. Codex review-owned tests now compile; no changes to that directory needed.

17:49 Codex compile: production protocol and server now compile. Client still lacks mapRoomSummaries. Protocol tests have 16 diagnostics in `build/v4-migration/R2FIX1-interim-compile.log`:

- ApiDtoValidationTest.kt:21 old engine MatchConfig -> ProtocolMatchConfig; :216 obsolete binding.deepCopy() on RoomBinding. Preserve distinct snapshot-copy assertions by constructing binding.copy(snapshot = binding.snapshot.copy()), not reintroducing a compatibility API.
- FakeServerConnectionSmokeTest.kt old domain ActionChoice/ShufflePlanRequest/PlayerUpdate/ReceiptStatus imports must become protocol models; requestControl(false) should releaseControl(), requestControl(true) claimControl(), preserving original semantic assertions.
- ProtocolSerializationTest.kt:63 string params need ProtocolParamValue.StringValue; :187 missing import com.majiang.server.api.FakeServerConnection; :215/:220 obsolete safeStartingScores/safeInitialTargets -> real public fields.

Do not claim module test migrations complete until these call sites align with actual declarations. Client/server tests will compile only after mapRoomSummaries is added; Codex will then send any further diagnostics. No review test modifications.

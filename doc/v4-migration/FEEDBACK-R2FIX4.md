# R2FIX4 measured baseline

URGENT 20:00 latest compile PASSES but behavior regresses (`R2FIX4-resumed-interim-2.log`): independent 33/52, protocol 90/91. Nearly every new failure is invented `roundNumber must be >= 1: 0`. Actual engine/protocol/client roundNumber is ZERO-BASED; roundNumber=0 is valid in lobby, active first round, history and RoundEnded event fixtures. Remove all newly invented >=1 constraints; validate only actual domain/constructor invariants as assigned, never rewrite fixtures to hide this. Also unidentifiedErrorResponseRemainsDecodable fails because you now require ApiResponse.requestId despite correction below. Preserve explicit nullable requestId contract and reject only wrong supplied types. Original four defect probes now pass, so focus on these regressions and audit any other newly invented scalar ranges before final delivery.

IMPORTANT CORRECTION 19:52: original review incorrectly treated ApiResponse.requestId as required. Actual DTO declaration is String? = null (current server uses "unknown" as malformed-request fallback; DTO still explicitly permits null). Codex corrected the review test to preserve the nullable response contract and reject PRESENT but malformed requestId:{} instead. Do not change nullable DTO to appease the old assertion. Current ApiResponseAdapter silently discards wrong object types via takeIf; fix that actual bug for all nullable string fields. R2FIX4 task item 4 now reflects actual contract. This is a review correction, not permission to weaken production tests.

Codex ran expanded model probes after task launch: 25/29 passed (`build/v4-migration/R2FIX4-baseline.log`). Four actual failures:

- responseEnvelopeRequiresRequestIdentity accepts success:true with requestId=null.
- nestedControlRejectsUnsupportedRole accepts ProtocolSeatControl.role=INVALID, bypassing constructor invariant.
- legalActionsRequireExplicitPlayerIdentity accepts missing player as Pass(player=0).
- scalarScoreRejectsUnknownFields accepts unrecognized score field unexpected:1.

nestedClockRejectsFractionalDeadline already PASSES baseline (do not break it or claim it was a new observed failure); the broader nested scalar validation audit remains necessary. All previously fixed model tests still pass. Existing full green baseline includes 713 module tests and desktop/Android builds; no existing test edits by Anti. Complete narrowly assigned gaps, keep semantic defaults only where declared. Codex will verify.

19:42 user resumed; paused 299 source hashes unchanged before this run. Codex strengthened server EngineProtocolMapperTest to compare complete normalized event/score fields and roundtrip all 14 actions; all 8 mapper tests pass (`R2-mapping-full-fields.log`). Those existing test files remain Codex-owned.

19:47 registration audit of current codec: these declared ProtocolModels types still lack explicit adapters (unless your pending shared factory will handle them): ProtocolFuritenStatus, ProtocolActionOption, ProtocolActionReceipt, ProtocolAccessCapabilities, ProtocolClock, ProtocolSeatControl, ProtocolShufflePreparation, ProtocolShuffleConfig, ProtocolFuLine, ProtocolScoreResult. Also review non-Protocol-prefixed room/error/envelope DTOs listed in task. Constructor invariants and unknown enum/role/null handling must survive nested Gson decoding.

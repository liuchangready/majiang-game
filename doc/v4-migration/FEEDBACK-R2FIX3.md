# R2FIX3 independent baseline

Codex ran 24 model probes at task start (`build/v4-migration/R2FIX3-model-baseline.log`): 19 pass, 5 fail. These failures correspond directly to assigned task requirements, not new scope:

- missingRequiredSnapshotFieldsAreNotInvented: decoder accepts missing snapshot.cursor (loop also checks required view.roundWind and view.discards after that).
- clientConfigurationCopiesCallerCollections: mutate input scores[0] after constructing client.api.MatchConfig changes config.startingScores[0] from 25000 to 0. Client constructor input aliasing is real.
- publishedClientSnapshotCollectionsAreImmutable: decision.options accepts clear() (same test also checks clocks, outer discards).
- requestDecoderRejectsUnknownNestedFields: nested choice.takeover:true is accepted.
- fractionalScoreIsNotSilentlyTruncated: snapshot.view.scores[0]=25000.5 accepted/coerced.

Separate existing protocol test yakuLines Int-to-Double failure remains tracked in task. ClientParamValue equals compatibility must be removed even though explicit value tests now pass. Preserve already passing R1 21 tests/client 59 tests/server 40 tests; run nothing yourself, Codex owns checks.

18:26 interim validation after client model updates: client 59/59 PASS. Model+facade 22/25 PASS: input collection copy, published snapshot immutability and facade isolation now pass. Remaining model failures exactly missing required cursor, unknown nested choice field, fractional score. Protocol yakuLines separate failure still pending. Log `R2FIX3-client-interim.log`. Continue assigned schema fixes; no need to rewrite the now-passing client code again.

18:30 after unknown-field helper: combined independent 44/46 PASS (all 21 R1 pass); protocol 90/91. Unknown nested field test now passes. Remaining: missingRequiredSnapshotFieldsAreNotInvented; fractionalScoreIsNotSilentlyTruncated; full score yakuLines Int->Double roundtrip. Log `R2FIX3-protocol-interim.log`. Adapter still directly defaults cursor=0 and decodes List<Int> via Gson coercion; yakuLines TypeToken still yields Double. These are the final currently observed behavior failures. Continue exact typed/required decoding rather than changing assertions.

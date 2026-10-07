# GLM-C: GameClient lifecycle and recovery

Own ONLY client/src/main/kotlin/com/majiang/client/GameClient.kt and sync/**. Do not edit network (T/P), engine (H), core (U), build or tests.

Review actual code and AsyncClientAcceptanceTest/ClientSynchronizationAcceptanceTest. Specific recovery bug: triggerAutoResume recursively calls itself while isResuming still true, before finally resets it; if the returned snapshot advances but buffered later gap remains, no second request is sent and client can remain stuck forever. Retry after releasing the flag only if recovery made progress; no infinite tight loop on an unchanged server cursor or errors. Retain pending gap/read-only state on no progress, with explicit retry method if needed. Preserve no rollback of newer live updates, session/seat identity and epoch rules.

close() must immediately exceptionally complete an in-flight connect future rather than wait for access snapshot forever. No late callback may install or notify after close. Guard synchronous exceptions from subscribe/snapshot/submit/start so pending flags release and Future fails, not permanently stuck state. Avoid locks held across user callbacks and render dispatcher work; all listener payloads isolated. Do not broaden API rewriting. Codex writes additional tests and runs builds. Report GLM-C.

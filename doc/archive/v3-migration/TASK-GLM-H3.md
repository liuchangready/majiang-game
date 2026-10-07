# GLM-H3: remove synchronous session AI and preserve host responsiveness

Own engine production host/** only. No tests/build/client/core/server/DTO edits. H/H2 complete before this task starts. Read latest code.

New Codex test `engine/src/test/kotlin/com/majiang/v3/HostAiIsolationAcceptanceTest.kt`: regular turn stalled AI passes; dealer session choice test FAILS at line 64, host.tick holds table lock and directly calls strategy.decideAction in WAITING_DEALER_CHOICE. A configured AI may block forever and freeze queries/entire host.

Fix dealer-choice handling to never call strategy on the authority/render thread or while holding table lock. Either reuse bounded async scheduling with session/decision/controlEpoch validation plus bounded deterministic fallback (do not use single-round clock illegally for session choices), or explicitly define a deterministic automated session policy choosing a legal option without invoking arbitrary AI. User asks configurable AI for gameplay; session progression may use a documented host continuation/end policy. Keep one action per tick, rhythm/pause gating, no stale task can act after control reclaim. Do not introduce an infinite wait on stalled AI. Preserve human dealer choice and all old round state/score results.

Codex tests all. Report delivery-GLM-H3.md with chosen policy and reasons.

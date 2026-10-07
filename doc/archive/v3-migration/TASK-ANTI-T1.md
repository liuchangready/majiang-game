# ANTI-T1: finish real TCP transport and room lifecycle

User switched production back to Antigravity. GLM-T partially rewrote transport/server but hit quota before review. Own client/src/main/kotlin/com/majiang/network/*.kt excluding protocol/**, and server/src/main/** ONLY. Implement remaining TASK-GLM-T.md / TASK-N3.md based on current source, not a wholesale rewrite.

R1 owns protocol and host; C1 GameClient/sync; U1 core UI. No tests/build edits. Read NetworkAcceptanceTest and new client/v3/ClientTransportAcceptanceTest (blackhole peer deadline, malformed response, EOF/close pending futures). Preserve asynchronous factory signatures. Verify writes off caller, close/displacement identity, actual bound local endpoints, no lock inversion, truthful disconnect state, request timeout/heartbeat, no callbacks with dangling futures. Codex compiles/tests/reviews. Report delivery-ANTI-T1.md.

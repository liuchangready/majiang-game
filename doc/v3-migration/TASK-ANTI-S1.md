# ANTI-S1: authoritative session and recovery projection

User switched production to Antigravity. ANTI-R1 completed both prerequisite H3/P2 repairs; engine/client now compile. Implement TASK-GLM-S.md against current sources. Own engine/access/**, engine/host/TableHost.kt, client/network/protocol/** ONLY. C1 owns GameClient/sync, T1 network transport/server, U1 core. No tests/build edits.

Keep exact DTO/method contract from TASK-GLM-S.md, using appended defaults to avoid disrupting concurrent code. Preserve R1 deterministic automated session policy and generic enum repair. Read existing relevant test files and new RecoveryProjectionAcceptanceTest when available. Codex tests/builds/reviews.

Use actual domain ranking and state getters; never expose private ledger entries/reasons or opponent hidden hands. On terminal recovery include only correct visible settlement events with complete ScoreBreakdown. Paused snapshot serverNow should be the paused authority time, not later wall time (avoid a freshly connected paused client displaying exhausted reserve). Publish control/pause changes without cursor churn from serverNow alone.

Review actual codec roundtrip errors in build/v3-migration/anti-first-review.log once available. Fix issues within your owned scope, leave client lifecycle/server/UI failures to other tasks.

Report delivery-ANTI-S1.md. Implement fully; do not claim test success without actual execution.

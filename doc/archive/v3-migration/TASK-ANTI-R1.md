# ANTI-R1: resume after GLM quota, compiler and AI isolation fixes

User explicitly requests Antigravity to resume production development. All earlier GLM processes have exited, including partial deliveries. Preserve all existing work; do not restart or revert migration. Codex owns tests/builds/review.

Own ONLY client/src/main/kotlin/com/majiang/network/protocol/** and engine/src/main/kotlin/com/majiang/host/** for this small first repair. Do not edit tests, UI, GameClient/sync, transport/server, Gradle or other production modules.

1. Implement TASK-GLM-P2.md against current code: actual compile failure at ProtocolJson.kt:200, invalid generic T.entries. Use actual Kotlin generic enum API preserving strict rejection. Avoid rewriting codecs.
2. Implement TASK-GLM-H3.md: TableHost WAITING_DEALER_CHOICE directly calls arbitrary strategy under table lock, fails HostAiIsolationAcceptanceTest. Use a documented deterministic host session policy or properly isolated bounded async policy. Keep human choice unchanged, one automatic action per tick, rhythm/pause and epoch rules. All four cheat ledger tests now pass, do not alter resource accounting.

Read exact task files and test assertions. Use built-in read/edit tools, do not run shell/tests. Report delivery-ANTI-R1.md only; Codex verifies with actual Gradle commands. No external messages or extra agents.

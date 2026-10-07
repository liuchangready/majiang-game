# ANTI-U1: finish V3 UI after partial GLM work

User switched production back to Antigravity. All GLM processes ended. Own core/src/main/kotlin/com/majiang/MahjongGame.kt, screen/**, presentation/** ONLY. No tests/build/engine/client/server changes.

Implement TASK-GLM-U.md and TASK-F2.md against current code. GLM wrote three presentation adapters but did not finish composition root or GameScreen. Preserve good partial changes and inspect actual source instead of trusting old reports. Codex writes tests/runs builds. Read AsyncScreenAcceptanceTest and tools/architecture/V3RenderSmoke.java.

R1 separately fixes host/protocol, C1 fixes GameClient/sync, T1 transport/server. Do not edit their files. Later S task will append defaulted snapshot controls and settlement recovery projection; no dependency on that yet. Finish default asynchronous gameplay, UI-thread/lifecycle callback guards, input pending gate, real host ownership and menu disposal. Keep explicit legacy test seam valid. All production API calls must exist, no reflection or blocking waits on render thread.

Report delivery-ANTI-U1.md with actual edited paths, outstanding limits, no invented test success.

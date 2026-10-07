# R1FIX3 — three compilation corrections only

Continue prior context; do not inspect/rework unrelated code. Codex compiled your R1FIX2 and found exactly these compile errors (build/v4-migration/R1FIX2-review.log). Apply ONLY these tiny production corrections with file editing tools, no commands, and finish promptly so Codex can test:

1. server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt: add `import com.majiang.server.api.ApiErrors`.
2. protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt: define `const val SERVER_BUSY = "SERVER_BUSY"` and include it in `ALL` (your new in-flight capacity rejection refers to it).
3. server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt: in handleReq closing response, replace nonexistent `ApiErrors.SERVER_SHUTDOWN` with existing `ApiProtocol.SERVER_SHUTDOWN` (already imported).

Do not touch tests; Codex updates the expected error-code set. Do not change any other behavior. Do not reread the repository or repeat the prior architecture analysis. Write concise delivery-ANTI-R1FIX3.md listing the three changes and state tests not run.

# ANTI-C1: client lifecycle and gap recovery

User switched production back to Antigravity. GLM-C only read files, no writes. Own client/src/main/kotlin/com/majiang/client/GameClient.kt and sync/** ONLY. Implement TASK-GLM-C.md fully. No tests/build/network/engine/UI edits.

Read actual AsyncClientAcceptanceTest and ClientSynchronizationAcceptanceTest. Fix chained gap recovery, immediate failed pending connect on close, synchronous access exceptions, no late callback resurrection or user callback under internal lock. Keep existing public interface and physical candidate identity. Codex tests independently. Report delivery-ANTI-C1.md.

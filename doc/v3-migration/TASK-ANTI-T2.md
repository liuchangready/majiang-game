# ANTI-T2: actual server compiler failure after T1

T1 process completed. Own server/src/main/** and client/network/*.kt excluding protocol, same as T1. No other edits/tests/build.

Codex actually ran Gradle after R1. See build/v3-migration/anti-first-review.log: TableServer.kt:281 unresolved CompletableFuture, cascades at whenComplete/resp/err. The java.util.concurrent.CompletableFuture import is missing in the current actual source despite T1 static report saying imports compile. Fix that and inspect analogous real missing references. Do not rewrite transport or generate new APIs. Codex runs actual server compile and loopback tests; preserve all T1 fixes. Report delivery-ANTI-T2.md and distinguish static inspection from real test results.

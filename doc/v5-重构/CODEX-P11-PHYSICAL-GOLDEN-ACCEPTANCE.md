# P1.1 physical golden independent acceptance — 2026-10-24

Accepted delivery: `b96bdf56da847e39125be67d574a6241880567d2`, thread `thread_20261024_045327_5717`. Bridge `approve_task` returned COMPLETED. Acceptance covers only G-001a/b/c, G-002 and G-003a/b. V4 remains production; this is not G1/G5 acceptance.

The reviewer read both new test implementations, the generated JSON and delivery report, and checked the production calls and mapping boundaries. The delivery adds only its four authorized files. It changes no V4/C# production source, project configuration, verifier or existing golden file. The manifest retains its historical aggregate status; this review does not claim a fresh audit of all 73 entries.

## Independent execution

In `D:/majiang-wt/physical-golden` at the accepted delivery:

```powershell
$env:V5_PHYSICAL_GOLDEN_EXPORT='1'
.\gradlew.bat :engine:test --tests 'com.majiang.model.RedFiveTest' --tests 'com.majiang.model.TileMovementContractTest' --tests 'com.majiang.model.V5PhysicalGoldenExporterTest' --tests 'com.majiang.v3.ShuffleAlgorithmAcceptanceTest' --tests 'com.majiang.v3.ShuffleAtomicDealAcceptanceTest' --tests 'com.majiang.rules.RiichiRulesVerificationTest.34维索引正确性' --tests 'com.majiang.rules.RiichiRulesVerificationTest.136张牌物理守恒' --rerun-tasks --console=plain
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
```

| Check | Observed result |
|---|---|
| V4 exporter and targeted physical tests | exit 0; XML 33 tests, 0 failures/errors/skips |
| Domain Release tests | exit 0; 322 passed, 0 failed/skipped, including 9 new golden Facts |
| Regenerated six `samples` compared with committed JSON | Exact equality after JSON parsing and deterministic serialization |
| Original golden file restored after export | SHA256 `21a3c4c84604cd6e873b0346632d765e232704be988247f3c93d0d6d679160f2` |
| Delivery worktree / whitespace | clean; `git diff HEAD --check` passes |

The independent export truthfully records the new HEAD and clean tree. These provenance fields differ from the original pre-commit export. The actual six sample payloads are identical; no provenance was rewritten to force byte equality. Both files are retained.

Local evidence: `D:/majiang-wt/physical-golden/build/physical-golden-codex-review/` contains committed/regenerated JSON, raw Gradle and dotnet logs, all six Gradle XML suites and `sha256.csv`. This is a local evidence path, not an assertion that ignored logs are published with the PR.

## Semantic limits checked

- All 136 identities, 34 type groups, red fives, valid round trips and illegal bounds are compared through C# public APIs.
- Twenty V4 zone/owner constructions preserve actual outcomes and exception types. C# seatless factories enforce owner absence structurally; the report does not pretend to invoke an impossible invalid-owner constructor.
- Thirty-two seeds record complete walls/base walls and 4,352 positions. C# consumes the recorded order and compares tile positions and conservation. This does not claim equal JVM/C# seeded RNG or equal internal hashes.
- Missing-key and bound-ledger corruption execute real V4 audits twice and retain the damaged state. C# immutable-map constructor rejection and read-only placements are the documented structural counterpart. Duplicate MutableMap keys remain NOT_CONSTRUCTIBLE; C# mutable-ledger injection remains NOT_APPLICABLE.

## Integration and single PR

The isolated caller tree `D:/majiang-wt/codex-physical-golden-integration-20261024` starts at published PR19 head `32f645e`. It merges the previously unpushed independent evidence chain ending at `083fb02` and accepted physical delivery without conflicts. The former adds only `CODEX-P32C-INTEGRATION-20261024.md` relative to the published baseline and preserves historical integrated/remote failures.

At integration merge `182b969bd98ad4d7005d2ed48dfbf0dc20fca8e4`, the same Release Domain command passes 322/0/0, exit 0. Its raw log is retained under the caller tree's `build/physical-golden-review/`. The final acceptance commit adds documentation only; whitespace checks pass.

Publication uses the existing single PR19, `feat/v5-1-integration -> v5`; no per-task PR is created. At the batch review, CI environment restoration `thread_20261024_045327_0dd4` is still running and stream scheduling `thread_20261024_045327_5e21` is waiting review. Their unaccepted source is not part of this publication. Full-solution/real-PG/WSS gates and new remote CI are NOT_RUN by this physical-golden acceptance; historical failures remain unresolved here. Platform devices, physical networks and Unity IL2CPP are NOT_RUN.

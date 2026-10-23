# P3.1 / WSS accepted component integration

Date: 2026-10-24 (UTC+8). V4 remains the production baseline. G1 is BLOCKED.

The global bridge ledger was inspected once for this scheduled review. P3.1
`thread_20261023_230533_3b83` and WSS `thread_20261023_230533_87c3` were already
approved as components. Their former PostgreSQL dependency-only review loops
must not be restarted. Component approval does not make the batch gate PASS.

## Reviewed integration

- Existing caller worktree: `D:/majiang-wt/codex-integration-20261023`, branch
  `wt/v5-codex-integration-20261023`. Main checkout remains `d833348`; its
  unrelated `artifacts/` is preserved.
- Actor source and tests at integration `654e8e29657973f8ff0eff38b67bbccd834fb1e4`
  exactly match accepted P3.1 `5b13208dbe1f0b83e7d8b38b74299000f4552320`.
- WSS source and tests already include `b182e832264d82d7e3987fe210376a397af1f712`.
  Accepted final WSS `a4a46082eee4f70a67ad211aff4c34d3fffaad00` adds only delivery
  evidence. Its merge is `83de4fab59c521cc88cbc65f8dc8e0b19159b147`.
- `git diff --check origin/feat/v5-1-integration..HEAD` passes. Source did not
  change during this review; no unchanged full-suite rerun was needed.

The existing integrated Release run at source `654e8e2` is retained verbatim:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -RunnerMode dotnetTest `
  -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -LogDir artifacts/p31-round16-review/release
```

Result: **723 passed / 0 failed / 1 PostgreSQL skipped; OVERALL FAIL, exit 1**.
Solution build and scratch copy of 12 DLLs PASS. Per-project passes are Client
132, Domain 313, AI 14, Lobby 77, Contracts 82, TableRuntime 25, WSS 80. The
single skip belongs to the previously integrated Lobby baseline. Original
summary, commands and logs remain under the stated artifact directory; this
review inspected their actual contents. Independent actor probes previously
passed 3/3 and WSS probes 17/17; these are prior executed evidence, not new runs.

This accepted-component update goes to the existing single PR #19,
`feat/v5-1-integration -> v5`. The remote head before this update was
`5770b8a37e76dafa396cdcada7028269e6939f47`; ancestry is a normal fast-forward.
Neither the unaccepted PostgreSQL follow-up nor in-flight CI diagnostics are
included. The caller owns push and merge-back records. No default branch or
receiver worktree is reset or updated.

## Remaining evidence

- PostgreSQL round 2 `4cabd6b` is reviewed separately in caller-only candidate
  worktree `D:/majiang-wt/codex-pg-review-20261024` (candidate `1b0a0ea`). It is
  not accepted or published by this component integration.
- CI diagnostics `thread_20261024_004515_a654` remains in same-thread GLM
  round-2 rework for log collisions, failed purge provenance and exceptions.
- Existing remote runs 39977041658 (PR) and 39977029218 (push) at `5770b8a`
  both FAIL with 618 passed / 0 failed / 1 PostgreSQL skip. Their logs and
  artifacts are preserved under `artifacts/progress-20261023-2345/`.
- Historical run 39970580589's Client failure remains UNRESOLVED: the uploaded
  bundle lacks the referenced original framework failure log. Later passes
  do not establish its cause. No blind retry or flaky classification is made.
- Remote verification of this new integration head, P3.2, P3.3, P3.4 and G1
  remain pending. Unity/IL2CPP, Android/iOS devices and physical-network claims
  remain subject to their actual evidence and environment gates.

Scheduler `Mahjong-V5-0-1-Progress` is enabled, PT30M, IgnoreNew. The completion
flag is absent and must remain absent before genuine G5 completion.

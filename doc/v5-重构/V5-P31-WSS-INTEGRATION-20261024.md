# P3.1 / WSS accepted component integration

Date: 2026-10-24 (UTC+8). V4 remains the production baseline. G1 is BLOCKED.

The global bridge ledger was inspected once for this scheduled review. P3.1
`thread_20261023_230533_3b83` and WSS `thread_20261023_230533_87c3` were already
approved as components. Their former PostgreSQL dependency-only review loops
must not be restarted. Component approval does not make the batch gate PASS.

## Reviewed integration

- Final caller worktree: `D:/majiang-wt/codex-integration-20261024`, branch
  `wt/v5-codex-integration-20261024`. Main checkout remains `d833348`; its
  unrelated `artifacts/` is preserved. The older caller integration tree is
  preserved and must not be reused concurrently.
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
The final source tree excludes the unaccepted PostgreSQL follow-up and
in-flight CI diagnostics. The correction below records the initial accidental
inclusion without rewriting history. The caller owns push and merge-back
records. No default branch or receiver worktree is reset or updated.

## Concurrent caller correction

Another caller merged PostgreSQL candidate `a6ae227` into the old shared caller
worktree between this review's checks and its WSS evidence merge. The resulting
`2fa3171` therefore accidentally included unaccepted PG follow-up `4cabd6b`.
The caller failed to recheck the complete parent/source diff immediately before
pushing and corrects that error explicitly here; the earlier claim that PG was
excluded from `2fa3171` was incorrect.

An isolated new caller worktree was created from `2fa3171`. Normal commit
`78ef4bad23cf26460ae61d9c9af0c81389d21917` reverts only the PG candidate merge,
with no force push. `git diff --exit-code 654e8e2..HEAD -- dotnet tools unity
.github` then exits 0: final code/test/build/platform files are exactly the
previously reviewed accepted-component source, with only delivery documentation
added. The unaccepted PG history remains auditable; it is not accepted by the
revert or the component merge records. Future PG integration must deliberately
restore the reverted component plus its accepted fixes, rather than rely on
ancestry alone.

Automatic runs 39993867388 (PR) and 39993860742 (push) on `2fa3171` were cancelled
after discovering the accidental inclusion; neither is PASS evidence. Only
reviewed final source may be used for subsequent CI. No receiver tree was reset.
The unused P3.3 preparation at `D:/majiang-wt/p33` was never dispatched and must
not be used as a task baseline; it contains the superseded `2fa3171` tree.

## Remaining evidence

- PostgreSQL round 2 `4cabd6b` is reviewed separately in caller-only candidate
  worktree `D:/majiang-wt/codex-pg-review-20261024` (candidate `1b0a0ea`). It is
  not accepted or published by this component integration.
- Independent PG review at `1b0a0ea` actually ran `verify-all.ps1` with an owned
  PostgreSQL 16.10 on loopback 55439: build 0 warning/error, ordinary Lobby suite
  89 passed / 0 failed / 2 phase skips, then separate writer/read processes each
  1 passed / 0 skipped around a real immediate database restart. Postmaster
  start changed from `2026-10-23 17:15:22.236353+00` to
  `2026-10-23 17:15:46.515195+00`; durable snapshot included five receipts and one
  ticket consumption. Harness exit 0 is not the unified gate result.
- Independent negative PG checks at owned loopback 55438 pass 13/13: five
  foreign-listener entry points reject without changing the simulated other
  cluster, repeated start preserves marker, and cleanup WhatIf preserves process
  identity/files. Both temporary negative clusters were cleaned up.
- With the real PG configuration still set, the unchanged unified Release
  `dotnetTest` entry at candidate `1b0a0ea` gives **735 passed / 0 failed /
  2 skipped; OVERALL FAIL, exit 1**, build and 12 DLL copy PASS. Both phase-only
  Facts inevitably skip in the normal entry. This is a material PG integration
  defect, not a missing DB environment. The original PG thread is already in
  round-3 rework for this same defect from another caller; no duplicate rework
  is sent. After that review, the owned positive PG was safely cleaned up.
- Raw candidate logs, original framework TestResults copies, summary and
  cleanup evidence are under
  `D:/majiang-wt/codex-pg-review-20261024/artifacts/codex-review-20261024-0115/`.
  PG restart archive is `D:/majiang-pg-tmp/p53-pg-evidence-20261024-011458/`;
  negative evidence is
  `D:/majiang-pg-tmp/codex-pg-negative-20261024-0120/negative-summary.json`.
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

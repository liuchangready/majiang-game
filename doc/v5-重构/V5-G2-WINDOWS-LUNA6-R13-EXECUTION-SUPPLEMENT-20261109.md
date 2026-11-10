# G2 Windows Luna6 execution supplement — Round 13

Status: **NOT ACCEPTED**. The bridge supplied clean HEAD `c08236b9f052ae07783472ef23211d8758f0e739` (parent `1d5c600811a4078d2a80fb1f0bf852bb3ea85f6b`, merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). The only code change is the in-scope WindowsActions runner. The 32-row Binding remains at its last reviewed 18 PASS / 11 PARTIAL / 3 NOT_RUN; no product row was upgraded this round.

## D-12-01 — G4 log gate accepted corrupted results (MAJOR; fixed in source)

- **Location:** `tools/unity/run-p45-c04-player-actions.ps1`, `Assert-C04LocalControlProbeResult` and the same-package G4 result branch.
- **Reproduction:** `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File E:/g2-review-r12-e4ab1cfd84cc477d81db25053c424370/repro-control-gate.ps1` extracts and invokes the current function. Before repair it accepted the healthy input and all four mutations, returning native exit 1 from the independent assertion harness.
- **First failing evidence:** original mutation matrix `E:/g2-review-r12-e4ab1cfd84cc477d81db25053c424370/control-gate-mutations.json`, SHA-256 `5708442C9FC660A749EE17FAE5ADD907EA1A877901394A272F03A9C91515A436`; reproducer `repro-control-gate.ps1`, SHA-256 `FC4CC11E0376ACC2DDA6E5153033AF301E1344976385E505DD52F1210C372727`; prior repro log SHA-256 `579EF1BA879D5DBFD58105CFF4394AEC4D723721649FB9206FD34744F930C63B`.
- **Actual vs expected:** previous substring/partial regex accepted suffixed stale reasons, a contradictory FAIL, a fake PASS suffix, and an empty AI-decision identity. The gate now requires exactly one timestamped RESULT record with exact field order/tokens, nonempty AI decision and round IDs, exact epoch transitions and stale reasons, one exact PASS marker, and no FAIL marker. Runtime `Player.log` separately requires an exact PASS token and rejects FAIL. Timestamp fractions are optional (0 or 1–7 digits) with an explicit UTC designator/offset; an initial R13 repro exposed that the valid whole-second independent fixture had no fractional part, and the parser was corrected before the successful rerun.
- **Repair mapping:** the runner gate and FixtureOnly matrix were changed; no verifier, production control policy, or Census assertion was weakened. The same-package G4 runner still classifies this explicit ReleaseControl probe as supporting evidence only, not a real disconnect-policy pass.
- **Verification:** the exact independent repro now exits 0: healthy accepted; `wrongReasonSuffix`, `contradictoryFailure`, `fakePassMarker`, and `missingAiIdentity` all rejected. Complete FixtureOnly exited 0 and emitted `C04_TOOL_FIXTURES_PASS positive=4 negative=79 protobuf=PASS malformed=2 timeoutTree=PASS mutexTimeoutSeal=PASS mutexCancellationSeal=PASS outputDrain=PASS outputFiles=123`. The integrated additional negatives cover missing/duplicate RESULT, corrupted/prefix reasons, missing/empty/duplicate identities, epoch jump/overflow, missing/fake/duplicate PASS, FAIL conflict, and runtime Player-log marker corruption/conflict. `tools/tests/G2TestRunner.Tests.ps1` exited 0 with 30 assertions.
- **Evidence:** R13 repro log `E:/g2-review-r13-d12-gate/control-gate-repro.log`, SHA-256 `584ACB04AB9BC5444C5269D1978953E52B59BD17BC7C8DCA4C85A6D17C8CEED5`; review manifest `E:/g2-review-r13-d12-gate/review.json`, SHA-256 `F5DE3D653585EBE36C0C56755F8958315C37310EC977FDD0C165B40E81B703AD`. Runner SHA-256 `3D3DDF1180208420D7769DE7696B4F8097D5BE99DBC31F8D1CCAF6E055759EA5`. Fixture seal `build/13c04d1209b04806bd4f04b1482444f8/fixture-seal.json`, SHA-256 `1EB996D0A6E1720600FF657428C776A5A7038A320B6D4F1F34801F74B38ACED7`; runner log SHA-256 `1B466BFBE2EB4B9ED6C7EF86C3E8A523DC2C2BA3BA38EFC30AA809F7B85A4340`. Runner test summary `build/g2-runner-test-2ceebc5176124d8080b1e03f97c153f1/test-summary.json`, SHA-256 `EFC8D880A143C34F2D181784197DE0A105909BC83812293CCC5E0F58BB43C8A3`.
- **Acceptance:** D-12-01 source/tool gate is closed; this does not prove the G4 Player process ran. Actual ordinary Player execution remains outstanding under D-1-02.

## Still-open findings (no new IDs; all MAJOR)

### D-1-02 — missing deterministic game/control cases and final bindings

- **Location:** `LocalPlayerActionScenario`, `C04PlayerActionProbe`, `LocalControlPlayerProbe`, WindowsActions/PlayMode/service bindings.
- **Reproduction:** execute the fresh real action runner (without `-FixtureOnly`) using a new 32-hex `-RunRoot`, pinned Unity and .NET paths; compare GAME/PlayMode originals with the 32-row Binding. Current round ran only FixtureOnly and the gate repro.
- **Actual vs expected:** exhaustive draw/dealer continuation with a new `roundId`, added-kan/chankan/rinshan coverage and complete invalid/stale zero-change evidence remain absent or unexecuted. G4 still uses explicit ReleaseControl rather than a disconnect. Accepted G7-02 service support is not yet bound to the final candidate.
- **Evidence:** prior R12 execution supplement and the unchanged Binding; R13 tool evidence above is not Player evidence.
- **Repair / acceptance:** implement and run the missing deterministic real-channel/Player cases, run all five existing action Player and PlayMode cases plus control probe, and run GAME verifier positives and per-case mutation negatives. Bind G7-02 accepted LOCAL refusal/zero-side-effect and server-derived positive to the final candidate. Require nonzero counts, 0 skip, 136-entity image, receipts/state/events, epoch/deadline/idempotency/privacy/terminal invariants.

### D-1-01 — G8 layout and physical input

- **Location:** C04-G8-01/02 capture and input path.
- **Reproduction:** compare Binding to `build/review-r2-a198409d/ui2/`; no new capture or input run occurred in R13.
- **Actual vs expected:** only the previously verified base Menu is available; wide/tall table/settlement frames and physical keyboard/mouse flow tied to authority receipts remain unverified.
- **Evidence:** R2 DPI-aware originals and unchanged Binding.
- **Repair / acceptance:** execute a single-DPI-context capture/input collector with isolated smoke data and absolute Player log; visually verify supported base/wide/tall Menu/Table/Settlement, then bind physical input to at most one legal request and its authoritative receipt. Cancel/focus/stale input must produce no submission. Do not alter Canvas scaling or substitute API driver actions for physical input.

### D-1-05 — same candidate/package closure

- **Location:** final candidate, ordinary Windows Player, 8 DLLs, full resource/plugin/BuildReport closure and 32-row collector.
- **Reproduction:** compare per-suite package manifests/runs with the current Binding; R13 produced no Player package.
- **Actual vs expected:** there is still no single legal candidate and complete Player package carrying all required row originals.
- **Evidence:** prior package manifests and unchanged Binding.
- **Repair / acceptance:** finish the executable common collector, then use bridge clean/fresh/verifier lifecycle. Bind candidate/source/generated inputs, all DLLs/resources/plugins/BuildReport/full-file hashes to each row's request/state/receipt/screenshot originals. No package splicing, stub-hash substitution, stale DLL, skip or summary refresh. Android nine rows remain excluded/NOT_RUN.

### D-1-04 — offline G7-01 (external environment condition)

- **Location:** C04-G7-01.
- **Reproduction / evidence:** unchanged preflight `E:/g2-r4-offline-preflight.txt`, SHA-256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`.
- **Actual vs expected:** approved isolated/no-egress environment remains unavailable; the real offline full match remains NOT_RUN.
- **Repair / acceptance:** do not alter host-wide network/security or repeat unchanged preflight. Complete other work first. When this is the sole remaining item, retain the specific environment condition and run plan; PASS requires same-run process/package/local transport/no-egress observation bound to scores, rankings, ledger and terminal state.

No Unity, PlayMode, ordinary Windows Player, visual input, offline match, or full same-package matrix ran in Round 13. No Android or G2 sign-off is claimed. No commit, push, merge, or V4 status change is claimed.

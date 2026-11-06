# G2-C02 controlled collection: independent component acceptance

Date: 2026-11-01. Original thread `thread_20261031_204528_aa7e`, original DeepSeek receiver session retained. A repeated R4 notification was checked against the ledger: R5 was already delivered and waiting for review. No duplicate rework or new task/session was created.

Receiver final: `699bbaf4989245c487cb6b40f1e6418153242882`; R5 implementation: `6cae2910bd8afde984f5b5efd4b3e5c4b8fc1507`; frozen implementation base: `6750b4362063169cd435302e0cc9c6685325bbe5`.

The independent controlled collector/verifier implementation and its version-bound development evidence are accepted within this task's scope. No known scoped defect remains open. This is not approval of C03's unified Player, C04's recovery/outbox reconciliation, the old Compare path, P4.5/G2 or G3-G5.

## Behavior and retained defect closure

The collector pins source/build/tool identities, full Player package and plugin closure, directly owns its launched process instances, and seals original save/export bytes with phase/run/challenge/process identities. The offline verifier requires an independently supplied digest, validates the sealed file inventory and chain, and independently decodes the original saves. Recovery facts, collection-chain validity and caller acceptance remain separate. Case files retain `PENDING_INDEPENDENT_REVIEW`; acceptance is this caller decision, not a rewritten sealed claim.

The earlier source/empty-array, required BEFORE-export and EOF fixes remain closed. Ownership hardlink/junction, leaf-output and deletion regressions remain in the actual entry suite. R5 closes the final two C02-R1-02 subitems:

- Copy destinations are created with write/read-attributes/DELETE rights from the beginning. The validated writing handle is also used for instability cleanup; a new pathname lookup is not used for deletion. Cleanup failure is reported rather than silently treated as success.
- The deterministic deletion test checks the original leaf in its actual location, uses a same-name foreign sentinel outside OwnershipRoot, and requires a completed/attempted worker. Disabling actual deletion in a private collector copy now makes the unchanged delivered test fail for the retained original leaf.

Caller native-thread probes additionally record an actual parent-swap attempt inside the function's held-handle window. The rename is refused, the validated original leaf is deleted and foreign bytes remain unchanged. Same-account process injection, an administrator/kernel compromise or a reviewer forging its own observations remain outside the declared local threat boundary; this component does not claim a cryptographic or permission-isolated trust root.

## Independent caller execution

The caller used an isolated worktree at the receiver final, then a separate integration worktree from existing PR20 head `d1acb120cc2b753c07d2e267af57461e61169026`. Only the four final C02 files were applied, preserving their exact receiver contents and leaving the separate recovery branch's old driver/history out of this integration. Integration commit tested: `c43187efba69c03753f952276900af2c52287246`.

| Check | Actual result | Evidence SHA-256 |
|---|---|---|
| Fresh decoder build at receiver source | Native0, zero warnings/errors. The .NET source matches accepted recovery baseline `6750b436`. Caller-built decoder DLL pin: `0EC8A5EA8850C6A8AAA5733CEFE869AB38474CB1A73FAA00CAC541A80445DB73`. | `decoder-build.log` and native-exit record |
| Unchanged full suite at receiver final | **27/27**, failed0/skipped0/native0; fixture cases remain explicitly non-GAME. | `test-summary.json`: `DFB0808D9DA9571C56832C353A4110F07F4B78449C8A23F369DD23E9420B2D5B` |
| Unchanged suite on PR20 integration | **27/27**, failed0/skipped0/native0, using the explicit fixed decoder path described below. | Summary `5D6224E4B19297EF6BA53EE0F94D152E5E5C4CCDA283A78B1DD8CD063F2CB1BC`; log `BB2BB7897251B2634F60DA723522B19C8A229EA15E3DFB3FAE4101DCDCE5AA07` |
| Actual unstable copy and stable control | A real concurrent writer changes an own 64MiB source. Actual function returns `COPY_UNSTABLE`; invalid destination is absent, foreign sentinel unchanged. Direct DELETE-capable handle deletion and stable-copy hashes pass. Native0. | `copy-result.json`: `14E68720D4BB7E1C44DED3D2EDB81C756A1A791A81BAEA6856AB7BBC3CC5F0D7` |
| No-delete sensitivity control | Actual delivered case/body/assertions unchanged; only a private copy's MarkDelete is disabled. Expected PASS0/FAIL1 for the undeleted original leaf; harness native0, original collector unchanged. | `noop-result.json`: `FB9A87399C71B16EA6C1F36D4C7300A825066C6807FCAD29B600FDDF3D45DECC` |
| Real held-handle race | Worker timestamps fall inside function lifetime; parent move denied, original deleted, same-name foreign sentinel outside OwnershipRoot unchanged. Native0. | `delete-window-result.json`: `F6C529D47ED0A130D48FB4D77ED389BBA9F71C25A758F60A36F4EFFCF820B742` |
| Eight recorded R5 GAME cases | Original offline entry rerun **8/8 native0**, chain PASS, GAME=true and caller acceptance pending. Uses the exact original decoder pin, not the newly built DLL. | `recorded-game-results.json`: `18F3EFB34F68FE8277CD9F4A0D8DFD7510F95EB61A4DB3BCC9B56D2A4EE6D040` |
| Recorded full Player package | All **205** length/hash entries match; aggregate `B7F40673E4B3E16126F89FBDB7A28914BA12FCD5FBF5F028FAD0970A5356B38A`. The seven own plugin DLLs plus Protobuf match packaged DLLs; framework-library resolution is not equated to copying every plugin file. | `recorded-package-result.json`: `670212EB2EED915A57E4503BFAAE6A0E3463B7E1BEB43EADEF9DF99BD86A141A` |

The eight real recorded outcomes are Normal, Force, truncated save, bad checksum and missing primary with valid backup: RECOVERED/factsEqual=true; incompatible or both bad: REJECTED/factsEqual=false; both missing: FRESH_START/factsEqual=false. The original decoder SHA is `E83D03F47E664AB483FD8A4D057BFE576BA2960010A8C7C52E625A5BB43DC3EC`. Strong process termination and expected rejected AFTER exits are retained as observed facts, not coerced to native0.

The recorded R5 shared build and Windows Player build are native0. Their log digests match `50CD6F4A1A5FB18398C7B3C594D8D890C142ED7DCDAEDE50123AD05AA5258411` and `BC771AF719F1ABE7689275B4E29C6D510412138B404BCF42C056152E58B37751`. Source/build commit is `6cae2910`, with 21 declared generated Unity/plugin/settings dirty paths and diff digest `9819741E1D827A9F2F6B50B942025C19F2CF54189CD1BB3529041028987C2272`. This is receiver development evidence independently checked, not a new caller Unity run or a clean integration/publication-head Player build.

Three PowerShell files parse on PS5.1 with zero errors. The integration matches the exact four receiver files and diff-check passes. The final caller publication adds only this acceptance document after the tested integration. No Unity mutex was acquired or other task interrupted during this caller review.

## Dependency, environment and publication boundaries

The accepted exporter/`P45RecoveryVerifier` source is still in the separately accepted recovery baseline `6750b436`, pending C03's selective PR24/PR20 unification. It is not in current PR20's tree. The initial attempt to build the integration's default in-tree decoder project correctly returned MSB1009 and is preserved. This component already supports an explicit `-VerifierPath`; integration validation uses the freshly built, fixed-hash accepted decoder from the independent baseline worktree:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45ControlledRecoveryCollection.Tests.ps1 -VerifierPath <fixed-accepted-decoder.dll> -OutputDir <new-owned-output>
```

This does not claim a self-contained default decoder build or that C03 has completed. No new decoder, exporter, shared schema or old driver is introduced by this four-file integration. The dependency is already delivered and accepted; it is not a reason to rework this passing component or fabricate another task.

D: reached zero free bytes during the caller's initial decoder/copy attempt. Those disk-full failures are preserved and are not attributed to a code regression. Only this review's own large disposable inputs were moved to a validated E: archive with matching digests, retaining their contents; previous evidence and other worktrees were untouched. Clean validation then ran on E:. An initial caller sensitivity-harness hash check also aliased its own collector-path variable to the private mutant; that harness check was corrected in a new run, leaving the original log intact. Its original test already rejected the mutant, and the corrected run verifies the actual original file hash.

Raw evidence is under the independent receiver-source and integration worktrees' ignored `build/caller-review/`; the initial disk-full records and moved input digests are retained separately. No historical failure, old Compare BLOCKED17, sealed manifest or caller-acceptance field was rewritten. No new-head CI, C03 unified-version Player, C04 outbox/request reconciliation, Android/iOS or G2-G5 pass is inferred. The sole existing PR20 remains Draft; no component MR, merge, release, deployment, scheduler or complete.flag operation is authorized here.

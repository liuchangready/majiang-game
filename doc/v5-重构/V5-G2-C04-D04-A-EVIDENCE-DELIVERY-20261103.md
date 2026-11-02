# G2-C04-D04-A raw evidence component delivery

Date: 2026-11-03 (Asia/Shanghai)
Task: D04-A, R1-04 only
Model: Luna 6
Workspace: `E:/majiang-review/g2-c04-d04-evidence-luna6-20261103`
Starting source head: `63206301378338d1360fce6d3a5783b121072051`

## Review result

The D04-A verifier and thin caller module are implemented and ready for dedicated review. The component verifies a real serialized `LOCALHOST_CHANNEL` authorized run and rejects the retained R8 evidence bypasses. This is a .NET component result; it is not Unity, Windows Player, same-package D04 acceptance, or GAME evidence. The original D04 acceptance standard is unchanged. D04-B still owns R1-05, D04-C still owns R1-06, and the six-row full D04 matrix remains open.

The six changed tracked files are exactly the D04-A allowlist. No domain, runtime, client, contract, assembly definition, Unity, scheduler, or machine configuration files were changed. All older task trees and evidence remain read-only.

## Retained R8 R1-04 defect group and batch repair map

Source of record: `E:/majiang-review/scheduled-c04-d04-r8-review-20261101-1918/build/caller-review/REVIEW.txt`, SHA-256 `18FD12054A170A7174A5AD55B8EF4060F3A9869D92703A1A2D42B210A5FB1A44`, lines 12–14, 25–33, 107–127. It recorded 19 Strict inputs with 3 incorrectly accepted, including `nominal-no-raw-private-ledger-or-136-observations.json`, `arbitrary-different-valid-raw-hash.json`, and `negative-two-unrelated-reasons-same-payload-nonzero-effects.json`. These are retained original failures, not new findings.

| ID / severity | Location and exact reproduction | R8 actual vs required | Evidence and repair | Acceptance assertion / current result |
|---|---|---|---|---|
| `C04-G3-R1-04` (original MAJOR), missing originals / summary-only acceptance | Prior production path `Assert-C04EvidenceStrict` / `Probe`; reproduce with R8 `strict-probes.json` input `nominal-no-raw-private-ledger-or-136-observations.json` using the R8 `run-review.ps1` at its recorded pinned tree. New regression: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheatEvidence.Tests.ps1 -DotNetPath D:/Unity/dotnet/dotnet.exe -VerifierDll <isolated-build>/C04PlayerCheatVerifier2.dll -OutputRoot <new-build-guid>/evidence-tool-tests`. | R8 accepted nominal PASS claims without original command, receipt, private publication, recovery, ledger, or physical bytes. Required: each outcome is re-derived from typed raw originals and recovery. | Fixed by `C04PlayerCheatVerifier2 verify`: required indexed raw members, path confinement/reparse checks, typed `WireCodec` decode, and read-only recovery parsing. Original finding: R8 `strict-probes.json`; new result: `build/2b2c2b78-974d-4dc9-a5bd-3c9aeb06fce7/evidence-tests-component-2.log`. | Missing index/raw input returns nonzero with structured reason. New PowerShell component matrix: 8/8 native checks passed; real raw positive also passes. |
| `C04-G3-R1-04` (same MAJOR), caller-selected/arbitrary raw hash | Same R8 pinned `run-review.ps1`, input `arbitrary-different-valid-raw-hash.json`. New test mutates a real capture's member digest to a different valid 64-hex value, updates the index digest in case metadata, then invokes the fixed CLI. | R8 accepted a different valid 64-hex hash without checking source bytes. Required: recompute hashes from every raw member; independently recompute the canonical domain digest from command bytes. | Fixed in verifier `ReadRaw` and `CanonicalPayloadDigest.Compute` comparison. Regression log: `build/2b2c2b78-974d-4dc9-a5bd-3c9aeb06fce7/evidence-tests-component-2.log`. | Returns nonzero `RAW_FILE_SHA_MISMATCH`; altered input is rejected even with a valid-looking hash and a refreshed index hash. |
| `C04-G3-R1-04` (same MAJOR), unrelated rejection reasons / fake zero effects | Same R8 pinned `run-review.ps1`, input `negative-two-unrelated-reasons-same-payload-nonzero-effects.json`; R8 details in `REVIEW.txt` lines 107–127. New negative verification contract is exercised by the fixed `verify --case negative` path and the real authority rejection channel tests. | R8 accepted two claimed rejects with `MALFORMED_MESSAGE` / `INTERNAL_ERROR`, duplicate canonical/raw hashes despite separate IDs, and claimed zero effects although state version moved 1→2. Required: typed distinct original payloads/requests, exact business reason codes, and byte-derived zero version/event/cheat/ledger/entity deltas. | Fixed by typed negative receipt reason checks, distinct request and canonical digest checks, and recovery comparison of persisted state version, event sequence, cheat version/fingerprint, ledger digest, physical digest, and all 136 positions. The two actual LocalHost business rejection controls are in `localhost-c04-only.log`; the verifier's negative semantics are implemented in `Program.cs` and described in the evidence contract. | Any protocol/internal reason, repeated payload, altered recovery value, version/event/ledger/entity mutation, missing original, or wrong reason returns nonzero. Actual LocalHost test class: 4 passed, 0 failed, 0 skipped; includes actual insufficient-balance and missing-decision rejections with zero authority delta. Full captured negative raw-index verification is not claimed by this A delivery. |

These three historic acceptance modes are one defect group, as R8 classified them. The batch repair retains the distinct-payload and nonzero-state controls; no assertion was relaxed and no historical summary is used as a substitute.

## Implementation and positive raw result

`C04PlayerCheatVerifier2` implements the fixed native contract:

```text
dotnet <C04PlayerCheatVerifier2.dll> verify --evidence <case-json> --case <authorized|negative> --source-head <sha> --package-manifest <path>
```

It emits one structured JSON result, returns native 0 only for PASS, and returns nonzero for missing, malformed, misbound, modified, non-definitive, or semantically wrong evidence. The PowerShell module calls only this executable and returns its native exit code; it contains no second semantic verifier.

The independent capture mode `capture-localhost-authorized` records actual serialized command and receipt envelopes, the exact accepted private PlayerUpdate, and before/commit recovery bytes from `LocalTableHost` through `TableSession` and the production `ITableChannel`. The component capture is classified `LOCALHOST_CHANNEL`, never GAME. In the verified run all five commands shared round `c04-g3-r1`, room `c04-g3-cheats`, session `c04-7`, account `local-c04-g3-cheats-seat0`, scope `c04-6`, seat 0. Derived command-version sequence was 0→1, 10→11, 11→12, 16→17, 17→18; receipts carried event sequences 3, 22, 24, 34, 36 and publication cursors were independently decoded.

The real raw output passed all tool assertions: costs 1/10/10, balances 120→119→109→109→99, exactly three private ledger debits, no choice debit, 136 unique physical IDs at every captured state, exact SECOND source/destination, and exact SWAP entities. `WireReceipt.CanonicalPayloadSha256` is checked against the canonical-domain digest; recovery request facts are checked against SHA-256 of the exact original canonical-payload bytes. These hashes intentionally have separate meanings.

## Validation evidence

All output is under the ignored exclusive directory `build/2b2c2b78-974d-4dc9-a5bd-3c9aeb06fce7`.

| Check | Result | Evidence |
|---|---:|---|
| Build `C04PlayerCheatVerifier2.csproj` with warnings as errors and isolated artifacts | exit 0; 0 warnings / 0 errors | `build-capture-semantic.log`, SHA-256 `4356A9EBB71AA841FC3CFEAA6C810C921544C0D80565F230C3E67A1ECD635089` |
| Evidence tool script and actual LocalHost raw capture positive plus failure controls | exit 0; 8/8 passed | `evidence-tests-component-2.log`, SHA-256 `231C7265F19AB1948AE74166E4E3B8DF0569C834FE2B01ADBA31FCE68194C349` |
| Direct verifier on the captured production-channel originals | native 0; structured `status=PASS`, five rows | `evidence-tests-component-2/verify-real-localhost-pass.jsonl`, SHA-256 `7368B4128A53E67D816E43C3DA3FD9FDACC188BC4F92B29416D4EF05BEF40DAD` |
| Existing actual LocalHost C04 channel tests via xUnit v3 in-process class filter | 4 passed; 0 errors / failed / skipped / not run | `localhost-c04-only.log`, SHA-256 `1DC50517BE0EB0E560B2E59273555D66E8BCB9CF6B4AA172D80B14B6A9DF28D3` |
| `git diff --check` and allowlist audit | clean; only the six expected tracked paths | captured again at handoff |

The repository's `dotnet test --filter FullyQualifiedName~C04PlayerCheatChannelTests --artifacts-path ...` was also attempted. Its custom xUnit in-process runner ignored the VSTest class filter and ran 151 cases; 137 passed and 14 failed because the isolated artifacts root lacked unrelated cross-test fixture / Release crashworker outputs and one LocalMatch case failed. That command is not treated as the targeted C04 result. The targeted xUnit v3 class-filter invocation above completed all four C04 channel cases successfully and was not a Unity or Player run.

## Remaining D04 scope and limits

- R1-05, original MAJOR: the real Unity Player second-discard sequence and complete UI intent/receipt/publication diagnosis remain D04-B work. The old known-bad Player is not re-run or promoted as evidence.
- R1-06, original MAJOR: complete frozen package/source closure, safe Player process lifecycle, and final archive/manifest remain D04-C work.
- No Unity PlayMode, Windows Player, final frozen full package, or same-package six-row matrix was run here. The two raw production rejections have existing real LocalHost unit-test evidence, but this A delivery does not claim a captured negative raw evidence index or final GAME acceptance.
- R1-01/02/03 and R3-07 retain their prior accepted status. No historical evidence is overwritten, combined across runs, or re-labeled.

# N3 note adjudication and serial metadata ledger: caller acceptance

Date: 2026-10-26. Bridge thread: `thread_20261026_061703_b390`.
Accepted delivery: `d9159a3550b6a03931e0b576dce216d194d2ad5a`; frozen baseline `6c3573620f61e8cbb6a13c8c4b81abef0b173fd9`.

The note evidence task is accepted: **81 frozen records = 77 classified + 4 pending + 0 bound**. D-NOTE-01 and D-NOTE-02 are closed. Delivery adds exactly the two authorized paths `V5-N3-NOTE-ADJUDICATION.md` and `.tsv`; former paths are absent. The tree is clean and baseline-to-delivery `git diff --check` exits 0. All 81 identities, original note values/SHA256, kind/parent/histClass and exporter literal locations match; nine source pins and both frozen census hashes match. The TSV has 81 data rows and 29 columns. Five historical truncated parent cells remain frozen, with complete fixture text separately hashed.

The original `tm-capacity-tiles` note says `8 marked + 4 > 12`. It is preserved as provenance, not used as a behavior requirement. Exporter line 418 supplies groups=2 and marked16..27 (12 tiles), selecting 0/4/8/12 (four tiles). Derived obligations consistently require the real **12+4>12** capacity behavior and retain the C# exactly-four-group representability caveat. Since 8+4=12, no false arithmetic assertion is required. This record remains pending.

N3-META-01 classifies exporter narrative only when its described behavior already has a value-level anchor and the text does not enter domain returns, inputs, events, receipts, hashes or protocol. Classified remains uncovered and never counts as full. The other pending note records are `mv-swap-drawn-with-wall-backfill`, `tm-decision-mismatch` and `bm-activate-deadwall-empty`. Their specific obligations and deadlines are retained.

## Fresh caller validation in the delivery workspace

E = `D:/majiang-wt/n3-note-adjudication/artifacts/TEMP/codex-note-r2-review-219a5ed65805454193df6c91c70f8d16`. TEMP/TMP were process-local under E. The previous review evidence was not overwritten. `dotnet` below means `D:/Unity/dotnet/dotnet.exe`.

```powershell
dotnet build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj --no-incremental --no-restore
dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-incremental --no-restore
dotnet dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGoldenTests' -class '*CheatGoldenBindingTests' -result-xml <E>/<variant>.xml
dotnet dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -class '*N3TableCheatGoldenAdmissionTests' -result-xml <E>/<variant>.xml
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/verify-subset.ps1 -RepoRoot . -SubsetTsv doc/v5-重构/V5-N3-NOTE-ADJUDICATION.tsv
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/check-note02.ps1 -RepoRoot .
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/codex-note-r2-audit.ps1
```

Both fresh builds exit 0, zero warnings/errors. Tests run only after successful builds. XML confirms exact class selection, counts, and zero Errors/Skipped/NotRun. `V5_CHEAT_GOLDEN_PATH` and `V5_TABLE_CHEAT_GOLDEN_PATH` select ignored variant copies; original fixture/tests are unchanged.

| Direct runner selection | Total | Passed | Failed | Native exit | Verdict |
|---|---:|---:|---:|---:|---|
| Domain baseline | 95 | 95 | 0 | 0 | PASS |
| Actor baseline | 7 | 7 | 0 | 0 | PASS |
| Domain all-notes | 95 | 95 | 0 | 0 | PASS |
| Actor all-notes | 7 | 7 | 0 | 0 | PASS |
| Domain B1 structured negative | 95 | 92 | 3 | 1 | Expected rejection |
| Domain B8 structured negative | 95 | 93 | 2 | 1 | Expected rejection |
| Actor B8 structured negative | 7 | 5 | 2 | 1 | Expected rejection |
| Domain nonexistent-class control | 0 | 0 | 0 | 0 | Selector control only |

The matrix is 8/8 as expected. All 81 target notes differ in all-notes; every other value matches. B1 negative changes exactly `wallEndExclusiveAfter` 68→69. B8 negative changes exactly `tm-allowed` outcome and reason. Text inertness corroborates source analysis; it does not by itself justify classification. Subset verification passes 14 checks; capacity correction passes 10; independent source/census/scope/variant audit exits 0. A caller audit initially tried hashing its own open output log; the helper was corrected and rerun successfully. It did not expose a delivery defect or change tracked files.

| Evidence under E | SHA256 |
|---|---|
| domain-build.log | `0A35BB0852823A6FEBCA7B0CF05E6BB1550D037D3721EB68EFBAE81172CABDB4` |
| table-build.log | `A17F4727559AA943EDD23824F618E9F2E6B19BA6060D803BE4C3182441D67968` |
| matrix.csv | `569EC5A5B2A6EA2151304B08D7BDE19750D8BFB2DE1D78C59DAB202C88D0D145` |
| subset.log | `81441CDAFB10D1BD03BEDD606D7098E5F5E5634A5516B97417DE7A55A6A10EE4` |
| capacity.log | `32AA9047E3AAD4A065A25001AB386B89903A8D52167C8C13335BF8D848BFF54D` |
| audit.log | `E6416061FFEF5E54BBC18D72E470DCD2121B133A050B0DA472505608FB9889E1` |

`E/evidence-sha256.csv` also records every individual XML/log. Delivery raw/LF hashes remain `d9e5260eae502207b2f53078da0681567da5a235e46c95502c550778a38dfa0d` (report) and `4dee4f0452370d1f61db73128d2f690c13903e8d7a868b6a523b0417464a241b` (subset).

## Caller serial ledger update and integrated checks

Bridge `approve_task` returned COMPLETED for this same thread. No new task/session or receiver was created. At integration, the same-batch AI text evidence (`thread_20261026_061703_8ad8`) and G1 evidence report (`thread_20261026_061703_d877`) were already accepted and published in PR19 through `d2fd6998df59b9647c50523424cace35037ffc76`. They are retained as ancestors. Unaccepted P4.1 work and unrelated unpublished branches are excluded.

Caller integration worktree `D:/majiang-wt/codex-note-final-integration-20261026`, branch `wt/codex-note-final-integration-20261026`, merges the note delivery at `b6ff615af2c49ef1cb1591377ca5c902bdb28fc8`. Only two delivery files are added by that merge. Their exact bytes are preserved after checkout. Entire integrated `dotnet`, `engine` and `tests` trees equal the freshly tested delivery. No implementation, test, fixture, catalog, manifest, platform or unified verifier changes are introduced.

The caller then applies both accepted subsets serially: 81 note records plus 18 AI records, **93 pending→classified and six remaining pending**. Persisted ledger is now **592 = 411 bound / 175 classified / 6 pending**. 493 non-target raw rows, all 411 bound rows, every frozen identity and the 1516/655145/592/17 denominators are unchanged. B1 becomes 18/5/1, B6 0/9/2, B8 108/80/3, B9 0/8/0 (bound/classified/pending); other ten packages remain unchanged. The six exact residuals, rules, owners and dated revisit conditions appear in ledger §14 and TSV. No residual is silently waived.

Shared evidence registry grows from 26 to 37 pins; only B1/B6/B8/B9 adjudication digests change. The original B8 blocked-case equality check still applies to the four original outcome/reason cases; path selection now explicitly distinguishes those from narrative note classifications. New classifications retain per-identity digest, package count, evidence and obligation checks. No checks are removed. A caller first run exposed Windows PowerShell decoding literal non-ASCII paths in the new registry; paths were changed to the existing `$docDirName` construction and the full verifier passed. This caller integration correction was not a receiver rework.

I = `D:/majiang-wt/codex-note-final-integration-20261026/artifacts/TEMP`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot .
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot .
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/serial-audit-and-negatives.ps1
# Each rejection variant uses the unchanged full check path:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot . -LedgerPath artifacts/TEMP/<variant>.tsv
```

Full integrated ledger verification exits 0 with **24 CHECK-OK**, including all 37 evidence hashes and 14 package digests. Catalog exits 0: six fixtures, 73 requirements, 10,389 cases, 10,000 random inputs, 45 Facts, full17/partial21/none32/V5-only3. Serial audit exits 0. Four caller rejection variants each exit 1 with the required diagnostic: same-count B1 verdict swap (`adjudication-pin`), unknown classified evidence (`classified-evidence`), corrupted frozen parent (`history-fidelity`), and erased pending owner (`pending-owner`). Original historical 20 rejection controls are not presented as rerun.

| Evidence under I | SHA256 |
|---|---|
| integrated-ledger-fixed.log | `FF4EDD71B82C2B44AA04F09D9692BB8F42794DE93B3A2832572B4948C52964C1` |
| integrated-catalog.log | `95C46B46E1397E80F3A64290234F99D138221A03188DE4A049B32D162B3EB73E` |
| serial-audit.log | `BFBC1F6F89EF23716B4AA0326C0943E66ED7360FFA25B9DE4EEADDFFE743CB77` |
| reject-same-count-swap.log | `F6E39D4E887FC7564ECA10507D3B3DB183862D57A9AA7D395E36946C111BC84F` |
| reject-unknown-evidence.log | `A909F46F6E8AE081BADFC983826DF572DB4BDE9125A22321FBD63B4142FD41EC` |
| reject-frozen-parent.log | `1D582E0F18C70F292A000002494943105FCA99789EB10E6DD885D1849A304FF7` |
| reject-pending-owner.log | `559DCA11C165028685A96492088AA4B7EF33AFE10AB028E3B80EC9A4520B7CF8` |

The verifier still requires the pinned external census and seven ignored historical review files; staging those evidence copies is not implementation or portable CI coverage. Historical G1/AI/component reports retain their original time and scope. This acceptance does not claim zero pending, N3/gate2/G1/G5 approval, a new remote CI pass, or new solution-wide/PG/WSS/Unity/IL2CPP/Gradle/device/physical-network validation. V4 remains the sole production architecture; no `complete.flag` is created. N-2 host wiring and final G1 adjudication remain separate prerequisites under existing rules.

The caller publishes this batch once to the existing `origin` branch `feat/v5-1-integration` and existing PR19 (`feat/v5-1-integration` → `v5`). No component MR or PR merge is created. The final publication SHA and MR synchronization are recorded using `record_merge_request` for this original thread.

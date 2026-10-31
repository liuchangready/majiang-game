# G2-C05 tooling and development-evidence acceptance

Date: 2026-11-01. Caller independently reviewed original bridge thread `thread_20261101_030608_3ef1`, retaining its original Luna receiver session. Receiver final: `a215f2d95d2747735c059425336187630442b30c`; frozen implementation base: `a8d58ffb81957e3f2afb7281d1d009b834e0ee5d`.

The scoped Android build/device-evidence tooling is accepted. This does not approve the final C03 platform candidate, Android gameplay/visual/input execution, C05 phase completion, or G2-G5. V4 remains production.

## Change and defect closure

The six delivery files add a full-game Android ARM64 IL2CPP build entry, source/provenance/native build evidence, explicit-device install/start/stop/capture tools, meaningful tool fixtures, and delivery documentation. They preserve the actual enabled production scenes and shared plugin closure. Screenshot collection always leaves game/visual semantics for human review.

The first review's four MAJOR and two MINOR issues are closed within this scope:

- GUID-owned test/evidence outputs preserve existing caller data and historical failures.
- Source preflight checks staged, unstaged and untracked inputs, propagates Git failure, and binds permitted dirty bytes.
- Artifact verification binds hashes, build identity, package, six required scenes, TMP, ARM64-only badging and SDK intent. Build expectations come from fixed production requirements and actual project settings; report fields cannot define their own expected scene set. Installation preflight rejects incomplete or contradictory evidence before adb mutation. Start/capture retain explicit `UNVERIFIED_INSTALLED_PACKAGE` linkage instead of claiming proof of the installed binary.
- Native adb results retain status/stdout/stderr, concurrent output draining and bounded timeouts; only the exact owned process identity is eligible for termination. An unrelated instance survives the timeout tests.
- Windows argument quoting preserves spaces, embedded quotes and trailing separators.
- Device enumeration/selection distinguishes absent, offline, unauthorized and ambiguous devices.

The R2 review retained the artifact-validation group, including SDK contradictions exposed by the new installation preflight. Scene/ABI gaps found in a supplementary concurrent R1 review had been omitted from the already dispatched R2 feedback; that caller omission was recorded, not attributed to the receiver. R3 fixes the full group together. No known scoped defect remains open after the checks below.

## Caller execution and evidence

The caller created an isolated integration worktree from existing PR20 head `1eb029d34378a587bc36b812baae4cef26f16665` and merged the receiver without conflict. Tested merge: `dc2aeadb8306ac7d70cedefc57c4ca6702b0dbda`. Its only changes from that remote head are the six expected delivery files; exact C05 tool/Editor contents match the receiver. No business code, scenes, ProjectSettings, shared verifier or CI changes are added by this integration.

Actual commands in that worktree:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45AndroidGame.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/caller-review/probe-gates.ps1
git diff --check 1eb029d34378a587bc36b812baae4cef26f16665..HEAD
```

| Check | Actual caller result | Evidence SHA-256 |
|---|---|---|
| Unmodified full tool suite | Native exit 0; **29/29 named assertions**. Includes two complete runs, **36/36** invalid Install/Start/Stop input combinations blocked before mutation, and valid controls. No skip or empty acceptance. | `selftest.log`: `7FED2C155FEF311ED4A143B72A4506280876465AFAEA981A6A1042A5D80CEC5B` |
| Independent actual-function gates | Native exit 0; **28/28** build/run checks, with valid controls and rejection of empty/missing scenes, false/missing TMP, extra/wrong/malformed/missing/duplicate ABI, SDK contradictions and failed report. Uses actual committed functions and real project settings. | `gate-results.json`: `ED70FE7C2E32BC69359AADBA0A716F0EB8F4EBBAE1818C759AA4910876A96569` |
| Historical real development artifact through final gates | Both build verdict and installation preflight accept the original pinned R2 development APK read-only. No original evidence was modified. | `historical-artifact-gates.json`: `3BBA6FF84A905BAB3B19F8B14F37182F9527A35C13E3F7017288923D3EBAA115` |
| Syntax/scope | Three PS5.1 scripts, zero parse errors; six expected delivery files; diff-check native0; tracked clean at the tested merge. | `parse-results.json`, `scope-result.json` |

Caller raw evidence is under the isolated integration worktree's ignored `build/caller-review/` and `build/p45-tool-tests/`. Mock adb/artifact cases are **TOOL_FIXTURE**, not Android game execution. An initial broad code comparison included earlier accepted C01 files and returned 1; that context diff is preserved. The authoritative comparison restricted to the exact C05 paths returned 0. No product failure was suppressed.

The publication after this tested merge adds only this acceptance document; it does not relabel a new code version as tested. The original receiver commits and all historical failures remain in the single existing PR20 history.

## Real Android evidence and remaining boundaries

The receiver's R2 clean-cache full-game development build remains the accepted build record: source `ce74c277272e395df6ab09d063d356dc889b2d12` plus four permitted dirty task inputs. Unity returned native0, BuildReport `Succeeded`, errors0/warnings194. APK: **52,142,409 bytes**, SHA-256 `0506AEEA2593B7E70C7B2796CE353D3B401A12ADCBF1FFFCC24A0464481C8D6B`.

The prior independent caller audit read the actual APK with aapt2/native0 and checked all 108 ZIP members' structure: package `com.majiang.game`, minSdk25/targetSdk36, ARM64-only native libraries including `libil2cpp.so`. The report contains Boot/Menu/Lobby/Table/Spectator/Settlement and TMP. The final corrected gates accept the same original build summary (`62AD41746D25B3B40E889F35154CA02A7EFBF97B1B55AD0042C9F8577821716B`), BuildReport (`AC9C1279A141E43761833403A66ABCC6C709D0302EB0E5AF07CC0011EDBCEF52`) and badging (`3952FE66E4A72AB88063A7CFEB107B13D8BBBDE3D19A6523BCBC85ECFF675BEE`). Project-setting input hashes match. This is a recheck of historical development evidence, not a new Unity build at the receiver final, tested merge, publication head or C03 candidate.

The first Android Unity build failure, later mutex-blocked attempt and stale-cache rejection are preserved. R3 changed PowerShell gates/tests/documentation, leaving the previously built Editor entry unchanged; another Unity build was not required for this scoped repair. No shared Unity resources were acquired or other work interrupted during this caller verification.

Real pinned adb enumeration in the preceding review returned native0 with no devices. Device installation, launch, gameplay, visual semantics, touch/back/background-resume, screen shapes and safe-area checks remain **NOT_RUN**. Final C03 must rebuild shared plugins/provenance and a fresh complete APK at its exact candidate, then exercise the documented matrix on authorized hardware. iOS, physical-network coverage, C02/C04 recovery closeout and other batch gates are separate. No merge, release, deployment, scheduler or complete.flag action is implied.

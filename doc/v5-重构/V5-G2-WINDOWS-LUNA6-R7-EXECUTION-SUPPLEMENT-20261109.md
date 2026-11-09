# G2 Windows Luna6 — Round 7 scope correction and execution record

This continues `thread_20261108_190230_2b67`; it is not a new task. The bridge delivered `ba3da037121420a22c76b8294897a9efe2c0c285` (parent `33173f613ac88a4b74d11cdc5126368b17f7bcf4`, merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). The parent is retained. No commit was made manually, and nothing was pushed or merged.

## D-6-01 — MAJOR — incorrect Unity project root polluted the candidate

- **Location:** root `Library/`, `Packages/`, `ProjectSettings/`, `Temp/`, `UserSettings/`, `assets/**/*.meta`; Unity compile invocation and candidate scope guard.
- **Reproduction:** compare `git diff --name-only 33173f613ac88a4b74d11cdc5126368b17f7bcf4 HEAD`; inspect `E:/g2-r6-unity-compile4-33173f61/unity-compile.log` COMMAND LINE ARGUMENTS and project-root lines.
- **Actual vs expected:** the diff contained 561 files; 536 were generated under the repository root (444 under `Library/`). The failed Editor invocation recorded `-projectPath` followed by the worktree root and a separate `\unity\Mahjong.Unity` argument; the selected root was the repository root. It then failed with Win32 112 while writing `Library/Bee`. Expected: one validated absolute `unity/Mahjong.Unity` argument and no unrelated root-project outputs in the candidate.
- **Evidence:** compile4 log SHA-256 `F88A15B83BDF4D1F3C7138891B2DCBC9ACFA5A539951C730596D1C1BEE73BEBA`; original independent review `E:/g2-review-r6-b10d45844d024f0293499ea2691d046d/review.json` SHA-256 `14EA1914429CC97B9BDBC14D3901EAF496D13AFBD4C425F11808E70D73669457`.
- **Repair performed:** captured every one of the 536 generated paths, byte lengths and SHA-256 values to `E:/g2-r7-d6-preserved-root-generated/preserved-generated-files.json` (SHA-256 `85E183586DBDB3FBD4D506EB2180F97DBAB03073C65F2678DC6D053C56156F65`). Ran `git update-index --force-remove -z --stdin` with exactly those 536 paths; native exit 0. Rechecked all 536 physical files remain and every hash matches. No recursive deletion, physical move, `.gitignore` edit, or manual commit was performed. The index has 536 staged removals, while the preserved physical bytes appear as 71 untracked entries (five directories and 66 asset metadata files); therefore a scope-correct clean bridge candidate is **not yet established**. An earlier attempt to remove generated directories was rejected by automatic approval; that command was not repeated or wrapped.
- **Repair still required:** bridge must carry the explicit index-only scope correction into its candidate without re-adding the preserved root files. Before any new Unity process, validate the resolved absolute project root contains `Assets/`, `Packages/`, and `ProjectSettings/ProjectVersion.txt`, pass that single path as one process argument, then require the Editor log's selected project root to equal it. Keep C-space preflight and use E for output/TEMP. Do not remove or move the preserved files unless a separately approved safe handling plan exists.
- **Acceptance:** relative to `33173f61`, no root-generated file is tracked in the candidate; original intended Unity sources/assets remain intact; compile4 failure and file manifest remain preserved; the correct nested project is compiled with non-zero relevant test evidence and no storage error.

### Correct-root Unity compile

After verifying the nested project markers and acquiring `Local\MahjongV5P45UnityValidation`, launched Unity with a quoted, single `-projectPath` value and E-drive TEMP/output:

`D:/game_dev_tools/unity/Editor/Unity.exe -batchmode -nographics -quit -projectPath "<absolute worktree>/unity/Mahjong.Unity" -logFile E:/g2-r7-unity-compile/unity.log`

The process started with the nested project as working directory, used E-drive TEMP/TMP, completed with native exit 0, and the log confirms `Successfully changed project path to: <absolute worktree>/unity/Mahjong.Unity`. Log SHA-256 `EB9E5239E8904F50EC2CB3EA2BD5F6EDBA0FEDA89521D2F98CA8C124E550816D`. This is a script compile only; it is not a Windows Player matrix or G8 evidence. C free space was 633,126,912 bytes during the run and 645,496,832 bytes at the latest check; no Unity rerun is authorized until a new capacity preflight.

## Other known findings — unchanged IDs, all remain open

### D-1-02 — MAJOR — missing G1/G2/G4 Player cases and final G7-02 row binding

- **Location:** Census/Binding G1-01/02/03, G2-01..04, G4-01..03, G7-02; `C04PlayerActionProbe`, `LocalControlPlayerProbe`, and the real service ingress path.
- **Reproduction:** run the fresh WindowsActions/Player and control Player probes on a legally frozen, scope-correct candidate; run `C04PlayerActionVerifier verify <new GAME action evidence>` and a negative mutation matrix against that same new original.
- **Actual vs expected:** the new invalid G2 request and old-AI/claim/epoch assertions have not run in an ordinary Player. G1 real win/draw/dealer continuation/new-round evidence and G2 rinshan/chankan remain missing. The G7-02 LOCAL settlement asset rejection and server-derived positive are independently confirmed at the service boundary and need not be repeated; final candidate/row binding is outstanding.
- **Evidence:** R6 execution supplement; independent service test `E:/g2-review-r6-b10d45844d024f0293499ea2691d046d/G7LocalAsset.xml` SHA-256 `86524D27F8318052061D96AE8FA635B7F4EA153AFC892305AF92A5662C64CDF6`; delivered full TableService 204/204 XML and R6 LocalHost 196/196 XML. Current Binding remains 18 PASS / 11 PARTIAL / 3 NOT_RUN.
- **Repair:** implement deterministic real-channel G1 win/draw/dealer-continuation and G2 kan/rinshan/chankan branches; then execute new Player evidence. Exercise verifier positive on actual `EvidenceSource=GAME` output and negatives for each required rejection field missing, bad digest, request/scope mismatch, mismatched authority image/version/event, and altered 136-tile image. Existing verifier protobuf/digest `selftest` is not evidence for this branch. Preserve the already passed G7 service originals and bind them only when the final candidate/package is known.
- **Acceptance:** Census assertions for 136-tile opening, real win/draw, new round ID, action positives and zero-change negatives, DoubleRiichi event, kan/rinshan/chankan, old AI epoch rejection/re-entry/claim, and G7 real LOCAL refusal plus server-derived positive all have request/state/receipt originals, non-zero execution and zero skips. Independent PlayMode, ordinary seven cases and P2 retain their contracts.

**Unified negative matrix:** illegal/unadvertised action; stale/cross-scope request; missing or mismatched GAME rejection payload/digest/request/scope/raw receipt; changed authority version/event/physical image; stale AI result after epoch increment; LOCAL asset sent to real service ingress. Each must reject with no unintended authority, ledger, outbox, settlement or physical-tile mutation. No fixture, tool-only evidence or stale package counts as Player PASS.

### D-1-01 — MAJOR — G8 physical input and complete layout

- **Location:** C04-G8-01/02 ordinary Windows Player.
- **Reproduction:** DPI-aware capture and physical mouse/keyboard run on the final common Player at supported base/wide/tall client sizes.
- **Actual vs expected:** only the R2 complete base Menu is established; table/settlement frames and end-to-end physical input are missing.
- **Evidence:** `build/review-r2-a198409d/ui2/`; current compile and PlayMode logs are not G8 evidence.
- **Repair / acceptance:** use one DPI context for measurement, screen conversion, capture and physical input, absolute `Player.log`, isolated smoke data; visually verify menu/table/settlement at all supported ratios and bind each input to at most one valid request/receipt. Cancel, focus loss and stale input must not submit. Do not alter production Canvas for the withdrawn capture-coordinate issue.

### D-1-05 — MAJOR — one candidate and common Player package closure

- **Location:** candidate/packages/runs, Windows 32-row binding, eight DLLs, assets/plugins/BuildReport.
- **Reproduction:** compare source commit and whole-package manifests across suites.
- **Actual vs expected:** plugin refresh and separate suite packages do not prove a single complete Player covers the required rows; the current bridge HEAD also contained D-6-01 outputs.
- **Evidence:** current `git diff` against `33173f61`, R6 plugin provenance, prior suite manifests, and D-6-01 manifest above.
- **Repair / acceptance:** first obtain the scope-correct clean bridge candidate; then use the original fresh/clean guards and a common ordinary Player collection path. Bind source/generated inputs, 8 DLLs, all assets/plugins, BuildReport, whole package hashes and each row's originals. Do not splice packages, skip, refresh summaries or relax the verifier. Android 9 stays excluded/NOT_RUN.

### D-1-04 — MAJOR — G7-01 safe offline environment

- **Location:** C04-G7-01.
- **Reproduction:** retain the unchanged `E:/g2-r4-offline-preflight.txt`; do not repeat absent an environment change.
- **Actual vs expected:** no approved isolated offline environment or egress observer is available; G7-01 remains NOT_RUN.
- **Evidence:** preflight SHA-256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`.
- **Repair / acceptance:** do not alter host-wide firewall/network/security. When a safe environment exists, bind process/package, local transport/no-egress observation and a complete local match's scores/ranking/ledger/terminal state. Until then, keep NOT_RUN while completing other feasible work.

## Verifier and platform status

The current Release verifier build and selftest passed (`valid=1 invalid=2 canonicalDigest=PASS wrongDigestRejected=1`), but the selftest does not exercise the new GAME rejection evidence branch. The independent real G7 service test passed 1/1 with 0 skips. The 32-row Binding statuses are unchanged; Android 9 rows remain excluded. No final Windows task acceptance, G2 sign-off, commit, push or merge occurred.

# P5.4b-A caller acceptance — 2026-10-29

Component verdict: **PASS**, limited to thread `thread_20261027_230302_93fb`, round 5. D-LH-GATE-01 is closed. V4 remains production; G1 is approved and G2–G5 remain unapproved. This does not accept the complete LocalHost/Unity/batch gate.

## Sources and scope

- Receiver baseline: `c0f16eb77c70296978f658dd5382b9288d556e05`; final candidate: `cbeec43` (round-5 mapper implementation `feeeb8a`).
- Reviewed PR20 base: `e3ddd83fec0bc2592bb490594e5452b672bbb8f7`.
- Caller integration: `23b6042078b3d2c621fd7396909f69ba4108f844`, branch `wt/v5-p54b-caller-closeout-89b473c1`. No diff between receiver and integration for TableService, DurableHost tests, PersistenceWorker or the durable-host runner.
- Receiver changes comprise 23 authorized paths across the host, tests/worker, runner and delivery report. No Contracts/Wire/Domain/TableRuntime/Infrastructure implementation, Unity, scheduler or golden fixture/pin edits.
- Caller registration already collected in `cdcb031`: two solution projects, the exact TableService → Infrastructure dependency edge (including positive/negative guards), and separate disposable durable-host databases/environment in the existing PG batch wrapper. The global verifier is unchanged here.
- Receiver and caller integration were tracked-clean before this acceptance report; receiver baseline..HEAD and caller remote..HEAD whitespace checks passed.

## Current caller executions

Evidence root: `D:/majiang-wt/cx54-89b473c1/artifacts/r5-mvt0eab2`. SDK: `D:/Unity/dotnet/dotnet.exe` 8.0.426. The following command ran against the integrated current sources, not historical DLLs:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-durable-table-host.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 58361 -HostPort1 58362 -HostPort2 58363 -HostPort3 58364 -Root D:/majiang-wt/cx54-89b473c1/artifacts/r5-mvt0eab2/pg -LogDir D:/majiang-wt/cx54-89b473c1/artifacts/r5-mvt0eab2/logs
~~~

Six fresh Release builds (`--no-incremental`, `-nr:false`) passed. Native wrapper exit **0**; **29/29 stages PASS**. Intentional negative probes retain their nonzero native exits (TLS refusals 1, unavailable DB 2); they are not successful connection attempts.

| Assembly | Selected | Passed | Failed / skipped / errors / notRun | Native exit |
|---|---:|---:|---|---:|
| TableService | 152 | 152 | 0 / 0 / 0 / 0 | 0 |
| TableRuntime | 448 | 448 | 0 / 0 / 0 / 0 | 0 |
| Infrastructure, real PG | 42 | 42 | 0 / 0 / 0 / 0 | 0 |
| DurableHost, real PG | 59 | 59 | 0 / 0 / 0 / 0 | 0 |
| Architecture | 132 | 132 | 0 / 0 / 0 / 0 | 0 |

Total across these five assemblies: **833/833**. Architecture was freshly compiled with `BuildProjectReferences=false` against the integrated prerequisites, then run directly with XML output; this was not a second full closure rebuild.

Independently loaded every XML with XmlDocument, checked all leaf results against root totals, and rehashed **222/222** runner manifest entries (length and SHA256). `logs/manifest.sha256.json` SHA256: `A38DBEBFDF169F4A38E3AE24F3AB85B853AAEDE896E35EF571FF1503C2BF3269`. Counts and full XML hashes are in `independent-audit.json`; audit script is `Audit.ps1`. Initial caller-audit array parsing and execution-policy invocation errors were corrected in this ignored audit helper only; no production runner/evidence was modified to obtain PASS.

## Persistent host assertions

The live WSS/PG chain passed bootstrap, one real APPLIED action, same-key replay without another side effect, live-client force kill, a new process cold recovery, historical receipt lookup, fail-closed uncertain replay, dual-owner refusal, revoked-scope receipt lookup, cross-account refusal, unavailable DB startup refusal, strict TLS foreign-root/wrong-name/expired-leaf negatives, and corrupt-hash/event-gap refusal with readiness 503.

The added claimed-opening chain passed real Pon + checkpoint, force kill while sockets remained live, poisoned alternative round source and recovery in a different process/boot. `claimed-phase2.json` records old PID **50260**, new PID **59040**, different boot IDs, recovered/claim version **2**, original claim receipt **Found**, claim ledger rows **1**, total APPLIED ledger rows **3**, a new legal action at version **4**, **136** tiles, **0** unassigned, unchanged opening fact, claimed tile absent from river, exact meld and extended history. No in-process object recreation substitutes for this proof.

Runner verified cluster ownership before cleanup and removed its exclusive cluster. Ports 58361–58364 have no remaining listeners. All auxiliary control-probe processes exited.

## D-LH-GATE-01 mapping and real client contract

The mapper now requires frozen fields 20/string and 21/uint64, explicitly rejects legacy field 1, and retains identity, role, room, scope and owner fences. Public Contracts/validator were not changed. The original caller probe uses the actual integrated CommandPayloadFactory and public Map: Claim and Release both reach Control with expected epoch 7 and the correct reclaim flag, **2/2**, fresh build/run exit 0. `control-probe.log` SHA256: `57C2801F41DD97EA89E1573F5E4A6425B65035347CD7E602B0EC249C4E11DEF0`.

The required strict-WSS two-command probe uses real factory payloads and an isolated root/leaf: Claim and Release receipts are Applied and Release changes epoch 0→1; build/run exits 0. `control-wss-two.log` SHA256: `25B11A572321E25BA648A90661614E40266DDB1C67AB5C80B356C63BCD68D305`.

Also freshly built the receiver's original worktree and directly selected its 10 new mapper tests: **10/10**, zero failed/skipped/errors/notRun, build/run exit 0. Evidence: `D:/majiang-wt/p54b-durable-host-glm-20261027/artifacts/caller-r5-mvt0ndnh`; XML SHA256 `843964B1BCABFE20CB05ECDF9D98064A1946EBEC4965013915F0D069DD0F0D28`. These 10 are a separate rerun and are not added again to the 833 total. Their private mapper seam is explicitly distinguished from the public factory probe because the receiver baseline predates PR20's corrected validator.

### Preserved control-session diagnostic limitation

An extra three-command probe (Claim, Release, reconnect, Claim) initially observed LOCAL_CLOSED while waiting for the third receipt, native `-532462766`; the first two receipts were Applied. That failed attempt remains in `control-wss.log` (SHA256 `CBB9E09037BF73FC8DE026D90548B9779DE3F6F9677C1A27D96B539A2CC0A2DE`) with its original full source retained. Seven additional independently started diagnostic runs passed all three controls and observed epoch 0→1→2; they do not erase the first failure or establish unconditional receipt-before-close delivery.

The frozen P3.2 stream contract explicitly terminates stale-control subscriptions (`V5-P32B-ACTOR-STREAMS.md`, transport integration text), so a test demanding every extra control receipt precede that termination is stronger than the mapper acceptance being performed. This record accepts the field/route repair and the two required successful WSS commands; it does not accept comprehensive control-session continuity. The existing Applied receipt reason fallback `INTERNAL_ERROR` is retained as reported by the receiver; no new control protocol or stream-fence weakening is included.

## Batch gate and publication boundary

The prior batch run remains **FAIL**, not rewritten: `artifacts/batch-05905b12cc/unified/summary.log` recorded 17 PASS/1 FAIL when the unchanged global verifier turned the expected RECOVERY_LEASE_HELD stderr into a PowerShell RemoteException and lost the DurableHost XML. D-CAPTURE-01 is owned by existing thread `thread_20261029_114646_759c`; its in-flight code is excluded. That batch failure does not negate the current independently completed component runner.

D-LH-GATE-02/04b1 and full Unity LocalHost activation/e4ae are not accepted or imported by this review. Complete batch acceptance remains pending. Remote PR20 checks 40805666808/40805673521 were blocked before execution by account payment/spending-limit errors; no remote code/test PASS is inferred. No blind retry, merge into v5, deployment or complete.flag.

NOT_RUN for this component: Lobby HTTPS ticket issuance, multi-client full match, account asset settlement, production CA/domain/TLS deployment and cross-physical-network testing. Existing Android FAIL, iOS BLOCKED, device gaps and the 181 classified coverage gap remain unchanged. Publication reuses the one existing Draft PR20; no child MR is created.

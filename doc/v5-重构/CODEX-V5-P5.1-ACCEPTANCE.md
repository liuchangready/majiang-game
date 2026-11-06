# P5.1 independent acceptance and PR20 integration

Date: 2026-10-27. Task: `thread_20261026_163027_db9e`, round 6.

P5.1 component **APPROVED** through `approve_task`. Candidate:
`581d8b1ec67b913487b3a7b08109d103f8b8630d`, frozen base
`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`. The candidate worktree is
`D:/majiang-wt/p51-table-service-glm-20261026`, tracked clean, with
`git diff --check 8fd7c6cb..HEAD` exit 0. Its net changes are restricted to
TableService, TableService.Tests and the delivery report. No complete.flag.

## Final review finding

**D-5-01 closed.** Round 6 changes only the decision tests and delivery report.
The original random deal could legitimately open a claim window, so waiting for
seat 1 without resolving that window was an invalid test assumption. Production
cryptographic shuffling and response-window behavior remain intact.

The fixed no-claim fixture now proves APPLIED, version advance, same-key receipt
replay, a single discard and seat 1's turn. The new deterministic claim fixture
proves currentSeat remains 0 with exactly PASS/PON choices, submits PASS through
the published decision, checks APPLIED and same-key replay, then observes seat 1's
own decision, one river tile and no pon meld. The existing projection tile-count
assertion is only a visibility sanity check; full physical conservation is owned
by the Domain map. The report preserves the historical 150/151 failure and
corrects the inference that failure to reproduce had established a fix.

## Independent execution on the candidate

SDK: `D:/Unity/dotnet/dotnet.exe`; process-only DOTNET_ROOT and TEMP/TMP, the latter
inside the candidate evidence directory. Build succeeded before running fresh
executables. Direct runner XML assembly counts were checked, including not-run.

Evidence root relative to the candidate worktree:
`dotnet/tests/Mahjong.TableService.Tests/bin/review-r6-codex-c47f5bdc3cad42faad696bd65be4564c`.

| Command | Native exit | Actual result |
|---|---:|---|
| `dotnet.exe build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj --nologo -v q` | 0 | 0 warnings, 0 errors; includes service reference |
| `Mahjong.TableService.Tests.exe -class Mahjong.TableService.Tests.RuntimeDecisionRuntimeTests -xml <evidence>/decision-class.xml` | 0 | 4/4; failed/errors/skipped/not-run all 0; 3.296s |
| `Mahjong.TableService.Tests.exe -xml <evidence>/suite.xml` | 0 | 152/152; failed/errors/skipped/not-run all 0; 91.712s |
| `dotnet.exe run --project dotnet/tests/Mahjong.TableService.Tests/bin/r5-evidence/probe-r5/ReviewProbe.csproj` | 0 | 16 PROBE outputs match expected values |

The unchanged prior caller probe independently reconfirms single-live admission
handover, cross-suit chi rejection, pon then discard with false tsumogiri,
per-capture isolation, open-hand closed-kan advertising, turns 1/2 and wide-tag
concatenation rejection through a real WS close 4400. Its exit alone is not the
assertion: all printed expected/actual values were inspected. The 152-test suite
also includes isolated real WSS positive/negative certificate cases, ticket,
privacy, framing, backpressure and lifecycle coverage.

| Candidate evidence | SHA256 |
|---|---|
| build-tests.log | 5F6F40D287E8F8109C134BE291C70AB2B81989979D3C0564981760B53F36DE16 |
| decision-class.xml | 0284B3E130817ECC5877AB66357DCBE3589BB075161057A50B3D17ADB3F4B78A |
| suite.xml | A164B930E01E1008D35F72CCF903C673CD73EB1BA65D73CCB37CB5EA042DBFB0 |
| suite.log | A50F794E7ACAAE239FC9B42A40745E92505FE9C4B9DB7E86853984EAA2043A54 |
| probe.log | 68C7C25BEC3E5286DAB57405A911CEBA5FEA31B32EAAC496FE0E0AF7E98679FF |

## Caller integration

The existing single draft [PR20](https://github.com/liuchangready/majiang-game/pull/20)
targets `v5` from `feat/v5-post-g1-batch`. Its previously synced source was
`36014038bcbe61f7b9acdd4fad4694f329bbd4d2`. Caller integration worktree:
`D:/majiang-wt/codex-p51-r6-integration-20261027`.

The candidate was merged without conflicts as
`73e2322f0a7dab9b242eb8f2bcfbbfd0c0449724`. The caller registers the service and
tests in Mahjong.sln, adds the exact tenth production-module rule and its build
reference to Architecture.Tests, and extends the existing negative matrices with
nine forbidden dependency cases. No runtime source is changed during integration;
the two service-project comment updates only describe their new registration.
The unified verifier already auto-discovers the test project, so its script,
manifest and CI workflow need no changes.

The recent-task ledger shows the other accepted batch components already synced
to PR20. P4.1 and the new Client-event/Android-toolchain work remain outside this
accepted component and are not included. Their status does not reject P5.1.

Integration evidence relative to the caller tree:
`artifacts/p51-r6-integration-f4dd52bb8dbf4ebe9863b1d449eb558e`.
The same process-local SDK/TEMP settings apply. Commands run in this tree:

| Command | Native exit | Actual result |
|---|---:|---|
| `dotnet.exe build dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj -c Release --nologo -v q` | 0 | 0 warnings/errors; all ten production projects built |
| `Mahjong.Architecture.Tests.exe -xml <evidence>/architecture.xml` | 0 | 92/92, failed/errors/skipped/not-run all 0; 1.154s |
| `dotnet.exe build dotnet/Mahjong.sln -c Release --nologo -v q` | 0 | 0 warnings/errors; new service/test projects registered |
| `Mahjong.TableService.Tests.exe -xml <evidence>/table-service.xml` | 0 | 152/152, failed/errors/skipped/not-run all 0; 99.621s |

The final post-build source edits to these projects are comments only. There are
no runtime or test-behavior changes relative to the tested integrated tree. The
service/test implementation still equals the accepted candidate. The unified
PostgreSQL batch suite was not rerun locally for this isolated integration; its
new-source CI result remains separate, and no sibling skip is treated as a P5.1
failure.

| Integration evidence | SHA256 |
|---|---|
| build-architecture.log | 87B921E519B08B88093DD8D8A7C169A65D68528367ABBB478EC08EB1E70988AF |
| architecture.xml | 4A1B1F98CDE5CC209D6330AA5239CC1C4AAA6F6FC3BE0A924654C72E5CAF7EF7 |
| build-solution.log | EB45207E8AD8203CDB71EA6C9766B1A57F5834C423597E9C3AC0D7D43BE23C1C |
| table-service.xml | 415C7C13D981D7AA313ABAA38930C97620CC5A8E39C2C0E8E4ADDC7DEDBE3D82 |
| table-service.log | 5A87A62A674099EA795E9929080EA09C97F6A790E115FF435D73DA954559B731 |

## Boundaries

This accepts the bounded WSS host/admission/authority component, not G3 or a full
remote product. Real Lobby ticket consumption plus PostgreSQL composition,
production Client/WSS full-match integration, physical networks, devices and
capacity/weak-network qualification remain unverified here. The documented
closed-kan encoder fail-closed platform boundary is unchanged. No new remote CI
result is claimed by local tests. G1 remains approved; G2–G5 remain unapproved,
V4 remains production, and this change neither merges to v5 nor deploys.

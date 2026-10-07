# R4A live review

**21:08 R4A-interim-2.log:** now 6 server diagnostics: PlayerDecision.deepCopy import still missing at ServerTableHost:433; InProcessBinding capability DTO three errors unchanged; InProcessServer.tick still returns Boolean while new host tick is Future<Boolean>. Update all inprocess/udp callers and tests consistently, do not reintroduce blocking `.get()` to preserve Boolean. Review tests now include paused injected executor: construction/invocation must return without executing queue, close finishes queued futures, injected executor remains caller-owned, running old queued tasks after close does not mutate.

21:10 queue helper audit: enqueueOperation removes future from pendingFutures BEFORE scheduling completionExecutor. close between those steps can drop queued completion and leave future untracked forever. Keep tracking until outer Future really completes (whenComplete removal), check closed/rejection paths consistently. Same lifecycle principle as R3.

UdpRoom still supplies fullSnapshot via tableHost.snapshot(...).get(2s) to UdpClientSession.handleResend, which is called on UDP receiver path. That can still block all rooms if one authority queue is busy. Use asynchronous full-snapshot response composition or a safe published immutable snapshot cache; no waiting on receiver. freezeOnceOnAcceptedStart .get(5s) should likewise compose Futures instead of swallowing failed control initialization; do not publish successful frozen match when AI seating setup failed. Tick itself is now asynchronous; retain that improvement.

## 21:05 latest compilation

R4A-interim-1.log: engine now COMPILES. Server compilation errors: ServerTableHost:354 missing correct application.deepCopy import (PlayerDecision vs PlayerView extension resolution); :507/530/543/559/595 refer to `.events` but authority auto-step result now has different shape; InProcessBinding:146 supportedPlayerCounts expects Set<Int>, not List, and :148/149 invented resumptionSupported/authRequired fields (actual ProtocolAccessCapabilities has resumption, persistence, remoteTransport, protocolVersion, supportedPlayerCounts). Read real declarations and wire consistently. No behavioral results on new path yet.

Additional review-owned V4ServerHostAcceptanceTest added: stalled AI cannot block query/timeout, old AI result after human reclaim cannot commit or gift time, slow receipt future consumer cannot occupy authority queue. APIs currently ServerTableHost.start/snapshot/releaseControl/claimControl; if you change signatures Codex will adapt wiring. Existing slow observer test retained.

## 21:02 first authority compile

R4A-engine-interim.log: TableAuthority.kt:177 nullable constructor parameter coordinator shadows nonnull property in init (use this.coordinator); :726 nonexistent GameAction.StartSession; :814 nonexistent GameAction.NextRound. Use real domain declarations/result types; do not add fake gameplay actions just to satisfy auto-step result construction. No behavior tests executed on new path yet.

## 21:04 first host review - required queue/lifecycle fixes

- ServerTableHost serial executor must be injectable for deterministic contention/close tests (task requirement). Constructor currently submits init then `.get(2s)`; that prevents injecting a paused executor. Initialize private domain baseline before exposure, or make initialization itself async without caller blocking.
- tick() currently submit().get(2s), still blocks render/global room ticker and queues more work after swallowed timeouts. Make tick submission nonblocking/asynchronous and update callers to use completion where needed. Do not keep Boolean compatibility as an excuse: no old entry compatibility required; old product uses untouched old TableHost.
- close() shutdownNow drops queued CompletableFuture.supplyAsync tasks without completing their outer futures. Track pending operations and finish all queued futures on close/rejection. No 1s wait on caller for authority close; deterministic resource termination and no post-close mutation.
- Completing supplyAsync on serialExecutor runs arbitrary downstream whenComplete/thenApply (including UDP network send and UI callbacks) on authority queue. Separate authority computation from external future completion so a slow receipt consumer cannot block authoritative queries, just as a slow update consumer must not.
- subscribeSeat currently reads stream.lastEmittedSnapshot/cursor outside queue and may directly call authority.querySnapshot on caller. Baseline registration/cursor capture must serialize with publication; asynchronous delivery must recheck subscription/closed/generation validity. publishStateChanges shares one mutable domain update copy across listeners: isolate each listener or publish truly immutable values.
- UdpRoom.commandTarget checks active session only BEFORE enqueue; queued work currently forwards only seat. Revalidate connection/generation at authoritative execution point or serialize binding replacement in same queue. Old queued connection must not act after replacement/leave linearizes.
- Preserve pause/control clock behavior and bounded histories. Existing R1-R3 invariants still mandatory, not just new source gate.

Codex will add direct new-host tests once executor injection/API is available; no changes to review-owned files by Anti.

Codex-owned review sources: tools/architecture/v4-review/V4AuthorityAcceptanceTest.kt and V4AuthorityBoundariesTest.kt. Current tests cover slow observer vs caller/query, two competing choices committing exactly one discard, invalid choice preserving deadline and timeout committing once, pure TableAuthority existence/forbidden imports, and removal of legacy TableHost/SessionAccess imports from both V4 bindings.

Baseline logs: R4-authority-baseline.log and R4-authority-baseline-2.log under build/v4-migration. Do not modify review tests. Codex will adapt API wiring only if necessary while retaining behavioral assertions. Existing host rhythm/AI isolation/preparation/bankruptcy tests under engine/src/test/com/majiang/v3 are important preservation sources; merely leaving them testing the old host does not establish replacement coverage.

Keep stage A lobby policies stable. Do not implement readiness/room registry/spectators until R4B; complete actual domain/server authority split and new live local/UDP path first. Read latest feedback before delivery.

Code-reading preservation points: new authority should configure ApplicationCoordinator to defer automatic actions so AI work is actually scheduled outside the server mutation queue, not silently executed inside coordinator calls. ControllerRegistry's trusted control epoch is domain state; ConnectionControl's connection/disconnect timestamps/policy and background AI executor belong server. Preserve old HostRhythmAcceptanceTest behavior: 150ms automatic rhythm, winning/kan decisions remain available until deadline, pause shifts deadline, release/reclaim rotates epoch without gifting decision time. Existing config defaults to shuffle disabled; do not silently add preparation/ready gates during R4A.

Baseline #2 confirmed: 2/5 pass, 3 expected failures (slow observer, missing pure authority, old live host imports). Old-host preservation baseline has 20 tests across 5 files: BankruptcyHost 3, HostRhythm 4, HostAiIsolation 2, TableHost 4, ShufflePreparation 7; see R4-host-test-preservation-baseline.json.

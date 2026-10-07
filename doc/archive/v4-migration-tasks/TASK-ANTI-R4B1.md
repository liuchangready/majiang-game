# R4B1: one room service for in-process and UDP, complete player lobby

Prepared review brief; execute ONLY after Codex accepts R4A and explicitly invokes this task. This is the first R4B batch. R4B2 will add independent spectator projection and remaining presentation preview data. Do not start R5 or remove old V3 yet.

Read CONTRACT.md, TASK-ANTI-R4.md and accepted R4A evidence. Anti production/module tests; Codex independent tests/builds. Built-in file tools only, no shells/builds/commits/settings/other agents. Never modify tools/architecture/v4-review. Preserve verified Mahjong algorithms and R1-R3 runtime invariants.

## Ownership and architecture

- server room/registry/host/inprocess/udp, protocol player-lobby contracts/codecs, client.api models and ServerConnectionSession mapping/runtime, relevant module tests. Minimal domain integration only if actual lifecycle requires it, no rule rewrite. presentation stays R5.
- Introduce transport-neutral shared RoomRegistry/RoomService (names may differ). BOTH real InProcessBinding and UDP requests delegate to the SAME membership, configuration, readiness, authority-start, leave and lifecycle code. Shared business code must not depend on UdpClientSession/socket or emulate UDP transport internally. Bindings adapt identity and transport notifications only. New room service uses R4A ServerTableHost + TableAuthority, never old engine TableHost.
- Keep binding generations/tokens validated at actual command execution. Same-room start/config/join/leave/ready races must have one authoritative order. Domain queue cannot be blocked by Future.get/join, callbacks or network sends. No unbounded busy tick queue. Closing rooms terminates in-flight operations and suppresses stale publication.

## Typed contract and real workflows

- Add immutable protocol RoomInfo/member/RoomConfig/lifecycle and corresponding DISTINCT client.api view/input types, strict codec validation and complete mappers. Room configuration includes match rules, 20+3 time-control defaults and existing shuffle options; reuse domain validation through server mapping. No Any/raw map/engine DTO leaks.
- Provide real getRoomInfo, setReady, updateConfig and start operations through protocol -> both bindings -> shared service -> client LobbySession/GameSession runtime. Existing start can remain sole game-start API if coherently exposed to lobby; do not add dummy defaults. Make member/config/ready/lifecycle changes observable through ordered typed room updates so another client learns changes without UI polling. Integrate room update delivery with runtime dispatcher, generation invalidation, close and reconnect safeguards; server state is authoritative.
- Explicit lifecycle: LOBBY, PREPARING, PLAYING, SETTLED, FINISHED, CLOSED, derived consistently from accepted domain transitions. PREPARING means shuffle selection and is distinct from lobby ready. Settlement and final history retain existing public round records.
- Host-only config changes in LOBBY. Network start requires occupied human players ready, rejects unauthorized/not-ready requests without dealing, freezes seat admission at accepted start, fills vacant seats with AI under explicit room policy. Configuration changes reset human readiness as needed; return stable reason codes. Host may mark own readiness explicitly. Retrying start/config/ready respects existing request-id idempotency.
- Default singleplayer is an assembly policy: one human, 3 AI, auto-ready, same validation/service. Add an unbound in-process ServerConnection creation path so menu can create/configure/leave/recreate through the same LobbySession APIs. Existing createLocalTable helper may remain temporarily for stage tests, using same service; no second business implementation. No fake LOCAL_TABLE_UNSUPPORTED leave.
- Explicit player leave revokes resume token, releases pregame seat or leaves AI in started game; host leave succeeds to caller and dissolves room for others with ROOM_CLOSED. Accidental disconnect preserves resume identity and remaining time. Displacement, lost terminal ACK, server shutdown and repeated leave retain accepted R1 semantics. Returning to unbound lobby lets the same live connection create/join again where transport allows it.
- Pause/resume accessible as server room policy, local host allowed; network policy enforced server-side. Presentation animation is only a non-authoritative pacing hint, never prevents overdue timeout.

## Tests and delivery

Add shared-service tests plus parameterized/equivalent binding integration tests: unready/unauthorized start rejected, ready members start once; config permission/phase/reset; member push to other connection; create/join/leave/recreate; token revocation versus disconnect resume; replacement queued stale commands; lifecycle prep/play/settled/finished; close resources. Use fake clocks/executors for deterministic concurrency. Preserve all R4A 20 equivalent tests and old rules tests.

Existing R1-R4 tests that assumed network start without readiness need honest setup migration (mark players ready through real API) while preserving original timeout/privacy/terminal-ACK assertions. Tell Codex exact changed setup for reviewer-owned sources, don't edit them. Do not silently allow unready network start for compatibility. Do not weaken assertions or skip tests.

Write concise delivery-ANTI-R4B1.md: actual shared call chains, contract additions, policy decisions, touched files and pending R4B2 work. Read FEEDBACK-R4B1.md before finalizing. Tests are run by Codex, not Anti. R4B1 alone does NOT accept R4; spectator privacy and presentation previews remain R4B2.

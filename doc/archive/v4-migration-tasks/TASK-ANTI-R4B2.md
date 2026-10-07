# R4B2: independent spectator projection and server-provided presentation previews

Draft for execution ONLY after Codex accepts R4B1 and explicitly invokes this task. Anti develops; Codex tests/reviews. Built-in file tools only, no shell/builds/settings/commits/other agents. Reviewer-owned tools/architecture/v4-review remains read-only. Read CONTRACT.md and TASK-ANTI-R4.md; preserve verified rules, physical tiles, clocks and accepted room/runtime behavior.

## Ownership

Engine pure projection/preview integration using existing algorithms; server room/authority/mapping/bindings; protocol contracts/codecs; client.api models/runtime mapping; corresponding module tests. Presentation consumer migration and actual product default switch remain R5. Do not remove legacy product files yet.

## Spectator workflow

- Add real join-as-spectator through the shared R4B1 room service, both in-process and UDP bindings, and client facade. Spectators do not occupy one of four player seats or receive player authorization. Distinguish membership/identity explicitly; do not fake spectator as seat0 or weaken legal seat validation.
- Build an independent PUBLIC projection from domain public state. Do not mask only myClosedTiles in a player snapshot. No private drawn tiles, sleeves, unexposed wall/ura order, preparation choices/reservations/balances, player decision/options or private event payloads. Public board, tile counts, public scores/clocks, room state and public settlement/history remain visible. Examine nested records and events, not only top-level fields.
- Spectator requests to submit/discard, shuffle, claim/release control, ready/start/update configuration and private decision/query endpoints must be rejected server-side even for forged wire calls. Public read and leave remain valid. Player resume token cannot be substituted with spectator identity to gain a seat.
- Give public snapshots/updates an independent ordered stream and explicit full-recovery baseline. Use existing reliable UDP machinery and client dispatcher/generation protections. Old listener/connection callbacks cannot publish into replacement binding. Keep window bounds, terminal ACK, close and reconnection lifecycle coherent.
- Spectator leave revokes its membership without AI/control changes to any seat. Host room closure/server shutdown terminates spectators with the same room/transport semantics as players. Joining a full four-player room as spectator must work without evicting anyone. Never send private player projections to spectator transport as an internal convenience.

## Presentation data supplied by server

- Current UI calls engine session providers directly: GameScreen uses TenpaiPreviewProvider.waits, RiichiSelectionModel uses RiichiPreviewProvider.options, MeldSelectionModel uses MeldChoiceProvider.options. Reuse these verified pure algorithms at the authority/server projection boundary and expose distinct immutable protocol/client data for R5.
- Preserve physical tile identity/red-five choice, discard-to-riichi wait lists and furiten details, candidate meld groups and authoritative option IDs. Preview data must correspond to the same roundId/decisionId/controlEpoch as submitted option selection. UI must not implement a second legality, waits or scoring algorithm.
- Existing settlement DTO already carries full score breakdown, yaku/fu/han and history. Audit completeness against SettlementDetailRenderer; add missing typed fields only if genuinely absent rather than inventing duplicate ledgers. Keep shuffle preparation private, clocks authoritative and configuration round-trip complete.
- Preview work must not execute synchronously on UDP receive/UI callers, nor leak another player's concealed information via public/spectator projection. Pure computations in engine are allowed; transport and subscriptions remain server-owned.

## Required evidence

Both bindings: full room + spectator admission, read-only forged request rejection, nested privacy during preparation/turn/settlement/history, independent cursor/full recovery, spectator leave versus host dissolution, stale identity and callback suppression. Player preview tests compare existing providers on deterministic fixtures: red-five discard, riichi waits/furiten, chi/pon/kan alternatives, full scoring details. Protocol round-trip, strict validation, immutable source/getter collections and client-only public facade types remain mandatory.

Report concise delivery-ANTI-R4B2.md with actual call chains/DTOs and R5 consumer wiring notes. Read FEEDBACK-R4B2.md before finalizing. Codex runs all tests and accepts R4 only after B1+B2 pass; R5 remains gated.

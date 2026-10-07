# ROOM compiler/cursor/generation repair

Your ROOMFIX1 delivery remains unaccepted and does not compile. Continue same conversation, same owned room/host/inprocess files and module tests. Built-in file tools only; no shell/build/reviewer edits. Read FEEDBACK-R4B1ROOMFIX1.md now; late compiler findings arrived while the previous call finalized.

Do these concrete corrections first:
- InProcessBinding.kt:250/:259 ResumeRequest has `token`, not `resumeToken`.
- ServerTableHost.performQuerySnapshotOnQueue mixes String syncEpoch with else0L. Require valid seat and use actual seat stream syncEpoch AND current authoritative stream.cursor. Remove supplied cursor parameter (resume request cursor is a client's old baseline, never authority state version). SharedRoom.resumeSession must use that narrow host primitive.
- New SharedRoom.bindingGenerations increments on leave/resume, but existing InProcessBinding.makeValidator and callbacks check only connection-local bindingGeneration. Capture shared seat generation on bind and validate it on execution and notification, so replacing a live guest prevents the old connection from changing readiness/actions/query state. Retiring old connection must not later disconnect the newly resumed seat. Add appropriate replaced notification consistent with existing API if possible; at least stale actions reject and correct resume doesn't close room.

Then finish remaining concrete feedback in FEEDBACK-R4B1ROOMFIX1.md (wrong/revoked identity, guestclose stays resumable), without broad redesign. Codex selected20 tests will check actual behavior after compiler fix. Read FEEDBACK-R4B1ROOMFIX2.md immediately before delivery if present. Do not claim success based only on code reading. Write delivery-ANTI-R4B1ROOMFIX2.md; no tests run by Anti. No UDP or later stages.

# ROOMFIX3 live evidence

23:40 R4B1ROOMFIX3-server1.log runs86 servertests,83pass/3fail. Independent21 passed earlier, but full module run exposed a REAL race: `local host can leave and create another room on the live connection` gets APPLIED leave but conn.closed=true (line371). Fix production; preserve this assertion. InProcessBinding callback correction is explicitly added to this task's ownership.

Likely race visible in code: SharedRoom.leave increments seat generation, while old queued seat update callback compares sharedgen and calls handleDisplacedByNewConnection even during `isExplicitlyLeaving`. It also has an `else if !isCurrentGen && !closed` outside the captured localGen/currentRoom check, so a callback from a retired room can close a newly rebound connection. First guard captured connection-local generation AND room identity, then shared generation; suppress self-displacement during deliberate leave until subscriptions detach. Same for BindingRoomObserver callbacks. Add deterministic callback/latch regression if possible; do not rely only on a single lucky run.

Other2 module failures are test setup/contract mismatches:
- replacement resume test lines529+: marks guest ready but never host, then expects host.start APPLIED line555. Set host readiness through real API, retain old binding rejection and resumed identity/cursor assertions. Don't allow unready host in production.
- SharedRoomTest observer test expects3 callbacks (baseline and both toggles), but task explicitly permits bounded latest-full-view coalescing. Verify monotonically increasing observed versions and eventual latest known ready=true/version, and block one observer while another progresses if testing isolation. Don't demand all intermediate snapshots; do not weaken eventual-latest/order guarantees. This is contract correction, not deletion of valid rule coverage.

Await final servertests and independent selected21 after your changes. No ROOM acceptance yet. No UDP work in this call.

Reviewer local leave/create/start probe now repeats20 times on the same connection, preserving all original assertions. This is justified by the actual intermittent conn.closed regression seen in module run after a single independent pass. Same test count; don't alter reviewer source.

23:47 final before external quota block: your CLI stopped23:42:34 on429 (reset estimate02:52Oct6). Current source compiles. R4B-current-review.log independent109:100pass/9fail, including SAME local leave race with20iteration probe. R4B-current-server.log server86:83pass/3fail, SAME three failures above. HostSeat assembly edits persisted; binding race/test setup corrections still pending. Resume this task after quota, do not start UDP. Details and logs in RESUME.md.

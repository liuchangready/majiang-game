# ANTI-CLOCK: visible compact player timer

User requests visible available time on game screen formatted like `3+20`. Implement production only in core/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt and core/src/main/kotlin/com/majiang/screen/GameScreen.kt. No tests/build/engine changes. No shell. Codex tests and reviews. This is a small focused UI change, do not redesign architecture or unrelated behavior. Read only relevant ClockDisplay and renderPlayerStatusBox code, not whole large GameScreen when unnecessary. Write concise delivery-ANTI-CLOCK.md and finish.

Design:
- ClockDisplay.displayText exactly `free+reserve` integer seconds, e.g. active beginning `3+20`, at 1s `2+20`, at 3s `0+20`, at 4s `0+19`, expired `0+0`. Both positive millisecond values ceil to seconds; never negative. Keep freeSeconds/reserveSeconds API arithmetic and current authoritative deadline/serverNow/local elapsed logic. Do not reset time or hardcode 3 for active snapshots.
- Idle has no current free decision time: display `0+20` (or actual reserve), hasActiveTiming=false. Null clock shows `--+--`.
- Existing bottom-left status box is ~175x82; keep location/rectangle to avoid touching hand tiles. Make the compact time the primary large text, scale ~1.8 at y+48, enough room between top seat/control at y+70 and bottom points y+20. This must be visibly bigger than current scale0.75 text. Preserve/restore font scale/color. Active use bright yellow/white; <5s remaining reserve after free exhausted red, idle muted; existing border urgent logic preserved.
- PAUSED status belongs in top small label (e.g. P0 [PAUSED]); still render the same large frozen timer. Disconnected show top P0 [OFFLINE], large --+--, preserving exit interaction. No timer text running across rectangle with long prefixes. Existing points line stays.
- Clock is display only, no authority/timing rules altered; current pause elapsed=0 mechanism retained.

Codex will extend existing recovery timer test with boundary/format checks, run :core:test and actual GL screenshot smoke. Do not weaken or edit tests.

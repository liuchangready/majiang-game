# Finish interrupted opponent meld UI task

Original ANTI-OPPONENT-MELDS process hit bridge timeout and exited. It wrote production changes but no final report. Preserve those changes; do NOT reimplement. Read TASK-ANTI-OPPONENT-MELDS.md for ownership/contract. Scope only same three production files and report; no tests/build/shell.

Codex actual compile log build/v3-migration/anti-opponent-melds-review.log: GameScreen line706 calls displayedTableMelds() but that method was never added! Add public `fun displayedTableMelds(): List<MeldDisplayGroup> = MeldDisplayLayout.layoutAll(currentView)` next to existing displayedMelds(), which remains own-only. Verify wiring compiles by static inspection; Codex runs builds.

The rest is largely done: layoutAll, ownerSeat/y, side/top layout, renderer, relative table labels, river/getDiscardStart mappings, animation relative seat. computeHandPosition is already passed relSeat by its caller, so DO NOT remap that parameter a second time.

One layout adjustment: top group CALLED/ADD labels use y595; top player's name/score currently at y590 overlaps these labels around x500..775. Move top name/score to y580: below sublabels, above top river y530+36=566. Keep upper meld tiles y600, labels y666.

Review for other incomplete edits, keep minimal, then write concise delivery-ANTI-OPPONENT-MELDS-FINISH.md and finish promptly. Six new Codex acceptance tests already exist at core/src/test/kotlin/com/majiang/render/OpponentMeldDisplayAcceptanceTest.kt; do not edit them. Real GL smoke script also exists and will be executed by Codex. No need to search historical docs or read full GameScreen.

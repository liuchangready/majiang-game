# Task F: visual acceptance corrections

P/U/L have completed; Codex full 233-test run passed, actual LWJGL selection -> confirm -> 13-wait -> cancel smoke passed. Two visual issues need your production fixes. Own only screen/GameScreen.kt and delivery-F.md. Keep changes minimal, no tests/Gradle/dependencies. Built-in file tools ONLY; no MCP tools, terminal or RunCommand. Codex runs tests/screenshots.

1. Actual screenshot `build/riichi-choice/discard-6s-wait-2s-5s.png` shows `[DRAW]` and `[SELECTED]` drawn at exactly the same position above the selected drawn tile. Fix by rendering one unambiguous label (e.g. `SELECTED` only for selected drawn tile), or separate non-overlapping labels without colliding with action buttons. `renderPlayerHands` and `renderRiichiSelectionOverlay` currently both use rect.y+height+18. Applies to every skin and sorting mode.
2. The hover header `DISCARD (PREVIEW)` starts at x250 and can extend beyond the x360 divider into the right-side `PREVIEW WAITS` title at x380. Use short `DISCARD` on the left for all cases; the right-side preview title already indicates hover mode. Verify no header overlap.
3. Check Android BACK actually reaches keyDown: if libGDX BACK isn't caught, add lifecycle-safe `Gdx.input.setCatchKey(Input.Keys.BACK, true)` while this screen is active and restore the previous catch setting on hide/dispose if necessary. Scope only what is needed for the requested cancellation behavior; do not alter normal game rules.

Do not claim visual validation; Codex will regenerate and inspect screenshots. Return delivery-F.md and normal text after edits.

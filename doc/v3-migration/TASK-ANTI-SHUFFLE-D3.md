# D3 bounded font coverage repair

Own core/src/main/kotlin/com/majiang/presentation/ShufflePreparationPanel.kt only. No tests/build/shell. PriorD/D2done. Current explicit subset STILL misses visible glyphs: Chinese comma ， and characters in translateServerReason (请 该 更 接 方 案 或 etc). Codex added `Chinese font subset covers player labels and translated errors` test in core/src/test/kotlin/com/majiang/ShufflePreparationUiAcceptanceTest.kt; READ only. Add all visible fixed text/error glyphs. Prefer derive subset from a shared constant list of UIstrings or audit everyrenderliteral; no wholeUnicode.

Also bound latecallback guard to !client.isClosed && model.isActive && model.roundId==targetRoundId, skip callback when current submittingRequestId is a DIFFERENT nonnull request. Do not lose successful response after matching confirmed snapshot (submittingIdclearedbyupdate). Keep interface/layoutstable. Report delivery-ANTI-SHUFFLE-D3.md briefly.

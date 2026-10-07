# Final domain atomicity correction

Continue accepted-in-progress R4B1DOMAIN source. Own only ApplicationCoordinator.kt and relevant engine test if useful; built-in files only, no shells/builds/commits/reviewer edits. Current three independent domain tests and348 engine tests pass, but one final public-helper issue remains.

ApplicationCoordinator.updateLobbyConfig currently validates versions and phase but DOES NOT validate newConfig before mutating MatchSession. Calling it with invalid startingScores can change session/rule properties then throw in RoundStateMachine's loop, leaving partial state. Make helper internal (it is for TableAuthority within engine), and validate newConfig before the first mutation regardless, so atomicity doesn't depend on a caller remembering validation. Preserve other edits. No broader redesign or reading needed. Add a small meaningful engine regression that calls invalid config and verifies session rule config/scores/round state unchanged if not already covered.

Write delivery-ANTI-R4B1DOMAINFIX.md; tests run by Codex. Do not read or edit room/network code. Deliver this small correction immediately after implementation.

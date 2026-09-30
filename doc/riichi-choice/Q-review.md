# Task Q: final BACK lifecycle ownership correction

Own only screen/GameScreen.kt and delivery-Q.md. P/U/L/F have delivered; do not redo their work. Native file edits ONLY, no MCP, shell, RunCommand, or tests. Codex tests it.

F restored previousCatchBack but still restores unconditionally in both hide and dispose. A hidden screen disposed after another screen has changed BACK handling overwrites the new screen's value. Repeated show also overwrites the original saved value. Add a private ownership/active flag: capture previous value once when becoming active, restore it once on hide/dispose only if owned, then clear ownership. Disposing a never-shown screen must not alter BACK state. Repeated show must not overwrite the original previousCatchBack. Retain current inputProcessor identity check; no other changes.

Codex regression `RiichiChoiceScreenContractTest.android back catch setting is restored when screen leaves` now covers hide -> other screen setting -> old dispose and duplicate show. Keep all visual fixes and selection behavior. Write a short delivery-Q.md and finish without commands.

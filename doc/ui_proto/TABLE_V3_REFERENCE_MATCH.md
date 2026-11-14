# V3 reference match iterations

## Baseline and goal

- Worktree: `D:\majiang-wt\table-ref-match-20261112`
- Branch: `task/table-ref-match-20261112`
- Base branch / commit: `ui_prot` / `c67ce10936cbde44815afb5de7e6836a536588ad`
- Baseline screenshot: `build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6/screenshots/table-20261112-111711-070-1.png` (1200x690)
- Reference screenshot: user-provided 600x345 image (40:23); compare at exactly 2x scale.
- Preserve V1 and V2. V3 is a new direct-launch view using `--prototype-mahjong-table-v3`; C-key captures remain in the current build root.

## Baseline differences observed

1. V2 joined all three opponent rows into a U and shared both corners. The reference has three visibly separate rows with open gaps at both corners.
2. After normalizing the reference to 1200x690, its north row is approximately x=500..840, y=16..58. V2 was approximately x=365..840, y=47..94: too long, too low, and centered too far left.
3. The reference side rows sit near the outer edges: left approximately x=140..224 and right x=1020..1130. V2 rows were around x=270..370 and x=830..930.
4. Reference side rows are vertically staggered: left about y=40..315; right about y=218..510. V2 used a symmetric U, with both sides starting at the same north corner.
5. Reference backs are plain orange with narrow cream/gold top bevels and dark brown side faces. V2 used large green backs with bright diamond emblems and pale thick top rails.
6. V2's 1200x690 screenshot had small top/bottom letterbox bars; V3 selects height-based canvas scaling and crops the felt for 40:23.

## Iteration plan

- Keep V2's shared chassis/projection for consistent per-tile thickness, but use a separate reference layout with three independent rows and no shared corner joins.
- Reduce the canonical V3 tile body to about 0.081×0.15 world units, use 14 tiles in each row, and tune row screen offsets to the normalized reference bounds.
- Restore the reference's orange backs and subtle gold/brown edge colors; remove the emblem from V3 backs.
- Build a fresh Windows Player, open V3 directly, capture with C, inspect the PNG, then adjust based on the remaining visible mismatch.

## Results

### Final geometry and comparison

- Latest direct-table screenshot: `build/ui_proto/v3-four-faces-final-8f38e06680b345a195867e85a7f59f2f/screenshots/table-20261112-131721-070-0.png` (1200x690; final C-key capture).
- Final measured wall bounds from the captured client image: north approximately x=499..841, y=17..58; west x=149..224, y=40..314; east x=1018..1135, y=223..527. These align with the reference at 2x scale; the remaining side-wall endpoint error is only a few pixels.
- North, west and east walls are separate. Each opposing wall uses individually split orange tile backs, thin dark seams, a cream/gold top bevel, and a brown side face. The side rows keep the target's different vertical starts and lengths.
- The V3 wall box now exposes named projected surfaces: front, back, top, bottom and side. Wall assembly draws the orange back, top bevel, side thickness, bottom contact face and ground shadow as separate surfaces; the opposite front face remains available for the marked tile face.
- V3 river tiles use a separate front chassis with the face glyph, plus independently rendered top, side and bottom thickness surfaces. Existing V1/V2 flat-tile sprites remain unchanged. The rotated side rivers were widened and repositioned, and the self river was raised to match the reference row.
- The 40:23 client canvas, rivers, center panel and player hand remain aligned to the normalized reference. A slight V3-only horizontal felt crop widens the table frame while preserving the source's full vertical range.
- Remaining scene-level visual differences are outside the wall geometry: the reference uses different anime portraits, extra edge controls and labels, and a slightly different felt-table border. The repository's available portrait and felt assets do not reproduce those exact pixels; this prototype is visually aligned for the requested 2.5D walls, but the full scene is not pixel-identical.

### Iteration notes

1. First V3 capture exposed the incorrect right-wall start, narrow/short side rows, overly light felt and mispositioned rivers/center panel. The rivers and panel were moved to the normalized reference coordinates; wall rows were separated from the V2 U layout.
2. Second and third captures showed stale-process screenshots and the right row appearing to start at the top. A fresh Player plus one-shot mesh bounds confirmed the actual row geometry; the temporary diagnostic log was removed after confirmation.
3. The first darker felt pass overcorrected. The wash alpha was reduced; the north-wall gap was narrowed while side-wall seams stayed visible. West and east row length, offset and slope were tuned independently.
4. A V3-only background crop experiment enlarged the border vertically. PNG dimensions (`1672x941`) showed that the original image should be cropped horizontally only; vertical cropping was reverted, keeping only a small horizontal crop.
5. The first full-screen comparison showed missing edge controls, undersized portraits, no player status bars, and seat labels overlapping avatars/hand. The V3 composition now includes the reference counter panel, title, settings/hand-type buttons, left/right action rails, alert/network indicators and avatar status bars. Portraits and score plates were moved to clear the hand; text size was retuned after the first pass was too large.
6. Final side-wall drift was tuned from the fresh capture: the east row's lower end was pulled inward, while the west row's lower end was aligned under its top end.
7. Following the full-scene comparison, V3 was changed to assemble wall and river tiles from separate faces rather than rely on one all-in-one flat chassis. A fresh build and C capture verified the face assembly, side-row width and self-river position.

### Build and runtime evidence

- Latest build root: `build/ui_proto/v3-four-faces-final-8f38e06680b345a195867e85a7f59f2f/`.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-ui-proto-windows.ps1 -RunRoot <fresh root>` passed: native Unity exit 0, asset preflight 0, compiler errors 0, BuildReport `Succeeded`/0 errors/32 warnings, 181 Player files.
- An intermediate build was blocked because the isolated tree's shared DLLs did not match its ignored provenance record. Rebuilt the current .NET shared projects (`0 warnings, 0 errors`), copied 19 outputs, and verified provenance (`156 inputs`, `19 outputs`, `0 failures`) before retrying.
- Latest Player launched from the fresh output with `--prototype-mahjong-table-v3` (PID 5568). A focused C key-down/up produced the PNG above. The latest Player remains open for inspection.
- Unity's generated importer and performance-run file changes were restored. The shared DLL/PDB outputs remain synchronized with the provenance record so future Unity builds in this worktree pass the guard. No generated files were copied to another worktree.
- The latest comparison pass added explicit four-face composition for V3 walls and discards, then inspected the fresh screenshot. The orange back, white marked face, warm side and bottom faces, and small contact shadow now render as separate surfaces.
- Remaining visible differences are the exact anime portrait art, the reference's more detailed felt/table perimeter, and its original ornamental textures. The current worktree only has the animal medallions and blue felt asset, so these areas remain approximations; no pixel-identical full-scene claim is made.
- No unrelated test suite was run. No commit, push, merge or deployment was performed.

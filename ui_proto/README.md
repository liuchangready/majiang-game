# ui_proto Windows Demo

## Open in Unity

Open `unity/Mahjong.Unity` as the Unity project. In the Project window, open `Assets/Scenes/Prototypes/V5ArtPrototype.unity` and press Play. Alternatively use `Tools > Mahjong > V5 Art Prototype > Create and Open Preview`.

## Run the Windows Demo

Keep the complete `player` folder together and launch `player/ui_proto.exe`; Unity runtime DLLs and `ui_proto_Data` are required alongside the executable. To build it from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-ui-proto-windows.ps1
```

The script writes a full Windows player under `build/ui_proto/<GUID>/player/`, plus Unity logs, a BuildReport, a summary and a SHA-256 manifest. It waits for the shared Unity validation lock and refuses to launch batchmode while a Unity Editor is open.

## UI flow and controls

1. From the menu, click `单机对局` to see the table preparation state.
2. Click `偷天换日 · 30点` to open the six-target picker. Choose one unified shuffle target and confirm, or return to preparation.
3. Start the visual demo with the selected preview, or use `直接开局演示 · 0点` without selecting a target.
4. Once the demo starts, use V5's animation controls for draw, select, discard, riichi, opponent discards, and chi/pon/kan. `Esc` or `Tab` navigates back.

The skill selection is a UI preview only: it does not spend points, submit an authoritative skill, or change shuffle behavior. The preparation view does not show match hands, wall, or match HUD before the visual demo begins.

## Bridge delivery and validation provenance

This bridge delivery tree is `E:\agent-space\thread_20261111_012152_abd6\worktree` on branch `mesh/task/thread_20261111_012152_abd6`, based on `58b27b00696c458585a790409931c121f3e51314`. The earlier development tree is `D:\majiang-wt\ui-proto-20261111` on `task/ui_proto-20261111`; it is not the bridge delivery tree. The final verified package and evidence are under `build/ui_proto/20db8bf6c84d4820aee18435a7f456ee/` in this tree; older GUID roots are retained as rework history.

The final preview-method log, Player.log, menu/preparation/selector/table screenshots, BuildReport and SHA-256 manifest are retained under `build/ui_proto/20db8bf6c84d4820aee18435a7f456ee/`.

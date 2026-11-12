# ui_proto Windows Demo

## Open in Unity

Open `unity/Mahjong.Unity` as the Unity project. In the Project window, open `Assets/Scenes/Prototypes/V5ArtPrototype.unity` and press Play. Alternatively use `Tools > Mahjong > V5 Art Prototype > Create and Open Preview`.

## Run the Windows Demo

Keep the complete `player` folder together and launch `player/ui_proto.exe`; Unity runtime DLLs and `ui_proto_Data` are required alongside the executable. To build it from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-ui-proto-windows.ps1
```

The script writes a full Windows player under `build/ui_proto/<GUID>/player/`, plus Unity logs, a BuildReport, a summary and a SHA-256 manifest. It waits for the shared Unity validation lock and refuses to launch batchmode while a Unity Editor is open.

## Open the table-only screenshot prototype

After a successful build, launch the comparison view directly at the 本家 table layout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-ui-proto-table.ps1
```

This opens the newest successful player in a 1280×720 window. Launching `ui_proto.exe` normally still opens the main menu.

While this direct table-only Player is focused, press **C** to save a PNG in the active build root's `screenshots/` folder. Each press uses a unique timestamped name (`table-YYYYMMDD-HHmmss-fff-<n>.png`); these captures are independent of the Player folder and remain with that build's evidence.

## UI flow and controls

1. From the menu, click `单机对局` to see the table preparation state.
2. Click `偷天换日 · 30点` to open the six-target picker. Choose one unified shuffle target and confirm, or return to preparation.
3. Start the visual demo with the selected preview, or use `直接开局演示 · 0点` without selecting a target.
4. Once the demo starts, use V5's animation controls for draw, select, discard, riichi, opponent discards, and chi/pon/kan. `Esc` or `Tab` navigates back.

The skill selection is a UI preview only: it does not spend points, submit an authoritative skill, or change shuffle behavior. The preparation view does not show match hands, wall, or match HUD before the visual demo begins.

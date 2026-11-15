# Debug scene insertion

In Unity Editor or a Development build, pass a JSON path to the boot player:

```text
-debug-scene unity/debug-scene.example.json
```

The app validates the file before creating its composition or opening a host, then
starts the selected local fixture and navigates directly to the existing Table
scene. A load error is logged as `DEBUG_SCENE_LOAD_FAILED` with a JSON field path
and the app exits without creating a room.

Version 1 intentionally selects one of the existing accepted LocalHost fixtures:

| Field | Type | Meaning |
| --- | --- | --- |
| `version` | integer | Must be `1`. |
| `scenario` | string | One of `pon`, `chi`, `riichi`, `closed-kan`, `dealer-tsumo`. |

These fixtures create a legal initial deal with all 136 physical TileIds and
replay any setup actions through the existing domain command path before LocalHost
creates the room checkpoint. Pre-advanced pon/chi setup carries its committed
event history and command bindings into that checkpoint, so the baseline and cold
recovery preserve the same rivers and event identities. The JSON does not accept arbitrary hands, rivers,
scores, wall positions, melds, riichi flags, or cheat ledger values: those fields
cannot currently be made consistent with the authoritative event and recovery
contracts from an external snapshot. Extend this format by adding a legal deal
plus an accepted command script, not by importing a RoundRecovery checkpoint.

Each launch gets a fresh room id, round id, and save directory under
`LocalSaveDirectory/debug-scenes/`. The round id retains the reserved `c04-`
fixture prefix because LocalHost uses it to preserve legally pre-advanced fixture
history in the initial checkpoint and projection; the unique suffix prevents
round identity reuse. Table commands continue through the normal local authority.
The importer, argument switch, and launch route are compiled
only for Editor, Development, and test targets; Release builds contain no
`-debug-scene` handler.

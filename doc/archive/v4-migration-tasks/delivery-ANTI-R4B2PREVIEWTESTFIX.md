# R4B2PREVIEWTESTFIX 交付说明

## 1. 任务概述
根据 `TASK-ANTI-R4B2PREVIEWTESTFIX.md` 授权，修复 `server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolPreviewMapperTest.kt` 中 `test MeldPreviewOption for all meld kinds roundtrip` 的加杠（`ADDED_KAN`）测试装配。生产代码保持完全冻结，未改动任何生产校验或放宽预期，未删除或跳过任何测试。

## 2. 修改文件清单
1. `server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolPreviewMapperTest.kt`
   - 将 `addedKanOpt` 中原单张牌非法动作 `GameAction.DeclareKan(0, listOf(addedTile), MeldType.ADDED_KAN)` 修正为包含全部 4 张物理牌的真实领域动作：`GameAction.DeclareKan(0, listOf(ponTile1, ponTile2, calledPon, addedTile), MeldType.ADDED_KAN)`；
   - 保持 `handTiles` 仅为补入手牌 `listOf(addedTile)`（1张）；
   - 保持 `tiles` 为 `listOf(ponTile1, ponTile2, calledPon, addedTile)`（4张）；
   - 保持 `calledTile = calledPon`、`addedTile = addedTile`、`fromPlayer = 1`；
   - 增加显式往返断言：
     - `assertEquals(4, (roundtripAddedKan.action as GameAction.DeclareKan).tiles.size)`
     - `assertEquals(1, roundtripAddedKan.handTiles.size)`
     - `assertEquals(addedTile, roundtripAddedKan.handTiles[0])`
     - `assertEquals(addedTile, roundtripAddedKan.addedTile)`
     - `assertEquals(calledPon, roundtripAddedKan.calledTile)`
     - `assertEquals(1, roundtripAddedKan.fromPlayer)`
     - `assertTrue(roundtripAddedKan.tiles.any { it.isRedFive })`
2. `doc/v4-migration/delivery-ANTI-R4B2PREVIEWTESTFIX.md` (本文档)

## 3. 测试运行说明
- **重要声明**：按照分工约束与规则指令，Anti 未运行任何 Gradle 构建、编译器或自动化测试命令（测试由 Codex 独立编译并执行验证）。
- 在修改前已查阅 `doc/v4-migration/FEEDBACK-R4B2PREVIEWTESTFIX.md`（该文件当前不存在）。

## 4. 未解决项 (Unresolved items)
- 无未解决项。本任务授权范围内的唯一测试装配缺陷已全部修正。

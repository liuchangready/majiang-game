# 任务简报：删除怀疑度系统与声东击西（及全部四大绝技未接线代码）

> Anti 专用简报。**只用文件工具（读/写/编辑/新建/删除），禁止执行任何命令**——不跑 gradle、不跑测试、不跑 git。编译与测试由主会话（Claude）代跑验收。

## 背景与目标

V4 迁移后，线上千术只有三个：**落汗（cheat/mark）、袖里藏花（cheat/bloom）、偷梁换柱（cheat/beam）**。
早期“四大绝技”（袖里乾坤/移花接木/神仙透视/声东击西）及其配套的怀疑度/视线/抓包系统从未接入对局链路，属于死代码。经用户确认：**全部删除，不保留旧入口兼容性**。

## 必须删除的内容（按模块）

### 0. 怀疑度孤儿事件链（简报修订追加，与下述各条同批处理）

`SuspicionSystem` 已删后，`SuspicionChanged` 与 `GazeShifted` 两个事件再无发布方，全链路删除：

- engine `event/GameEvent.kt:89,92`：`GameEvent.SuspicionChanged`、`GameEvent.GazeShifted` 两个 data class。
- engine `projection/PublicProjectionFactory.kt:155-156`：两个 `-> null` 过滤分支；`:108-109` 注释中“怀疑度变化 (SuspicionChanged)、视线转移 (GazeShifted)”字样（**`CheatPointsChanged` 保留**，它是 CheatLedger 经济事件）。
- protocol `ProtocolModels.kt:1401-1414`：两个 DTO；`ApiMessage.kt:775-782,792-802`：两个编解码分支；`ProtocolPublicModels.kt:445-446`：清单条目。
- server `EngineProtocolMapper.kt:843,856`：两个映射分支。
- client `ViewModels.kt:1300,1313`：两个视图事件；`PublicViewModels.kt:464-465`：清单条目；`ProtocolViewMapper.kt:449,451,1044,1046`：映射分支。
- 测试：engine `PublicProjectionTest.kt:319-320`；protocol `ProtocolPublicModelsCodecTest.kt:408-409`、`ProtocolSerializationTest.kt:105,107`；server `EngineProtocolMapperTest.kt:349,351`；client `ProtocolViewMapperTest.kt:605-606`；tools `V4PublicProjectionAcceptanceTest.kt:177-178`（保留同行 `CheatPointsChanged`）、`V4PublicWireAcceptanceTest.kt:314-315`（保留 `CheatPointsChanged`）、`V4SpectatorBindingAcceptanceTest.kt:106`（仅删 `"SuspicionChanged", "GazeShifted"` 两个字符串，保留 `"CheatPointsChanged"`）。

### engine（`engine/src/main/kotlin/com/majiang/`）

1. **整文件删除**：
   - `cheat/SuspicionSystem.kt`（怀疑度/视线/抓包系统）
   - `cheat/CheatActionExecutor.kt`（四大绝技执行器）
2. **`event/GameEvent.kt`**：删除 `enum class CheatTechnique`（4 个枚举值全删）、`GameEvent.BustedEvent`、`GameEvent.CheatAttempted`。
3. **`state/GameAction.kt:49`**：删除 `GameAction.UseCheatTechnique`。
4. **`round/RoundRuleService.kt:82-84`**：删除 `is GameAction.UseCheatTechnique -> { return RejectionReason.UNSUPPORTED_ACTION }` 分支。
5. **泛型处理链中的 UseCheatTechnique 分支**（删除各 when 臂）：
   - `application/EventSnapshot.kt:22`
   - `state/ResponseResolver.kt:58`
   - `state/ResponseWindow.kt:93` 与 `:111`
   - `application/ApplicationCoordinator.kt:2758`
   - `session/PlayerView.kt:139`
6. **`session/SessionUpdate.kt`**：
   - 删除 `filterVisibleEvents` 中 `hasDivineSight` 特例（:74-77）——里宝牌授权今后仅由终局结算触发（`isUraDoraAuthorized = isWinSettlement`）；
   - 删除 `redactForViewer` 中 `is GameEvent.CheatAttempted` 分支（:96）；
   - 同步修订该函数的 KDoc（删除千术事件/神仙透视相关行，:64-65）。
7. **`projection/PublicProjectionFactory.kt`**：删除 `is GameEvent.BustedEvent -> snap` 分支（:144）及 :104 注释中“公开抓包 (BustedEvent)/神仙透视”相关字样。
8. **`model/TileMovement.kt:512`**：删除 `swapHandWithSleeve`（袖里乾坤专属物理原语，删除执行器后无生产调用方；唯一测试引用见下）。**注意：`swapHandWithWall`（:477）被 `application/ApplicationCoordinator.kt:1394,1894` 生产使用（偷梁换柱/袖里藏花实现），绝对保留。**

### protocol（`protocol/src/main/kotlin/com/majiang/transport/api/`）

9. **`ProtocolModels.kt`**：删除 `enum class ProtocolCheatTechnique`（含 DIVERSION，:45-53 附近）、`ProtocolGameEvent.BustedEvent`（:1408 附近）、`ProtocolGameEvent.CheatAttempted`、`ProtocolLegalAction.UseCheatTechnique`。
10. **`ApiMessage.kt`**：删除 `"BustedEvent"`（:784-790）、`"CheatAttempted"`、`"UseCheatTechnique"` 三个 JSON 编解码分支及其序列化侧对应分支。
11. **`ProtocolPublicModels.kt:435`**：从公开事件类型清单中删除 `BustedEvent`；同文件如有 `CheatAttempted` 一并删除。

### server（`server/src/main/kotlin/com/majiang/server/mapping/`）

12. **`EngineProtocolMapper.kt`**：删除 `mapCheatTechnique`/反向映射（:250-257 区域）、`GameEvent.BustedEvent`（:850）、`GameEvent.CheatAttempted`、`GameAction.UseCheatTechnique` 的全部映射分支。

### client（`client/src/main/kotlin/com/majiang/client/`）

13. **`api/ViewModels.kt`**：删除 `enum class CheatTechniqueView`（:54-58）、`LegalActionView.UseCheatTechnique`（:317 附近）、`GameEventView.CheatAttempted`（:1294）、`GameEventView.BustedEvent`（:1307），以及 :50-52 相关注释。
14. **`api/PublicViewModels.kt:454,463`**：从事件类型清单中删除 `BustedEvent`、`CheatAttempted` 条目。
15. **`mapping/ProtocolViewMapper.kt`**：删除 `mapCheatTechnique`/`toProtocolCheatTechnique`（:160-171）、`UseCheatTechnique` 双向映射（:265,:289）、`CheatAttempted`（:448,:1043）与 `BustedEvent`（:450,:1045）映射分支，及 :20 import。

### 测试调整（只删用例，禁止削弱其他断言）

16. `engine/src/test/.../state/UnifiedActionAcceptanceTest.kt:48`：删除 `UseCheatTechnique(DIVERSION)` 条目。
17. `engine/src/test/.../state/CommandValidationContractTest.kt:87`：删除 `rejectWithoutMutation(..., UseCheatTechnique(SLEEVE_SWAP))` 用例及 :4 import。
18. `engine/src/test/.../projection/PublicProjectionTest.kt:317,333`：删除 CheatAttempted/BustedEvent 相关事件用例（其余公开事件断言保留）。
19. `engine/src/test/.../model/TileMovementContractTest.kt:107-108`：删除 `swapHandWithSleeve` 测试块（`swapHandWithWall` 的测试与 :112 保留）。
20. `protocol/src/test/.../ProtocolModelsTest.kt:64-70`：删除 UseCheatTechnique codec 用例。
21. `protocol/src/test/.../ProtocolPublicModelsCodecTest.kt:407` 及事件清单（:480 附近）：删除 CheatAttempted/BustedEvent 条目。
22. `protocol/src/test/.../ProtocolSerializationTest.kt:106`：删除 BustedEvent 用例。
23. `client/src/test/.../ProtocolViewMapperTest.kt:139,159,587,604`：删除 UseCheatTechnique/BustedEvent/CheatAttempted 用例及 :9 import。
24. `client/src/test/.../EngineDtoMapperTest.kt:124,142,150-151`：删除 UseCheatTechnique 用例及 :12 import 别名。
25. `tools/architecture/v4-review/V4ModelAcceptanceTest.kt:3,71`：删除 UseCheatTechnique 用例。
26. `tools/architecture/v4-review/V4SpectatorBindingAcceptanceTest.kt:105`：从 privateTypes 清单删除 `"CheatAttempted"` 字符串。
27. `tools/architecture/v4-review/V4PublicWireAcceptanceTest.kt:313`：删除 CheatAttempted 断言。
28. `tools/architecture/v4-review/V4PublicProjectionAcceptanceTest.kt:168,175-176,187`：删除 BustedEvent/CheatAttempted 用例（CheatLedgerEntry/Summary 经济部分 :90,:128,:136 **保留**，仅 :128 reason 字符串 "sleeve-swap-*" 可改为中性词如 "legacy-cost-*"）。

### 文档（同步更新）

29. `CLAUDE.md` 项目简介行：`（袖里藏牌、偷天换日、透视、声东击西、怀疑度与防抓包）` 改为 `（落汗、袖里藏花、偷梁换柱）`。
30. `doc/FRAMEWORK_ARCHITECTURE.md`：删除/改写“四大千术绝技与视线博弈”与 `SuspicionSystem` 怀疑度系统段落，改为三个线上千术 + `CheatLedger` 经济的现况描述；删除 :221 前后关于 `CheatActionExecutor`/`BustedEvent` 的说明。
31. `doc/README.md`：第 2 节要点中“四大千术绝技与视线博弈/`SuspicionSystem` 怀疑度系统（0~100%），AI 动态视线追踪与抓包判定（Busted）”改为三个线上千术的描述。
32. `doc/SERVER_CLIENT_API.md`：删除 BustedEvent/CheatAttempted/UseCheatTechnique/千术枚举的协议条目（若仅出现在历史叙述句中则一并清理该句）。
33. `doc/beam-swap/DESIGN.md`、`doc/sleeve-bloom/DESIGN.md`、`doc/tile-mark/DESIGN.md`：仅当有句子把已删系统描述为现存行为时做最小修订；交付/验收/反馈类历史文档（`doc/**/DELIVERY-*`、`FEEDBACK-*`、`ACCEPTANCE-*`、`doc/archive/**`、`doc/v4-migration/**`）**一律不动**。

## 明确保留（不得改动）

- `cheat/mark`、`cheat/bloom`、`cheat/beam` 三套线上千术全部代码与测试。
- `engine/src/main/kotlin/com/majiang/economy/` 的 `CheatLedger` 经济体系（线上千术扣费在用）。
- `GameState.sleeveTiles`、`TileMovement.swapHandWithWall`、`TilePool`/`StateInvariants` 的守恒审计（136 张闭环）。
- `doc/archive/` 与一切历史交付记录。
- 工作区中与本任务无关的未提交改动：`lwjgl3/build.gradle`、`lwjgl3/src/main/kotlin/com/majiang/desktop/SnapshotLauncher.kt`、`snapshot/` 目录——**禁止触碰**。

## 陷阱清单

- `swapHandWithWall` 名字与 `swapHandWithSleeve` 相近，但前者是**生产代码**（袖里藏花/偷梁换柱），删错会毁掉线上功能。
- 删除 when 分支时注意 Kotlin 密封类穷尽性：删掉类型定义后，各映射 when 需同步删臂，不能留悬空分支。
- `SessionUpdate.filterVisibleEvents` 删除 `hasDivineSight` 后，确认 `isUraDoraAuthorized` 逻辑退化为仅 `isWinSettlement`，相关测试（里宝牌可见性）如引用神仙透视授权路径需一并调整，但**终局结算公开里宝牌的既有断言必须保留通过**。
- 行号是撰写简报时的参考锚点，以符号名为准；若发现简报未列出的 `SuspicionSystem|CheatActionExecutor|CheatTechnique|CheatAttempted|BustedEvent|UseCheatTechnique|applyDiversion|swapHandWithSleeve|SuspicionChanged|GazeShifted` 残留引用，一律按同样原则删除并在交付说明中列出。`CheatPointsChanged` 是 CheatLedger 经济事件，**不属于怀疑度系统，必须保留**。
- 测试只能“删除被移除符号的用例”，不得借机删除或放松任何其他断言（项目规矩：禁止通过削弱有效测试完成迁移）。

## 完成标准（由主会话验收）

1. `./gradlew build`（含全部模块测试）绿。
2. `./gradlew -I tools/architecture/v4-review.init.gradle :server:test --tests 'com.majiang.review.*'` 绿。
3. 全仓 `src/` 与 `tools/` 下 grep 不到上述已删符号（历史文档除外）。
4. `git status` 仅出现本简报列出的文件变更，`lwjgl3/`、`snapshot/` 未被触碰。

完成后在仓库根新建 `doc/legacy-cheat-removal/DELIVERY-ANTI.md`，列出：实际改动文件清单、简报之外的发现与处理、遗留风险。

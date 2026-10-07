# R4B2 WIREFINAL 交付简报 (Anti)

**交付时间**: 2026-10-06  
**任务**: R4B2WIREFINAL  
**开发者**: Antigravity (Anti)  
**验收负责人**: Codex (监督、构建、独立测试与整体验收)

---

## 1. 修改文件清单

1. [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
   - 生产代码所有权：仅 `classifyPush` 函数实现与对应 KDoc 注释。
2. [`protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPublicModelsCodecTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPublicModelsCodecTest.kt)
   - 模块测试所有权：仅修复两处 JUnit `assertNotNull` 返回 `Unit` 导致的 Kotlin 编译类型推导问题。

---

## 2. 具体修改内容与行为说明

### 2.1 `ApiMessage.kt` (`classifyPush`)
- **区分明确 null 与真正缺失**：
  - 通过 `obj.has("type")` 严格区分信封是否包含 `"type"` 键。
  - 若包含 `"type"` 键，检查其元素是否为非空 primitive string。若为 explicit `null`（如 `{"type": null}`）、数字、布尔或对象，一律抛出 `JsonParseException("PUSH 'type' must be a non-null string")`，严格修复此前 `typeElem.isJsonNull` 被误判为 `PLAYER` 的问题。
  - 对已知合法字符串精准分发：`"ROOM_UPDATE"` 返回 `PushType.ROOM`，`"PUBLIC_UPDATE"` 返回 `PushType.PUBLIC`，其余未知 type 抛出 `JsonParseException`。
- **校验无 type 分支的合法玩家信封结构**：
  - 当且仅当 `!obj.has("type")` 时进入玩家推送分支。
  - 检查必须存在 `"snapshot"` 字段且为 JSON 对象；调用 `validateSnapshotObject` 验证快照核心嵌套字段（`sessionId`, `syncEpoch`, `view`, `decision`）。
  - 执行 `decodePushEnvelope(json)`，确保空对象 `{}` 或无玩家 update 的非法载荷抛出 `JsonParseException`，同时完全保留真实 `encodePush` -> `decodePush` 的合法玩家推送基线。
- **更新对应 KDoc 注释**，精确阐述显式定型、null 校验以及无 type 玩家信封的严格契约。

### 2.2 `ProtocolPublicModelsCodecTest.kt`
- **修复 line 248 `safe.board` 类型断言**：
  - 将 `val b = assertNotNull(safe.board)` 改为先执行 `assertNotNull(safe.board)` 断言非空，再以 `val b = safe.board!!` 进行强类型解包。
  - 完整保留 `safe.clocks`、`safe.settlementEvents` 以及后续 `b.scores`、`b.handTileCounts`、`b.riichiStatus`、`b.discards`、`b.melds` 等全部嵌套不可变断言。
- **修复 line 454 `result.publicEvents` 类型断言**：
  - 将 `assertImmutableList(assertNotNull(result.publicEvents))` 改为先执行 `assertNotNull(result.publicEvents)`，再对解包后的非空列表执行 `assertImmutableList(result.publicEvents!!)`。
  - 完整保留嵌套不可变验证与 `assertEquals(createPublicEvents(), result.publicEvents)` 断言。
- **未删除或跳过任何测试用例**。

---

## 3. 测试执行说明

- **明确声明**：依据迁移指令要求与角色分工边界，Anti 本次仅使用内置文件读写工具进行精确修改，**未在本地执行任何 Gradle 构建、编译命令、测试用例或 Shell 终端**。
- 所有编译验证与功能验收均交由 Codex 独立执行。

---

## 4. 未解决项 (Unresolved Items)

- **无**。R4B2WIREFINAL 任务指派的两个精准收尾问题已全部实现并落盘。

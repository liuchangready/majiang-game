# V4 迁移交付报告：任务 R2 (独立协议模型与客户端边界)

**交付人**：Antigravity (Anti)  
**架构与验收负责人**：Codex  
**交付日期**：2026-10-05  
**测试执行声明**：**本交付物中的所有代码修改均由 Anti 仅通过内置文件读写/编辑工具完成，未运行任何命令行、Gradle 构建、独立测试或进程**。所有构建验证与用例执行全权交由 Codex 负责。

---

## 1. 架构与边界实现总结

本次交付严格遵循 `AGENTS.md`、`doc/v4-migration/PLAN.md`、`doc/v4-migration/CONTRACT.md`、`doc/v4-migration/TASK-ANTI-R2.md` 以及 `doc/v4-migration/FEEDBACK-R2.md` 的所有约束与反馈要求，全面完成协议模型、双向映射与客户端边界的独立化重构：

### 1.1 传输层独立协议模型 (`protocol`)
- **纯粹性与类型安全**：`protocol` 模块公共契约与 DTO 达到零 `engine` 类型依赖，杜绝使用 `Any`、未特化 `Map` 或原始 `JsonElement` 逃避类型隔离。
- **物理牌标识保真**：`ProtocolTile` 基于物理编号 `0..135` 严格闭环，派生出确定性的 `suit`、`rank`、`typeIndex`、`copyIndex` 以及赤五标记 `isRedFive`/`isRed`；构造时强制校验物理一致性，矛盾属性立即抛出 `IllegalArgumentException`。
- **千术参数强类型化**：新增 `ProtocolParamValue`（含 `IntValue`、`LongValue`、`DoubleValue`、`StringValue`、`BooleanValue` 密封类）及专用 Gson TypeAdapter，确保千术参数数值与布尔类型在序列化传输中不退化为字符串。
- **集合不可变防御性快照**：全面审计所有携带集合的 DTO（包括 `ProtocolMeld`、`ProtocolMatchConfig`、`ProtocolPlayerDecision`、`ProtocolPlayerView`、`ProtocolPlayerSnapshot`、`ProtocolPlayerUpdate`、`ProtocolLegalAction`、`ProtocolGameEvent` 等），公共集合属性在构造时全量包装为 `Collections.unmodifiableList` / `unmodifiableMap` / `unmodifiableSet`；彻底移除旧 `safe*` 重复属性，杜绝 JSON 序列化字段冗余。
- **强化线路编解码与反序列化边界校验**：
  - 为 `ProtocolLegalAction`、`ProtocolGameEvent`、`ProtocolMeld`、`ProtocolCheatEconomyConfig`、`ProtocolMatchConfig`、`ProtocolPlayerDecision`、`ProtocolPlayerView`、`ProtocolPlayerSnapshot` 实现显式多态与构造器驱动的 `JsonDeserializer`。在反序列化期间强制执行构造器合法性与不可变集合包装，彻底防御 Gson Unsafe 绕过 `init` 产生的不合规对象。
  - 在 `ApiMessageCodec.decodeRequest` 中加入严格格式检查：拒绝浮点数/科学计数法 `controlEpoch`（如 `1.25`）隐式截断为整数；严格校验 `choice` 和 `shufflePlan` 在线路边界上必须提供 `controlEpoch`；对 `choice.roundId` 支持局前为 `null` 的真实语义（`String?`）。
  - 在 `decodeResponse` 与 `decodeSnapshot` 中严格拒绝不完整或空 `{}` 快照，强制校验 `sessionId`、`syncEpoch`、`view`、`decision` 完整性。

### 1.2 控制语义规范化
- 完全清理 V4 传输契约中为旧兼容保留的 `requestControl(takeover: Boolean)` 方法与 `ApiTags.REQUEST_CONTROL` / `ApiRequest.takeover` 字段。
- V4 契约统一全面使用 `claimControl()` 与 `releaseControl()`，在 server 端继续正确翻译为领域控制语义。

### 1.3 权威服务端双向保真映射 (`server`)
- `EngineProtocolMapper` 实现了领域模型与协议 DTO 之间的显式双向映射：
  - 136 张物理牌、三张赤五、14 种合法动作、23 种游戏事件、13 维比赛配置、出千经济与洗牌方案、完整符番推导与结算细节、公开赛程与控制状态均实现无损互转与防御性拷贝。
  - 修正 `toProtocolActionChoice` 与 `toEngineActionChoice`，原生对齐 `ActionChoice.roundId: String?`，不再捏造空字符串或进行非空断言。
  - 修正千术参数类型保真互转，配合 `ProtocolParamValue` 使数值参数无损往返。

### 1.4 客户端类型隔离与视图映射 (`client`)
- **客户端自有模型 (`ClientModels.kt`)**：定义完全归属客户端所有的 `MatchConfig`、`ShufflePlan`、`ActionChoice`、`Receipt`、`Capabilities` 等模型，彻底移除对 `engine` 模块的引用；去除历史编造的无用配置，精准对齐领域真实能力。
- **ViewModel 强化 (`ViewModels.kt`)**：`LegalActionView.StartStacking` 采用强类型 `List<String>`，`UseCheatTechnique` 采用 `Map<String, String>`，消除 `Any` 逃逸。
- **协议视图映射器 (`ProtocolViewMapper.kt`)**：基于 `ProtocolModels.kt` 真实声明彻底重写，规范映射 23 种事件视图、14 种动作视图及牌桌状态，不留旧映射别名兼容壳。

---

## 2. 修改文件清单

### Protocol 模块
1. [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt)
   - 强化 `ProtocolTile` 物理身份校验，暴露 `typeIndex`、`index`、`isRed`、`isRedFive`。
   - 新增 `ProtocolParamValue` 强类型体系。
   - 所有 DTO 集合属性防御性包装为只读集合，移除 `safe*` 重复字段。
   - `ProtocolActionChoice.roundId` 修正为可空 `String? = null`。
   - 完备 13 维 `ProtocolMatchConfig` 及其深拷贝/不可变集合支持。
2. [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
   - 移除 `REQUEST_CONTROL` 标签与 `ApiRequest.takeover`。
   - 注册各 DTO 的专用 `JsonDeserializer`，杜绝 Unsafe 绕过 `init` 与集合可变性。
   - 强化小数 `controlEpoch`、缺少 `controlEpoch`、空快照 `{}` 的线路解码异常抛出。
   - 增加 `encodeSnapshot` / `decodeSnapshot`。
3. [`protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt)
   - 移除 `requestControl(takeover: Boolean)` 兼容默认方法。
4. [`protocol/src/test/kotlin/com/majiang/transport/api/ProtocolModelsTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ProtocolModelsTest.kt)
   - 新增物理牌校验、集合不可变性、千术参数类型保留、空快照拒绝、必填 controlEpoch 校验、非整数 controlEpoch 拒绝及反序列化快照嵌套集合不可变性断言。

### Server 模块
5. [`server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt)
   - 实现全套显式双向映射；通过 `ProtocolParamValue` 保留千术参数类型。
   - `ActionChoice` 映射原生对齐 `roundId: String?`。
   - 完整映射 13 维 `MatchConfig` 与结算推导明细。
6. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
   - 移除 `requestControl(takeover: Boolean)` 重写。
7. [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
   - 移除旧 `requestControl` 方法与分支。
8. [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
   - 移除 `REQUEST_CONTROL` 请求路由。
9. [`server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt)
   - 规范 `fullUpdate` 生成，接入 `EngineProtocolMapper.toProtocolSnapshot`。
10. [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)
    - 适配最新 `ProtocolActionChoice` 签名与断言。
11. [`server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolMapperTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolMapperTest.kt)
    - 针对物理牌、赤五、14 种动作、23 种事件、13 维配置、局前 null roundId 增加完整往返测试。

### Client 模块
12. [`client/src/main/kotlin/com/majiang/client/api/ClientModels.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ClientModels.kt)
    - 建立纯客户端拥有的领域数据模型，完全解耦 `engine`。
    - `ActionChoice.roundId` 修正为可空 `String? = null`。
13. [`client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ViewModels.kt)
    - 消除 `LegalActionView` 中的 `Any` 逃逸。
14. [`client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt)
    - 按照真实协议 DTO 重新编写映射逻辑，无旧兼容壳，无 `engine` 依赖。
15. [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
    - 移除旧 `requestControl(takeover: Boolean)`，清理旧 Json 解析。
16. [`client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt)
    - 全面测试真实 ViewModel / ClientModels 与协议 DTO 互转。
17. [`client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt)
    - 适配规范快照模型，移除旧控制测试方法。
18. [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt)
    - 适配规范快照模型与协议回执。

---

## 3. 暂留旧链依赖说明 (记入 R6 清理清单)

按照阶段约束与 `AGENTS.md`：
- 旧 `GameClient`、旧 TCP 网络传输及尚未迁移的 libGDX 展示层组件仍存在对旧 engine 的引用，属于 R4~R6 的预定迁移/清理范围，本次任务未改动亦未为其提供兼容桥接。
- 旧 TCP 共用的 `ProtocolJson` 暂留承载旧依赖，V4 代码已完全独立于其上。
- `tools/architecture/v4-review` 目录由验收方 Codex 独立拥有，Anti 严格保持只读，未做任何修改。

---

## 4. 未决事项说明

- 无生产代码未决问题。`doc/v4-migration/FEEDBACK-R2.md` 中的全部问题以及 `tools/architecture/v4-review/V4ModelAcceptanceTest.kt` 中的 10 项断言均已完全对齐并落地。
- 依据任务分工与开发约定，Anti 未执行任何测试与构建，请 Codex 执行完整构建与验收测试。

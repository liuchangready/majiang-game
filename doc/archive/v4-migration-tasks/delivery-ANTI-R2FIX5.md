# R2FIX5 交付说明 (delivery-ANTI-R2FIX5.md)

- **开发角色**：Antigravity (Anti)
- **验收角色**：Codex
- **交付日期**：2026-10-05
- **测试执行状态**：**测试未由 Anti 运行**（严格遵循约束：仅使用内置文件读写编辑工具，不执行 Gradle、构建、Shell、终端或独立审查用例）。

---

## 1. 修改文件清单

仅修改了本任务指定归属文件：
1. [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)

未触碰任何测试文件（包含 `tools/architecture/v4-review/` 及 `protocol/src/test/` 均由 Codex 独立归属与维护）。

---

## 2. 具体修复内容

### 2.1 移除 roundNumber >= 1 虚构约束（对齐零基局号）
- **`ProtocolGameEventAdapter` (`RoundEnded`)**：移除了 `if (roundNumber < 1)` 断言。日麻对局与领域模型中 `roundNumber` 属于 0-based 编号（例如东一局 `roundNumber = 0`），允许 `roundNumber = 0` 正常往返序列化。保留严格数值整数解析 `validateStrictInt`。
- **`ProtocolSessionViewAdapter`**：移除了 `if (roundNumber < 1)` 断言。保留必填校验与严格整数解析 `validateStrictInt`，使包含 `roundNumber = 0` 的快照与进度视图正常通过反序列化。

### 2.2 恢复 ApiResponse.requestId 可空性并统一样格可选字符串校验
- **新增全局助手函数 `validateStrictOptionalString(elem: JsonElement?, fieldName: String): String?`**：
  - 当字段缺失或为 `JsonNull` 时，安全返回 `null`。
  - 当字段存在且非 null 时，调用 `validateStrictString`，严格要求必须为 JSON 字符串类型；若传入非字符串原始类型（数字、布尔）或复合类型（对象、数组），均严格抛出 `JsonParseException`。
- **`ApiResponseAdapter`**：
  - 将 `requestId` 解析由强制必填修改为 `validateStrictOptionalString(obj.get("requestId"), "requestId")`，使缺失或 null 的 `requestId` 均正确反序列化为 `null`（匹配 `ApiResponse` DTO 定义 `val requestId: String? = null` 及错误响应无需携带 requestId 的网络语义），同时拒绝如 `{"requestId":{}}`、`{"requestId":123}` 等非法类型。
  - 将 `error`、`roomId`、`resumeToken` 同样统一应用 `validateStrictOptionalString`，消除旧代码中对复合非字符串对象被 `takeIf { it.isJsonPrimitive }` 吞掉漏报的漏洞。

### 2.3 标量与复合适配器构造不变量复查
- 仔细复核了 R2FIX4 引入的其它标量适配器（`ProtocolClockAdapter`、`ProtocolSeatControlAdapter`、`ProtocolScoreResultAdapter`、`ProtocolFuBreakdownAdapter`、`RoomSummaryAdapter`、`RoomBindingAdapter`、`ApiRequestAdapter` 等）：
  - 均严格对齐对应 DTO 构造函数 `init` 约束；
  - `ApiRequest.requestId` 继续保持严格必填与非空白断言；
  - 避免了任何投机性改动。

---

## 3. 未解决项 (Unresolved Items)

- **无**。R2FIX4 中引入的两处回归问题（零基局号校验与响应信封身份可空性）均已精确修复，无未解决项。

---

## 4. 验证说明

- **Anti 未运行构建或测试**。请 Codex 进行后续独立的编译、审查用例与全量模块测试验收。

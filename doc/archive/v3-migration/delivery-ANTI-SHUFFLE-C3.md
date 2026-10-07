# 任务 ANTI-SHUFFLE-C3 交付报告：协议编译修复与配置边界对齐

- **任务编号**：ANTI-SHUFFLE-C3
- **交付日期**：2026-10-02
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **设计与验收**：Codex (Designer, Test Author, Reviewer & Builder)
- **遵循契约**：[`SHUFFLE-CONTRACT.md`](file:///D:/majiang/doc/v3-migration/SHUFFLE-CONTRACT.md)
- **前置交付**：[`delivery-ANTI-SHUFFLE-C2.md`](file:///D:/majiang/doc/v3-migration/delivery-ANTI-SHUFFLE-C2.md)

---

## 1. 变更清单与所有权边界

本次任务严格受限于 `client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt`，未修改任何其他生产代码、测试、Gradle 脚本或跨模块配置：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| [`client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt) | 修改 | 1. 补充缺失的 `import java.util.Collections`，修复 `ShuffleSelectionAdapter` 中 `Collections.unmodifiableSet` 编译报错；<br>2. 对齐 `ShuffleConfigAdapter` 的数值校验边界，使其与权威领域模型 [`ShuffleConfig`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt) 及 [`MatchConfig`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/match/MatchConfig.kt) 严格一致（`selectionMillis > 0`，`plannedDraws in 0..18`，`strength in 0.0..1.0` 且有限，`interference >= 0.0` 且有限），支持 `interference = 2.0` 等任意非负有限值的双向往返序列化。 |
| [`doc/v3-migration/delivery-ANTI-SHUFFLE-C3.md`](file:///D:/majiang/doc/v3-migration/delivery-ANTI-SHUFFLE-C3.md) | 新增 | 本交付报告 |

---

## 2. 修复细节说明

### 2.1 修复 `Collections` 未解析引用
在 `ShuffleSelectionAdapter.read` 中构建不可变防御性集合时调用了 `Collections.unmodifiableSet(LinkedHashSet(...))`，但头部遗漏了 `java.util.Collections` 的导入导致 root 编译失败。已在顶部 import 区域补全：
```kotlin
import java.io.StringReader
import java.util.Collections
```

### 2.2 对齐 `ShuffleConfig` 参数校验边界
对照 `engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt` 与 `MatchConfig.kt` 的规范：
- `selectionMillis`: 必须为正整型（`> 0L`）；
- `plannedDraws`: 必须在 `0..18`；
- `strength`: 必须为有限浮点数且在 `[0.0, 1.0]` 区间；
- `interference`: 冲突干扰衰减系数允许任意非负且有限的数值（`>= 0.0 && isFinite()`，例如 `2.0`），此前若限制在 `0..1` 会导致扩展衰减配置往返失败。现调整为：
```kotlin
val actualStrength = strength ?: defaultConfig.strength
if (actualStrength.isNaN() || !actualStrength.isFinite() || actualStrength !in 0.0..1.0) {
    throw JsonParseException("ShuffleConfig.strength must be in 0.0..1.0, got $actualStrength")
}
val actualInterference = interference ?: defaultConfig.interference
if (actualInterference.isNaN() || !actualInterference.isFinite() || actualInterference < 0.0) {
    throw JsonParseException("ShuffleConfig.interference must be non-negative and finite, got $actualInterference")
}
```

---

## 3. 交付状态

改动已全部就绪，无越界改动。由 Codex 执行全量构建、测试套件验证与验收集成。

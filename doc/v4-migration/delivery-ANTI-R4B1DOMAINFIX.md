# R4B1DOMAINFIX 交付报告 (delivery-ANTI-R4B1DOMAINFIX)

## 1. 任务概述与职责声明

- **任务标识**：R4B1DOMAINFIX（ApplicationCoordinator 领域原子性终验修正）
- **执行角色**：Antigravity (Anti) 生产开发者
- **验收角色**：Codex 独立测试与验收
- **测试执行声明**：**Anti 严格遵循指令约束，仅使用内置文件读写工具，未运行任何 shell、RunCommand、Gradle 构建或自动化测试。全部用例由 Codex 独立构建与运行验收。**

---

## 2. 修改文件清单

| 文件路径 | 修改性质 | 主要改动内容 |
|---|---|---|
| `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 生产代码 | 1. 将 `updateLobbyConfig` 访问修饰符调整为 `internal`（供模块内 `TableAuthority` 调度，避免向外暴露非权威入口）；<br>2. 在执行任何领域状态突变前，前置全量校验 `session.version <= 1L`、`round.version == 0L`、阶段状态以及 `newConfig.validateSupported()`，杜绝因非法配置导致 `session` 变更后在 `round` 校验抛错产生的半提交不一致。 |
| `engine/src/test/kotlin/com/majiang/authority/TableAuthorityTest.kt` | 模块测试 | 增强非法配置原子回归断言，显式断言在 `updateLobbyConfig`（包括通过 `authority` 与直接通过 `coordinator`）拦截非法配置时，`session.ruleConfig`、`session.betweenRoundScores` 以及单局 `round.state.scores` 均完全保持不变。 |

---

## 3. 问题闭环核对

1. **助手方法作用域受敛**：
   - `ApplicationCoordinator.updateLobbyConfig` 设为 `internal`，属于领域内部在桌级协调时由 `TableAuthority` 调用的受限事务方法。
2. **前置完整性校验与真正原子性**：
   - 在写锁与并发原子守卫内，先校验会话与单局版本有效性；
   - 在首次调用 `session.updateLobbyConfig` 之前调用 `newConfig.validateSupported()`，若配置不合法直接抛出 `IllegalArgumentException`，此时未发生任何写操作，零副作用回滚并释放锁。
3. **回归用例覆盖**：
   - 在 `TableAuthorityTest.kt` 中强化断言：非法配置抛出异常后，校验会话规则、分数账本与当前局手牌状态均未被污染。

---

## 4. 未解决项

- 无未解决项。本修正完全聚焦并闭环了 R4B1DOMAIN 领域的原子性边界。

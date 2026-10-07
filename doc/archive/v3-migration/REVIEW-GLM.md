# Claude Code GLM 接续验收记录

2026-10-01。生产开发使用用户现有 Claude Code 配置 `glm-5.2[1m]`，通过本地 Agent Mesh MCP Bridge 执行。开发代理限于约定生产文件；Codex 编写验收用例、运行 Gradle、检查实际图像与审阅交付。

## 任务边界

| 任务 | 范围 | 验收重点 |
|---|---|---|
| GLM-H / H2 / H3 | engine 宿主、计时、账本交接 | 串行裁决、20＋3、跨局资源、AI 不阻塞宿主 |
| GLM-P | 网络协议与 JSON 编解码 | 实体牌标识、完整番符及宝牌详情、严格协议版本 |
| GLM-T | TCP 客户端、服务端和房间 | 异步写入、超时、身份绑定、连接替换、锁顺序 |
| GLM-U | core 组合根、展示桥接和 Screen | 默认 V3 入口、主线程展示、待确认禁重复操作、释放宿主 |
| GLM-C | GameClient / sync | 关闭中的 Future、连续缺口恢复、迟到回调隔离 |

## 已复现问题与证据

1. 首次引擎回归：316 项，315 通过。下一局出千资源从 `[3,20,30,40]` 变成全零。原因是新局 `resetForNewRound()` 清空整场余额；H2 修复后，账本集成 4 项复测通过（`build/v3-migration/ledger-retest.log`）。
2. 协议编译：原先不存在的 `DecisionOption`、时钟字段、`NetworkMessages` 引用已交给 P/T；P 新实现还有泛型枚举 `T.entries` 编译错误，需真实编译验收。
3. 新增故意停顿 AI 的验收：局内 AI 不阻塞且到期默认弃牌通过；局间庄家选择在宿主锁内执行策略，超时断言失败（`ai-isolation-review.log`）。H3 要移除该同步调用。
4. 客户端补充验收：初次恢复前进后仍存在第二个缺口时继续恢复；关闭立即终结未完成 connect；迟到恢复不能倒退较新的实时状态。
5. 端点生命周期补充验收：同座位仅关闭一个端点不托管；最后端点关闭触发托管；重连需显式收回，且不重置当前截止时间。

## 清理记录

- 删除没有生产或测试引用的重复 `access/LocalSessionAccess.kt`（GLM-H 已清空实现）。实际本地访问使用 `access/local/LocalSession.kt`。
- 删除仅用于迁移前诊断的 `tools/architecture/v3-premigration-check.init.gradle`。模块已实际迁移，不再需要注入临时 sourceSets。
- 保留承载原规则和 UI 回归的旧同步测试入口；不能仅因接口旧或测试失败而删除。

尚在开发的任务不能凭交付报告认定通过。最终结果以全模块测试、桌面实际 GL 冒烟与 Android 构建为准。

## 12:26 检查点

GLM 所有剩余进程因 HTTP 429 / 错误 1308 结束，提示 2026-10-01 17:05:02 重置 5 小时用量。最新引擎全量 318 项，317 通过，1 失败；client 编译仍受 `T.entries` 阻塞。H3/P2/S 尚未执行，C 未写代码，U 仅修改展示适配器。精确续接顺序与未完成范围见 [STATUS.md](STATUS.md)。

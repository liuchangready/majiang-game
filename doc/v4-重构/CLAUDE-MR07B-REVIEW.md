# MR-07b 公开载荷编码复用 — 验收记录 (CLAUDE-MR07B-REVIEW.md)

- **验收方**: Claude Code 主会话（独立检视、代跑构建与测试）
- **开发方**: Anti（Antigravity，经 agent-mesh-bridge 派发，thread_20261018_104523_8753）
- **基线 SHA**: fe5e359（MR-07a 提交后）
- **需求依据**: doc/v4-重构/IMPROVEMENT-PLAN.md 第 4 节 MR-07 广播部分；简报 tools/architecture/v4-improvement/ANTI-MR07B-TASK.txt
- **交付说明**: doc/v4-重构/ANTI-MR07B-DELIVERY.md（含第 2 轮返修记录）
- **验收日期**: 2026-10-18
- **结论**: **通过**（:server:test 214 项全绿；新测试 3 连跑稳定；protocol 回归通过）

---

## 1. 交付范围核对

| 文件 | 类型 | 核对结果 |
|---|---|---|
| server/tcp/PublicUpdateFrameCache.kt | 新建 | 五元组键（sessionId/syncEpoch/cursor/fromCursor/isFullSnapshot，字段名经 ProtocolModels.kt 核对）；锁内恰好编码一次；FIFO 条目+字节双上限淘汰；releaseSession/clear；hit/miss/encode/cachedBytes 指标。首轮幻影参数 maxMaxBytes 已清理 |
| server/tcp/TcpTableServer.kt | 修改 | 观战订阅回调改走缓存（编码次数 N→1）；四路会话释放：host 离房 closed、巡检 closed 分支、onRoomRecycled、停服 clear；单一创建点（首轮重复创建已清理） |
| server/tcp/TcpServerConfig.kt | 修改 | publicFrameCacheMaxEntries=64 / publicFrameCacheMaxBytes=1MiB（拟定默认值 + init 校验） |
| server/host/ServerTableHost.kt | 修改 | 仅 internal val publicSessionId 只读透传（authority.sessionId，无新建生成逻辑）——最小改动符合简报授权 |
| 测试 | 新建 PublicUpdateFrameCacheTest(8)/PublicBroadcastEquivalenceTest(5)；追加 CapacityAndLifecycle 1 | 214 = MR-07a 基线 201 + 13；未删除/削弱任何既有测试 |

## 2. R07b-T1～T6 逐条验收结论

| 编号 | 验收项 | 结果 | 载荷 |
|---|---|---|---|
| T1 | 1/8/32 观战者收到逐字节一致的公开更新；断线观战者不影响他者 | ✅ | EquivalenceTest 1/8/32 + 断线隔离用例（assertArrayEquals 逐字节比较） |
| T2 | 同一条更新无论多少观战者 encodeCount 恰好 +1 | ✅ | 同上（encodeDelta==1 断言；确定性依据：shuffle 默认关闭、庄家为人类房主，开赛后对局停在庄家首决策，恰好一条 PUBLIC_UPDATE） |
| T3 | 键完整性：五元组任一不同不串缓存；全量与连续互不命中 | ✅ | CacheTest（含 isFullSnapshot/cursor/fromCursor 区分） |
| T4 | 双上限 FIFO 淘汰；淘汰后已返回字节不受影响 | ✅ | CacheTest（容量与字节边界 + 持有引用不变） |
| T5 | 会话释放：房间回收/停服后 entryCount/cachedBytes 归零 | ✅ | EquivalenceTest T5（LEAVE 解散即释放）+ 停服 clear + 巡检兜底 |
| T6 | 回归：既有 201 项零回归 | ✅ | :server:test 214 全绿；新测试 3 连跑稳定；:protocol:test 通过 |

## 3. 验证执行记录

1. `./gradlew :server:test` → **214 项全部通过**（0 失败 0 跳过）。
2. 新增两测试类 `--rerun-tasks` 连跑 3 遍 → 全部 BUILD SUCCESSFUL（时序稳定性确认）。
3. `./gradlew :protocol:test` → 通过（server-only 改动，无跨模块影响）。

## 4. 已知事项与后续跟进（不阻塞）

1. **缓存收益未做压测归档**：编码次数 N→1 已由 encodeCount 断言验证；CPU/时延收益未单独压测（MR-10 测量驱动优化范畴，如需再做）。
2. **RoomInfo（房间信息）帧未纳入共享编码**：按计划口径 07b 仅覆盖 PUBLIC_UPDATE；RoomInfo 编码复用如需可作后续小任务（收益小：内容小且合并语义下单帧单次）。
3. MR-07 整体（07a+07b）就此收官；IMPROVEMENT-PLAN 后续为 MR-08a～d（表现层/Codec 拆分）。

## 5. 过程记录

共 2 轮。首轮缺陷：集成测试 START_MATCH 前缺 SET_READY（5 用例同根因全挂）、缓存构造器幻影参数、TcpTableServer 重复创建。逐字级返修一轮收敛。较 MR-07a（9 轮 6 返修）交付质量显著提升——简报陷阱清单前置（字段名核对、包路径范式）与返修逐字化指令有效。

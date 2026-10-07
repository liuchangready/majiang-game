# 洗牌千术：并行开发与验收

验收时间：2026-10-02。Codex 负责契约、测试、构建和检视；Antigravity 经已安装的 Agent Mesh MCP Bridge 负责生产代码。未提交工作区改动。

## 并行任务

| 任务 | 独占范围 | 交付内容 |
|---|---|---|
| [A](TASK-ANTI-SHUFFLE-A.md) | engine/stacking | 六类目标、重叠矩阵、多目标混合、物理牌山生成 |
| [B](TASK-ANTI-SHUFFLE-B.md) | engine 其余生产代码 | 独立准备期、请求校验、私有方案、单局记录、原子发牌扣点、跨局结转 |
| [C](TASK-ANTI-SHUFFLE-C.md) | client、server | 异步请求、严格协议、席位绑定、Local/Remote/Mall 支持 |
| [D](TASK-ANTI-SHUFFLE-D.md) | core 生产代码 | 中文选择面板、预算预览、独立倒计时、锁定与恢复、默认产品接线 |

四组首轮同时执行；返修按独占文件继续交给 Anti。所有开发进程已退出。共享规范见 [SHUFFLE-CONTRACT.md](SHUFFLE-CONTRACT.md)。开发交付报告不是测试执行证据，以本验收记录和 Gradle 日志为准。

## 已实现行为

- 每次发牌前固定 10 秒，包含下一局和连庄；全员提前确认不提前发牌，不占用普通 `3+20` 时间。
- 偷天换日 40 点影响起手牌；瞒天过海 20 点影响默认前 8 次正常顺位预计摸牌。吃碰杠改变实际取牌顺位，开局后不会重新调整牌山。
- 每种千术可多选目标：清一色万/条/筒、大三元、国士无双、大四喜。组合表示混合倾向，不保证同时完成所选牌型。
- 东风战整场每人 50 点、半庄 100 点；每局和连庄不恢复。确认先预留，成功发牌才扣费；跳过或未确认超时不花费。明确配置的自定义预算保留。
- 准备阶段无手牌、宝牌或正常操作候选；对手看不到选择和预留。既有公开点数协议保留，使用后的余额变化不是秘密。
- 保留 136 张唯一物理牌及红五身份；不修改王牌区。牌山种子由宿主私有随机源产生，失败重试保留同一种子。
- 重复提交幂等、旧控制代次拒绝、重连保留方案和截止时间、暂停冻结并平移时间。客户端关闭会结束挂起的确认请求。
- 默认 AI 不花费；显式允许时原生电脑席位按预算选择千术，接管真人的 AI 不会擅自消费真人点数。
- 默认桌面/Android 产品入口已开启。旧兼容入口和未显式开启的 MatchConfig 保持原发牌方式。

## 执行结果

| 模块 | 测试数 | 失败 / 错误 / 跳过 |
|---|---:|---:|
| engine | 332 | 0 / 0 / 0 |
| client | 34 | 0 / 0 / 0 |
| core | 169 | 0 / 0 / 0 |
| server | 4 | 0 / 0 / 0 |
| 合计 | **539** | **0 / 0 / 0** |

保留原 511 项测试，新增 28 项。数量来自各模块 `build/test-results/test/TEST-*.xml`。最终联跑包含桌面 classes 和 Android assembleDebug，日志：`build/v3-migration/shuffle-final-validation.log`。

新增测试覆盖：六目标两阶段的固定种子趋势、同类争牌衰减、槽位映射、牌张守恒、王牌不变、重现性、固定截止与暂停、私有预留、幂等和拒绝、跨局预算、AI 消费边界、非法牌山与不足余额零提交、扣费流水保留、严格网络类型、真实 TCP 重连、中文字符覆盖、UI 预算/草稿/跳过/双击/超时/快照时钟。

真实 OpenGL 冒烟通过：默认入口启动 → 中文准备面板 → 选择国士无双 → 确认并显示 60 点可用余额 → 等待实际 10 秒 → 发牌并扣 40 点 → 玩家弃牌 → AI 响应 → 返回菜单释放宿主。最终日志：`build/v3-migration/shuffle-render-final.log`，标记 `V3_DEFAULT_ASYNC_CLIENT_RENDER_PASS`。截图已检视：`v3-shuffle-selection.png`、`v3-shuffle-confirmed.png`，以及发牌和弃牌截图（均在 `build/v3-migration/`）。

验收中修正了账本被发牌重置清空、确认后余额二次预减、暂停/到期仍可提交、跳过保留旧草稿、迟到回执污染、关闭时 Future 竞态、公开局号推导种子、字体漏字及跨模块编译问题。跨局测试最初失败另有夹具原因：四名真人需要全部 ready；已修正夹具，未放松产品的全员准备规则。旧连接失效允许明确拒绝回执或传输异常，两种均被测试接受。

## 运行与边界

```powershell
.\gradlew.bat :engine:test :client:test :core:test :server:test :lwjgl3:classes :android:assembleDebug --no-parallel --max-workers=2 --console=plain
.\gradlew.bat -I tools/architecture/v3-smoke.init.gradle :lwjgl3:v3Smoke --no-parallel --max-workers=2 --console=plain
.\gradlew.bat :lwjgl3:run
```

本次只开放洗牌千术，摸打阶段千术未实现。算法效果是统计倾向，当前强度与默认 8 次摸牌范围仍可后续进行游戏平衡调整。Android 已通过构建，未做真机测试；网络验证使用本机真实 TCP，不代表公网部署验收。生成失败的零提交验证覆盖协调器发牌接缝；宿主重试节流与种子保留另经代码检视。

中文字体采用 Noto Sans SC，来源及 SIL OFL 授权随 `assets/fonts/` 分发。

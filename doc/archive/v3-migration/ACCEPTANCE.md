# V3 迁移验收记录

由 Codex 维护；Anti 的交付报告不等于验收通过。2026-10-01 17:07（Asia/Shanghai）：ANTI-U4 完成后，最终代码实测通过。

## 基线与迁移

- Git 基线 f524d60；原 436 项测试与桌面 classes 通过（build/v3-baseline.log）。
- Anti 编写迁移脚本，Codex 审查、dry-run 后执行 Apply；137 个文件移动，扫描的 187 个文件 SHA-256 校验通过，50 个文件保留 core。清单：build/v3-migration/migration-manifest.json。
- 原 71 个测试源文件均存在于新结构，未通过删除失败用例规避回归。
- 无调用的空 LocalSessionAccess 和迁移前临时构建脚本已清理。旧显式入口仍支撑有效兼容与回归测试，未盲目删除。

## 自动化验收范围

| 测试组 | 验收约束 |
|---|---|
| 既有规则与展示 | 136 张守恒、和碰杠吃优先级、振听、立直选择/暗杠、副露展示、完整番符宝牌与支付明细 |
| TimeControlAcceptanceTest | 每席位 20+3、截止边界、并行响应窗口、同决策不重置、暂停、局/场储备 |
| MatchEconomyAcceptanceTest | 短赛、风场推进、击飞、同分排名、独立资源账本幂等与隔离 |
| TableHostAcceptanceTest / TableLifecycleAcceptanceTest | 发牌、席位授权、超时默认、幂等、旧局访问隔离、新局实例、动画不阻塞时钟 |
| CheatLedgerIntegrationAcceptanceTest | 资源跨局交接、跨局幂等、失败原子性、牌张与分数不被资源操作改变 |
| HostRhythmAcceptanceTest / HostAiIsolationAcceptanceTest | 立直 150ms 单步摸切、自摸/暗杠保留选择、托管不赠送时间、慢 AI 不阻塞宿主 |
| ClientSynchronizationAcceptanceTest / AsyncClientAcceptanceTest | 序号缺口、旧代次、事件去重、请求门控、关闭中断、同步异常、连续缺口恢复 |
| Codec / Transport / NetworkAcceptanceTest | 协议版本与完整分数明细、真实 TCP、私有牌过滤、连接替换、掉线恢复、超时/畸形帧终结 Future |
| RecoveryProjectionAcceptanceTest | 公开历史/排名/资源、终局快照、托管态、暂停计时、新局清理、数组隔离 |
| AsyncScreenAcceptanceTest / ScreenRecoveryAcceptanceTest | 非阻塞输入、重复点击门控、暂停禁用、过期时钟归零、结算重连及跨结算替换、同结算保留标签页 |
| V3RenderSmoke | 真实 GL 默认入口、发牌、点击弃牌、AI 响应、退出释放宿主、截图检视 |

## 最终运行命令

```powershell
.\gradlew.bat :engine:test :client:test :core:test :server:test :lwjgl3:classes :android:assembleDebug :server:installDist --console=plain
.\gradlew.bat -I tools/architecture/v3-smoke.init.gradle :lwjgl3:v3Smoke --console=plain
```

## 实测结果

| 模块 | 测试数 | 失败 / 错误 / 跳过 |
|---|---:|---|
| engine | 318 | 0 / 0 / 0 |
| client | 30 | 0 / 0 / 0 |
| core | 154 | 0 / 0 / 0 |
| server | 3 | 0 / 0 / 0 |
| 合计 | **505** | **0 / 0 / 0** |

原基线 436 项，当前增加 69 项。计数来自各模块 build/test-results/test/TEST-*.xml；最终联跑保留 Gradle 对未变化模块的正常增量检查，core 对 U4 修订重新执行完整 154 项，没有测试排除配置。

- 最终测试、桌面 classes、Android assembleDebug、server installDist 联跑成功：build/v3-migration/anti-final-u4-validation.log。
- 最终真实 GL 检查成功，输出 V3_DEFAULT_ASYNC_CLIENT_RENDER_PASS：build/v3-migration/anti-render-smoke-final.log。v3-dealt.png 和 v3-after-discard.png 已检视，确认牌面、时钟、控制状态与弃牌显示。
- Android 产物：android/build/outputs/apk/debug/android-debug.apk；服务器分发：server/build/install/server。服务器启动脚本 --help 检查成功，日志 anti-server-launcher.log。
- 本轮返修覆盖局间 AI 阻塞、异步关闭/异常/恢复、网络编译与连接生命周期、托管及终局投影、结算恢复、立直/副露交互兼容；没有通过删测试或放松断言掩盖失败。

Android 仅验证构建，未进行真机或模拟器测试。网络用本机真实 TCP 回环验证；没有把它表述为公网部署验收。详细能力边界见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 2026-10-02 时间显示调整

ANTI-CLOCK 将手牌左侧状态框的时间改为大字号 `3+20` 格式，免费时间耗尽后减少共用储备，暂停冻结、紧急时间标红。Codex 审查后运行 core 全量 154 项测试，全部通过；真实 GL 冒烟通过并检视截图，活动倒计时 `2+20` 清晰可见且未遮挡手牌。证据：build/v3-migration/anti-clock-validation.log、v3-dealt.png。本次未改动计时规则，未重新构建 Android APK。

## 2026-10-02 四家副露展示

设计见 [四家副露展示契约](TASK-ANTI-OPPONENT-MELDS.md)。根因是公开视图已有四家的副露，而原布局和 Screen 绘制仅读取本人。Anti 完成布局/渲染及观察者相对位置接线；首次调用超时后由 FINISH 任务补齐遗漏方法并调整顶部标签间距，Codex 检视和实测通过。

- 本人底部保留；下家右侧、对家上方、上家左侧，牌面朝向用户阅读。姓名、分数、牌河及弃牌动画采用同一观察者相对位置。
- 保留公开牌物理身份、鸣牌来源与赤牌皮肤；暗杠两端背面、加杠补入牌标记，换局清空，恢复快照直接重建。未读取他家暗牌或修改规则。
- core 全量 **160 项，0 失败、0 错误**；新增 6 项涵盖任意观察者、四组容量、真实其他座位吃碰各杠、皮肤指令、非交互/替换/清空、V3 初始快照恢复。
- 真实 GL 三场景通过：Wikipedia 皮肤 viewer=0 / viewer=2，Classic 皮肤 viewer=0，已检视 opponent-melds-0.png / 1.png / 2.png。容量截图刻意为四家各四组的展示夹具，不代表合法十六杠牌局；合法性验证来自真实引擎提交测试。
- 日志：build/v3-migration/anti-opponent-melds-final.log，包含 OPPONENT_MELDS_RENDER_PASS。运行命令：`.\gradlew.bat :core:test -I tools/architecture/meld-smoke.init.gradle :lwjgl3:meldSmoke --console=plain`。
- 本次未改动 engine/client/server，未重建 Android APK，未自动关闭正在运行的用户牌局。

## 2026-10-02 洗牌阶段千术

四组 Anti 并行开发和返修全部退出，Codex 完成独立测试与检视。新增 28 项，最终 engine 332 / client 34 / core 169 / server 4，共 **539 项，0 失败、0 错误、0 跳过**；桌面 classes、Android 调试 APK 和最终真实 GL 流程均通过。详细功能、测试覆盖、修正记录与限制见 [SHUFFLE-ACCEPTANCE.md](SHUFFLE-ACCEPTANCE.md)。证据：`build/v3-migration/shuffle-final-validation.log`、`shuffle-render-final.log`。未通过删测试规避问题；测试夹具按已有四真人全员准备和网络明确拒绝语义进行了修正。

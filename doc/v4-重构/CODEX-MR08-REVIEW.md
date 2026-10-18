# MR-08 独立检视与验收

日期：2026-10-18  
分工：Anti 开发；Codex 独立检视、测试与验收。  
基线：`feat/mj-tcp-migration` 的 `5d219c2`；验收完成后由用户要求纳入本地提交。

## 结论

**MR-08a–d 通过本轮代码与自动化验收。** 原有模块边界、请求入口及 Codec 外部 `ApiMessageCodec` 入口保持不变。Android 仅完成 APK 构建，未进行真机或模拟器实测。

## 实施范围

| 子任务 | 落点 | 检视结果 |
|---|---|---|
| 08a | `AuthorityCountdownTracker`、`DecisionIdentityGuard`；袖里藏花与偷梁换柱模型接入 | 计时采样、决策身份、断线流逝与候选失效测试通过；落汗与码牌阶段经评估保留各自语义 |
| 08b | `DiscardAnimationController`、`ModalPriorityCoordinator`；`GameScreen` 委托 | 动画队列由控制器持有；ESC/BACK 仍调用各业务保留、放弃或取消操作；原屏幕契约与输入门控测试通过 |
| 08c | 房间、公开流、玩家快照、千术 Adapter 分文件 | 73 个 Adapter 类体与 HEAD 逐项比对，除可见性和空白外一致；严格解析回归通过 |
| 08d | `UiAssets` 集中持有 17px/18px 中文字体；结算与码牌面板借用 | 字体释放归根资产；绘制恢复颜色、缩放；清理四处不参与生成的静态字表；真实 GL 截图中文可见，原布局坐标未改变 |

## 返修记录

1. 08a 新增测试使用过期 DTO 参数，首次 `presentation:test` 编译失败；Anti 修正后通过。
2. 08b 提取 `QueuedDiscard` 后破坏现有测试对 `GameScreen.QueuedDiscard` 的引用；Anti 补兼容层后通过。
3. 08c 首次搬迁漏掉 `ProtocolPublicUpdateAdapter.isFullSnapshot` 的编解码与游标校验，`protocol:test` 有 2 项失败；Anti 对照 HEAD 恢复，并复核其他 Adapter。最终 166 项协议测试通过。Codex 另用脚本比较了全部 73 个 Adapter 类体，未发现剩余差异。
4. 08d 首次编译缺少 `UiAssets` 导入；Anti 修正，并为共享字体释放加入幂等与引用身份检查。
5. 结算 GL 夹具 `V4SmokeSession.java` 少一个新增快照参数，Codex 仅补齐该夹具参数后完成实测；生产 DTO 未变。
6. 08d 首次交付遗漏三个仅供旧无头测试引用的绝技字表。Anti 清理字表与过时断言，保留并加强错误码中文翻译测试；全量测试和 Android 构建再次通过。

## 独立验证

- `./gradlew test --console=plain`：通过。XML 汇总 166 个测试套件、1,337 项用例，失败、错误、跳过均为 0。
- `./gradlew :android:assembleDebug --console=plain`：通过。
- `./gradlew -I tools/architecture/desktop-smoke.init.gradle :lwjgl3:architectureRenderSmoke --console=plain`：通过，368 帧。
- `./gradlew -I tools/architecture/settlement-detail-smoke.init.gradle :lwjgl3:settlementDetailSmoke --console=plain`：通过，25 张截图。
- 落汗、袖里藏花、偷梁换柱、弃牌输入门控的既有真实 GL 冒烟任务：通过。
- 抽查 `build/architecture-v2/desktop-shuffle-selection.png` 与 `build/settlement-detail/real-yaku.png`：中文字符正常显示，未见缺字方框。
- `git diff --check`：通过。

## 范围边界

本轮是表现层与 Codec 的局部拆分验收。未做 Android 真机/模拟器交互测试；该构建结果不能替代设备实测。未改动麻将规则、136 张物理守恒、TCP 权威传输与连接控制语义。

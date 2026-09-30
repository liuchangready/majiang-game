# 逐座位控制者与统一动作执行：验收记录

日期：2026-09-30。Codex 设计、编写测试、执行构建与检视；Antigravity 通过 Agent Mesh MCP bridge 开发。

## 交付结果

已完成引擎与本地会话改造，默认单机仍是一名本地真人与三名 AI。通过领域配置可指定四个座位分别为 LocalHuman、RemoteHuman、Computer 或 Takeover；本地/远端真人均等待自己的输入，并有独立立直自动摸切偏好。

玩家端点绑定座位并检查控制权。真人动作、AI 动作和自动摸切共用内部规则校验与执行；自动动作由同一命令守卫内的顺序循环推进，移除了原有递归 AI 执行分支。所有有资格的真人响应收齐前不会裁决，原有荣和优先和头跳规则保持有效。

错误、空返回或抛普通异常的 AI 决策只尝试一次策略，再选择合法过牌/摸切/弃牌兜底；不会冒充其他座位，也不会默认选择立直、和牌或杠。策略收到深拷贝且嵌套集合只读的视图与合法动作。守恒审计异常不作为策略异常吞掉。

控制者切换保持牌局状态及已记录响应；真人不能在 AI/托管持有操作权时提交遗留动作。显式配置及策略不再被 LocalGameSession.start 的默认初始化覆盖。

## 分工与检视

- E：SeatController、GameStateMachine、ResponseWindow。
- S：LocalGameSession，与 E 并行开发。
- F：根据 Codex 验收失败结果修复配置写入互斥、AI 嵌套动作数据隔离，并补齐 setSeatController 的显式配置标记。

首批新增 25 项测试有 23 项通过、2 项失败。失败分别复现初始化配置可在命令处理中被修改，以及 AI 可修改响应候选中的嵌套牌列表。之后补充开局前配置保留和响应牌序规范化用例，共新增 27 项。返修后全部通过。

代码检视确认：只有一个普通动作候选事务执行调用点；各类控制者使用共用校验；响应记录先验证候选并规范化，再更新振听；配置和玩家命令均使用原子守卫。旧 handleAction 保留为可信系统/模拟入口，LocalGameSession 玩家提交已迁移到绑定座位的 submitPlayerAction。

## 验证结果

执行：

```powershell
$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
.\gradlew.bat -I tools/architecture/discard-input-gate-smoke.init.gradle :lwjgl3:discardInputGateSmoke --console=plain
```

- JVM：401 项测试，0 失败、0 错误、0 跳过（原有 374 项 + 新增 27 项）。
- 桌面 classes 与 Android debug APK：构建成功。
- 真实 LWJGL/OpenGL 出牌门禁回归：`DISCARD_INPUT_GATE_RENDER_PASS screenshots=4 discardsWaited=4 submissions=2`。该图形用例使用可控会话快照验证动画和点击门禁；引擎多控制者行为由新增领域及会话测试验证。
- 已检查等待中的图形截图。Android 未做真机操作测试。

新增用例涵盖四真人逐步完成一局、远端真人不被自动代打、独立立直偏好和自摸/暗杠暂停、控制配置隔离、托管切换与已响应保留、错座位及非法动作拒绝、异常 AI 兜底、AI 荣和等待真人与头跳、加杠抢杠流程、嵌套集合隔离、并发与监听器重入、本地会话初始化兼容。

## 文件与证据

- 设计：`doc/seat-controllers/DESIGN.md`
- 开发交付：`doc/seat-controllers/delivery-E.md`、`delivery-S.md`、`delivery-F.md`
- 验收测试：`core/src/test/kotlin/com/majiang/state/SeatControllerAcceptanceTest.kt`、`UnifiedActionAcceptanceTest.kt`；`core/src/test/kotlin/com/majiang/session/SeatControllerSessionAcceptanceTest.kt`
- 首轮新增测试：`build/seat-controllers/acceptance-first.log`
- 返修前全量回归：`build/seat-controllers/validation-first.log`
- 最终全量构建及测试：`build/seat-controllers/validation.log`
- 最终图形验证：`build/seat-controllers/discard-gate-smoke-final.log`
- 图形截图：`build/discard-input-gate/`
- APK：`android/build/outputs/apk/debug/android-debug.apk`

## 边界

RemoteHuman 是已实现的领域控制类型；本次没有实现网络传输、共享房间广播、身份认证、断线重连协议、多方下一局准备或配置菜单。setSeatController 是供可信对局管理代码使用的切换接口，不是已经上线的网络掉线检测。AI 局末终了策略、千术和红五鸣牌组合不属于本次改造。

结论：本次逐座位控制者与统一动作执行改造验收通过。

# 落汗千术验收

2026-10-11 23:57，Codex 独立验收通过。生产代码由 Anti 经 agy 开发，调用包含 `--dangerously-skip-permissions`；测试及 GL 夹具由 Codex 编写和维护。最终回调为 [P1FIX2](CALLBACK-ANTI-P1FIX2.json)，会话 `2f66a631-d39d-4e47-927d-d1815957d1b8`。此前发现的问题已退回修正，完整记录见 [领域](REVIEW-ENGINE.md)、[接口](REVIEW-API.md)、[表现层](REVIEW-PRESENTATION.md)。

## 已交付行为

- 本人弃牌决策期间，选择自己手中的4张不同实体牌，每次成功消耗10额度；每人最多12张有效标记。
- 每组当局立即生效，另覆盖后续3个实际发牌局，包括连庄；各组独立到期。取消、拒绝、重试不重复扣费，施术不延长原决策截止时间。
- 私有投影跨局、断线重连保留；对手和观战者不收到施术者的私有追踪信息。暗藏不可见区域不暴露位置。
- 牌河使用当前 `roundId/seat/discardIndex/tileId` 定位；界面显示相对座位和从1开始的牌河序号。转入副露、换局或到期后清除旧位置效果。
- 对手已识别牌用独立小牌面显示，不暴露真实手牌槽位；自身手牌、牌河和授权副露显示汗滴。三组追踪均可点击翻页查看。
- 单机和新建房入口显式开启落汗；领域、协议、客户端模型默认仍关闭。保持V4模块边界、136张物理守恒及原有洗牌费用。

## Codex 最终独立验证

```
./gradlew.bat test --rerun-tasks :lwjgl3:classes :android:assembleDebug --console=plain
```

退出码0，36秒，**86个任务实际执行**，未以缓存结果代替最终测试。检查XML结果：

| 模块 | 套件 | 测试 | 失败/错误/跳过 |
|---|---:|---:|---|
| engine | 53 | 377 | 0/0/0 |
| protocol | 16 | 151 | 0/0/0 |
| client | 7 | 132 | 0/0/0 |
| server | 15 | 136 | 0/0/0 |
| presentation | 42 | 204 | 0/0/0 |
| 合计 | 133 | **1000** | **0/0/0** |

相较原928项新增72项，包含37项领域、11项协议、5项服务端、8项客户端及真实UDP贯通、11项表现层。实际代码修复覆盖跨座位请求ID冲突、关闭回调死锁、缺失牌河局标识、过期选择恢复及同步代次时钟等回归点。测试指纹与已冻结版本核对一致，验证前后生产源码指纹无变化。

## 真实 GL 与显示复核

```
./gradlew.bat :lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle --console=plain
./gradlew.bat :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --console=plain
./gradlew.bat :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --console=plain
```

全部退出码0。真实产品链输出 `TILE_MARK_GL_PASS frames=367 groups=3 fee=30 riverIndex=0`，验证菜单建房、真实选牌点击、双击防重、总费用30、12张上限、原截止时间、实际标记弃牌及返回菜单释放资源。两个尺寸的可视夹具均输出 `TILE_MARK_VISIBILITY_GL_PASS scenarios=7`，覆盖三个对手各12张、实际翻三组、四家牌河第7张、公开副露、对家识别牌与碰并存、私有标记清除。

Codex 实际查看最终截图，确认小牌面、翻页、汗滴、混合副露及菜单文案无上述越界/重叠问题。截图与完整日志是本地构建产物：

- `build/tile-mark/screenshots/`、`build/tile-mark/screenshots-scaled/`
- `build/tile-mark/verification-final-all.log`
- `build/tile-mark/verification-final-product-gl.log`
- `build/tile-mark/verification-final-visibility-gl.log`
- `build/tile-mark/verification-final-scaled-gl.log`

Android 已构建调试APK，**未进行Android真机触摸验收**。本次覆盖进程内与真实本机UDP链路，不宣称已具备公网账户、撮合、持久化恢复或加密信道。

用户原有 `lwjgl3/build.gradle`、`SnapshotLauncher.kt` 与 `snapshot/` 改动保留，不纳入本次提交。

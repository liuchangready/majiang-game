# 打牌动画：幽灵手取牌入弃牌区

Claude 设计与检视；Antigravity 开发。遵循 [architecture-v2/DESIGN.md](../architecture-v2/DESIGN.md) 表现层契约与既有纯函数+TileDrawBuffer 模式。

## 目标

每次打牌（人类点击或 AI 出牌）播放一段 **≈0.82s 的"幽灵白手套取牌入河"动画**；逻辑层零改动、零等待，动画纯表现层叠加。

## 分镜与时间轴

| 阶段 | 时长 | 表现 |
|:---|:---|:---|
| ① REACH | 0.12s | 幽灵手从打牌者座位对应的屏幕边缘滑入，伸向手牌区，alpha 0→0.75 淡入 |
| ② GRAB | 0.08s | 手到位"捏住"牌：牌 scale 1.0→1.1、上浮 6px、倾斜 ±8°（正负按座位方向） |
| ③ CARRY | 0.35s | 手+牌沿二次贝塞尔弧线飞向牌河目标格；ease-in-out；牌保持提起姿态 |
| ④ PLACE | 0.12s | 落格：squash（scaleY 0.92→1.0 回弹）；牌下阴影（半透明黑矩形，随飞行高度缩放，落地淡出）；手松开 |
| ⑤ RETRACT | 0.15s | 手缩回屏幕边缘，alpha 0.75→0 淡出 |

- **四座位出手起点**：P0 底部手牌区中央 / P1 右侧 / P2 顶部 / P3 左侧；弧线控制点按「起点-终点距离的 35% 向座位外侧偏移 + 上抛 40~90px」计算，飞行距离越远弧越高。
- **落点**：该牌在牌河中的最终格坐标（按既有牌河布局公式推算）。

## 幽灵白手套（程序化，零外部素材）

- 新建 `render/GhostHandTexture.kt`：用 **Pixmap 程序化绘制**一次性生成手部纹理并缓存（懒加载 + dispose）：
  - 掌部：圆角椭圆（约 64×80 px 区域），白色 alpha ≈ 0.75
  - 捏取双指：拇指 + 食指两条 capsule（Pixmap.fillCircle × N + fillRectangle 拼合），指尖朝牌
  - 柔边：边缘用低 alpha 逐层描边制造虚化轮廓
- 姿态固定（不做逐帧手势形变），动态感全部由位置/旋转/缩放/alpha 表达。
- 风格定位：半透明幽灵手套与"千术/仙气"题材契合，规避写实素材的风格与授权成本。

## 架构落点

### 纯逻辑（headless 可测）
`render/DiscardAnimation.kt`（固定 API）：

```kotlin
enum class DiscardPhase { REACH, GRAB, CARRY, PLACE, RETRACT, DONE }

data class DiscardPose(
    val x: Float, val y: Float,
    val scaleX: Float, val scaleY: Float,
    val tiltDeg: Float,          // 纹理皮肤旋转用；经典皮肤恒 0
    val handAlpha: Float,
    val phase: DiscardPhase
)

object DiscardAnimation {
    const val TOTAL_DURATION = 0.82f
    const val REACH = 0.12f; const val GRAB = 0.08f; const val CARRY = 0.35f
    const val PLACE = 0.12f; const val RETRACT = 0.15f

    fun bezier(t: Float, sx: Float, sy: Float, cx: Float, cy: Float,
               ex: Float, ey: Float): Pair<Float, Float>   // 二次贝塞尔
    fun easeInOutCubic(t: Float): Float                    // [0,1] 单调
    fun controlPoint(start: Pair<Float,Float>, end: Pair<Float,Float>,
                     seat: Int): Pair<Float,Float>          // 弧线控制点
    fun poseAt(elapsed: Float, start: Pair<Float,Float>,
               end: Pair<Float,Float>, seat: Int): DiscardPose
}
```

### 渲染集成
1. **触发**：对比上一帧 `PlayerView.discards` 与当前帧，新增末位牌即入队（开发时按 GameScreen 现有数据流选最小侵入方式，交付说明写明）。
2. **单槽队列**：同一时刻只播一个动画；后续打牌排队，先进先出；逻辑照常推进不等待。
3. **视图调和（关键）**：牌飞行期间，该玩家牌河**扣下这张牌不画**（按 Tile 身份匹配末位），改画于 `poseAt` 插值位置；DONE 瞬间切换为牌河常驻绘制。
4. **TileDrawBuffer 扩展**（保持单批次原则）：
   - `TextureOp` 增加可选 `rotationDeg`、`originX/Y` 与 `tint`（默认无旋转白色）——`SpriteBatch.draw` 原生支持旋转，不增加 flush；
   - 手部纹理与飞行牌走 overlay 时序之后的**新 pass 或 overlay 桶**（手压在牌之上）：实现时择简，交付说明论证不破坏「SpriteBatch 每帧恰好一次」。
   - 经典皮肤飞行牌不旋转（ShapeRenderer 限制，已知取舍）。
5. **边界情况**：
   - 飞行中该牌被吃/碰/杠取走 → 动画**快进到 DONE**，随后由现有副露展示逻辑接管；
   - 流局/和牌结算弹窗出现 → 未完成动画快进收尾；
   - 理牌开启：被抓走的牌离开手序列即重排（无状态排序天然支持）。

## 测试要求（Claude 检视基线）

1. `bezier`：t=0/1 端点精确、t=0.5 落在控制点相关中点、对 t 单调；
2. `easeInOutCubic`：值域 [0,1]、单调、端点精确；
3. `poseAt`：阶段切分时长精确、`elapsed ≥ TOTAL` 判 DONE、tilt/scale 值域（|tilt|≤8°，scale∈[0.9,1.15]）、handAlpha 渐变方向（REACH 升/RETRACT 降）；
4. `controlPoint`：四座位偏移方向正确（远离桌面中心）；
5. 视图调和纯函数：飞行牌身份匹配扣牌判定；
6. 全量回归：现有全部测试保持通过。

## 范围外

- 音效（无音频基建）
- 吃/碰/杠取牌的独立飞牌动画（本轮只做快进衔接）
- 摸牌动画（后续迭代）

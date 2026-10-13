# BS-E3 第一轮检视：需修复后再次回调

Codex于2026-10-13检视。当前全量1225项测试通过，但源码和已生成真实GL截图仍确认以下缺陷；不能据单元测试通过宣告最终验收。

1. **默认计时来源错误**：BeamSwapChoiceModel仍使用System.currentTimeMillis作为monotonicClock。改为System.nanoTime()/1_000_000，与SleeveBloomChoiceModel保持相同单调计时来源，防止系统校时延长本地可提交窗口。
2. **普通候选序号错误**：BeamSwapPanel用WallSlot绝对index+1显示标签，`build/beam-swap/screenshots-1280/offer-0.png`出现下58…下65等。按offer候选顺序显示下1…下8，实际请求继续使用原绝对slot。
3. **汗标被覆盖**：先font.draw汗，后tileDrawBuffer.flush，牌面遮掉汗标。`build/beam-swap/layout-screenshots-1280/maximum-selected.png`的标记中牌没有清晰汗标。先画牌体，再画汗滴/汗标和选中描边，保持半透明牌面和盲牌隐私。
4. **统一按钮布局**：VISUAL要求五个右列按钮集中定义或复用同一布局；目前分散硬编码在GameScreen/各Panel。请用共享布局定义生成五个44高、6间隔矩形（返回防御性副本），保持已通过几何与旧模态取消行为。

可一并处理开发中反馈记录的过时错误toast：异步回调显示错误前核对发起session/round/syncEpoch，旧scope结果不要向新局显示旧提示。

文档还需核实：DELIVERY当前列出`BEAM_SWAP_TIMEOUT`翻译，但源码没有该分支且服务端无该错误码；超时提示是快照驱动的本地展示，应准确描述，不能编造接口。暂停恢复会重新按恢复快照采样，不宜笼统写“暂停不重置采样”；重点是权威预算不被延长。

修复后先跑相关UI专项，再不筛选全量test及双端构建；重新运行两种视口的产品与最大布局GL，更新交付与正式回调。不得改Codex测试或GL脚本、提交或推送代码。Codex收到回调后独立重跑、检视截图及最终验收。
